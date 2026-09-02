using System.Text.Json;
using System.Diagnostics;
using Archie.Contracts;
using Archie.Core;
using Archie.Scanner.DotNet;
using Xunit;

namespace Archie.Scanner.DotNet.Tests;

public sealed class DotNetScannerTests
{
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

    private static void AssertEndpoint(IEnumerable<EntityObservation> entities, string method, string route) =>
        Assert.Contains(entities, item => item.Entity.Kind == NodeKind.HttpEndpoint &&
            item.Entity.Properties["httpMethod"].GetString() == method && item.Entity.Properties["routeTemplate"].GetString() == route);

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
        [new("archie.dotnet", "1.0.0")], result.Observations, result.Diagnostics, []);

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
