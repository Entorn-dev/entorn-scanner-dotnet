using Archie.Contracts;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Diagnostic = Archie.Contracts.Diagnostic;

namespace Archie.Scanner.DotNet;

internal sealed record EfContextDetection(string Type, string Path, SourceRange Range, string Symbol);

internal sealed record DataTargetDetection(
    string Provider,
    string ConfigurationKey,
    string ContextType,
    string Path,
    SourceRange Range,
    string Rule,
    string Symbol);

internal sealed record ExternalTargetDetection(
    string ClientIdentity,
    string Provider,
    string ResourceKind,
    string? ConfigurationKey,
    string Scheme,
    string Host,
    int? Port,
    string Path,
    SourceRange Range,
    string Rule,
    string Symbol);

internal sealed record DataScanResult(
    IReadOnlyList<EfContextDetection> Contexts,
    IReadOnlyList<DataTargetDetection> DataTargets,
    IReadOnlyList<ExternalTargetDetection> ExternalTargets,
    IReadOnlyList<Diagnostic> Diagnostics);

internal sealed record TargetCompilation(
    IReadOnlyList<MetadataReference> References,
    CSharpParseOptions ParseOptions,
    CSharpCompilationOptions CompilationOptions);

internal static class DataScanner
{
    private const long MaxAssetsBytes = 32L * 1024 * 1024;
    private const long MaxBuildInputBytes = 1024 * 1024;
    private const long MaxMetadataBytes = 32L * 1024 * 1024;
    private const long MaxTotalMetadataBytes = 256L * 1024 * 1024;
    private const int MaxMetadataReferences = 512;
    private const string SupportedTargetFramework = "net10.0";
    private const string EfCoreAssembly = "Microsoft.EntityFrameworkCore";
    private const string SqliteAssembly = "Microsoft.EntityFrameworkCore.Sqlite";
    private const string DbContextType = "Microsoft.EntityFrameworkCore.DbContext";
    private const string AddDbContextType = "Microsoft.Extensions.DependencyInjection.EntityFrameworkServiceCollectionExtensions";
    private const string SqliteExtensionsType = "Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions";
    private const string HttpClientExtensionsType = "Microsoft.Extensions.DependencyInjection.HttpClientFactoryServiceCollectionExtensions";

    public static async Task<DataScanResult> ScanAsync(ProjectModel project, CancellationToken cancellationToken)
    {
        if (project.Sources.Count == 0)
        {
            if (!project.CompileInputsUncertain) return new([], [], [], []);
            var candidateRelevant = false;
            foreach (var source in project.CandidateSources)
            {
                var text = await File.ReadAllTextAsync(source.FullPath, cancellationToken);
                if (!ContainsPotentialDataSyntax(text)) continue;
                candidateRelevant = true;
                break;
            }
            if (!candidateRelevant) return new([], [], [], []);
            return new([], [], [], [ProjectWarning("DOTNET_DATA_COMPILATION_UNAVAILABLE", project,
                $"Project '{project.Path}' has uncertain compile inputs or source ownership; no Slice 8 facts were emitted.")]);
        }
        var targetCompilation = await TargetCompilationAsync(project, cancellationToken);
        var trees = new List<SyntaxTree>();
        var hasRelevantPreprocessor = false;
        foreach (var source in project.Sources)
        {
            var text = await File.ReadAllTextAsync(source.FullPath, cancellationToken);
            var tree = CSharpSyntaxTree.ParseText(text, targetCompilation?.ParseOptions ?? CSharpParseOptions.Default,
                source.Path, cancellationToken: cancellationToken);
            if (!tree.GetDiagnostics(cancellationToken).Any(item => item.Severity == DiagnosticSeverity.Error))
            {
                trees.Add(tree);
                hasRelevantPreprocessor |= ContainsPotentialDataSyntax(text) && tree.GetRoot(cancellationToken)
                    .DescendantTrivia(descendIntoTrivia: true).Any(item => item.GetStructure() is DirectiveTriviaSyntax);
            }
        }
        if (trees.Count == 0) return new([], [], [], []);

        var relevantNode = trees.Select(tree => tree.GetRoot(cancellationToken)).SelectMany(DataNodes).FirstOrDefault();
        var potentiallyRelevant = relevantNode is not null || hasRelevantPreprocessor;
        if (!potentiallyRelevant) return new([], [], [], []);

        var diagnosticNode = relevantNode ?? trees[0].GetRoot(cancellationToken);
        var diagnostics = new List<Diagnostic>();
        if (hasRelevantPreprocessor)
        {
            diagnostics.Add(Warning("DOTNET_DATA_COMPILATION_UNAVAILABLE", diagnosticNode.SyntaxTree, diagnosticNode,
                $"Project '{project.Path}' has preprocessor-dependent data or external-target syntax; no Slice 8 facts were emitted.", project.Key));
            return new([], [], [], diagnostics);
        }
        if (targetCompilation is null)
        {
            diagnostics.Add(Warning("DOTNET_DATA_COMPILATION_UNAVAILABLE", diagnosticNode.SyntaxTree, diagnosticNode,
                $"Project '{project.Path}' has no proven compatible target restore/compile assets; no Slice 8 facts were emitted.", project.Key));
            return new([], [], [], diagnostics);
        }

        var packages = project.Packages.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sqliteAvailable = packages.Contains("microsoft.entityframeworkcore.sqlite");
        var efAvailable = sqliteAvailable || packages.Contains("microsoft.entityframeworkcore");
        var globalUsings = project.ImplicitUsings ? CSharpSyntaxTree.ParseText(ImplicitUsings(project),
            targetCompilation.ParseOptions, "__archie_data_global_usings.g.cs") : null;
        var compilationTrees = globalUsings is null ? trees : trees.Append(globalUsings);
        var outputKind = trees.Any(tree => tree.GetRoot(cancellationToken).DescendantNodes().OfType<GlobalStatementSyntax>().Any())
            ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary;
        var compilation = CSharpCompilation.Create($"Archie.Data.{Stable(project.Path)}", compilationTrees, targetCompilation.References,
            targetCompilation.CompilationOptions.WithOutputKind(outputKind));

        var contexts = new List<EfContextDetection>();
        var dataTargets = new List<DataTargetDetection>();
        var externalTargets = new List<ExternalTargetDetection>();
        var errors = compilation.GetDiagnostics(cancellationToken).Where(item => item.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            var codes = string.Join(", ", errors.Select(item => item.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            diagnostics.Add(Warning("DOTNET_DATA_COMPILATION_UNAVAILABLE", diagnosticNode.SyntaxTree, diagnosticNode,
                $"Project '{project.Path}' has semantic compilation errors ({codes}) affecting data or external-target analysis; no Slice 8 facts were emitted.", project.Key));
            return new([], [], [], diagnostics);
        }

        foreach (var tree in trees.OrderBy(item => item.FilePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = await tree.GetRootAsync(cancellationToken);
            var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            if (efAvailable) ScanContexts(tree, root, model, contexts);
            if (sqliteAvailable) ScanSqlite(project, tree, root, model, dataTargets, diagnostics);
            if (project.Classification == "web") ScanHttpClients(project, tree, root, model, externalTargets, diagnostics);
        }
        var ambiguousDataTargets = AmbiguousDataTargets(project, dataTargets, diagnostics);
        var ambiguousExternalTargets = AmbiguousExternalTargets(project, externalTargets, diagnostics);
        return new(
            contexts.DistinctBy(item => item.Type).OrderBy(item => item.Type, StringComparer.Ordinal).ToArray(),
            dataTargets.Where(item => !ambiguousDataTargets.Contains(item))
                .OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Range.StartLine).ToArray(),
            externalTargets.Where(item => !ambiguousExternalTargets.Contains(item))
                .DistinctBy(item => $"{item.ClientIdentity}:{item.Provider}:{item.Scheme}:{item.Host}:{item.Port}:{item.ConfigurationKey}", StringComparer.Ordinal)
                .OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Range.StartLine).ToArray(),
            diagnostics.DistinctBy(item => item.Id).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray());
    }

    private static HashSet<DataTargetDetection> AmbiguousDataTargets(
        ProjectModel project,
        IReadOnlyCollection<DataTargetDetection> targets,
        ICollection<Diagnostic> diagnostics)
    {
        var ambiguous = new HashSet<DataTargetDetection>();
        foreach (var group in targets.GroupBy(item => item.ContextType, StringComparer.Ordinal)
                     .Where(group => group.Select(item => $"{item.Provider}:{item.ConfigurationKey}")
                         .Distinct(StringComparer.Ordinal).Skip(1).Any()))
        {
            ambiguous.UnionWith(group);
            diagnostics.Add(AmbiguityWarning(project, group.First(),
                "An EF Core context has multiple configured datastore targets; no datastore fact was invented."));
        }
        foreach (var group in targets.GroupBy(item => $"{item.Provider}:{item.ConfigurationKey}", StringComparer.Ordinal)
                     .Where(group => group.Select(item => item.ContextType).Distinct(StringComparer.Ordinal).Skip(1).Any()))
        {
            ambiguous.UnionWith(group);
            diagnostics.Add(AmbiguityWarning(project, group.First(),
                "A configured datastore target is shared by multiple EF Core contexts; no datastore fact was invented."));
        }
        return ambiguous;
    }

    private static Diagnostic AmbiguityWarning(ProjectModel project, DataTargetDetection detection, string message) =>
        new($"diagnostic:archie.dotnet:dotnet_data_configuration_ambiguous:{Stable($"{project.Path}:{detection.ContextType}:{detection.Provider}:{detection.ConfigurationKey}")}",
            "DOTNET_DATA_CONFIGURATION_AMBIGUOUS", "warning", message, project.Key);

    private static HashSet<ExternalTargetDetection> AmbiguousExternalTargets(
        ProjectModel project,
        IReadOnlyCollection<ExternalTargetDetection> targets,
        ICollection<Diagnostic> diagnostics)
    {
        var ambiguous = new HashSet<ExternalTargetDetection>();
        foreach (var group in targets.GroupBy(item => item.ClientIdentity, StringComparer.Ordinal).Where(group => group
                     .Select(item => $"{item.Provider}:{item.Scheme}:{item.Host}:{item.Port}:{item.ConfigurationKey}")
                     .Distinct(StringComparer.Ordinal).Skip(1).Any()))
        {
            ambiguous.UnionWith(group);
            diagnostics.Add(new(
                $"diagnostic:archie.dotnet:dotnet_external_target_ambiguous:{Stable($"{project.Path}:{group.Key}")}",
                "DOTNET_EXTERNAL_TARGET_AMBIGUOUS", "warning",
                "A named or typed HTTP client has multiple distinct configured targets; no external target was invented.",
                project.Key));
        }
        return ambiguous;
    }

    private static void ScanContexts(SyntaxTree tree, SyntaxNode root, SemanticModel model, ICollection<EfContextDetection> contexts)
    {
        foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol || !DerivesFrom(symbol, DbContextType)) continue;
            contexts.Add(new(symbol.ToDisplayString(), tree.FilePath, Range(tree, declaration.Identifier), symbol.ToDisplayString()));
        }
    }

    private static void ScanSqlite(ProjectModel project, SyntaxTree tree, SyntaxNode root, SemanticModel model,
        ICollection<DataTargetDetection> targets, ICollection<Diagnostic> diagnostics)
    {
        foreach (var registration in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(registration).Symbol is not IMethodSymbol add || add.Name != "AddDbContext" ||
                add.ContainingType.ToDisplayString() != AddDbContextType || add.ContainingAssembly.Name != EfCoreAssembly ||
                add.TypeArguments.FirstOrDefault() is not INamedTypeSymbol context || !DerivesFrom(context, DbContextType)) continue;
            if (!TryConfigurationLambda(registration, model, "optionsAction", out var lambda, out var options))
            {
                diagnostics.Add(Warning("DOTNET_DATA_PROVIDER_UNRESOLVED", tree, registration,
                    "A semantically resolved EF Core context registration has an unsupported provider configuration argument; no datastore fact was invented.", project.Key));
                continue;
            }
            var recognizedProviders = lambda.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Where(invocation =>
                invocation.Expression is MemberAccessExpressionSyntax member &&
                SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(member.Expression).Symbol, options) &&
                model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method && method.Name == "UseSqlite" &&
                method.ContainingType.ToDisplayString() == SqliteExtensionsType && method.ContainingAssembly.Name == SqliteAssembly).ToArray();
            if (recognizedProviders.Length > 1)
            {
                diagnostics.Add(Warning("DOTNET_DATA_PROVIDER_AMBIGUOUS", tree, registration,
                    "An EF Core context registration selects multiple providers; no datastore fact was invented.", project.Key));
                continue;
            }
            var providers = recognizedProviders.Where(invocation => IsDirectLambdaExpression(invocation, lambda)).ToArray();
            if (providers.Length != 1)
            {
                diagnostics.Add(Warning("DOTNET_DATA_PROVIDER_UNRESOLVED", tree, registration,
                    "A semantically resolved EF Core context registration has no single direct supported provider; no datastore fact was invented.", project.Key));
                continue;
            }
            var provider = providers[0];
            var operation = model.GetOperation(provider) as IInvocationOperation;
            var connection = operation?.Arguments.FirstOrDefault(item => item.Parameter?.Name == "connectionString");
            var configuration = ConfigurationValueResolver.ResolveConnectionString(connection);
            if (configuration.DiagnosticCode is not null || configuration.Key is null)
            {
                diagnostics.Add(Warning(configuration.DiagnosticCode ?? "DOTNET_DATA_CONFIGURATION_UNRESOLVED", tree, provider,
                    "A semantically resolved EF Core SQLite provider has no safe direct connection-string configuration key; no datastore fact was invented.", project.Key));
                continue;
            }
            targets.Add(new("sqlite", configuration.Key, context.ToDisplayString(), tree.FilePath, Range(tree, provider),
                "roslyn:semantic-ef-core-sqlite-configuration", add.ToDisplayString()));
        }
    }

    private static void ScanHttpClients(ProjectModel project, SyntaxTree tree, SyntaxNode root, SemanticModel model,
        ICollection<ExternalTargetDetection> targets, ICollection<Diagnostic> diagnostics)
    {
        foreach (var registration in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(registration).Symbol is not IMethodSymbol add || add.Name != "AddHttpClient" ||
                add.ContainingType.ToDisplayString() != HttpClientExtensionsType ||
                add.ContainingAssembly.Name != "Microsoft.Extensions.Http") continue;
            if (model.GetOperation(registration) is not IInvocationOperation operation ||
                !TryHttpClientIdentity(add, operation, out var clientIdentity))
            {
                diagnostics.Add(Warning("DOTNET_EXTERNAL_TARGET_UNRESOLVED", tree, registration,
                    "A semantically resolved AddHttpClient registration has no direct constant named or typed client identity; no external target was invented.", project.Key));
                continue;
            }
            if (!TryConfigurationLambda(registration, model, "configureClient", out var lambda, out var client))
            {
                diagnostics.Add(Warning("DOTNET_EXTERNAL_TARGET_UNRESOLVED", tree, registration,
                    "A semantically resolved AddHttpClient registration has an unsupported configuration argument; no external target was invented.", project.Key));
                continue;
            }
            var assignments = lambda.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>().Where(assignment =>
                assignment.Left is MemberAccessExpressionSyntax member &&
                SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(member.Expression).Symbol, client) &&
                model.GetSymbolInfo(assignment.Left).Symbol is IPropertySymbol property && property.Name == "BaseAddress" &&
                property.ContainingType.ToDisplayString() == "System.Net.Http.HttpClient" &&
                property.ContainingAssembly.Name == "System.Net.Http").ToArray();
            if (assignments.Length != 1 || !IsDirectLambdaExpression(assignments[0], lambda))
            {
                diagnostics.Add(Warning("DOTNET_EXTERNAL_TARGET_UNRESOLVED", tree, registration,
                    "A semantically resolved AddHttpClient registration has no single direct BaseAddress assignment; no external target was invented.", project.Key));
                continue;
            }
            var configuration = ConfigurationValueResolver.ResolveHttpUri(
                model.GetOperation(assignments[0].Right), assignments[0].Right, model);
            if (configuration.DiagnosticCode is not null || configuration.Scheme is null || configuration.Host is null)
            {
                diagnostics.Add(Warning(configuration.DiagnosticCode ?? "DOTNET_EXTERNAL_TARGET_UNRESOLVED", tree, assignments[0],
                    "A semantically resolved HTTP client has no safe direct literal target fallback; no external target was invented.", project.Key));
                continue;
            }
            targets.Add(new(clientIdentity, "http", "external-service", configuration.Key, configuration.Scheme, configuration.Host,
                configuration.Port, tree.FilePath, Range(tree, assignments[0]),
                "roslyn:semantic-http-client-configured-base-address", add.ToDisplayString()));
        }
    }

    private static bool TryHttpClientIdentity(
        IMethodSymbol method,
        IInvocationOperation invocation,
        out string identity)
    {
        var name = invocation.Arguments.FirstOrDefault(item => item.Parameter?.Name == "name")?.Value.ConstantValue;
        if (name.HasValue)
        {
            identity = name.Value.Value is string text && !string.IsNullOrWhiteSpace(text) && text.Length <= 256
                ? $"named:{text}"
                : string.Empty;
            return identity.Length > 0;
        }
        if (method.TypeArguments.FirstOrDefault() is { TypeKind: not TypeKind.Error } clientType)
        {
            identity = $"typed:{clientType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}";
            return true;
        }
        identity = string.Empty;
        return false;
    }

    private static bool TryConfigurationLambda(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        string parameterName,
        out LambdaExpressionSyntax lambda,
        out IParameterSymbol parameter)
    {
        lambda = null!;
        parameter = null!;
        if (model.GetOperation(invocation) is not IInvocationOperation operation) return false;
        IOperation? value = operation.Arguments.FirstOrDefault(item => item.Parameter?.Name == parameterName)?.Value;
        while (value is IConversionOperation conversion) value = conversion.Operand;
        if (value is IDelegateCreationOperation delegateCreation) value = delegateCreation.Target;
        if (value is not IAnonymousFunctionOperation anonymous || anonymous.Symbol.Parameters.Length != 1 ||
            anonymous.Syntax is not LambdaExpressionSyntax syntax) return false;
        lambda = syntax;
        parameter = anonymous.Symbol.Parameters[0];
        return true;
    }

    private static bool IsDirectLambdaExpression(ExpressionSyntax expression, LambdaExpressionSyntax lambda) =>
        lambda.Body == expression || expression.Parent is ExpressionStatementSyntax statement &&
            statement.Parent is BlockSyntax block && block == lambda.Body && block.Statements.Count == 1;

    private static IEnumerable<SyntaxNode> DataNodes(SyntaxNode root) => root.DescendantNodes().Where(node => node switch
    {
        ClassDeclarationSyntax { BaseList: not null } declaration => declaration.BaseList.Types.Any(item =>
            item.Type.ToString().Split('.').Last().EndsWith("DbContext", StringComparison.Ordinal)),
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } =>
            member.Name.Identifier.ValueText is "AddDbContext" or "UseSqlite" or "AddHttpClient",
        AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax member } => member.Name.Identifier.ValueText == "BaseAddress",
        _ => false
    });

    private static bool ContainsPotentialDataSyntax(string source) =>
        new[] { "DbContext", "AddDbContext", "UseSqlite", "AddHttpClient", "BaseAddress" }
            .Any(value => source.Contains(value, StringComparison.Ordinal));

    private static async Task<TargetCompilation?> TargetCompilationAsync(
        ProjectModel project,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadTargetCompilationAsync(project, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidOperationException or ArgumentException or NotSupportedException or BadImageFormatException)
        {
            return null;
        }
    }

    private static async Task<TargetCompilation?> ReadTargetCompilationAsync(
        ProjectModel project,
        CancellationToken cancellationToken)
    {
        if (project.CompileInputsUncertain || project.TargetFrameworks is not [SupportedTargetFramework]) return null;
        var compilerOptions = ReadCompilerOptions(project);
        if (compilerOptions is null) return null;
        var assetsPath = Path.Combine(Path.GetDirectoryName(project.FullPath)!, "obj", "project.assets.json");
        if (!SafeRegularFile(assetsPath, MaxAssetsBytes)) return null;

        JsonDocument document;
        try
        {
            await using var stream = File.OpenRead(assetsPath);
            document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 64 }, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
        using (document)
        {
            var root = document.RootElement;
            if (!TryObject(root, out var rootProperties) ||
                !RequiredObject(rootProperties, "targets", out var targets) ||
                !TryObject(targets, out var targetFrameworks) || targetFrameworks.Count != 1 ||
                !RequiredObject(targetFrameworks, SupportedTargetFramework, out var target) ||
                !TryObject(target, out var targetEntries) ||
                !RequiredObject(rootProperties, "libraries", out var libraries) ||
                !TryObject(libraries, out var libraryEntries) ||
                !targetEntries.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(libraryEntries.Keys) ||
                !RequiredObject(rootProperties, "project", out var restoredProject) ||
                !TryRestoreProvenance(project, rootProperties, restoredProject, out var packageRoot) ||
                !RestoreHasNoErrors(rootProperties)) return null;

            var dependencyNames = RestoredDependencyNames(restoredProject);
            if (dependencyNames is null || project.Packages.Any(package =>
                    !dependencyNames.Contains(package.Name, StringComparer.OrdinalIgnoreCase)) ||
                dependencyNames.Any(dependency => targetEntries.Keys.Count(identity =>
                    identity.StartsWith($"{dependency}/", StringComparison.Ordinal)) != 1)) return null;

            var references = new List<MetadataReference>();
            var assemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalMetadataBytes = 0;
            foreach (var (entryName, entryValue) in targetEntries)
            {
                if (!TryObject(entryValue, out var targetEntry) ||
                    !RequiredString(targetEntry, "type", out var targetType) ||
                    !TryObject(libraryEntries[entryName], out var libraryEntry) ||
                    !RequiredString(libraryEntry, "type", out var libraryType) || targetType != libraryType)
                    return null;
                if (targetType != "package")
                {
                    if (targetType != "project") return null;
                    continue;
                }
                if (!TryPackageIdentity(entryName, out var packageName, out var packageVersion) ||
                    !RequiredString(libraryEntry, "path", out var libraryPath) ||
                    libraryPath != $"{packageName.ToLowerInvariant()}/{packageVersion.ToLowerInvariant()}" ||
                    !RequiredArray(libraryEntry, "files", out var files) ||
                    !StringArray(files, out var packageFiles) || targetEntry.ContainsKey("aliases") ||
                    !TryPackageDirectory(packageRoot, entryName, out var packageDirectory)) return null;
                if (!targetEntry.TryGetValue("compile", out var compile)) continue;
                if (!TryObject(compile, out var compileAssets)) return null;
                foreach (var (assetName, assetValue) in compileAssets)
                {
                    if (assetValue.ValueKind != JsonValueKind.Object || !packageFiles.Contains(assetName, StringComparer.Ordinal) ||
                        !ValidPackageRelativePath(assetName)) return null;
                    if (!assetName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    var reference = await ReadPackageReferenceAsync(packageDirectory, assetName, packageVersion,
                        assemblyNames, cancellationToken);
                    if (reference is null) return null;
                    totalMetadataBytes += reference.Value.Bytes;
                    if (references.Count >= MaxMetadataReferences || totalMetadataBytes > MaxTotalMetadataBytes) return null;
                    references.Add(reference.Value.Reference);
                }
            }

            var packages = project.Packages.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (packages.Contains("microsoft.entityframeworkcore.sqlite") &&
                (!assemblyNames.Contains(EfCoreAssembly) || !assemblyNames.Contains(SqliteAssembly))) return null;
            if (!AddFrameworkReferences(project, assemblyNames, references, ref totalMetadataBytes)) return null;
            return new(references,
                compilerOptions.Value.ParseOptions, compilerOptions.Value.CompilationOptions);
        }
    }

    private static (CSharpParseOptions ParseOptions, CSharpCompilationOptions CompilationOptions)? ReadCompilerOptions(
        ProjectModel project)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var projectDirectory = Path.GetDirectoryName(project.FullPath)!;
        if (Ancestors(projectDirectory).Any(directory =>
                FindFile(directory, "Directory.Build.targets") is not null || FindFile(directory, ".editorconfig") is not null))
            return null;
        var buildProps = Ancestors(projectDirectory).Select(directory => FindFile(directory, "Directory.Build.props"))
            .FirstOrDefault(path => path is not null);
        if (buildProps is not null && !ReadProperties(buildProps, properties, projectFile: false)) return null;
        if (!ReadProperties(project.FullPath, properties, projectFile: true)) return null;
        if (properties.TryGetValue("ManagePackageVersionsCentrally", out var centralVersions) &&
            (!bool.TryParse(centralVersions, out var centralVersionsEnabled) || centralVersionsEnabled)) return null;

        var languageVersion = LanguageVersion.Default;
        if (properties.TryGetValue("LangVersion", out var language) && !LanguageVersionFacts.TryParse(language, out languageVersion))
            return null;
        var symbols = Array.Empty<string>();
        if (properties.TryGetValue("DefineConstants", out var constants))
        {
            symbols = constants.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (symbols.Any(symbol => !SyntaxFacts.IsValidIdentifier(symbol))) return null;
        }
        var nullable = NullableContextOptions.Disable;
        if (properties.TryGetValue("Nullable", out var nullableValue))
            nullable = nullableValue.ToLowerInvariant() switch
            {
                "enable" => NullableContextOptions.Enable,
                "disable" => NullableContextOptions.Disable,
                "warnings" => NullableContextOptions.Warnings,
                "annotations" => NullableContextOptions.Annotations,
                _ => (NullableContextOptions)(-1)
            };
        if ((int)nullable < 0) return null;
        var warningsAsErrors = false;
        if (properties.TryGetValue("TreatWarningsAsErrors", out var warningValue) &&
            !bool.TryParse(warningValue, out warningsAsErrors)) return null;
        return (new CSharpParseOptions(languageVersion, preprocessorSymbols: symbols),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                generalDiagnosticOption: warningsAsErrors ? ReportDiagnostic.Error : ReportDiagnostic.Default,
                nullableContextOptions: nullable,
                specificDiagnosticOptions: new Dictionary<string, ReportDiagnostic>(StringComparer.Ordinal)
                {
                    ["CS1701"] = ReportDiagnostic.Suppress,
                    ["CS1702"] = ReportDiagnostic.Suppress
                }));
    }

    private static bool ReadProperties(
        string path,
        IDictionary<string, string> properties,
        bool projectFile)
    {
        if (!SafeRegularFile(path, MaxBuildInputBytes)) return false;
        XDocument document;
        try { document = XDocument.Load(path, LoadOptions.None); }
        catch (XmlException) { return false; }
        if (document.Root?.Name.LocalName != "Project" || document.Root.Attributes().Any(attribute =>
                !attribute.IsNamespaceDeclaration && attribute.Name.LocalName is not "Sdk")) return false;
        var fileProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in document.Root.Elements())
        {
            if (container.Name.LocalName == "PropertyGroup")
            {
                if (container.HasAttributes) return false;
                foreach (var property in container.Elements())
                {
                    if (property.HasAttributes || property.HasElements || property.Value.Contains("$(", StringComparison.Ordinal) ||
                        !SupportedBuildProperty(property.Name.LocalName) || !fileProperties.Add(property.Name.LocalName)) return false;
                    properties[property.Name.LocalName] = property.Value.Trim();
                }
            }
            else if (!projectFile || container.Name.LocalName != "ItemGroup" || container.HasAttributes ||
                     container.Elements().Any(item => item.Name.LocalName is not
                         ("PackageReference" or "ProjectReference" or "FrameworkReference" or "Compile")))
            {
                return false;
            }
        }
        return true;
    }

    private static bool SupportedBuildProperty(string name) => name is
        "AnalysisLevel" or "AssemblyName" or "DefineConstants" or "Deterministic" or "EnableDefaultCompileItems" or
        "ImplicitUsings" or "IsTestProject" or "LangVersion" or "Nullable" or "OutputType" or "RestorePackagesWithLockFile" or
        "RootNamespace" or "TargetFramework" or "TargetFrameworks" or "TreatWarningsAsErrors" or "ManagePackageVersionsCentrally";

    private static bool TryRestoreProvenance(
        ProjectModel project,
        IReadOnlyDictionary<string, JsonElement> root,
        JsonElement restoredProject,
        out string packageRoot)
    {
        packageRoot = string.Empty;
        if (!TryObject(restoredProject, out var projectProperties) ||
            !RequiredObject(projectProperties, "restore", out var restore) || !TryObject(restore, out var restoreProperties) ||
            !RequiredString(restoreProperties, "projectPath", out var projectPath) ||
            !Path.IsPathFullyQualified(projectPath) ||
            !PathComparer().Equals(Path.GetFullPath(projectPath), Path.GetFullPath(project.FullPath)) ||
            !RequiredString(restoreProperties, "packagesPath", out var packagesPath) ||
            !Path.IsPathFullyQualified(packagesPath) ||
            !RequiredArray(restoreProperties, "configFilePaths", out var configPaths) ||
            !StringArray(configPaths, out var configurations) || configurations.Count == 0 ||
            configurations.Any(path => !Path.IsPathFullyQualified(path) ||
                                       !SafeRegularFile(Path.GetFullPath(path), MaxBuildInputBytes)) ||
            !RequiredArray(restoreProperties, "originalTargetFrameworks", out var originalFrameworks) ||
            !StringArray(originalFrameworks, out var originalFrameworkNames) ||
            originalFrameworkNames is not [SupportedTargetFramework] ||
            !RequiredObject(restoreProperties, "frameworks", out var restoreFrameworks) ||
            !TryObject(restoreFrameworks, out var restoredFrameworkNames) || restoredFrameworkNames.Count != 1 ||
            !RequiredObject(restoredFrameworkNames, SupportedTargetFramework, out _) ||
            !RequiredObject(projectProperties, "frameworks", out var projectFrameworks) ||
            !TryObject(projectFrameworks, out var projectFrameworkNames) || projectFrameworkNames.Count != 1 ||
            !RequiredObject(projectFrameworkNames, SupportedTargetFramework, out _) ||
            !RequiredObject(root, "packageFolders", out var packageFolders) ||
            !TryObject(packageFolders, out var configuredRoots) || configuredRoots.Count != 1 ||
            configuredRoots.Single().Value.ValueKind != JsonValueKind.Object)
            return false;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile)) return false;
        var expectedRoot = Path.GetFullPath(Path.Combine(userProfile, ".nuget", "packages"));
        var configuredRoot = configuredRoots.Single().Key;
        if (!Path.IsPathFullyQualified(configuredRoot) ||
            !PathComparer().Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(packagesPath)), expectedRoot) ||
            !PathComparer().Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot)), expectedRoot) ||
            !SafeDirectory(expectedRoot)) return false;
        packageRoot = expectedRoot;
        return true;
    }

    private static IReadOnlyList<string>? RestoredDependencyNames(JsonElement restoredProject)
    {
        if (!TryObject(restoredProject, out var projectProperties) ||
            !RequiredObject(projectProperties, "frameworks", out var frameworks) ||
            !TryObject(frameworks, out var frameworkProperties) ||
            !RequiredObject(frameworkProperties, SupportedTargetFramework, out var framework) ||
            !TryObject(framework, out var targetProperties)) return null;
        if (!targetProperties.TryGetValue("dependencies", out var dependencies)) return [];
        if (!TryObject(dependencies, out var dependencyProperties)) return null;
        foreach (var dependency in dependencyProperties.Values)
            if (!TryObject(dependency, out var properties) ||
                !RequiredString(properties, "target", out var target) || target != "Package" ||
                !RequiredString(properties, "version", out _)) return null;
        return dependencyProperties.Keys.ToArray();
    }

    private static bool RestoreHasNoErrors(IReadOnlyDictionary<string, JsonElement> root)
    {
        if (!root.TryGetValue("logs", out var logs)) return true;
        if (logs.ValueKind != JsonValueKind.Array) return false;
        foreach (var log in logs.EnumerateArray())
        {
            if (!TryObject(log, out var properties) || !RequiredString(properties, "level", out var level)) return false;
            if (level.Equals("error", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static bool TryObject(JsonElement value, out IReadOnlyDictionary<string, JsonElement> properties)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (value.ValueKind != JsonValueKind.Object)
        {
            properties = result;
            return false;
        }
        foreach (var property in value.EnumerateObject())
            if (!normalized.Add(property.Name) || !result.TryAdd(property.Name, property.Value))
            {
                properties = result;
                return false;
            }
        properties = result;
        return true;
    }

    private static bool RequiredObject(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        out JsonElement value) => properties.TryGetValue(name, out value) && value.ValueKind == JsonValueKind.Object;

    private static bool RequiredArray(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        out JsonElement value) => properties.TryGetValue(name, out value) && value.ValueKind == JsonValueKind.Array;

    private static bool RequiredString(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        out string value)
    {
        value = string.Empty;
        return properties.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value = element.GetString()!);
    }

    private static bool StringArray(JsonElement value, out IReadOnlyList<string> values)
    {
        var result = new List<string>();
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()) ||
                result.Count >= 100_000 || !normalized.Add(item.GetString()!))
            {
                values = result;
                return false;
            }
            result.Add(item.GetString()!);
        }
        values = result;
        return true;
    }

    private static bool TryPackageIdentity(string identity, out string name, out string version)
    {
        name = string.Empty;
        version = string.Empty;
        if (identity.Contains('\\', StringComparison.Ordinal)) return false;
        var parts = identity.Split('/');
        if (parts.Length != 2 || parts.Any(part => !SafePackagePart(part))) return false;
        name = parts[0];
        version = parts[1];
        return true;
    }

    private static bool ValidPackageRelativePath(string assetName)
    {
        if (Path.IsPathRooted(assetName) || assetName.Contains('\\', StringComparison.Ordinal)) return false;
        var parts = assetName.Split('/');
        return parts.Length >= 2 && parts[0] is "lib" or "ref" &&
               parts.All(part => part.Length > 0 && part is not ("." or ".."));
    }

    private static async Task<(MetadataReference Reference, long Bytes)?> ReadPackageReferenceAsync(
        string packageDirectory,
        string assetName,
        string packageVersion,
        ISet<string> assemblyNames,
        CancellationToken cancellationToken)
    {
        if (!TryPackageAsset(packageDirectory, assetName, out var path)) return null;
        var file = new FileInfo(path);
        if (file.Length > MaxMetadataBytes) return null;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.LongLength != file.Length) return null;
        var image = ImmutableArray.Create(bytes);
        using var pe = new PEReader(image);
        if (!pe.HasMetadata) return null;
        var reader = pe.GetMetadataReader();
        if (!reader.IsAssembly) return null;
        var definition = reader.GetAssemblyDefinition();
        var assemblyName = reader.GetString(definition.Name);
        var expectedName = Path.GetFileNameWithoutExtension(assetName);
        var numericVersion = packageVersion.Split(['-', '+'], 2)[0];
        if (!assemblyName.Equals(expectedName, StringComparison.Ordinal) ||
            !Version.TryParse(numericVersion, out var expectedVersion) ||
            definition.Version.Major != expectedVersion.Major || !assemblyNames.Add(assemblyName)) return null;
        return (MetadataReference.CreateFromImage(image), bytes.LongLength);
    }

    private static bool AddFrameworkReferences(
        ProjectModel project,
        ISet<string> assemblyNames,
        ICollection<MetadataReference> references,
        ref long totalMetadataBytes)
    {
        var runtime = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        var dotnetRoot = runtime.Parent?.Parent?.Parent;
        if (dotnetRoot is null || !SafeDirectory(dotnetRoot.FullName)) return false;
        var packs = new List<string>
        {
            Path.Combine(dotnetRoot.FullName, "packs", "Microsoft.NETCore.App.Ref", runtime.Name, "ref", SupportedTargetFramework)
        };
        if (project.Classification == "web")
            packs.Add(Path.Combine(dotnetRoot.FullName, "packs", "Microsoft.AspNetCore.App.Ref", runtime.Name, "ref", SupportedTargetFramework));
        foreach (var pack in packs)
        {
            if (!SafeDirectory(pack)) return false;
            foreach (var path in Directory.EnumerateFiles(pack, "*.dll", SearchOption.TopDirectoryOnly)
                         .OrderBy(item => item, PathComparer()))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (assemblyNames.Contains(name)) continue;
                var file = new FileInfo(path);
                if (!SafeRegularFile(path, MaxMetadataBytes) || references.Count >= MaxMetadataReferences ||
                    totalMetadataBytes + file.Length > MaxTotalMetadataBytes || !assemblyNames.Add(name)) return false;
                references.Add(MetadataReference.CreateFromFile(path));
                totalMetadataBytes += file.Length;
            }
        }
        return true;
    }

    private static bool TryPackageDirectory(string packageRoot, string entryName, out string packageDirectory)
    {
        packageDirectory = string.Empty;
        if (entryName.Contains('\\', StringComparison.Ordinal)) return false;
        var parts = entryName.Split('/');
        if (parts.Length != 2 || parts.Any(part => !SafePackagePart(part))) return false;
        packageDirectory = Path.GetFullPath(Path.Combine(packageRoot, parts[0].ToLowerInvariant(), parts[1].ToLowerInvariant()));
        return IsWithin(packageDirectory, packageRoot) && SafeDirectory(packageDirectory);
    }

    private static bool TryPackageAsset(string packageDirectory, string assetName, out string path)
    {
        path = string.Empty;
        if (Path.IsPathRooted(assetName) || assetName.Contains('\\', StringComparison.Ordinal)) return false;
        var parts = assetName.Split('/');
        if (parts.Length < 2 || parts[0] is not ("lib" or "ref") ||
            parts.Any(part => part.Length == 0 || part is "." or "..")) return false;
        path = Path.GetFullPath(Path.Combine(packageDirectory, Path.Combine(parts)));
        return IsWithin(path, packageDirectory) && SafeRegularFile(path, long.MaxValue);
    }

    private static bool SafePackagePart(string value) => value.Length is > 0 and <= 256 && value is not ("." or "..") &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static bool SafeRegularFile(string path, long maximumBytes)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length <= maximumBytes && NoReparseComponents(file.FullName);
    }

    private static bool SafeDirectory(string path) => Directory.Exists(path) && NoReparseComponents(Path.GetFullPath(path));

    private static bool NoReparseComponents(string path)
    {
        FileSystemInfo? current = File.Exists(path)
            ? new FileInfo(Path.GetFullPath(path))
            : new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null
            };
        }
        return true;
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
            yield return directory.FullName;
    }

    private static string? FindFile(string directory, string name)
    {
        var matches = Directory.EnumerateFiles(directory).Where(path =>
            Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length switch { 0 => null, 1 => matches[0], _ => throw new InvalidDataException("Ambiguous build input.") };
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static string ImplicitUsings(ProjectModel project)
    {
        var usings = new List<string>
        {
            "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http",
            "System.Threading", "System.Threading.Tasks"
        };
        if (project.Classification == "web")
            usings.AddRange([
                "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Http",
                "Microsoft.AspNetCore.Routing", "Microsoft.Extensions.Configuration",
                "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging"
            ]);
        return string.Join(Environment.NewLine, usings.Select(item => $"global using {item};"));
    }

    private static bool DerivesFrom(INamedTypeSymbol type, string metadataName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == metadataName && current.ContainingAssembly.Name == EfCoreAssembly) return true;
        return false;
    }

    private static SourceRange Range(SyntaxTree tree, SyntaxNodeOrToken node)
    {
        var span = tree.GetLineSpan(node.Span);
        return new(span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1);
    }

    private static Diagnostic Warning(string code, SyntaxTree tree, SyntaxNode node, string message, string subject)
    {
        var range = Range(tree, node);
        return new($"diagnostic:archie.dotnet:{code.ToLowerInvariant()}:{Stable($"{tree.FilePath}:{range.StartLine}:{range.StartColumn}")}",
            code, "warning", message, subject);
    }

    private static Diagnostic ProjectWarning(string code, ProjectModel project, string message) =>
        new($"diagnostic:archie.dotnet:{code.ToLowerInvariant()}:{Stable($"{project.Path}:1:1")}",
            code, "warning", message, project.Key);

    private static string Stable(string value) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..12];

    private static StringComparer PathComparer() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
