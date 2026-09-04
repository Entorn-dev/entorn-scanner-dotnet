using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Diagnostic = Entorn.Scanner.Contracts.Diagnostic;

namespace Archie.Scanner.DotNet;

internal sealed record EndpointDetection(
    string Method,
    string Template,
    string Path,
    SourceRange Range,
    string Rule,
    string Symbol);

internal sealed record AspNetScanResult(
    IReadOnlyList<EndpointDetection> Endpoints,
    IReadOnlyList<Diagnostic> Diagnostics);

internal static class AspNetScanner
{
    private const string ControllerBaseType = "Microsoft.AspNetCore.Mvc.ControllerBase";
    private const string ControllerType = "Microsoft.AspNetCore.Mvc.Controller";
    private const string ControllerAttribute = "Microsoft.AspNetCore.Mvc.ControllerAttribute";
    private const string NonControllerAttribute = "Microsoft.AspNetCore.Mvc.NonControllerAttribute";
    private const string RouteAttribute = "Microsoft.AspNetCore.Mvc.RouteAttribute";
    private const string WebApplicationType = "Microsoft.AspNetCore.Builder.WebApplication";
    private const string WebApplicationBuilderType = "Microsoft.AspNetCore.Builder.WebApplicationBuilder";
    private const string RouteGroupBuilderType = "Microsoft.AspNetCore.Routing.RouteGroupBuilder";

    private static readonly IReadOnlyDictionary<string, string> MinimalMethods = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET",
        ["MapPost"] = "POST",
        ["MapPut"] = "PUT",
        ["MapDelete"] = "DELETE",
        ["MapPatch"] = "PATCH"
    };

    private static readonly IReadOnlyDictionary<string, string> ControllerAttributes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Microsoft.AspNetCore.Mvc.HttpGetAttribute"] = "GET",
        ["Microsoft.AspNetCore.Mvc.HttpPostAttribute"] = "POST",
        ["Microsoft.AspNetCore.Mvc.HttpPutAttribute"] = "PUT",
        ["Microsoft.AspNetCore.Mvc.HttpDeleteAttribute"] = "DELETE",
        ["Microsoft.AspNetCore.Mvc.HttpPatchAttribute"] = "PATCH",
        ["Microsoft.AspNetCore.Mvc.HttpHeadAttribute"] = "HEAD",
        ["Microsoft.AspNetCore.Mvc.HttpOptionsAttribute"] = "OPTIONS"
    };

    public static async Task<AspNetScanResult> ScanAsync(ProjectModel project, CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();
        var trees = new List<SyntaxTree>();
        foreach (var source in project.Sources)
        {
            var text = await File.ReadAllTextAsync(source.FullPath, cancellationToken);
            var tree = CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest), source.Path, cancellationToken: cancellationToken);
            if (tree.GetDiagnostics(cancellationToken).Any(item => item.Severity == DiagnosticSeverity.Error))
            {
                diagnostics.Add(new(
                    $"diagnostic:archie.dotnet:source-malformed:{Stable(source.Path)}", "DOTNET_SOURCE_MALFORMED", "error",
                    $"C# source '{source.Path}' contains syntax errors and was not partially scanned.", project.Key));
                continue;
            }
            trees.Add(tree);
        }
        if (diagnostics.Any(item => item.Severity == "error")) return new([], diagnostics);
        if (trees.Count == 0) return new([], diagnostics);

        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(PathComparer())
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var compilationTrees = project.ImplicitUsings
            ? trees.Append(CSharpSyntaxTree.ParseText("""
                global using System;
                global using System.Collections.Generic;
                global using System.Linq;
                global using System.Threading;
                global using System.Threading.Tasks;
                global using Microsoft.AspNetCore.Builder;
                global using Microsoft.AspNetCore.Http;
                global using Microsoft.AspNetCore.Routing;
                global using Microsoft.Extensions.DependencyInjection;
                global using Microsoft.Extensions.Hosting;
                """, path: "__archie_sdk_global_usings.g.cs"))
            : trees;
        var compilation = CSharpCompilation.Create(
            $"Archie.Scan.{Stable(project.Path)}", compilationTrees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        if (!KnownFrameworkSymbolsAvailable(compilation))
        {
            diagnostics.Add(new($"diagnostic:archie.dotnet:aspnet-semantics:{Stable(project.Path)}",
                "DOTNET_ASPNET_SEMANTICS_UNAVAILABLE", "warning",
                $"ASP.NET Core reference assemblies were unavailable while scanning '{project.Path}'; no HTTP facts were emitted.", project.Key));
            return new([], diagnostics);
        }

        var endpoints = new List<EndpointDetection>();
        foreach (var tree in trees.OrderBy(item => item.FilePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = await tree.GetRootAsync(cancellationToken);
            var semanticModel = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            ScanMinimalApis(project, tree, root, semanticModel, endpoints, diagnostics);
            ScanControllers(project, tree, root, semanticModel, endpoints, diagnostics);
        }
        return new(
            endpoints.OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Range.StartLine)
                .ThenBy(item => item.Method, StringComparer.Ordinal).ThenBy(item => item.Template, StringComparer.Ordinal).ToArray(),
            diagnostics.DistinctBy(item => item.Id).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray());
    }

    private static void ScanMinimalApis(
        ProjectModel project,
        SyntaxTree tree,
        SyntaxNode root,
        SemanticModel semanticModel,
        ICollection<EndpointDetection> endpoints,
        ICollection<Diagnostic> diagnostics)
    {
        var builders = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
        foreach (var variable in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (variable.Initializer?.Value is not InvocationExpressionSyntax invocation ||
                !IsFrameworkMethod(semanticModel, invocation, "CreateBuilder", WebApplicationType)) continue;
            if (semanticModel.GetDeclaredSymbol(variable) is ILocalSymbol local) builders.Add(local);
        }

        var hosts = new Dictionary<ILocalSymbol, string>(SymbolEqualityComparer.Default);
        foreach (var variable in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (variable.Initializer?.Value is not InvocationExpressionSyntax invocation ||
                !IsFrameworkMethod(semanticModel, invocation, "Build", WebApplicationBuilderType) ||
                invocation.Expression is not MemberAccessExpressionSyntax member ||
                semanticModel.GetSymbolInfo(member.Expression).Symbol is not ILocalSymbol receiver || !builders.Contains(receiver)) continue;
            if (semanticModel.GetDeclaredSymbol(variable) is ILocalSymbol host && IsType(host.Type, WebApplicationType)) hosts[host] = string.Empty;
        }

        var pending = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().ToList();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var variable in pending)
            {
                if (semanticModel.GetDeclaredSymbol(variable) is not ILocalSymbol local || hosts.ContainsKey(local) ||
                    variable.Initializer?.Value is not InvocationExpressionSyntax invocation ||
                    !IsFrameworkEndpointMethod(semanticModel, invocation, "MapGroup") ||
                    invocation.Expression is not MemberAccessExpressionSyntax member ||
                    semanticModel.GetSymbolInfo(member.Expression).Symbol is not ILocalSymbol receiver || !hosts.TryGetValue(receiver, out var prefix)) continue;
                var argument = PositionalArguments(invocation.ArgumentList.Arguments).FirstOrDefault();
                if (argument is null || !TryLiteral(argument.Expression, out var route))
                {
                    diagnostics.Add(Warning("DOTNET_ROUTE_UNRESOLVED", tree.FilePath, invocation,
                        "A semantically resolved ASP.NET route group has a non-literal route template; no endpoint fact was invented.", project.Key));
                    continue;
                }
                if (!IsType(local.Type, RouteGroupBuilderType)) continue;
                hosts[local] = Combine(prefix, route);
                changed = true;
            }
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member ||
                !MinimalMethods.TryGetValue(member.Name.Identifier.ValueText, out var method)) continue;
            var symbol = MethodSymbol(semanticModel, invocation);
            if (symbol is null)
            {
                diagnostics.Add(Warning("DOTNET_ASPNET_BINDING_UNAVAILABLE", tree.FilePath, invocation,
                    $"A '{member.Name.Identifier.ValueText}' call could not be semantically bound; no endpoint fact was emitted.", project.Key));
                continue;
            }
            if (!IsFrameworkEndpointMethod(symbol, member.Name.Identifier.ValueText) ||
                semanticModel.GetSymbolInfo(member.Expression).Symbol is not ILocalSymbol receiver ||
                !hosts.TryGetValue(receiver, out var prefix)) continue;
            var argument = PositionalArguments(invocation.ArgumentList.Arguments).FirstOrDefault();
            if (argument is null || !TryLiteral(argument.Expression, out var route))
            {
                diagnostics.Add(Warning("DOTNET_ROUTE_UNRESOLVED", tree.FilePath, invocation,
                    "A semantically resolved minimal API has a non-literal route template; no endpoint fact was invented.", project.Key));
                continue;
            }
            endpoints.Add(new(method, Combine(prefix, route), tree.FilePath, Range(tree, invocation),
                "roslyn:semantic-minimal-api-route-host-dataflow", symbol.ToDisplayString()));
        }
    }

    private static void ScanControllers(
        ProjectModel project,
        SyntaxTree tree,
        SyntaxNode root,
        SemanticModel semanticModel,
        ICollection<EndpointDetection> endpoints,
        ICollection<Diagnostic> diagnostics)
    {
        foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var classSymbol = semanticModel.GetDeclaredSymbol(declaration);
            var looksLikeController = declaration.Identifier.ValueText.EndsWith("Controller", StringComparison.Ordinal) ||
                declaration.BaseList?.Types.Any(type =>
                type.Type.ToString().Split('.').Last() is "Controller" or "ControllerBase") == true ||
                declaration.AttributeLists.SelectMany(item => item.Attributes).Any(item => SimpleAttributeName(item) == "Controller");
            if (classSymbol is null)
            {
                if (looksLikeController)
                    diagnostics.Add(Warning("DOTNET_ASPNET_BINDING_UNAVAILABLE", tree.FilePath, declaration,
                        "A controller-like declaration could not be semantically bound; no endpoint facts were emitted.", project.Key));
                continue;
            }
            var isController = classSymbol.DeclaredAccessibility == Accessibility.Public && !classSymbol.IsAbstract &&
                classSymbol.TypeParameters.Length == 0 &&
                !Attributes(declaration.AttributeLists).Any(item => IsFrameworkAttribute(semanticModel, item, NonControllerAttribute)) &&
                (classSymbol.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase) ||
                 Attributes(declaration.AttributeLists).Any(item => IsFrameworkAttribute(semanticModel, item, ControllerAttribute))) &&
                (Inherits(classSymbol, ControllerBaseType) || Inherits(classSymbol, ControllerType) ||
                 Attributes(declaration.AttributeLists).Any(item => IsFrameworkAttribute(semanticModel, item, ControllerAttribute)));
            if (!isController) continue;
            var controllerName = classSymbol.Name.EndsWith("Controller", StringComparison.Ordinal)
                ? classSymbol.Name[..^"Controller".Length] : classSymbol.Name;
            var classRouteAttributes = Attributes(declaration.AttributeLists)
                .Where(item => IsFrameworkAttribute(semanticModel, item, RouteAttribute)).ToArray();
            if (!TryRoutes(semanticModel, classRouteAttributes, out var classRoutes))
            {
                diagnostics.Add(Warning("DOTNET_ROUTE_UNRESOLVED", tree.FilePath, declaration,
                    "A semantically resolved controller has a non-literal route template; no endpoint facts were emitted for it.", project.Key));
                continue;
            }
            if (classRoutes.Count == 0) classRoutes = [string.Empty];

            foreach (var action in declaration.Members.OfType<MethodDeclarationSyntax>())
            {
                var methodSymbol = semanticModel.GetDeclaredSymbol(action);
                if (methodSymbol is null) continue;
                var attributes = Attributes(action.AttributeLists).ToArray();
                foreach (var httpAttribute in attributes)
                {
                    var attributeType = AttributeType(semanticModel, httpAttribute);
                    if (attributeType is null)
                    {
                        if (ControllerAttributes.Keys.Any(name => name.EndsWith($".{SimpleAttributeName(httpAttribute)}Attribute", StringComparison.Ordinal)))
                            diagnostics.Add(Warning("DOTNET_ASPNET_BINDING_UNAVAILABLE", tree.FilePath, httpAttribute,
                                "A controller HTTP attribute could not be semantically bound; no endpoint fact was emitted.", project.Key));
                        continue;
                    }
                    if (!KnownAspNetAssembly(attributeType.ContainingAssembly) ||
                        !ControllerAttributes.TryGetValue(attributeType.ToDisplayString(), out var httpMethod)) continue;
                    var inline = RouteArgument(semanticModel, httpAttribute);
                    if (inline.HasTemplate && inline.Constant is null)
                    {
                        diagnostics.Add(Warning("DOTNET_ROUTE_UNRESOLVED", tree.FilePath, httpAttribute,
                            "A semantically resolved controller HTTP attribute has a non-literal route template; no endpoint fact was emitted.", project.Key));
                        continue;
                    }
                    var routeAttributes = attributes.Where(item => IsFrameworkAttribute(semanticModel, item, RouteAttribute)).ToArray();
                    if (!TryRoutes(semanticModel, routeAttributes, out var declaredActionRoutes))
                    {
                        diagnostics.Add(Warning("DOTNET_ROUTE_UNRESOLVED", tree.FilePath, action,
                            "A semantically resolved controller action has a non-literal route template; no endpoint fact was emitted.", project.Key));
                        continue;
                    }
                    IReadOnlyList<string> actionRoutes = inline.HasTemplate ? [inline.Constant!] :
                        declaredActionRoutes.Count > 0 ? declaredActionRoutes : [string.Empty];
                    foreach (var classRoute in classRoutes)
                        foreach (var actionRoute in actionRoutes)
                        {
                            if (classRoute.Length == 0 && actionRoute.Length == 0)
                            {
                                diagnostics.Add(Warning("DOTNET_ROUTE_UNRESOLVED", tree.FilePath, httpAttribute,
                                    "A controller action relies on conventional routing; no route template was invented.", project.Key));
                                continue;
                            }
                            var template = CombineController(classRoute, actionRoute)
                                .Replace("[controller]", controllerName, StringComparison.OrdinalIgnoreCase)
                                .Replace("[action]", methodSymbol.Name, StringComparison.OrdinalIgnoreCase);
                            endpoints.Add(new(httpMethod, template, tree.FilePath, Range(tree, httpAttribute),
                                "roslyn:semantic-controller-route-attribute", $"{classSymbol.ToDisplayString()}.{methodSymbol.Name}"));
                        }
                }
            }
        }
    }

    private static bool KnownFrameworkSymbolsAvailable(Compilation compilation) =>
        new[] { ControllerBaseType, ControllerAttribute, NonControllerAttribute, RouteAttribute, WebApplicationType, WebApplicationBuilderType, RouteGroupBuilderType }
            .All(name => compilation.GetTypeByMetadataName(name) is { } symbol && KnownAspNetAssembly(symbol.ContainingAssembly));

    private static bool IsFrameworkMethod(SemanticModel model, InvocationExpressionSyntax invocation, string method, string containingType) =>
        MethodSymbol(model, invocation) is { } symbol && symbol.Name == method &&
        IsType(symbol.ContainingType, containingType) && KnownAspNetAssembly(symbol.ContainingAssembly);

    private static bool IsFrameworkEndpointMethod(SemanticModel model, InvocationExpressionSyntax invocation, string method) =>
        MethodSymbol(model, invocation) is { } symbol && IsFrameworkEndpointMethod(symbol, method);

    private static bool IsFrameworkEndpointMethod(IMethodSymbol symbol, string method)
    {
        var original = symbol.ReducedFrom ?? symbol;
        return original.Name == method && original.ContainingNamespace.ToDisplayString() == "Microsoft.AspNetCore.Builder" &&
               KnownAspNetAssembly(original.ContainingAssembly);
    }

    private static IMethodSymbol? MethodSymbol(SemanticModel model, InvocationExpressionSyntax invocation) =>
        model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;

    private static bool IsFrameworkAttribute(SemanticModel model, AttributeSyntax attribute, string metadataName) =>
        AttributeType(model, attribute) is { } type && IsType(type, metadataName) && KnownAspNetAssembly(type.ContainingAssembly);

    private static INamedTypeSymbol? AttributeType(SemanticModel model, AttributeSyntax attribute) =>
        (model.GetSymbolInfo(attribute).Symbol as IMethodSymbol)?.ContainingType;

    private static bool Inherits(INamedTypeSymbol type, string metadataName)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (IsType(current, metadataName) && KnownAspNetAssembly(current.ContainingAssembly)) return true;
        return false;
    }

    private static bool IsType(ITypeSymbol type, string metadataName) => type.ToDisplayString() == metadataName;

    private static bool KnownAspNetAssembly(IAssemblySymbol assembly) =>
        assembly.Name.Equals("Microsoft.AspNetCore", StringComparison.Ordinal) ||
        assembly.Name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal);

    private static IEnumerable<AttributeSyntax> Attributes(SyntaxList<AttributeListSyntax> lists) => lists.SelectMany(item => item.Attributes);

    private static string SimpleAttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name.ToString().Split('.').Last();
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    private static RouteValue RouteArgument(SemanticModel semanticModel, AttributeSyntax attribute)
    {
        if (semanticModel.GetSymbolInfo(attribute).Symbol is not IMethodSymbol constructor || attribute.ArgumentList is null)
            return new(false, null);
        var ordinal = 0;
        foreach (var argument in attribute.ArgumentList.Arguments)
        {
            if (argument.NameEquals is not null) continue;
            IParameterSymbol? parameter;
            if (argument.NameColon is not null)
            {
                var name = argument.NameColon.Name.Identifier.ValueText;
                parameter = constructor.Parameters.FirstOrDefault(item => item.Name.Equals(name, StringComparison.Ordinal));
            }
            else
            {
                parameter = ordinal < constructor.Parameters.Length ? constructor.Parameters[ordinal] : null;
                ordinal++;
            }
            if (parameter?.Name != "template") continue;
            var constant = semanticModel.GetConstantValue(argument.Expression);
            return new(true, constant.HasValue && constant.Value is string value ? value : null);
        }
        return new(false, null);
    }

    private static bool TryRoutes(SemanticModel semanticModel, IReadOnlyList<AttributeSyntax> attributes, out IReadOnlyList<string> routes)
    {
        var result = new List<string>();
        foreach (var attribute in attributes)
        {
            var route = RouteArgument(semanticModel, attribute);
            if (!route.HasTemplate || route.Constant is null)
            {
                routes = [];
                return false;
            }
            result.Add(route.Constant);
        }
        routes = result;
        return true;
    }

    private static IEnumerable<ArgumentSyntax> PositionalArguments(SeparatedSyntaxList<ArgumentSyntax> arguments) =>
        arguments.Where(item => item.NameColon is null);

    private static bool TryLiteral(ExpressionSyntax expression, out string value)
    {
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            value = literal.Token.ValueText;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static string CombineController(string controller, string action)
    {
        if (action.StartsWith("~/", StringComparison.Ordinal) || action.StartsWith("/", StringComparison.Ordinal))
            return NormalizeRoute(action);
        var normalizedController = controller.StartsWith("~/", StringComparison.Ordinal) || controller.StartsWith("/", StringComparison.Ordinal)
            ? NormalizeRoute(controller) : controller;
        return Combine(normalizedController, action);
    }

    private static string Combine(string left, string right)
    {
        var parts = new[] { left, right }.Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().Trim('/')).Where(item => item.Length > 0);
        return "/" + string.Join('/', parts);
    }

    private static string NormalizeRoute(string route) => "/" + route.Trim().TrimStart('~').Trim('/');

    private static SourceRange Range(SyntaxTree tree, SyntaxNode node)
    {
        var span = tree.GetLineSpan(node.Span);
        return new(span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1);
    }

    private static Diagnostic Warning(string code, string path, SyntaxNode node, string message, string subject)
    {
        var range = Range(node.SyntaxTree, node);
        return new($"diagnostic:archie.dotnet:{code.ToLowerInvariant()}:{Stable($"{path}:{range.StartLine}:{range.StartColumn}")}",
            code, "warning", message, subject);
    }

    private static string Stable(string value) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..12];

    private static StringComparer PathComparer() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record RouteValue(bool HasTemplate, string? Constant);
}
