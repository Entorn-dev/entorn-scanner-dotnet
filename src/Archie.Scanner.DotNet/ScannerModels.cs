using Archie.Contracts;

namespace Archie.Scanner.DotNet;

public sealed record DotNetScannerLimits(
    int MaxInputFiles = 20_000,
    long MaxInputBytes = 64 * 1024 * 1024);

public sealed record DotNetScanResult(
    IReadOnlyList<Observation> Observations,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Succeeded => Diagnostics.All(item => item.Severity != "error");
}

internal sealed record SourceFile(string Path, string FullPath, long Bytes);

internal sealed record LocatedValue(string Value, string Path, SourceRange? Range);

internal sealed record ProjectPackage(string Name, string? Version, string Path, SourceRange? Range);

internal sealed record ProjectReferenceModel(string Include, string? TargetPath, string Path, SourceRange? Range);

internal sealed record ProjectModel(
    string Key,
    string Name,
    string Path,
    SourceRange? Range,
    string? Classification,
    IReadOnlyList<string>? TargetFrameworks,
    IReadOnlyList<ProjectPackage> Packages,
    IReadOnlyList<ProjectReferenceModel> ProjectReferences,
    IReadOnlyList<SourceFile> Sources,
    bool ImplicitUsings,
    bool IsService);

internal sealed record SolutionProject(string ProjectPath, SourceRange? Range);

internal sealed record SolutionModel(
    string Key,
    string Name,
    string Path,
    SourceRange? Range,
    IReadOnlyList<SolutionProject> Projects);

internal sealed record StructureModel(
    IReadOnlyList<SolutionModel> Solutions,
    IReadOnlyList<ProjectModel> Projects,
    IReadOnlyList<Diagnostic> Diagnostics);
