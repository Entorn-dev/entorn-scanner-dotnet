using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using System.Text;

namespace Archie.Scanner.DotNet;

internal sealed record ConfigurationValue(
    string? Key,
    string? Scheme,
    string? Host,
    int? Port,
    string? DiagnosticCode);

internal static class ConfigurationValueResolver
{
    public static ConfigurationValue ResolveConnectionString(IArgumentOperation? argument)
    {
        if (argument?.Syntax is not ArgumentSyntax { Expression: InvocationExpressionSyntax syntax } ||
            argument.Value is not IInvocationOperation invocation || invocation.Syntax != syntax ||
            invocation.TargetMethod.Name != "GetConnectionString" ||
            invocation.TargetMethod.ContainingType.ToDisplayString() != "Microsoft.Extensions.Configuration.ConfigurationExtensions" ||
            invocation.TargetMethod.ContainingAssembly.Name != "Microsoft.Extensions.Configuration.Abstractions")
            return new(null, null, null, null, "DOTNET_DATA_CONFIGURATION_UNRESOLVED");

        var name = invocation.Arguments.FirstOrDefault(item => item.Parameter?.Name == "name")?.Value.ConstantValue;
        if (!name.HasValue || name.Value.Value is not string text || !SafeKey(text))
            return new(null, null, null, null, "DOTNET_DATA_CONFIGURATION_UNRESOLVED");
        return new($"ConnectionStrings:{text}", null, null, null, null);
    }

    public static ConfigurationValue ResolveHttpUri(
        IOperation? operation,
        ExpressionSyntax expression,
        SemanticModel semanticModel)
    {
        operation = Unwrap(operation);
        if (operation is not IObjectCreationOperation creation ||
            creation.Type?.ToDisplayString() != "System.Uri" ||
            creation.Type.ContainingAssembly.Name != "System.Runtime")
            return new(null, null, null, null, "DOTNET_EXTERNAL_TARGET_UNRESOLVED");

        var uriArgument = creation.Arguments.FirstOrDefault(item => item.Parameter?.Name == "uriString")?.Value;
        uriArgument = Unwrap(uriArgument);
        string? key = null;
        string? literal = null;
        var hasConfigurationFallback = uriArgument is ICoalesceOperation;
        if (uriArgument is ICoalesceOperation coalesce)
        {
            literal = ConstantString(Unwrap(coalesce.WhenNull));
        }
        else
        {
            literal = ConstantString(uriArgument);
        }
        key ??= ConfigurationKey(expression, semanticModel);

        if ((key is not null && !SafeKey(key)) || literal is null || hasConfigurationFallback && key is null)
            return new(key, null, null, null, "DOTNET_EXTERNAL_TARGET_UNRESOLVED");
        if (!Uri.TryCreate(literal, UriKind.Absolute, out var uri))
            return new(key, null, null, null, "DOTNET_EXTERNAL_TARGET_MALFORMED");
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return new(key, null, null, null, "DOTNET_EXTERNAL_TARGET_SENSITIVE_OR_UNSUPPORTED");
        return new(key, uri.Scheme, uri.IdnHost.ToLowerInvariant(), uri.IsDefaultPort ? null : uri.Port, null);
    }

    private static string? ConfigurationKey(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        if (expression is not ObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: > 0 } creation ||
            creation.ArgumentList.Arguments[0].Expression is not BinaryExpressionSyntax coalesce ||
            !coalesce.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.CoalesceExpression) ||
            coalesce.Left is not ElementAccessExpressionSyntax element || element.ArgumentList.Arguments.Count != 1 ||
            semanticModel.GetSymbolInfo(element.Expression).Symbol is not IPropertySymbol configurationProperty ||
            configurationProperty.Type is not INamedTypeSymbol configurationType || !IsConfiguration(configurationType)) return null;
        var receiverType = semanticModel.GetTypeInfo(element.Expression).Type as INamedTypeSymbol;
        if (receiverType is not null && !IsConfiguration(receiverType)) return null;
        var key = semanticModel.GetConstantValue(element.ArgumentList.Arguments[0].Expression);
        return key.HasValue && key.Value is string text && SafeKey(text) ? text : null;
    }

    private static string? ConstantString(IOperation? operation) =>
        operation?.ConstantValue is { HasValue: true, Value: string text } && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion) operation = conversion.Operand;
        return operation;
    }

    private static bool IsConfiguration(INamedTypeSymbol type)
    {
        var typeName = type.ToDisplayString();
        var assemblyName = type.ContainingAssembly.Name;
        var knownType =
            typeName == "Microsoft.Extensions.Configuration.IConfiguration" &&
            assemblyName == "Microsoft.Extensions.Configuration.Abstractions" ||
            typeName == "Microsoft.Extensions.Configuration.ConfigurationManager" &&
            assemblyName == "Microsoft.Extensions.Configuration";
        return knownType || type.AllInterfaces.Any(item =>
            item.ToDisplayString() == "Microsoft.Extensions.Configuration.IConfiguration" &&
            item.ContainingAssembly.Name == "Microsoft.Extensions.Configuration.Abstractions");
    }

    private static bool SafeKey(string value)
    {
        if (!TryNormalize(value, out var normalized) || string.IsNullOrWhiteSpace(normalized) || normalized.Length > 256 ||
            normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not (':' or '.' or '-' or '_')))
            return false;
        return !ContainsSensitiveMarker(normalized);
    }

    private static bool ContainsSensitiveMarker(string value)
    {
        if (value.Split([':', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => part.Equals("auth", StringComparison.OrdinalIgnoreCase))) return true;
        var normalized = new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return new[]
        {
            "password", "passwd", "pwd", "token", "secret", "credential", "apikey", "authorization", "cookie",
            "privatekey", "connectionstring"
        }.Any(normalized.Contains);
    }

    private static bool TryNormalize(string value, out string normalized)
    {
        try
        {
            normalized = value.Normalize(NormalizationForm.FormKC);
            return true;
        }
        catch (ArgumentException)
        {
            normalized = string.Empty;
            return false;
        }
    }
}
