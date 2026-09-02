using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Archie.Contracts;

namespace Archie.Scanner.DotNet;

public sealed class DotNetScanner(DotNetScannerLimits? limits = null)
{
    private const string ScannerId = "archie.dotnet";
    private const string ScannerVersion = "1.0.0";
    private readonly DotNetScannerLimits limits = limits ?? new();

    public async Task<DotNetScanResult> ScanAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(repositoryRoot))
            return new([], [Diagnostic("DOTNET_REPOSITORY_UNAVAILABLE", "error", "The requested repository directory does not exist.", null, repositoryRoot)]);

        var structure = await new SolutionModelBuilder(limits).BuildAsync(repositoryRoot, cancellationToken);
        if (structure.Diagnostics.Any(item => item.Severity == "error")) return new([], structure.Diagnostics);

        var observations = new List<Observation>();
        var diagnostics = new List<Diagnostic>(structure.Diagnostics);
        var projectCandidates = structure.Projects.ToDictionary(item => item.Path, ProjectCandidate, PathComparer());
        var solutionCandidates = structure.Solutions.ToDictionary(item => item.Path, SolutionCandidate, PathComparer());

        foreach (var solution in structure.Solutions)
        {
            observations.Add(Entity($"solution:{solution.Path}", solutionCandidates[solution.Path], solution.Path, solution.Range,
                "msbuild:solution-declaration", Confidence.Confirmed));
            foreach (var reference in solution.Projects)
            {
                var target = projectCandidates.TryGetValue(reference.ProjectPath, out var project)
                    ? project
                    : Candidate($"dotnet:project:{reference.ProjectPath}", NodeKind.Module, Path.GetFileNameWithoutExtension(reference.ProjectPath),
                        Resolution.Unresolved, Properties(("projectPath", reference.ProjectPath)));
                observations.Add(Relationship($"solution-project:{solution.Path}:{reference.ProjectPath}", EdgeKind.Contains,
                    solutionCandidates[solution.Path], target, solution.Path, reference.Range,
                    "msbuild:solution-project-entry", Confidence.Confirmed,
                    Properties(("ownership", "solution-project"), ("label", $"contains {target.Name}"))));
            }
        }

        foreach (var project in structure.Projects)
        {
            var projectCandidate = projectCandidates[project.Path];
            observations.Add(Entity($"project:{project.Path}", projectCandidate, project.Path, project.Range,
                "msbuild:project-properties", Confidence.Confirmed));
            EntityCandidate? serviceCandidate = null;
            if (project.IsService)
            {
                serviceCandidate = ServiceCandidate(project);
                observations.Add(Entity($"service:{project.Path}", serviceCandidate, project.Path, project.Range,
                    "msbuild:deployable-project-classification", Confidence.Confirmed));
                observations.Add(Relationship($"project-service:{project.Path}", EdgeKind.Contains,
                    projectCandidate, serviceCandidate, project.Path, project.Range,
                    "msbuild:deployable-project-ownership", Confidence.Confirmed,
                    Properties(("ownership", "project-service"), ("label", $"owns {serviceCandidate.Name}"))));
            }

            foreach (var reference in project.ProjectReferences)
            {
                EntityCandidate target;
                if (reference.TargetPath is not null && projectCandidates.TryGetValue(reference.TargetPath, out var resolved))
                {
                    target = resolved;
                }
                else
                {
                    var targetPath = reference.TargetPath ?? NormalizeReference(project.Path, reference.Include);
                    target = Candidate($"dotnet:project:{targetPath}", NodeKind.Module, Path.GetFileNameWithoutExtension(reference.Include),
                        Resolution.Unresolved, Properties(("projectPath", targetPath)));
                    diagnostics.Add(Diagnostic("DOTNET_PROJECT_REFERENCE_UNRESOLVED", "warning",
                        $"Project reference '{reference.Include}' from '{project.Path}' could not be resolved; an unresolved target remains visible.",
                        project.Key, $"{project.Path}:{reference.Include}"));
                }
                observations.Add(Relationship($"project-reference:{project.Path}:{reference.Include}", EdgeKind.References,
                    projectCandidate, target, reference.Path, reference.Range, "msbuild:project-reference", Confidence.Confirmed,
                    Properties(("include", reference.Include), ("label", $"references {target.Name}"))));
            }

            foreach (var package in project.Packages)
            {
                var packageCandidate = PackageCandidate(package.Name);
                observations.Add(Entity($"package:{package.Name}", packageCandidate, package.Path, package.Range,
                    "msbuild:package-reference", Confidence.Confirmed));
                observations.Add(Relationship($"package-dependency:{project.Path}:{package.Name}", EdgeKind.DependsOn,
                    projectCandidate, packageCandidate, package.Path, package.Range, "msbuild:package-reference", Confidence.Confirmed,
                    Properties(("requestedVersion", package.Version), ("package", package.Name), ("label", $"depends on {package.Name}"))));
            }

            var aspNet = await AspNetScanner.ScanAsync(project, cancellationToken);
            diagnostics.AddRange(aspNet.Diagnostics);
            if (aspNet.Diagnostics.Any(item => item.Severity == "error")) continue;
            foreach (var group in aspNet.Endpoints.GroupBy(item => $"{item.Method}\n{item.Template}", StringComparer.Ordinal))
            {
                var detections = group.OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Range.StartLine).ToArray();
                var ambiguous = detections.Length > 1;
                if (ambiguous)
                    diagnostics.Add(Diagnostic("DOTNET_ENDPOINT_AMBIGUOUS", "warning",
                        $"Project '{project.Path}' declares {detections.Length} endpoints for {detections[0].Method} {detections[0].Template}; they remain separate and ambiguous.",
                        project.Key, $"{project.Path}:{group.Key}"));
                foreach (var detection in detections)
                {
                    var endpoint = EndpointCandidate(project, detection, ambiguous);
                    var suffix = ambiguous ? $":{detection.Path}:{detection.Range.StartLine}:{detection.Range.StartColumn}" : string.Empty;
                    observations.Add(Entity($"endpoint:{project.Path}:{detection.Method}:{detection.Template}{suffix}", endpoint,
                        detection.Path, detection.Range, detection.Rule, Confidence.Confirmed,
                        Properties(("symbol", detection.Symbol))));
                    observations.Add(Relationship($"project-endpoint:{project.Path}:{detection.Method}:{detection.Template}{suffix}", EdgeKind.Contains,
                        projectCandidate, endpoint, detection.Path, detection.Range, detection.Rule, Confidence.Confirmed,
                        Properties(("ownership", "project-endpoint"), ("label", $"owns {detection.Method} {detection.Template}"))));
                    if (serviceCandidate is not null)
                        observations.Add(Relationship($"service-endpoint:{project.Path}:{detection.Method}:{detection.Template}{suffix}", EdgeKind.Exposes,
                            serviceCandidate, endpoint, detection.Path, detection.Range, detection.Rule, Confidence.Confirmed,
                            Properties(("httpMethod", detection.Method), ("routeTemplate", detection.Template),
                                ("ownership", "service-endpoint"), ("label", $"exposes {detection.Method} {detection.Template}"))));
                }
            }
        }

        if (diagnostics.Any(item => item.Severity == "error")) return new([], diagnostics.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray());
        return new(
            observations.DistinctBy(item => item.Id).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            diagnostics.DistinctBy(item => item.Id).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray());
    }

    private static EntityCandidate SolutionCandidate(SolutionModel solution) =>
        Candidate(solution.Key, NodeKind.System, solution.Name, Resolution.Resolved,
            Properties(("artifactKind", Path.GetExtension(solution.Path).TrimStart('.').ToLowerInvariant()), ("solutionPath", solution.Path)));

    private static EntityCandidate ProjectCandidate(ProjectModel project) =>
        Candidate(project.Key, NodeKind.Module, project.Name, Resolution.Resolved,
            Properties(("projectPath", project.Path), ("classification", project.Classification),
                ("targetFrameworks", project.TargetFrameworks)));

    private static EntityCandidate ServiceCandidate(ProjectModel project) =>
        Candidate($"dotnet:service:{project.Path}", NodeKind.Deployable, $"{project.Name} Service", Resolution.Resolved,
            Properties(("projectPath", project.Path), ("classification", project.Classification),
                ("targetFrameworks", project.TargetFrameworks)));

    private static EntityCandidate PackageCandidate(string package) =>
        Candidate($"dotnet:package:{package.ToLowerInvariant()}", NodeKind.Component, package.ToLowerInvariant(), Resolution.Resolved,
            Properties(("dependencyType", "nuget"), ("package", package.ToLowerInvariant())));

    private static EntityCandidate EndpointCandidate(ProjectModel project, EndpointDetection endpoint, bool ambiguous)
    {
        var baseKey = $"dotnet:endpoint:{project.Path}:{endpoint.Method}:{endpoint.Template}";
        var key = ambiguous ? $"{baseKey}:{endpoint.Path}:{endpoint.Range.StartLine}:{endpoint.Range.StartColumn}" : baseKey;
        return Candidate(key, NodeKind.HttpEndpoint, $"{endpoint.Method} {endpoint.Template}",
            ambiguous ? Resolution.Ambiguous : Resolution.Resolved,
            Properties(("httpMethod", endpoint.Method), ("routeTemplate", endpoint.Template),
                ("ownerProject", project.Path), ("detectionRule", endpoint.Rule)));
    }

    private static EntityCandidate Candidate(
        string key,
        NodeKind kind,
        string name,
        Resolution resolution,
        IReadOnlyDictionary<string, JsonElement> properties) =>
        new(key, kind, null, name, resolution, new Dictionary<string, string>(), properties);

    private static EntityObservation Entity(
        string stableKey,
        EntityCandidate candidate,
        string path,
        SourceRange? range,
        string rule,
        Confidence confidence,
        IReadOnlyDictionary<string, JsonElement>? extraEvidence = null)
    {
        var id = $"observation:archie.dotnet:{Stable(stableKey)}";
        return new(id, Evidence(id, path, range, rule, confidence, extraEvidence), candidate);
    }

    private static RelationshipObservation Relationship(
        string stableKey,
        EdgeKind kind,
        EntityCandidate from,
        EntityCandidate to,
        string path,
        SourceRange? range,
        string rule,
        Confidence confidence,
        IReadOnlyDictionary<string, JsonElement> properties)
    {
        var id = $"observation:archie.dotnet:{Stable(stableKey)}";
        return new(id, Evidence(id, path, range, rule, confidence), kind, from, to, properties);
    }

    private static Evidence Evidence(
        string observationId,
        string path,
        SourceRange? range,
        string rule,
        Confidence confidence,
        IReadOnlyDictionary<string, JsonElement>? extra = null)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["detectionRule"] = JsonSerializer.SerializeToElement(rule),
            ["observationSource"] = JsonSerializer.SerializeToElement("scanner")
        };
        if (extra is not null)
            foreach (var item in extra) properties[item.Key] = item.Value;
        return new($"evidence:{observationId}", observationId, EvidenceProvenance.Deterministic,
            ScannerId, ScannerVersion, rule, path.Replace('\\', '/'), range, confidence, properties);
    }

    private static IReadOnlyDictionary<string, JsonElement> Properties(params (string Name, object? Value)[] values) =>
        values.Where(item => item.Value is not null).ToDictionary(
            item => item.Name,
            item => JsonSerializer.SerializeToElement(item.Value, item.Value!.GetType()),
            StringComparer.Ordinal);

    private static Diagnostic Diagnostic(string code, string severity, string message, string? subject, string key) =>
        new($"diagnostic:archie.dotnet:{code.ToLowerInvariant()}:{Stable(key)}", code, severity, message, subject);

    private static string NormalizeReference(string projectPath, string include)
    {
        var segments = new List<string>();
        foreach (var segment in $"{Path.GetDirectoryName(projectPath)?.Replace('\\', '/')}/{include.Replace('\\', '/')}"
                     .Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == ".." && segments.Count > 0) segments.RemoveAt(segments.Count - 1);
            else if (segment != "..") segments.Add(segment);
        }
        return string.Join('/', segments);
    }

    private static string Stable(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private static StringComparer PathComparer() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
