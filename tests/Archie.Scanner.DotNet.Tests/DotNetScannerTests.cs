using System.Text.Json;
using System.Diagnostics;
using Archie.Contracts;
using Archie.Core;
using Archie.Scanner.DotNet;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Archie.Scanner.DotNet.Tests;

public sealed class DotNetScannerTests
{
    [Fact]
    public void SenderWriteSyntaxAcceptsOnlyOrdinaryDirectLocalDeclarations()
    {
        var root = CSharpSyntaxTree.ParseText("""
            class Flow
            {
                async void Send()
                {
                    var ordinary = CreateSender();
                    using var usingDeclaration = CreateSender();
                    await using var awaitUsingDeclaration = CreateSender();
                }
            }
            """).GetRoot();
        var block = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single().Body!;
        var declarations = block.Statements.OfType<LocalDeclarationStatementSyntax>()
            .ToDictionary(item => item.Declaration.Variables.Single().Identifier.ValueText, StringComparer.Ordinal);

        Assert.True(IsDirect(declarations["ordinary"]));
        Assert.False(IsDirect(declarations["usingDeclaration"]));
        Assert.False(IsDirect(declarations["awaitUsingDeclaration"]));

        bool IsDirect(LocalDeclarationStatementSyntax statement)
        {
            var variable = statement.Declaration.Variables.Single();
            return MessagingScanner.IsDirectSenderWrite(variable, variable.Initializer!.Value, block);
        }
    }

    [Fact]
    public void WorkerBudgetsMatchTheTrustedSliceFiveSupervisor()
    {
        Assert.Equal(1024 * 1024, DotNetWorkerLimits.MaxRequestBytes);
        Assert.Equal(1024 * 1024, DotNetWorkerLimits.MaxProtocolMessageBytes);
        Assert.Equal(128L * 1024 * 1024, DotNetWorkerLimits.MaxSerializedOutputBytes);
        Assert.Equal(100_000, DotNetWorkerLimits.MaxObservations);
    }

    [Fact]
    public void WorkerPreflightsObservationMessageAndSerializedOutputLimitsBeforeEmission()
    {
        Assert.False(DotNetWorkerProtocol.FitsOutput("ready", ["small"], 2, out var observationCode,
            maxObservations: 1, maxMessageBytes: 100, maxOutputBytes: 100));
        Assert.Equal("DOTNET_WORKER_OBSERVATION_LIMIT_EXCEEDED", observationCode);
        Assert.False(DotNetWorkerProtocol.FitsOutput("ready", ["oversized"], 0, out var messageCode,
            maxObservations: 1, maxMessageBytes: 3, maxOutputBytes: 100));
        Assert.Equal("DOTNET_WORKER_OUTPUT_LIMIT_EXCEEDED", messageCode);
        Assert.False(DotNetWorkerProtocol.FitsOutput("ready", ["one", "two"], 0, out var outputCode,
            maxObservations: 1, maxMessageBytes: 100, maxOutputBytes: 10));
        Assert.Equal("DOTNET_WORKER_OUTPUT_LIMIT_EXCEEDED", outputCode);
    }

    [Fact]
    public async Task RealWorkerRejectsOversizedRequestWithStructuredDiagnosticAndZeroObservations()
    {
        var worker = typeof(DotNetScanner).Assembly.Location;
        var start = new ProcessStartInfo("dotnet", worker)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(start)!;
        var ready = await process.StandardOutput.ReadLineAsync();
        await process.StandardInput.WriteLineAsync(new string('x', DotNetWorkerLimits.MaxRequestBytes + 1));
        process.StandardInput.Close();
        var diagnostic = await process.StandardOutput.ReadLineAsync();
        var completed = await process.StandardOutput.ReadLineAsync();
        await process.WaitForExitAsync();

        Assert.Contains("\"type\":\"ready\"", ready, StringComparison.Ordinal);
        Assert.Contains("DOTNET_WORKER_REQUEST_LIMIT_EXCEEDED", diagnostic, StringComparison.Ordinal);
        Assert.Contains("\"observationCount\":0", completed, StringComparison.Ordinal);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task ReferenceFixtureProducesNormalizedStructureHttpAndOwnership()
    {
        var result = await new DotNetScanner().ScanAsync(Fixture(), CancellationToken.None);
        var entities = result.Observations.OfType<EntityObservation>().ToArray();
        var relationships = result.Observations.OfType<RelationshipObservation>().ToArray();

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.Message)));
        Assert.Contains(entities, item => item.Entity.Key == "dotnet:solution:Reference.slnx" && item.Entity.Kind == NodeKind.System);
        Assert.Contains(entities, item => item.Entity.Key == "dotnet:solution:Legacy.sln" && item.Entity.Properties["artifactKind"].GetString() == "sln");
        Assert.Contains(entities, item => item.Entity.Key == "dotnet:project:Dashboard/Dashboard.csproj" &&
            item.Entity.Properties["classification"].GetString() == "web" &&
            item.Entity.Properties["targetFrameworks"].EnumerateArray().Select(value => value.GetString()).SequenceEqual(["net10.0", "net9.0"]));
        Assert.Contains(entities, item => item.Entity.Key == "dotnet:service:Dashboard/Dashboard.csproj" && item.Entity.Kind == NodeKind.Deployable);
        Assert.Contains(entities, item => item.Entity.Key == "dotnet:project:Shared/Shared.csproj" &&
            item.Entity.Properties["classification"].GetString() == "library");
        Assert.Contains(entities, item => item.Entity.Key == "dotnet:package:swashbuckle.aspnetcore");
        Assert.Contains(relationships, item => item.Relationship == EdgeKind.References &&
            item.From.Key == "dotnet:project:Dashboard/Dashboard.csproj" && item.To.Key == "dotnet:project:Shared/Shared.csproj");
        Assert.Contains(relationships, item => item.Relationship == EdgeKind.DependsOn &&
            item.To.Key == "dotnet:package:swashbuckle.aspnetcore" && item.Properties["requestedVersion"].GetString() == "7.2.0");
        AssertEndpoint(entities, "GET", "/api/books");
        AssertEndpoint(entities, "POST", "/api/orders");
        AssertEndpoint(entities, "GET", "/api/Inventory/{id}");
        AssertEndpoint(entities, "POST", "/api/Inventory/reserve");
        Assert.DoesNotContain(entities, item => item.Entity.Name.Contains("must-not-appear", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, item => item.Code == "DOTNET_ROUTE_UNRESOLVED");
        Assert.All(result.Observations, observation =>
        {
            Assert.Equal(EvidenceProvenance.Deterministic, observation.Evidence.Provenance);
            Assert.Equal("archie.dotnet", observation.Evidence.ScannerId);
            Assert.NotNull(observation.Evidence.Range);
            Assert.True(observation.Evidence.Properties.ContainsKey("detectionRule"));
        });
        Assert.Contains(relationships, item => item.Relationship == EdgeKind.Exposes &&
            item.From.Key == "dotnet:service:Dashboard/Dashboard.csproj" && item.To.Kind == NodeKind.HttpEndpoint);
        Assert.Contains(relationships, item => item.Relationship == EdgeKind.Contains &&
            item.From.Key == "dotnet:project:Dashboard/Dashboard.csproj" && item.To.Kind == NodeKind.HttpEndpoint);
    }

    [Fact]
    public async Task ScannerOutputAndReconciledGraphAreByteDeterministic()
    {
        var scanner = new DotNetScanner();
        var first = await scanner.ScanAsync(Fixture(), CancellationToken.None);
        var second = await scanner.ScanAsync(Fixture(), CancellationToken.None);
        var firstBundle = Bundle(first);
        var secondBundle = Bundle(second);

        Assert.Equal(ContractJson.WriteObservationBundle(firstBundle), ContractJson.WriteObservationBundle(secondBundle));
        var firstGraph = new Reconciler().Reconcile(firstBundle, null, new DateOnly(2026, 9, 2)).Snapshot;
        var secondGraph = new Reconciler().Reconcile(secondBundle, null, new DateOnly(2026, 9, 2)).Snapshot;
        Assert.Equal(ContractJson.WriteGraphSnapshot(firstGraph), ContractJson.WriteGraphSnapshot(secondGraph));
        Assert.Contains(firstGraph.Nodes, item => item.Kind == NodeKind.HttpEndpoint && item.Name == "POST /api/orders");
        Assert.Contains(firstGraph.Edges, item => item.Kind == EdgeKind.Exposes);
    }

    [Theory]
    [InlineData("Broken.csproj", "<Project>", "DOTNET_PROJECT_MALFORMED")]
    [InlineData("Broken.slnx", "<Workspace />", "DOTNET_SOLUTION_UNSUPPORTED")]
    [InlineData("Broken.sln", "not a solution", "DOTNET_SOLUTION_UNSUPPORTED")]
    public async Task MalformedOrUnsupportedInputsFailWithoutPartialObservations(string file, string content, string code)
    {
        using var temporary = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, "Valid.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, file), content);

        var result = await new DotNetScanner().ScanAsync(temporary.Path, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Observations);
        Assert.Contains(result.Diagnostics, item => item.Code == code && item.Severity == "error");
    }

    [Fact]
    public async Task MalformedSourceFailsWithoutPartialObservations()
    {
        using var temporary = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, "Api.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, "Program.cs"), "var app = WebApplication.CreateBuilder(args).Build(; app.MapGet(\"/partial\", () => true);");

        var result = await new DotNetScanner().ScanAsync(temporary.Path, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Observations);
        Assert.Contains(result.Diagnostics, item => item.Code == "DOTNET_SOURCE_MALFORMED" && item.Severity == "error");
    }

    [Fact]
    public async Task MultiSolutionOwnershipRemainsVisibleAndDiagnosed()
    {
        using var temporary = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, "Shared.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, "One.slnx"), "<Solution><Project Path=\"Shared.csproj\" /></Solution>");
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, "Two.slnx"), "<Solution><Project Path=\"Shared.csproj\" /></Solution>");

        var result = await new DotNetScanner().ScanAsync(temporary.Path, CancellationToken.None);
        var ownership = result.Observations.OfType<RelationshipObservation>()
            .Where(item => item.Relationship == EdgeKind.Contains && item.To.Key == "dotnet:project:Shared.csproj")
            .ToArray();

        Assert.True(result.Succeeded);
        Assert.Equal(2, ownership.Length);
        Assert.Contains(result.Diagnostics, item => item.Code == "DOTNET_PROJECT_OWNERSHIP_AMBIGUOUS" && item.Severity == "warning");
    }

    [Fact]
    public async Task MissingReferenceAndDuplicateEndpointRemainVisibleAndDiagnosed()
    {
        using var temporary = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, "Api.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup><ProjectReference Include="Missing/Missing.csproj" /></ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, "Program.cs"), """
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            app.MapGet("/duplicate", () => "one");
            app.MapGet("/duplicate", () => "two");
            """);

        var result = await new DotNetScanner().ScanAsync(temporary.Path, CancellationToken.None);
        var relationships = result.Observations.OfType<RelationshipObservation>().ToArray();
        var endpoints = result.Observations.OfType<EntityObservation>().Where(item => item.Entity.Kind == NodeKind.HttpEndpoint).ToArray();

        Assert.True(result.Succeeded);
        Assert.Contains(result.Diagnostics, item => item.Code == "DOTNET_PROJECT_REFERENCE_UNRESOLVED");
        Assert.Contains(result.Diagnostics, item => item.Code == "DOTNET_ENDPOINT_AMBIGUOUS");
        Assert.Contains(relationships, item => item.Relationship == EdgeKind.References && item.To.Resolution == Resolution.Unresolved);
        Assert.Equal(2, endpoints.Length);
        Assert.All(endpoints, item => Assert.Equal(Resolution.Ambiguous, item.Entity.Resolution));
        Assert.Equal(2, endpoints.Select(item => item.Entity.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task InputLimitFailsClosedWithoutPartialObservations()
    {
        using var temporary = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, "One.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, "Two.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var result = await new DotNetScanner(new(MaxInputFiles: 1, MaxInputBytes: 1024 * 1024))
            .ScanAsync(temporary.Path, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Observations);
        Assert.Contains(result.Diagnostics, item => item.Code == "DOTNET_INPUT_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task HostileSameNameAspNetLookalikesNeverBecomeConfirmedEndpoints()
    {
        using var temporary = new TemporaryDirectory();
        await WriteProject(temporary.Path, "Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
        await WriteProject(temporary.Path, "Program.cs", """
            var fakeBuilder = WebApplication.CreateBuilder(args);
            var fakeApp = fakeBuilder.Build();
            fakeApp.MapGet("/fake-minimal", () => { });
            UnknownApi unresolved = default!;
            unresolved.MapGet("/unbound", () => { });

            static class WebApplication
            {
                public static FakeBuilder CreateBuilder(string[] args) => new();
            }
            sealed class FakeBuilder { public FakeApp Build() => new(); }
            sealed class FakeApp { public void MapGet(string route, Action action) { } }

            [ApiController]
            [Route("fake-controller")]
            sealed class FakeController : ControllerBase
            {
                [HttpGet("action")]
                public void Get() { }
            }
            class ControllerBase { }
            sealed class ApiControllerAttribute : Attribute { }
            sealed class RouteAttribute(string route) : Attribute { }
            sealed class HttpGetAttribute(string route) : Attribute { }
            """);

        var result = await new DotNetScanner().ScanAsync(temporary.Path, CancellationToken.None);
        var endpoints = result.Observations.OfType<EntityObservation>().Where(item => item.Entity.Kind == NodeKind.HttpEndpoint).ToArray();

        Assert.True(result.Succeeded);
        Assert.Empty(endpoints);
        Assert.Contains(result.Diagnostics, item => item.Code == "DOTNET_ASPNET_BINDING_UNAVAILABLE" && item.Severity == "warning");
    }

    [Fact]
    public async Task UnsupportedMsBuildOverridesTaintEarlierFrameworkClassificationPackageAndReferenceFacts()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temporary.Path, "Other"));
        await WriteProject(temporary.Path, "Other/Other.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await WriteProject(temporary.Path, "Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <TargetFramework Condition="'true' == 'true'">net8.0</TargetFramework>
                <ActualOutput>Exe</ActualOutput>
                <OutputType>Library</OutputType>
                <OutputType>$(ActualOutput)</OutputType>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Case.Package" Version="1.0.0" />
                <PackageReference Update="Case.Package" Version="2.0.0" />
                <ProjectReference Include="Other/Other.csproj" />
                <ProjectReference Remove="Other/Other.csproj" />
              </ItemGroup>
            </Project>
            """);

        using var evaluated = JsonDocument.Parse(await EvaluateMsBuild(temporary.Path, "Api.csproj",
            "-getProperty:TargetFramework", "-getProperty:OutputType", "-getItem:PackageReference", "-getItem:ProjectReference"));
        var result = await new DotNetScanner().ScanAsync(temporary.Path, CancellationToken.None);
        var json = JsonSerializer.Serialize(result.Observations, ContractJson.Options);
        var project = Assert.Single(result.Observations.OfType<EntityObservation>(), item => item.Entity.Key == "dotnet:project:Api.csproj");

        Assert.Equal("net8.0", evaluated.RootElement.GetProperty("Properties").GetProperty("TargetFramework").GetString());
        Assert.Equal("Exe", evaluated.RootElement.GetProperty("Properties").GetProperty("OutputType").GetString(), ignoreCase: true);
        Assert.Equal("2.0.0", evaluated.RootElement.GetProperty("Items").GetProperty("PackageReference")[0].GetProperty("Version").GetString());
        Assert.Empty(evaluated.RootElement.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray());
        Assert.False(project.Entity.Properties.ContainsKey("classification"));
        Assert.False(project.Entity.Properties.ContainsKey("targetFrameworks"));
        Assert.DoesNotContain("case.package", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Observations.OfType<RelationshipObservation>(), item =>
            item.Relationship == EdgeKind.References && item.From.Key == "dotnet:project:Api.csproj");
        Assert.Contains(result.Diagnostics, item => item.Code == "DOTNET_MSBUILD_VALUE_UNEVALUATED");
        Assert.Contains(result.Diagnostics, item => item.Code == "DOTNET_MSBUILD_ITEM_UNEVALUATED");

        {
            using var compileTemporary = new TemporaryDirectory();
            await WriteProject(compileTemporary.Path, "Conditioned/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><Compile Remove=\"Excluded.cs\" Condition=\"'true' == 'true'\" /></ItemGroup></Project>");
            await WriteProject(compileTemporary.Path, "Conditioned/Excluded.cs", MinimalApi("/conditioned-compile"));
            await WriteProject(compileTemporary.Path, "Expanded/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Excluded>Excluded.cs</Excluded></PropertyGroup><ItemGroup><Compile Remove=\"$(Excluded)\" /></ItemGroup></Project>");
            await WriteProject(compileTemporary.Path, "Expanded/Excluded.cs", MinimalApi("/expanded-compile"));
            await WriteProject(compileTemporary.Path, "Imported/Imported.props", "<Project><ItemGroup><Compile Remove=\"Excluded.cs\" /></ItemGroup></Project>");
            await WriteProject(compileTemporary.Path, "Imported/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><Import Project=\"Imported.props\" /><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
            await WriteProject(compileTemporary.Path, "Imported/Excluded.cs", MinimalApi("/imported-compile"));

            foreach (var directory in new[] { "Conditioned", "Expanded", "Imported" })
            {
                using var compileEvaluation = JsonDocument.Parse(await EvaluateMsBuild(compileTemporary.Path, $"{directory}/Api.csproj", "-getItem:Compile"));
                Assert.DoesNotContain(compileEvaluation.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray(), item =>
                    item.GetProperty("Identity").GetString() == "Excluded.cs");
            }

            var compileResult = await new DotNetScanner().ScanAsync(compileTemporary.Path, CancellationToken.None);
            var endpoints = compileResult.Observations.OfType<EntityObservation>().Where(item => item.Entity.Kind == NodeKind.HttpEndpoint).ToArray();

            Assert.DoesNotContain(endpoints, item => item.Entity.Name.Contains("-compile", StringComparison.Ordinal));
            Assert.Equal(3, compileResult.Diagnostics.Count(item => item.Code == "DOTNET_COMPILE_EVALUATION_UNSUPPORTED"));
            Assert.Contains(compileResult.Diagnostics, item => item.Code == "DOTNET_MSBUILD_IMPORT_UNEVALUATED");
            Assert.Contains(compileResult.Diagnostics, item => item.Code == "DOTNET_MSBUILD_CONDITION_UNEVALUATED");
            Assert.Contains(compileResult.Diagnostics, item => item.Code == "DOTNET_MSBUILD_ITEM_UNEVALUATED");
        }

        {
            using var importTemporary = new TemporaryDirectory();
            await WriteProject(importTemporary.Path, "Other.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            await WriteProject(importTemporary.Path, "Imported.props", """
                <Project>
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Update="Case.Package" Version="2.0.0" />
                    <ProjectReference Remove="Other.csproj" />
                    <Compile Remove="Endpoint.cs" />
                  </ItemGroup>
                </Project>
                """);
            await WriteProject(importTemporary.Path, "Endpoint.cs", MinimalApi("/nested-import"));
            await WriteProject(importTemporary.Path, "Api.csproj", """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Case.Package" Version="1.0.0" />
                    <ProjectReference Include="Other.csproj" />
                  </ItemGroup>
                  <ImportGroup Condition="'true' == 'true'">
                    <Import Project="Imported.props" Condition="'true' == 'true'" />
                  </ImportGroup>
                </Project>
                """);

            using var importEvaluation = JsonDocument.Parse(await EvaluateMsBuild(importTemporary.Path, "Api.csproj",
                "-getProperty:TargetFramework", "-getProperty:OutputType", "-getItem:PackageReference", "-getItem:ProjectReference", "-getItem:Compile"));
            var importResult = await new DotNetScanner().ScanAsync(importTemporary.Path, CancellationToken.None);
            var importProject = Assert.Single(importResult.Observations.OfType<EntityObservation>(), item => item.Entity.Key == "dotnet:project:Api.csproj");

            Assert.Equal("net9.0", importEvaluation.RootElement.GetProperty("Properties").GetProperty("TargetFramework").GetString());
            Assert.Equal("Exe", importEvaluation.RootElement.GetProperty("Properties").GetProperty("OutputType").GetString(), ignoreCase: true);
            Assert.Equal("2.0.0", importEvaluation.RootElement.GetProperty("Items").GetProperty("PackageReference")[0].GetProperty("Version").GetString());
            Assert.Empty(importEvaluation.RootElement.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray());
            Assert.DoesNotContain(importEvaluation.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray(), item =>
                item.GetProperty("Identity").GetString() == "Endpoint.cs");
            Assert.False(importProject.Entity.Properties.ContainsKey("classification"));
            Assert.False(importProject.Entity.Properties.ContainsKey("targetFrameworks"));
            Assert.DoesNotContain(importResult.Observations.OfType<EntityObservation>(), item => item.Entity.Key == "dotnet:package:case.package");
            Assert.DoesNotContain(importResult.Observations.OfType<EntityObservation>(), item =>
                item.Entity.Kind == NodeKind.HttpEndpoint && item.Entity.Name == "GET /nested-import");
            Assert.DoesNotContain(importResult.Observations.OfType<RelationshipObservation>(), item =>
                item.Relationship == EdgeKind.References && item.From.Key == "dotnet:project:Api.csproj");
            Assert.Contains(importResult.Diagnostics, item => item.Code == "DOTNET_MSBUILD_IMPORT_UNEVALUATED");
            Assert.Contains(importResult.Diagnostics, item => item.Code == "DOTNET_COMPILE_EVALUATION_UNSUPPORTED");
        }

        {
            using var chooseTemporary = new TemporaryDirectory();
            await WriteProject(chooseTemporary.Path, "Endpoint.cs", MinimalApi("/choose-removed"));
            await WriteProject(chooseTemporary.Path, "Api.csproj", """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
                  <ItemGroup><PackageReference Include="Case.Package" Version="1.0.0" /></ItemGroup>
                  <Choose>
                    <When Condition="'true' == 'true'">
                      <PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
                      <ItemGroup>
                        <PackageReference Update="Case.Package" Version="2.0.0" />
                        <Compile Remove="Endpoint.cs" />
                      </ItemGroup>
                    </When>
                    <Otherwise><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Otherwise>
                  </Choose>
                </Project>
                """);

            using var chooseEvaluation = JsonDocument.Parse(await EvaluateMsBuild(chooseTemporary.Path, "Api.csproj",
                "-getProperty:TargetFramework", "-getProperty:OutputType", "-getItem:PackageReference", "-getItem:Compile"));
            var chooseResult = await new DotNetScanner().ScanAsync(chooseTemporary.Path, CancellationToken.None);
            var chooseProject = Assert.Single(chooseResult.Observations.OfType<EntityObservation>(), item => item.Entity.Key == "dotnet:project:Api.csproj");

            Assert.Equal("net9.0", chooseEvaluation.RootElement.GetProperty("Properties").GetProperty("TargetFramework").GetString());
            Assert.Equal("Exe", chooseEvaluation.RootElement.GetProperty("Properties").GetProperty("OutputType").GetString(), ignoreCase: true);
            Assert.Equal("2.0.0", chooseEvaluation.RootElement.GetProperty("Items").GetProperty("PackageReference")[0].GetProperty("Version").GetString());
            Assert.DoesNotContain(chooseEvaluation.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray(), item =>
                item.GetProperty("Identity").GetString() == "Endpoint.cs");
            Assert.False(chooseProject.Entity.Properties.ContainsKey("classification"));
            Assert.False(chooseProject.Entity.Properties.ContainsKey("targetFrameworks"));
            Assert.DoesNotContain(chooseResult.Observations.OfType<EntityObservation>(), item => item.Entity.Key == "dotnet:package:case.package");
            Assert.DoesNotContain(chooseResult.Observations.OfType<EntityObservation>(), item =>
                item.Entity.Kind == NodeKind.HttpEndpoint && item.Entity.Name == "GET /choose-removed");
            Assert.Contains(chooseResult.Diagnostics, item => item.Code == "DOTNET_MSBUILD_CONTAINER_UNEVALUATED");
            Assert.Contains(chooseResult.Diagnostics, item => item.Code == "DOTNET_COMPILE_EVALUATION_UNSUPPORTED");
        }
    }

    [Fact]
    public async Task SafeCompileDefaultsRemovesLinksAndSharedDirectoryOwnershipAreHonored()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temporary.Path, "Same"));
        Directory.CreateDirectory(Path.Combine(temporary.Path, "Linked"));
        Directory.CreateDirectory(Path.Combine(temporary.Path, "Shared"));
        await WriteProject(temporary.Path, "Same/One.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><Compile Remove=\"Excluded.cs\" /></ItemGroup></Project>");
        await WriteProject(temporary.Path, "Same/Two.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><Compile Remove=\"Excluded.cs\" /></ItemGroup></Project>");
        await WriteProject(temporary.Path, "Same/Included.cs", MinimalApi("/shared-owner"));
        await WriteProject(temporary.Path, "Same/Excluded.cs", MinimalApi("/excluded"));
        await WriteProject(temporary.Path, "Linked/Linked.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"../Shared/Endpoint.cs\" Link=\"Endpoint.cs\" /></ItemGroup></Project>");
        await WriteProject(temporary.Path, "Shared/Endpoint.cs", MinimalApi("/linked"));

        var result = await new DotNetScanner().ScanAsync(temporary.Path, CancellationToken.None);
        var ownership = result.Observations.OfType<RelationshipObservation>()
            .Where(item => item.Relationship == EdgeKind.Contains && item.To.Kind == NodeKind.HttpEndpoint).ToArray();

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.Message)));
        Assert.Contains(ownership, item => item.From.Key == "dotnet:project:Same/One.csproj" && item.To.Name == "GET /shared-owner");
        Assert.Contains(ownership, item => item.From.Key == "dotnet:project:Same/Two.csproj" && item.To.Name == "GET /shared-owner");
        Assert.Contains(ownership, item => item.From.Key == "dotnet:project:Linked/Linked.csproj" && item.To.Name == "GET /linked");
        Assert.DoesNotContain(ownership, item => item.To.Name == "GET /excluded");
    }

    [Fact]
    public async Task ControllerNamedMetadataAndRootedRoutesFollowAspNetSemantics()
    {
        using var temporary = new TemporaryDirectory();
        await WriteProject(temporary.Path, "Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await WriteProject(temporary.Path, "OrdersController.cs", """
            using Microsoft.AspNetCore.Mvc;
            [ApiController]
            [Route("api/[controller]")]
            public sealed class OrdersController : ControllerBase
            {
                [HttpGet(Name = "list-orders")]
                public IActionResult List() => Ok();
                [HttpPost(template: "child", Name = "create-order")]
                public IActionResult Create() => Ok();
                [HttpPut(template: "/rooted")]
                public IActionResult Rooted() => Ok();
                [HttpPatch(template: "~/tilde-rooted")]
                public IActionResult TildeRooted() => Ok();
            }
            """);

        var result = await new DotNetScanner().ScanAsync(temporary.Path, CancellationToken.None);
        var endpoints = result.Observations.OfType<EntityObservation>().Where(item => item.Entity.Kind == NodeKind.HttpEndpoint).ToArray();

        AssertEndpoint(endpoints, "GET", "/api/Orders");
        AssertEndpoint(endpoints, "POST", "/api/Orders/child");
        AssertEndpoint(endpoints, "PUT", "/rooted");
        AssertEndpoint(endpoints, "PATCH", "/tilde-rooted");
        Assert.DoesNotContain(endpoints, item => item.Entity.Name.Contains("list-orders", StringComparison.Ordinal));
        Assert.DoesNotContain(endpoints, item => item.Entity.Name.Contains("create-order", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RootedControllerTemplateIsNormalizedBeforeActionComposition()
    {
        using var temporary = new TemporaryDirectory();
        await WriteProject(temporary.Path, "Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await WriteProject(temporary.Path, "RootController.cs", """
            using Microsoft.AspNetCore.Mvc;
            [Route("~/root")]
            public sealed class RootController : ControllerBase
            {
                [HttpGet("child")]
                public IActionResult Get() => Ok();
            }
            """);

        var result = await new DotNetScanner().ScanAsync(temporary.Path, CancellationToken.None);

        AssertEndpoint(result.Observations.OfType<EntityObservation>(), "GET", "/root/child");
    }

    [Fact]
    public async Task KafkaOperationsRequireResolvedConfluentSymbolsAndEmitTopicsContractsAndDiagnostics()
    {
        using var temporary = new TemporaryDirectory();
        await WriteProject(temporary.Path, "Kafka.csproj", ExecutableProject("Confluent.Kafka"));
        await WriteProject(temporary.Path, "Flow.cs", """
            using Confluent.Kafka;
            public sealed record OrderSubmitted(string Id);
            public sealed class Flow
            {
                public Task Publish(IProducer<string, OrderSubmitted> producer) =>
                    producer.ProduceAsync("order.submitted", new Message<string, OrderSubmitted>());
                public void Consume(IConsumer<string, OrderSubmitted> consumer) => consumer.Subscribe("order.submitted");
                public void Dynamic(IConsumer<string, object> consumer, string topic) => consumer.Subscribe(topic);
            }
            public sealed class Lookalike
            {
                public void Produce(string topic, object value) { }
                public void Subscribe(string topic) { }
                public void Ignore() { Produce("fake.kafka", new object()); Subscribe("fake.kafka"); }
            }
            """);

        var result = await new DotNetScanner().ScanAsync(temporary.Path, CancellationToken.None);
        var messaging = Messaging(result);

        Assert.Equal(2, messaging.Count(item => item.To.Name == "order.submitted"));
        Assert.Contains(messaging, item => item.Relationship == EdgeKind.Publishes && item.Properties["contract"].GetString() == "OrderSubmitted");
        Assert.Contains(messaging, item => item.Relationship == EdgeKind.Subscribes && item.Properties["provider"].GetString() == "kafka");
        Assert.DoesNotContain(messaging, item => item.To.Name == "fake.kafka");
        Assert.Contains(result.Diagnostics, item => item.Code == "DOTNET_MESSAGE_DESTINATION_UNRESOLVED");
        Assert.All(messaging, item => Assert.Equal(Confidence.Confirmed, item.Evidence.Confidence));
    }

    [Fact]
    public async Task ServiceBusOperationsRequireResolvedAzureSymbolsAndPreserveSubscriptionUncertainty()
    {
        using var temporary = new TemporaryDirectory();
        await WriteProject(temporary.Path, "Bus.csproj", ExecutableProject("Azure.Messaging.ServiceBus"));
        await WriteProject(temporary.Path, "Flow.cs", """
            using Azure.Messaging.ServiceBus;
            public sealed record FulfilmentRequested(string Id);
            public sealed class Flow
            {
                public async Task Configure(ServiceBusClient client, string dynamicName)
                {
                    var sender = client.CreateSender("fulfilment.requested");
                    await sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(new FulfilmentRequested("1"))));
                    var processor = client.CreateProcessor(subscriptionName: "notifications", topicName: "dispatch.events");
                    var namedFirst = client.CreateProcessor(topicName: "named-first.events", "named-first-subscription");
                    var positionalNamed = client.CreateProcessor("positional-named.events", subscriptionName: "positional-named-subscription");
                    _ = processor;
                    _ = namedFirst;
                    _ = positionalNamed;
                    var dynamicSender = client.CreateSender(dynamicName);
                    _ = dynamicSender;
                }
            }
            public sealed class ServiceBusClientLookalike
            {
                public Sender CreateSender(string name) => new();
                public Processor CreateProcessor(string topic, string subscription) => new();
            }
            public sealed class Sender { public Task SendMessageAsync(object message) => Task.CompletedTask; }
            public sealed class Processor { }
            """);

        var result = await new DotNetScanner().ScanAsync(temporary.Path, CancellationToken.None);
        var messaging = Messaging(result);

        Assert.Contains(messaging, item => item.Relationship == EdgeKind.Publishes && item.To.Name == "fulfilment.requested" &&
            item.Properties["contract"].GetString() == "FulfilmentRequested");
        Assert.Contains(messaging, item => item.Relationship == EdgeKind.Subscribes && item.To.Name == "dispatch.events/notifications" &&
            item.Properties["subscription"].GetString() == "notifications");
        Assert.Contains(messaging, item => item.Relationship == EdgeKind.Subscribes &&
            item.To.Name == "named-first.events/named-first-subscription");
        Assert.Contains(messaging, item => item.Relationship == EdgeKind.Subscribes &&
            item.To.Name == "positional-named.events/positional-named-subscription");
        Assert.Contains(result.Observations.OfType<RelationshipObservation>(), item => item.Relationship == EdgeKind.Contains &&
            item.From.Name == "dispatch.events" && item.To.Name == "dispatch.events/notifications");
        Assert.Contains(result.Diagnostics, item => item.Code == "DOTNET_MESSAGE_DESTINATION_UNRESOLVED");
        Assert.Contains(result.Diagnostics, item => item.Code == "DOTNET_MESSAGE_CONTRACT_UNRESOLVED");
    }

    [Fact]
    public async Task AzureFunctionsBindingsRequireKnownAttributesAndResolveTriggerAndOutputContracts()
    {
        using var temporary = new TemporaryDirectory();
        await WriteProject(temporary.Path, "Functions.csproj", ExecutableProject(
            "Microsoft.Azure.Functions.Worker", "Microsoft.Azure.Functions.Worker.Extensions.ServiceBus"));
        await WriteProject(temporary.Path, "Functions.cs", """
            using Microsoft.Azure.Functions.Worker;
            public sealed record FulfilmentRequested(string Id);
            public sealed record DispatchRequested(string Id);
            public sealed class Functions
            {
                [Function("Fulfil")]
                [ServiceBusOutput("dispatch.requested")]
                public DispatchRequested Run([ServiceBusTrigger("fulfilment.requested")] FulfilmentRequested request) => new(request.Id);
                [Function("TopicInput")]
                [ServiceBusOutput(entityType: ServiceBusEntityType.Topic, queueOrTopicName: "events.out")]
                public DispatchRequested Topic(
                    [ServiceBusTrigger(subscriptionName: "workers", topicName: "events.in")] FulfilmentRequested request) => new(request.Id);
                [Function("NamedFirst")]
                [ServiceBusOutput(queueOrTopicName: "named-first.out", ServiceBusEntityType.Topic)]
                public DispatchRequested NamedFirst(
                    [ServiceBusTrigger(topicName: "named-first.in", "named-first-subscription")] FulfilmentRequested request) => new(request.Id);
                [Function("PositionalNamed")]
                [ServiceBusOutput("positional-named.out", entityType: ServiceBusEntityType.Topic)]
                public DispatchRequested PositionalNamed(
                    [ServiceBusTrigger("positional-named.in", subscriptionName: "positional-named-subscription")] FulfilmentRequested request) => new(request.Id);
            }
            namespace Hostile
            {
                public sealed class FunctionAttribute(string name) : Attribute { }
                public sealed class ServiceBusTriggerAttribute(string name) : Attribute { }
                public sealed class ServiceBusOutputAttribute(string name) : Attribute { }
                public sealed class Fake
                {
                    [Function("Fake")]
                    [ServiceBusOutput("fake.output")]
                    public object Run([ServiceBusTrigger("fake.input")] object value) => value;
                }
            }
            """);

        var result = await new DotNetScanner().ScanAsync(temporary.Path, CancellationToken.None);
        var messaging = Messaging(result);

        Assert.Contains(messaging, item => item.Relationship == EdgeKind.Subscribes && item.To.Name == "fulfilment.requested" &&
            item.Properties["contract"].GetString() == "FulfilmentRequested");
        Assert.Contains(messaging, item => item.Relationship == EdgeKind.Publishes && item.To.Name == "dispatch.requested" &&
            item.Properties["contract"].GetString() == "DispatchRequested" && item.Properties["channelKind"].GetString() == "queue" &&
            !item.Properties.ContainsKey("topic"));
        Assert.Contains(messaging, item => item.Relationship == EdgeKind.Subscribes && item.To.Name == "events.in/workers" &&
            item.Properties["topic"].GetString() == "events.in" && item.Properties["subscription"].GetString() == "workers");
        Assert.Contains(messaging, item => item.Relationship == EdgeKind.Publishes && item.To.Name == "events.out" &&
            item.Properties["channelKind"].GetString() == "topic" && item.Properties["topic"].GetString() == "events.out");
        Assert.Contains(messaging, item => item.Relationship == EdgeKind.Subscribes &&
            item.To.Name == "named-first.in/named-first-subscription");
        Assert.Contains(messaging, item => item.Relationship == EdgeKind.Publishes && item.To.Name == "named-first.out" &&
            item.Properties["channelKind"].GetString() == "topic" && item.Properties["topic"].GetString() == "named-first.out");
        Assert.Contains(messaging, item => item.Relationship == EdgeKind.Subscribes &&
            item.To.Name == "positional-named.in/positional-named-subscription");
        Assert.Contains(messaging, item => item.Relationship == EdgeKind.Publishes && item.To.Name == "positional-named.out" &&
            item.Properties["channelKind"].GetString() == "topic" && item.Properties["topic"].GetString() == "positional-named.out");
        Assert.DoesNotContain(messaging, item => item.To.Name.StartsWith("fake.", StringComparison.Ordinal));
        Assert.Contains(result.Observations, item => item.Evidence.ExtractionMethod == "roslyn:semantic-azure-functions-service-bus-trigger");
        Assert.Contains(result.Observations, item => item.Evidence.ExtractionMethod == "roslyn:semantic-azure-functions-service-bus-output");
    }

    [Fact]
    public async Task MessagingCompilationAndPackageAvailabilityFailClosed()
    {
        using var temporary = new TemporaryDirectory();
        await WriteProject(temporary.Path, "Missing.csproj", ExecutableProject());
        await WriteProject(temporary.Path, "Flow.cs", """
            using Azure.Messaging.ServiceBus;
            using Confluent.Kafka;
            using Microsoft.Azure.Functions.Worker;
            public sealed class Flow
            {
                public void Kafka(IConsumer<string, Event> consumer) => consumer.Subscribe("must.not.exist");
                public ServiceBusProcessor Bus(ServiceBusClient client) => client.CreateProcessor("must.not.exist");
                [Function("Missing")]
                public void Run([ServiceBusTrigger("must.not.exist")] Event value) { }
            }
            public sealed record Event(string Id);
            """);

        var result = await new DotNetScanner().ScanAsync(temporary.Path, CancellationToken.None);

        Assert.Empty(Messaging(result));
        Assert.Contains(result.Diagnostics, item => item.Code == "DOTNET_MESSAGING_COMPILATION_UNAVAILABLE");
        Assert.DoesNotContain("must.not.exist", JsonSerializer.Serialize(result.Observations, ContractJson.Options), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<PackageReference Include=\"Confluent.Kafka\" Version=\"2.12.0\" ExcludeAssets=\"compile\" />")]
    [InlineData("<PackageReference Include=\"Confluent.Kafka\" Version=\"2.12.0\" IncludeAssets=\"runtime\" />")]
    [InlineData("<PackageReference Include=\"Confluent.Kafka\" Version=\"2.12.0\" Aliases=\"KafkaAlias\" />")]
    [InlineData("<PackageReference Include=\"Confluent.Kafka\" Version=\"2.12.0\" UnknownMetadata=\"value\" />")]
    [InlineData("<PackageReference Include=\"Confluent.Kafka\" Version=\"2.12.0\"><Aliases>KafkaAlias</Aliases></PackageReference>")]
    [InlineData("<PackageReference Include=\"Confluent.Kafka\" Version=\"2.12.0\"><UnknownMetadata>value</UnknownMetadata></PackageReference>")]
    public async Task MessagingPackageCompileAssetMetadataTaintsAvailability(string packageReference)
    {
        using var temporary = new TemporaryDirectory();
        await WriteProject(temporary.Path, "Tainted.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Library</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>{{packageReference}}</ItemGroup>
            </Project>
            """);
        await WriteProject(temporary.Path, "Flow.cs", """
            using Confluent.Kafka;
            public sealed record Event(string Id);
            public sealed class Flow
            {
                public void Subscribe(IConsumer<string, Event> consumer) => consumer.Subscribe("tainted.kafka");
            }
            """);

        var result = await new DotNetScanner().ScanAsync(temporary.Path, CancellationToken.None);

        Assert.Empty(Messaging(result));
        Assert.Contains(result.Diagnostics, item => item.Code == "DOTNET_NUGET_COMPILE_ASSETS_UNEVALUATED");
        Assert.Contains(result.Diagnostics, item => item.Code == "DOTNET_MESSAGING_COMPILATION_UNAVAILABLE");
        Assert.DoesNotContain(result.Observations.OfType<EntityObservation>(), item => item.Entity.Key == "dotnet:package:confluent.kafka");
    }

    [Fact]
    public async Task ServiceBusSenderFlowRequiresOneDirectReachingLiteralAssignment()
    {
        using var temporary = new TemporaryDirectory();
        await WriteProject(temporary.Path, "Bus.csproj", ExecutableProject("Azure.Messaging.ServiceBus"));
        await WriteProject(temporary.Path, "Flow.cs", """
            using Azure.Messaging.ServiceBus;
            public sealed record Event(string Id);
            public sealed record BatchEvent(string Id);
            public sealed class Flow
            {
                private static ServiceBusSender Keep(ServiceBusSender sender) => sender;

                public async Task Send(ServiceBusClient client, string dynamicName, bool condition)
                {
                    var valid = client.CreateSender("valid.direct");
                    await valid.SendMessageAsync(cancellationToken: default,
                        message: new ServiceBusMessage(BinaryData.FromObjectAsJson(new Event("0"))));
                    ServiceBusSender validAssignment;
                    validAssignment = client.CreateSender("valid.assignment");
                    await validAssignment.SendMessagesAsync(cancellationToken: default,
                        messages: new[] { new ServiceBusMessage(BinaryData.FromObjectAsJson(new BatchEvent("0"))) });
                    var constant = client.CreateSender("stale.constant");
                    constant = client.CreateSender("reassigned.constant");
                    await constant.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(new Event("1"))));
                    var dynamic = client.CreateSender("stale.dynamic");
                    dynamic = client.CreateSender(dynamicName);
                    await dynamic.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(new Event("2"))));
                    var original = client.CreateSender("aliased");
                    var alias = original;
                    await alias.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(new Event("3"))));
                    ServiceBusSender unbraced;
                    if (true) unbraced = client.CreateSender("nested.unbraced");
                    await unbraced.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(new Event("4"))));
                    ServiceBusSender braced;
                    if (true) { braced = client.CreateSender("nested.braced"); }
                    await braced.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(new Event("5"))));
                    ServiceBusSender loop;
                    do loop = client.CreateSender("nested.loop"); while (false);
                    await loop.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(new Event("6"))));
                    var conditional = condition
                        ? client.CreateSender("conditional.first")
                        : client.CreateSender("conditional.second");
                    await conditional.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(new Event("7"))));
                    ServiceBusSender crossBlock;
                    { crossBlock = client.CreateSender("nested.cross-block"); }
                    await crossBlock.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(new Event("8"))));
                    ServiceBusSender argument;
                    Keep(argument = client.CreateSender("nested.argument"));
                    await argument.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(new Event("9"))));
                    ServiceBusSender discard;
                    _ = (discard = client.CreateSender("nested.discard"));
                    await discard.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(new Event("10"))));
                    ServiceBusSender forClause;
                    for (forClause = client.CreateSender("nested.for"); false;) { }
                    await forClause.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(new Event("11"))));
                    ServiceBusSender usingClause;
                    await using (usingClause = client.CreateSender("nested.using")) { }
                    await usingClause.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(new Event("12"))));
                    await using ServiceBusSender usingDeclaration = client.CreateSender("nested.using-declaration");
                    await usingDeclaration.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(new Event("13"))));
                }
            }
            """);

        var result = await new DotNetScanner().ScanAsync(temporary.Path, CancellationToken.None);

        var publishes = Messaging(result).Where(item => item.Relationship == EdgeKind.Publishes).ToArray();
        Assert.Equal(2, publishes.Length);
        Assert.Contains(publishes, item => item.To.Name == "valid.direct" && item.Properties["contract"].GetString() == "Event");
        Assert.Contains(publishes, item => item.To.Name == "valid.assignment" && item.Properties["contract"].GetString() == "BatchEvent");
        Assert.Equal(13, result.Diagnostics.Count(item => item.Code == "DOTNET_MESSAGE_SENDER_FLOW_UNRESOLVED"));
        Assert.Contains(result.Diagnostics, item => item.Code == "DOTNET_MESSAGE_DESTINATION_UNRESOLVED");
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "DOTNET_MESSAGE_CONTRACT_UNRESOLVED" &&
            item.Message.Contains("valid.", StringComparison.Ordinal));
        Assert.Contains(result.Observations.OfType<RelationshipObservation>(), item =>
            item.Relationship == EdgeKind.UsesContract && item.From.Name == "valid.direct" && item.To.Name == "Event");
        Assert.Contains(result.Observations.OfType<RelationshipObservation>(), item =>
            item.Relationship == EdgeKind.UsesContract && item.From.Name == "valid.assignment" && item.To.Name == "BatchEvent");
    }

    [Fact]
    public async Task GenericKafkaAndServiceBusContractsRemainUnresolved()
    {
        using var temporary = new TemporaryDirectory();
        await WriteProject(temporary.Path, "Generic.csproj", ExecutableProject("Confluent.Kafka", "Azure.Messaging.ServiceBus"));
        await WriteProject(temporary.Path, "Flow.cs", """
            using Azure.Messaging.ServiceBus;
            using Confluent.Kafka;
            public sealed class Flow<T>
            {
                public void Kafka(IConsumer<string, T> consumer) => consumer.Subscribe("generic.kafka");
                public async Task Bus(ServiceBusClient client, T value)
                {
                    var sender = client.CreateSender("generic.bus");
                    await sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(value)));
                }
            }
            """);

        var result = await new DotNetScanner().ScanAsync(temporary.Path, CancellationToken.None);
        var messaging = Messaging(result);

        Assert.Contains(messaging, item => item.To.Name == "generic.kafka" && !item.Properties.ContainsKey("contract"));
        Assert.Contains(messaging, item => item.To.Name == "generic.bus" && !item.Properties.ContainsKey("contract"));
        Assert.Equal(2, result.Diagnostics.Count(item => item.Code == "DOTNET_MESSAGE_CONTRACT_UNRESOLVED"));
        Assert.DoesNotContain(result.Observations.OfType<RelationshipObservation>(), item =>
            item.Relationship == EdgeKind.UsesContract && item.From.Name.StartsWith("generic.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FunctionsDynamicAttributeDestinationFailsClosedWithoutPartialMessagingFacts()
    {
        using var temporary = new TemporaryDirectory();
        await WriteProject(temporary.Path, "Functions.csproj", ExecutableProject(
            "Microsoft.Azure.Functions.Worker", "Microsoft.Azure.Functions.Worker.Extensions.ServiceBus"));
        await WriteProject(temporary.Path, "Functions.cs", """
            using Microsoft.Azure.Functions.Worker;
            public sealed record Event(string Id);
            public sealed class Functions
            {
                private static readonly string DynamicQueue = "dynamic.queue";
                [Function("Dynamic")]
                public void Dynamic([ServiceBusTrigger(DynamicQueue)] Event value) { }
            }
            """);

        var result = await new DotNetScanner().ScanAsync(temporary.Path, CancellationToken.None);

        Assert.Empty(Messaging(result));
        Assert.Contains(result.Diagnostics, item => item.Code == "DOTNET_MESSAGING_COMPILATION_UNAVAILABLE");
    }

    [Fact]
    public async Task AmbiguousContractsRemainSeparateAndAreDiagnosedDeterministically()
    {
        using var temporary = new TemporaryDirectory();
        await WriteProject(temporary.Path, "Kafka.csproj", ExecutableProject("Confluent.Kafka"));
        await WriteProject(temporary.Path, "Flow.cs", """
            using Confluent.Kafka;
            public sealed record First(string Id);
            public sealed record Second(string Id);
            public sealed class Flow
            {
                public void Configure(IConsumer<string, First> first, IConsumer<string, Second> second)
                { first.Subscribe("ambiguous"); second.Subscribe("ambiguous"); }
            }
            """);

        var scanner = new DotNetScanner();
        var first = await scanner.ScanAsync(temporary.Path, CancellationToken.None);
        var second = await scanner.ScanAsync(temporary.Path, CancellationToken.None);

        Assert.Contains(first.Diagnostics, item => item.Code == "DOTNET_MESSAGE_CONTRACT_AMBIGUOUS");
        Assert.Equal(2, first.Observations.OfType<RelationshipObservation>().Count(item =>
            item.Relationship == EdgeKind.UsesContract && item.From.Name == "ambiguous"));
        Assert.Equal(ContractJson.WriteObservationBundle(Bundle(first)), ContractJson.WriteObservationBundle(Bundle(second)));
    }

    private static void AssertEndpoint(IEnumerable<EntityObservation> entities, string method, string route) =>
        Assert.Contains(entities, item => item.Entity.Kind == NodeKind.HttpEndpoint &&
            item.Entity.Properties["httpMethod"].GetString() == method && item.Entity.Properties["routeTemplate"].GetString() == route);

    private static RelationshipObservation[] Messaging(DotNetScanResult result) => result.Observations
        .OfType<RelationshipObservation>().Where(item => item.Relationship is EdgeKind.Publishes or EdgeKind.Subscribes).ToArray();

    private static string ExecutableProject(params string[] packages) => $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
          <ItemGroup>{{string.Join("", packages.Select(package => $"<PackageReference Include=\"{package}\" Version=\"1.0.0\" />"))}}</ItemGroup>
        </Project>
        """;

    private static Task WriteProject(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.WriteAllTextAsync(path, content);
    }

    private static async Task<string> EvaluateMsBuild(string root, string project, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(project);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet msbuild.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }

    private static string MinimalApi(string route) => $$"""
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();
        app.MapGet("{{route}}", () => Results.Ok());
        """;

    private static ObservationBundle Bundle(DotNetScanResult result) => new(
        "observations/v1", ObservationSource.Scanner, new string('a', 64),
        new("dotnet-reference", null, "reference", false, new string('b', 64)),
        [new("archie.dotnet", "1.1.0")], result.Observations, result.Diagnostics, []);

    private static string Fixture() => Path.Combine(AppContext.BaseDirectory, "fixtures", "dotnet-reference");

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"archie-dotnet-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
