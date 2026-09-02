using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Archie.Contracts;

namespace Archie.Scanner.DotNet;

internal sealed partial class SolutionModelBuilder(DotNetScannerLimits limits)
{
    private static readonly string[] SupportedExtensions = [".sln", ".slnx", ".csproj", ".cs"];

    public async Task<StructureModel> BuildAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var diagnostics = new List<Diagnostic>();
        var files = EnumerateInputs(root, diagnostics, cancellationToken);
        if (diagnostics.Any(item => item.Severity == "error")) return new([], [], diagnostics);

        var projects = new List<ProjectModel>();
        foreach (var file in files.Where(item => item.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = await ReadProjectAsync(root, file, files, diagnostics, cancellationToken);
            if (project is not null) projects.Add(project);
        }
        if (diagnostics.Any(item => item.Severity == "error")) return new([], [], diagnostics);

        var solutions = new List<SolutionModel>();
        foreach (var file in files.Where(item => item.Path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
        {
            var solution = await ReadSlnxAsync(root, file, diagnostics, cancellationToken);
            if (solution is not null) solutions.Add(solution);
        }
        foreach (var file in files.Where(item => item.Path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)))
        {
            var solution = await ReadSlnAsync(root, file, diagnostics, cancellationToken);
            if (solution is not null) solutions.Add(solution);
        }
        if (diagnostics.Any(item => item.Severity == "error")) return new([], [], diagnostics);

        var projectPaths = projects.ToDictionary(item => item.Path, PathComparer());
        foreach (var solution in solutions)
            foreach (var reference in solution.Projects)
            {
                if (!projectPaths.ContainsKey(reference.ProjectPath))
                    diagnostics.Add(Warning("DOTNET_SOLUTION_PROJECT_UNRESOLVED", solution.Path,
                        $"Solution '{solution.Path}' references project '{reference.ProjectPath}', which was not found.", solution.Key));
            }
        foreach (var group in solutions.SelectMany(solution => solution.Projects.Select(project => (solution, project)))
                     .GroupBy(item => item.project.ProjectPath, PathComparer()).Where(group => group.Count() > 1))
            diagnostics.Add(Warning("DOTNET_PROJECT_OWNERSHIP_AMBIGUOUS", group.Key,
                $"Project '{group.Key}' belongs to multiple solutions; every ownership relationship remains visible.", $"dotnet:project:{group.Key}"));

        return new(
            solutions.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray(),
            projects.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray(),
            diagnostics.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray());
    }

    private IReadOnlyList<SourceFile> EnumerateInputs(
        string root,
        ICollection<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var result = new List<SourceFile>();
        long bytes = 0;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new(root));
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = stack.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    diagnostics.Add(Warning("DOTNET_SYMLINK_SKIPPED", Relative(root, entry.FullName),
                        "The .NET scanner does not follow symbolic links.", null));
                    continue;
                }
                if (entry is DirectoryInfo child)
                {
                    if (child.Name is not ("bin" or "obj" or ".git" or "node_modules")) stack.Push(child);
                    continue;
                }
                if (entry is not FileInfo file || !SupportedExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)) continue;
                bytes += file.Length;
                if (result.Count >= limits.MaxInputFiles || bytes > limits.MaxInputBytes)
                {
                    diagnostics.Add(Error("DOTNET_INPUT_LIMIT_EXCEEDED", Relative(root, file.FullName),
                        $"The .NET scan exceeded its bounded input budget ({limits.MaxInputFiles} files or {limits.MaxInputBytes} bytes)."));
                    return [];
                }
                result.Add(new(Relative(root, file.FullName), file.FullName, file.Length));
            }
        }
        return result.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
    }

    private static async Task<ProjectModel?> ReadProjectAsync(
        string root,
        SourceFile file,
        IReadOnlyList<SourceFile> repositoryFiles,
        ICollection<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        XDocument document;
        try
        {
            await using var stream = File.OpenRead(file.FullPath);
            document = await XDocument.LoadAsync(stream, LoadOptions.SetLineInfo, cancellationToken);
        }
        catch (XmlException)
        {
            diagnostics.Add(Error("DOTNET_PROJECT_MALFORMED", file.Path, $"Project '{file.Path}' is not valid XML."));
            return null;
        }
        if (document.Root?.Name.LocalName != "Project")
        {
            diagnostics.Add(Error("DOTNET_PROJECT_UNSUPPORTED", file.Path, $"Project '{file.Path}' does not have a supported MSBuild Project root."));
            return null;
        }

        var rootElement = document.Root;
        if (HasCondition(rootElement))
        {
            diagnostics.Add(Warning("DOTNET_MSBUILD_CONDITION_UNEVALUATED", file.Path,
                $"Project '{file.Path}' has a root condition and was not interpreted.", $"dotnet:project:{file.Path}"));
            return null;
        }
        var sdk = ((string?)rootElement.Attribute("Sdk") ?? string.Empty).Trim();
        var projectDirectory = Path.GetDirectoryName(file.FullPath)!;
        var subject = $"dotnet:project:{file.Path}";
        var sdkUnknown = false;
        if (!IsLiteral(sdk))
        {
            diagnostics.Add(Warning("DOTNET_MSBUILD_VALUE_UNEVALUATED", file.Path,
                $"Project '{file.Path}' has an expanded SDK value; SDK defaults and classification were not interpreted.", subject));
            sdk = string.Empty;
            sdkUnknown = true;
        }
        var implicitImports = FindImplicitImports(root, projectDirectory).ToArray();
        foreach (var implicitImport in implicitImports)
            diagnostics.Add(Warning("DOTNET_MSBUILD_IMPORT_UNEVALUATED", file.Path,
                $"Project '{file.Path}' may receive values from '{implicitImport}'; imported values were not interpreted.", subject));

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var taintedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packages = new Dictionary<string, ProjectPackage>(StringComparer.OrdinalIgnoreCase);
        var references = new Dictionary<string, ProjectReferenceModel>(PathComparer());
        var frameworkReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var taintedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var taintedReferences = new HashSet<string>(PathComparer());
        var taintedFrameworkReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var compileOperations = new List<CompileOperation>();
        var importsUnknown = implicitImports.Length > 0;
        var packageItemsUnknown = importsUnknown;
        var referenceItemsUnknown = importsUnknown;
        var frameworkItemsUnknown = importsUnknown;
        var compileUnknown = importsUnknown || sdkUnknown;

        void TaintAllEvaluationDomains()
        {
            importsUnknown = packageItemsUnknown = referenceItemsUnknown = frameworkItemsUnknown = compileUnknown = true;
            properties.Clear();
            packages.Clear();
            references.Clear();
            frameworkReferences.Clear();
        }

        foreach (var group in rootElement.Elements())
        {
            var container = group.Name.LocalName;
            if (container is "Import" or "ImportGroup" or "Sdk")
            {
                diagnostics.Add(Warning("DOTNET_MSBUILD_IMPORT_UNEVALUATED", file.Path,
                    $"Project '{file.Path}' contains a direct or grouped MSBuild import or SDK element; facts it could alter were omitted.", subject));
                TaintAllEvaluationDomains();
                continue;
            }
            if (container is "Target" or "UsingTask" or "ProjectExtensions") continue;
            if (container is not ("PropertyGroup" or "ItemGroup"))
            {
                diagnostics.Add(Warning("DOTNET_MSBUILD_CONTAINER_UNEVALUATED", file.Path,
                    $"Project '{file.Path}' contains unsupported evaluation container '{container}'; facts it could alter were omitted.", subject));
                TaintAllEvaluationDomains();
                continue;
            }
            if (container == "PropertyGroup")
            {
                var propertyGroupConditioned = HasCondition(group);
                if (propertyGroupConditioned)
                    diagnostics.Add(Warning("DOTNET_MSBUILD_CONDITION_UNEVALUATED", file.Path,
                        $"A conditioned PropertyGroup in '{file.Path}' was not interpreted; affected properties were tainted.", subject));
                foreach (var property in group.Elements())
                {
                    var propertyName = property.Name.LocalName;
                    var value = property.Value.Trim();
                    if (propertyGroupConditioned || HasCondition(property) || !IsLiteral(value))
                    {
                        taintedProperties.Add(propertyName);
                        properties.Remove(propertyName);
                        diagnostics.Add(Warning("DOTNET_MSBUILD_VALUE_UNEVALUATED", file.Path,
                            $"MSBuild property '{propertyName}' in '{file.Path}' is conditioned or expanded; that property was omitted.", subject));
                    }
                    else if (!importsUnknown && !taintedProperties.Contains(propertyName))
                    {
                        properties[propertyName] = value;
                    }
                }
                continue;
            }

            var itemGroupConditioned = HasCondition(group);
            if (itemGroupConditioned)
                diagnostics.Add(Warning("DOTNET_MSBUILD_CONDITION_UNEVALUATED", file.Path,
                    $"A conditioned ItemGroup in '{file.Path}' was not interpreted; affected item identities were tainted.", subject));
            foreach (var item in group.Elements())
            {
                var kind = item.Name.LocalName;
                if (kind is not ("PackageReference" or "ProjectReference" or "FrameworkReference" or "Compile")) continue;
                var conditioned = itemGroupConditioned || HasCondition(item);
                if (HasCondition(item))
                    diagnostics.Add(Warning("DOTNET_MSBUILD_CONDITION_UNEVALUATED", file.Path,
                        $"Conditioned {kind} item in '{file.Path}' was not interpreted; its affected identity was tainted.", subject));
                var include = ((string?)item.Attribute("Include") ?? string.Empty).Trim();
                var remove = ((string?)item.Attribute("Remove") ?? string.Empty).Trim();
                var update = ((string?)item.Attribute("Update") ?? string.Empty).Trim();
                var exclude = ((string?)item.Attribute("Exclude") ?? string.Empty).Trim();
                if (kind == "Compile")
                {
                    if (conditioned || update.Length > 0 || exclude.Length > 0 || !IsLiteral(include) || !IsLiteral(remove) ||
                        (include.Length == 0) == (remove.Length == 0))
                    {
                        compileUnknown = true;
                        diagnostics.Add(Warning("DOTNET_MSBUILD_ITEM_UNEVALUATED", file.Path,
                            $"A conditioned, expanded, Update/Exclude-based, or ambiguous Compile item in '{file.Path}' made compile ownership unknown.", subject));
                    }
                    else if (!compileUnknown)
                    {
                        compileOperations.Add(new(include.Length > 0, SplitItemSpecs(include.Length > 0 ? include : remove).ToArray()));
                    }
                    continue;
                }

                var operations = new[] { include, remove, update }.Where(item => item.Length > 0).ToArray();
                var operation = operations.Length == 1 ? operations[0] : string.Empty;
                var operationIsBounded = operations.Length == 1 && IsLiteral(operation) &&
                    operation.IndexOfAny(['*', '?', ';']) < 0 && exclude.Length == 0;
                var unsupported = conditioned || update.Length > 0 || remove.Length > 0 || !operationIsBounded;
                if (unsupported)
                {
                    diagnostics.Add(Warning("DOTNET_MSBUILD_ITEM_UNEVALUATED", file.Path,
                        $"Conditioned, expanded, Remove/Update/Exclude-based, or empty {kind} item in '{file.Path}' was not interpreted; its affected identity was omitted.", subject));
                    TaintItem(kind, operationIsBounded ? operation : null, packages, references, frameworkReferences,
                        taintedPackages, taintedReferences, taintedFrameworkReferences,
                        ref packageItemsUnknown, ref referenceItemsUnknown, ref frameworkItemsUnknown);
                    continue;
                }
                if (kind == "PackageReference")
                {
                    var identity = include.ToLowerInvariant();
                    var versionElements = item.Elements().Where(child => child.Name.LocalName == "Version").ToArray();
                    var packageMetadataUnknown = item.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration &&
                        attribute.Name.LocalName is not ("Include" or "Version")) ||
                        item.Elements().Any(child => child.Name.LocalName != "Version") ||
                        versionElements.Length > 1 || versionElements.Any(child => child.HasAttributes || child.HasElements);
                    if (packageMetadataUnknown)
                    {
                        packages.Remove(identity);
                        taintedPackages.Add(identity);
                        diagnostics.Add(Warning("DOTNET_NUGET_COMPILE_ASSETS_UNEVALUATED", file.Path,
                            $"NuGet package '{include}' has aliases, asset filters, or unsupported metadata; its compile-time availability was omitted.", subject));
                        continue;
                    }
                    var version = ((string?)item.Attribute("Version") ??
                        versionElements.FirstOrDefault()?.Value)?.Trim();
                    var versionUnknown = !IsLiteral(version ?? string.Empty);
                    if (versionUnknown) version = null;
                    if (!packageItemsUnknown && !taintedPackages.Contains(identity))
                        packages[identity] = new(identity, version, file.Path, Range(item));
                    if (version is null)
                        diagnostics.Add(Warning("DOTNET_NUGET_VERSION_UNEVALUATED", file.Path,
                            $"NuGet package '{include}' has no bounded literal version; imported or central version information was omitted.", subject));
                }
                else if (kind == "FrameworkReference")
                {
                    if (!frameworkItemsUnknown && !taintedFrameworkReferences.Contains(include)) frameworkReferences.Add(include);
                }
                else
                {
                    var fullPath = Path.GetFullPath(Path.Combine(projectDirectory, include));
                    var target = IsWithin(fullPath, root) && File.Exists(fullPath) ? Relative(root, fullPath) : null;
                    var identity = include.Replace('\\', '/');
                    if (!referenceItemsUnknown && !taintedReferences.Contains(identity))
                        references[identity] = new(identity, target, file.Path, Range(item));
                }
            }
        }

        if (taintedProperties.Contains("EnableDefaultCompileItems") ||
            properties.ContainsKey("DefaultItemExcludes") || properties.ContainsKey("DefaultItemExcludesInProjectFolder"))
            compileUnknown = true;
        IReadOnlyList<SourceFile> sources;
        if (compileUnknown)
        {
            diagnostics.Add(Warning("DOTNET_COMPILE_EVALUATION_UNSUPPORTED", file.Path,
                $"Project '{file.Path}' has conditioned, expanded, imported, or unsupported compile evaluation; no source ownership was inferred.", subject));
            sources = [];
        }
        else
        {
            sources = ResolveCompileSources(root, file, repositoryFiles, sdk, properties, compileOperations, diagnostics);
        }

        var frameworkUnknown = importsUnknown || taintedProperties.Contains("TargetFramework") || taintedProperties.Contains("TargetFrameworks");
        IReadOnlyList<string>? targetFrameworks = frameworkUnknown ? null :
            Value(properties, "TargetFrameworks", Value(properties, "TargetFramework", string.Empty))
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var classificationUnknown = sdkUnknown || importsUnknown || packageItemsUnknown || frameworkItemsUnknown ||
            taintedPackages.Any(item => item is "microsoft.net.test.sdk" or "xunit" or "nunit") ||
            taintedFrameworkReferences.Contains("Microsoft.AspNetCore.App") ||
            new[] { "IsTestProject", "OutputType" }.Any(taintedProperties.Contains);
        var isTest = Value(properties, "IsTestProject", "false").Equals("true", StringComparison.OrdinalIgnoreCase) ||
                     packages.Values.Any(item => item.Name is "microsoft.net.test.sdk" or "xunit" or "nunit");
        var isWeb = sdk.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ||
                    frameworkReferences.Any(item => item.Equals("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase));
        var isWorker = sdk.Contains("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase);
        var outputType = Value(properties, "OutputType", "Library");
        var classification = classificationUnknown ? null : isTest ? "test" : isWeb ? "web" : isWorker ? "worker" :
            outputType is "Exe" or "WinExe" ? "executable" : "library";
        var name = importsUnknown || taintedProperties.Contains("AssemblyName")
            ? Path.GetFileNameWithoutExtension(file.Path) : Value(properties, "AssemblyName", Path.GetFileNameWithoutExtension(file.Path));
        return new(
            $"dotnet:project:{file.Path}", name, file.Path, Range(rootElement), classification,
            targetFrameworks,
            packages.Values.OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Version, StringComparer.Ordinal).ToArray(),
            references.Values.OrderBy(item => item.Include, StringComparer.Ordinal).ToArray(), sources,
            !importsUnknown && !taintedProperties.Contains("ImplicitUsings") && Value(properties, "ImplicitUsings", "disable") is "enable" or "true",
            classification is "web" or "worker" or "executable");
    }

    private static IReadOnlyList<SourceFile> ResolveCompileSources(
        string root,
        SourceFile project,
        IReadOnlyList<SourceFile> repositoryFiles,
        string sdk,
        IReadOnlyDictionary<string, string> properties,
        IReadOnlyList<CompileOperation> operations,
        ICollection<Diagnostic> diagnostics)
    {
        var projectDirectory = Path.GetDirectoryName(project.FullPath)!;
        var knownSdk = sdk.Equals("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase) ||
                       sdk.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ||
                       sdk.Equals("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase);
        var defaults = !Value(properties, "EnableDefaultCompileItems", "true").Equals("false", StringComparison.OrdinalIgnoreCase);
        var sources = new Dictionary<string, SourceFile>(PathComparer());
        if (defaults && knownSdk)
            foreach (var source in repositoryFiles.Where(item => item.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && IsWithin(item.FullPath, projectDirectory)))
                sources[source.Path] = source;
        else if (defaults && sdk.Length > 0)
            diagnostics.Add(Warning("DOTNET_COMPILE_EVALUATION_UNSUPPORTED", project.Path,
                $"Project '{project.Path}' has an unsupported SDK; no default compile ownership was inferred.", $"dotnet:project:{project.Path}"));

        foreach (var operation in operations)
            foreach (var pattern in operation.Patterns)
                if (operation.Include)
                {
                    foreach (var source in repositoryFiles.Where(item => item.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
                    {
                        var relative = Path.GetRelativePath(projectDirectory, source.FullPath).Replace('\\', '/');
                        if (GlobMatches(pattern, relative)) sources[source.Path] = source;
                    }
                }
                else
                {
                    foreach (var source in sources.Values.ToArray())
                    {
                        var relative = Path.GetRelativePath(projectDirectory, source.FullPath).Replace('\\', '/');
                        if (GlobMatches(pattern, relative)) sources.Remove(source.Path);
                    }
                }
        return sources.Values.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
    }

    private static void TaintItem(
        string kind,
        string? identity,
        IDictionary<string, ProjectPackage> packages,
        IDictionary<string, ProjectReferenceModel> references,
        ISet<string> frameworkReferences,
        ISet<string> taintedPackages,
        ISet<string> taintedReferences,
        ISet<string> taintedFrameworkReferences,
        ref bool packagesUnknown,
        ref bool referencesUnknown,
        ref bool frameworkReferencesUnknown)
    {
        if (kind == "PackageReference")
        {
            if (identity is null)
            {
                packagesUnknown = true;
                packages.Clear();
            }
            else
            {
                identity = identity.ToLowerInvariant();
                taintedPackages.Add(identity);
                packages.Remove(identity);
            }
        }
        else if (kind == "ProjectReference")
        {
            if (identity is null)
            {
                referencesUnknown = true;
                references.Clear();
            }
            else
            {
                identity = identity.Replace('\\', '/');
                taintedReferences.Add(identity);
                references.Remove(identity);
            }
        }
        else if (identity is null)
        {
            frameworkReferencesUnknown = true;
            frameworkReferences.Clear();
        }
        else
        {
            taintedFrameworkReferences.Add(identity);
            frameworkReferences.Remove(identity);
        }
    }

    private static bool GlobMatches(string pattern, string path)
    {
        var expression = "^" + Regex.Escape(pattern.Replace('\\', '/'))
            .Replace("\\*\\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace("\\*\\*", ".*", StringComparison.Ordinal)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal)
            .Replace("\\?", "[^/]", StringComparison.Ordinal) + "$";
        var options = RegexOptions.CultureInvariant;
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) options |= RegexOptions.IgnoreCase;
        return Regex.IsMatch(path, expression, options);
    }

    private static IEnumerable<string> SplitItemSpecs(string value) => value
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(item => item.Replace('\\', '/'));

    private static IEnumerable<string> FindImplicitImports(string root, string projectDirectory)
    {
        var directory = projectDirectory;
        while (IsWithin(directory, root) || PathComparer().Equals(directory, root))
        {
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
                if (File.Exists(Path.Combine(directory, name))) yield return Relative(root, Path.Combine(directory, name));
            if (PathComparer().Equals(directory, root)) yield break;
            directory = Path.GetDirectoryName(directory)!;
        }
    }

    private static bool HasCondition(XElement element) => element.Attribute("Condition") is not null;

    private static bool IsLiteral(string value) =>
        !value.Contains("$(", StringComparison.Ordinal) &&
        !value.Contains("@(", StringComparison.Ordinal) &&
        !value.Contains("%(", StringComparison.Ordinal);

    private static async Task<SolutionModel?> ReadSlnxAsync(
        string root,
        SourceFile file,
        ICollection<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        XDocument document;
        try
        {
            await using var stream = File.OpenRead(file.FullPath);
            document = await XDocument.LoadAsync(stream, LoadOptions.SetLineInfo, cancellationToken);
        }
        catch (XmlException)
        {
            diagnostics.Add(Error("DOTNET_SOLUTION_MALFORMED", file.Path, $"Solution '{file.Path}' is not valid SLNX XML."));
            return null;
        }
        if (document.Root?.Name.LocalName != "Solution")
        {
            diagnostics.Add(Error("DOTNET_SOLUTION_UNSUPPORTED", file.Path, $"Solution '{file.Path}' does not have a supported SLNX root."));
            return null;
        }
        var projects = document.Descendants().Where(item => item.Name.LocalName == "Project")
            .Select(item => ((string?)item.Attribute("Path"), Element: item))
            .Where(item => !string.IsNullOrWhiteSpace(item.Item1))
            .Select(item => new SolutionProject(NormalizeReferencedPath(root, file.FullPath, item.Item1!), Range(item.Element)))
            .OrderBy(item => item.ProjectPath, StringComparer.Ordinal).ToArray();
        return new($"dotnet:solution:{file.Path}", Path.GetFileNameWithoutExtension(file.Path), file.Path, Range(document.Root), projects);
    }

    private static async Task<SolutionModel?> ReadSlnAsync(
        string root,
        SourceFile file,
        ICollection<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(file.FullPath, cancellationToken);
        if (!text.StartsWith("Microsoft Visual Studio Solution File", StringComparison.Ordinal))
        {
            diagnostics.Add(Error("DOTNET_SOLUTION_UNSUPPORTED", file.Path, $"Solution '{file.Path}' does not have a supported SLN header."));
            return null;
        }
        var projects = new List<SolutionProject>();
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var match = SlnProject().Match(lines[index]);
            if (!match.Success) continue;
            var value = match.Groups[1].Value;
            if (!value.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Warning("DOTNET_SOLUTION_PROJECT_UNSUPPORTED", file.Path,
                    $"Solution project '{value}' is not a supported C# project and was not interpreted.", $"dotnet:solution:{file.Path}"));
                continue;
            }
            projects.Add(new(NormalizeReferencedPath(root, file.FullPath, value), new(index + 1, 1, index + 1, lines[index].TrimEnd('\r').Length + 1)));
        }
        return new($"dotnet:solution:{file.Path}", Path.GetFileNameWithoutExtension(file.Path), file.Path,
            new(1, 1, 1, Math.Max(2, lines[0].TrimEnd('\r').Length + 1)), projects.OrderBy(item => item.ProjectPath, StringComparer.Ordinal).ToArray());
    }

    private static string NormalizeReferencedPath(string root, string ownerPath, string value)
    {
        var fullPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ownerPath)!, value.Replace('\\', Path.DirectorySeparatorChar)));
        return IsWithin(fullPath, root) ? Relative(root, fullPath) : value.Replace('\\', '/');
    }

    private static string Value(IReadOnlyDictionary<string, string> properties, string name, string fallback) =>
        properties.TryGetValue(name, out var value) && value.Length > 0 ? value : fallback;

    private static SourceRange? Range(XObject item)
    {
        if (item is not IXmlLineInfo info || !info.HasLineInfo()) return null;
        return new(info.LineNumber, info.LinePosition, info.LineNumber, info.LinePosition + 1);
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static StringComparer PathComparer() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static Diagnostic Error(string code, string path, string message) =>
        new($"diagnostic:archie.dotnet:{code.ToLowerInvariant()}:{Stable(path)}", code, "error", message, null);

    private static Diagnostic Warning(string code, string path, string message, string? subject) =>
        new($"diagnostic:archie.dotnet:{code.ToLowerInvariant()}:{Stable($"{path}:{message}")}", code, "warning", message, subject);

    private static string Stable(string value) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..12];

    [GeneratedRegex("^Project\\(\\\"[^\\\"]+\\\"\\)\\s*=\\s*\\\"[^\\\"]+\\\",\\s*\\\"([^\\\"]+)\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex SlnProject();

    private sealed record CompileOperation(bool Include, IReadOnlyList<string> Patterns);
}
