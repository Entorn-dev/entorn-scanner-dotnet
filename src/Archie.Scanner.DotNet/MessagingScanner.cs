using Archie.Contracts;
using Azure.Messaging.ServiceBus;
using Confluent.Kafka;
using Microsoft.Azure.Functions.Worker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Diagnostic = Archie.Contracts.Diagnostic;

namespace Archie.Scanner.DotNet;

internal sealed record MessageChannelDetection(
    string Provider,
    string ChannelKind,
    string Name,
    string? Topic,
    string? Subscription,
    EdgeKind Relationship,
    string? Contract,
    string Path,
    SourceRange Range,
    string Rule,
    string Symbol);

internal sealed record MessagingScanResult(
    IReadOnlyList<MessageChannelDetection> Detections,
    IReadOnlyList<Diagnostic> Diagnostics);

internal sealed record ServiceBusDestination(
    string Name,
    string? Topic,
    string? Subscription,
    string Kind);

internal static class MessagingScanner
{
    private const string KafkaAssembly = "Confluent.Kafka";
    private const string ServiceBusAssembly = "Azure.Messaging.ServiceBus";
    private const string FunctionsAssemblyPrefix = "Microsoft.Azure.Functions.Worker";
    private const string KafkaProducer = "Confluent.Kafka.IProducer<TKey, TValue>";
    private const string KafkaConsumer = "Confluent.Kafka.IConsumer<TKey, TValue>";
    private const string ServiceBusClient = "Azure.Messaging.ServiceBus.ServiceBusClient";
    private const string ServiceBusSender = "Azure.Messaging.ServiceBus.ServiceBusSender";
    private const string FunctionAttribute = "Microsoft.Azure.Functions.Worker.FunctionAttribute";
    private const string ServiceBusTriggerAttribute = "Microsoft.Azure.Functions.Worker.ServiceBusTriggerAttribute";
    private const string ServiceBusOutputAttribute = "Microsoft.Azure.Functions.Worker.ServiceBusOutputAttribute";

    public static async Task<MessagingScanResult> ScanAsync(ProjectModel project, CancellationToken cancellationToken)
    {
        if (project.Sources.Count == 0) return new([], []);
        var trees = new List<SyntaxTree>();
        foreach (var source in project.Sources)
        {
            var text = await File.ReadAllTextAsync(source.FullPath, cancellationToken);
            var tree = CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
                source.Path, cancellationToken: cancellationToken);
            if (tree.GetDiagnostics(cancellationToken).Any(item => item.Severity == DiagnosticSeverity.Error))
                continue; // AspNetScanner owns the fail-closed malformed-source diagnostic.
            trees.Add(tree);
        }
        if (trees.Count == 0) return new([], []);

        var packages = project.Packages.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kafkaAvailable = packages.Contains("confluent.kafka");
        var functionsAvailable = packages.Contains("microsoft.azure.functions.worker") &&
                                 packages.Contains("microsoft.azure.functions.worker.extensions.servicebus");
        var serviceBusAvailable = packages.Contains("azure.messaging.servicebus") ||
                                  packages.Contains("microsoft.azure.functions.worker.extensions.servicebus");
        var references = ReferencePaths(kafkaAvailable, serviceBusAvailable, functionsAvailable)
            .Distinct(PathComparer()).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var globalUsings = project.Classification == "web"
            ? """
                global using System;
                global using System.Collections.Generic;
                global using System.IO;
                global using System.Linq;
                global using System.Net.Http;
                global using System.Threading;
                global using System.Threading.Tasks;
                global using Microsoft.AspNetCore.Builder;
                global using Microsoft.AspNetCore.Hosting;
                global using Microsoft.AspNetCore.Http;
                global using Microsoft.AspNetCore.Routing;
                global using Microsoft.Extensions.Configuration;
                global using Microsoft.Extensions.DependencyInjection;
                global using Microsoft.Extensions.Hosting;
                global using Microsoft.Extensions.Logging;
                """
            : """
                global using System;
                global using System.Collections.Generic;
                global using System.IO;
                global using System.Linq;
                global using System.Net.Http;
                global using System.Threading;
                global using System.Threading.Tasks;
                """;
        var compilationTrees = project.ImplicitUsings
            ? trees.Append(CSharpSyntaxTree.ParseText(globalUsings, path: "__archie_messaging_global_usings.g.cs"))
            : trees;
        var outputKind = trees.Any(tree => tree.GetRoot(cancellationToken).DescendantNodes().OfType<GlobalStatementSyntax>().Any())
            ? OutputKind.ConsoleApplication
            : OutputKind.DynamicallyLinkedLibrary;
        var compilation = CSharpCompilation.Create($"Archie.Messaging.{Stable(project.Path)}", compilationTrees, references,
            new CSharpCompilationOptions(outputKind));

        var diagnostics = new List<Diagnostic>();
        var detections = new List<MessageChannelDetection>();
        var messagingNode = trees.Select(tree => tree.GetRoot(cancellationToken)).SelectMany(MessagingNodes).FirstOrDefault();
        var compilationErrors = compilation.GetDiagnostics(cancellationToken)
            .Where(item => item.Severity == DiagnosticSeverity.Error).ToArray();
        if (messagingNode is not null && compilationErrors.Length > 0)
        {
            var codes = string.Join(", ", compilationErrors.Select(item => item.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            diagnostics.Add(Warning("DOTNET_MESSAGING_COMPILATION_UNAVAILABLE", messagingNode.SyntaxTree, messagingNode,
                $"Project '{project.Path}' has semantic compilation errors ({codes}) affecting messaging analysis; no messaging facts were emitted.", project.Key));
            return new([], diagnostics);
        }
        foreach (var tree in trees.OrderBy(item => item.FilePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = await tree.GetRootAsync(cancellationToken);
            var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            if (kafkaAvailable) ScanKafka(project, tree, root, model, detections, diagnostics);
            if (serviceBusAvailable) ScanServiceBusClients(project, tree, root, model, detections, diagnostics);
            if (functionsAvailable) ScanFunctions(project, tree, root, model, detections, diagnostics);
        }
        return new(
            detections.OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Range.StartLine)
                .ThenBy(item => item.Provider, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray(),
            diagnostics.DistinctBy(item => item.Id).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray());
    }

    private static void ScanKafka(ProjectModel project, SyntaxTree tree, SyntaxNode root, SemanticModel model,
        ICollection<MessageChannelDetection> detections, ICollection<Diagnostic> diagnostics)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member || member.Name.Identifier.ValueText is not ("Produce" or "ProduceAsync" or "Subscribe"))
                continue;
            var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (symbol is null)
            {
                diagnostics.Add(Warning("DOTNET_MESSAGING_BINDING_UNAVAILABLE", tree, invocation,
                    $"A '{member.Name.Identifier.ValueText}' call could not be semantically bound; no messaging fact was emitted.", project.Key));
                continue;
            }
            if (!KnownAssembly(symbol.ContainingAssembly, KafkaAssembly)) continue;
            var containing = symbol.ContainingType.OriginalDefinition.ToDisplayString();
            var relationship = containing == KafkaProducer && symbol.Name is "Produce" or "ProduceAsync" ? EdgeKind.Publishes :
                containing == KafkaConsumer && symbol.Name == "Subscribe" ? EdgeKind.Subscribes : (EdgeKind?)null;
            if (relationship is null) continue;
            var destination = FirstStringArgument(model, invocation, symbol);
            if (destination is null)
            {
                diagnostics.Add(Warning("DOTNET_MESSAGE_DESTINATION_UNRESOLVED", tree, invocation,
                    "A semantically resolved Kafka operation has a non-constant topic; no topic or relationship fact was invented.", project.Key));
                continue;
            }
            var contract = MessageType(symbol.ContainingType.TypeArguments.ElementAtOrDefault(1));
            if (contract is null)
                diagnostics.Add(Warning("DOTNET_MESSAGE_CONTRACT_UNRESOLVED", tree, invocation,
                    $"Kafka topic '{destination}' was resolved, but its message contract was not safely resolvable.", project.Key));
            detections.Add(new("kafka", "topic", destination, destination, null, relationship.Value, contract,
                tree.FilePath, Range(tree, invocation), "roslyn:semantic-confluent-kafka-operation", symbol.ToDisplayString()));
        }
    }

    private static void ScanServiceBusClients(ProjectModel project, SyntaxTree tree, SyntaxNode root, SemanticModel model,
        ICollection<MessageChannelDetection> detections, ICollection<Diagnostic> diagnostics)
    {
        var senderFactories = new List<(InvocationExpressionSyntax Invocation, IMethodSymbol Symbol, ServiceBusDestination Destination)>();
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol symbol ||
                !KnownAssembly(symbol.ContainingAssembly, ServiceBusAssembly) ||
                symbol.ContainingType.ToDisplayString() != ServiceBusClient || symbol.Name is not ("CreateSender" or "CreateProcessor")) continue;
            if (!TryServiceBusDestination(model, invocation, symbol, out var destination))
            {
                diagnostics.Add(Warning("DOTNET_MESSAGE_DESTINATION_UNRESOLVED", tree, invocation,
                    $"A semantically resolved Service Bus {symbol.Name} call has a non-constant destination; no channel or relationship fact was invented.", project.Key));
                continue;
            }
            if (symbol.Name == "CreateProcessor")
            {
                diagnostics.Add(Warning("DOTNET_MESSAGE_CONTRACT_UNRESOLVED", tree, invocation,
                    $"Service Bus destination '{destination.Name}' was resolved, but processor registration alone does not prove a message contract.", project.Key));
                detections.Add(new("azure-service-bus", destination.Kind, destination.Name, destination.Topic, destination.Subscription, EdgeKind.Subscribes,
                    null, tree.FilePath, Range(tree, invocation), "roslyn:semantic-service-bus-processor-dataflow", symbol.ToDisplayString()));
            }
            else
            {
                senderFactories.Add((invocation, symbol, destination));
            }
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member ||
                model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol symbol ||
                !KnownAssembly(symbol.ContainingAssembly, ServiceBusAssembly) ||
                symbol.ContainingType.ToDisplayString() != ServiceBusSender || symbol.Name is not ("SendMessageAsync" or "SendMessagesAsync")) continue;
            if (model.GetSymbolInfo(member.Expression).Symbol is not ILocalSymbol receiver)
            {
                if (senderFactories.Count == 0)
                    diagnostics.Add(Warning("DOTNET_MESSAGE_SENDER_FLOW_UNRESOLVED", tree, invocation,
                        "A semantically resolved Service Bus send has no resolved sender factory in this source file; no relationship fact was invented.", project.Key));
                continue;
            }
            if (!TryReachingSenderDestination(root, model, receiver, invocation, out var sender))
            {
                diagnostics.Add(Warning("DOTNET_MESSAGE_SENDER_FLOW_UNRESOLVED", tree, invocation,
                    "A semantically resolved Service Bus send does not have one unambiguous direct local sender factory; no relationship fact was invented.", project.Key));
                continue;
            }
            var payload = (model.GetOperation(invocation) as IInvocationOperation)?.Arguments
                .FirstOrDefault(item => item.Parameter?.Name is "message" or "messages")?.Value.Syntax as ExpressionSyntax;
            var contract = ServiceBusContract(model, payload);
            if (contract is null)
                diagnostics.Add(Warning("DOTNET_MESSAGE_CONTRACT_UNRESOLVED", tree, invocation,
                    $"Service Bus destination '{sender.Name}' was resolved, but its sent message contract was not safely resolvable.", project.Key));
            detections.Add(new("azure-service-bus", sender.Kind, sender.Name, sender.Topic, sender.Subscription,
                EdgeKind.Publishes, contract, tree.FilePath, Range(tree, invocation),
                "roslyn:semantic-service-bus-sender-dataflow", symbol.ToDisplayString()));
        }

        foreach (var (invocation, symbol, destination) in senderFactories)
        {
            if (!detections.Any(item => item.Provider == "azure-service-bus" && item.Relationship == EdgeKind.Publishes &&
                                        item.Name == destination.Name && item.Path == tree.FilePath))
                diagnostics.Add(Warning("DOTNET_MESSAGE_CONTRACT_UNRESOLVED", tree, invocation,
                    $"Service Bus destination '{destination.Name}' was resolved from a sender factory, but the message contract was not safely resolvable.", project.Key));
            detections.Add(new("azure-service-bus", destination.Kind, destination.Name, destination.Topic, destination.Subscription,
                EdgeKind.Publishes, null, tree.FilePath, Range(tree, invocation),
                "roslyn:semantic-service-bus-sender-factory", symbol.ToDisplayString()));
        }
    }

    private static void ScanFunctions(ProjectModel project, SyntaxTree tree, SyntaxNode root, SemanticModel model,
        ICollection<MessageChannelDetection> detections, ICollection<Diagnostic> diagnostics)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var methodSymbol = model.GetDeclaredSymbol(method);
            if (methodSymbol is null || !Attributes(method.AttributeLists).Any(attribute => IsAttribute(model, attribute, FunctionAttribute))) continue;
            foreach (var parameter in method.ParameterList.Parameters)
            {
                var trigger = Attributes(parameter.AttributeLists).FirstOrDefault(attribute => SimpleName(attribute) == "ServiceBusTrigger");
                if (trigger is null) continue;
                if (!IsAttribute(model, trigger, ServiceBusTriggerAttribute))
                {
                    diagnostics.Add(Warning("DOTNET_MESSAGING_BINDING_UNAVAILABLE", tree, trigger,
                        "A ServiceBusTrigger-like attribute did not resolve to the Azure Functions extension; no trigger fact was emitted.", project.Key));
                    continue;
                }
                if (!TryBindingDestination(model, trigger, out var name, out var topic, out var subscription))
                {
                    diagnostics.Add(Warning("DOTNET_MESSAGE_DESTINATION_UNRESOLVED", tree, trigger,
                        "An Azure Functions Service Bus trigger has a non-constant destination; no channel or relationship fact was invented.", project.Key));
                    continue;
                }
                var parameterSymbol = model.GetDeclaredSymbol(parameter);
                var contract = MessageType(parameterSymbol?.Type);
                if (contract is null)
                    diagnostics.Add(Warning("DOTNET_MESSAGE_CONTRACT_UNRESOLVED", tree, parameter,
                        $"Azure Functions trigger '{name}' was resolved, but its parameter does not prove an event contract.", project.Key));
                detections.Add(new("azure-service-bus", subscription is null ? "queue" : "subscription", name, topic, subscription,
                    EdgeKind.Subscribes, contract, tree.FilePath, Range(tree, trigger),
                    "roslyn:semantic-azure-functions-service-bus-trigger", methodSymbol.ToDisplayString()));
            }

            var output = Attributes(method.AttributeLists).FirstOrDefault(attribute => SimpleName(attribute) == "ServiceBusOutput");
            if (output is null) continue;
            if (!IsAttribute(model, output, ServiceBusOutputAttribute))
            {
                diagnostics.Add(Warning("DOTNET_MESSAGING_BINDING_UNAVAILABLE", tree, output,
                    "A ServiceBusOutput-like attribute did not resolve to the Azure Functions extension; no output-binding fact was emitted.", project.Key));
                continue;
            }
            if (!TryBindingDestination(model, output, out var outputName, out var outputTopic, out var outputSubscription))
            {
                diagnostics.Add(Warning("DOTNET_MESSAGE_DESTINATION_UNRESOLVED", tree, output,
                    "An Azure Functions Service Bus output has a non-constant destination; no channel or relationship fact was invented.", project.Key));
                continue;
            }
            if (!TryOutputEntityKind(model, output, out var outputKind))
            {
                diagnostics.Add(Warning("DOTNET_MESSAGE_ENTITY_KIND_UNRESOLVED", tree, output,
                    "An Azure Functions Service Bus output has an unsupported entity type; no channel or relationship fact was invented.", project.Key));
                continue;
            }
            outputTopic = outputKind == "topic" ? outputName : null;
            var contractType = UnwrapTask(methodSymbol.ReturnType);
            var outputContract = MessageType(contractType);
            if (outputContract is null)
                diagnostics.Add(Warning("DOTNET_MESSAGE_CONTRACT_UNRESOLVED", tree, method,
                    $"Azure Functions output '{outputName}' was resolved, but its return type does not prove an event contract.", project.Key));
            detections.Add(new("azure-service-bus", outputKind, outputName,
                outputTopic, outputSubscription, EdgeKind.Publishes, outputContract, tree.FilePath, Range(tree, output),
                "roslyn:semantic-azure-functions-service-bus-output", methodSymbol.ToDisplayString()));
        }
    }

    private static IEnumerable<SyntaxNode> MessagingNodes(SyntaxNode root) =>
        root.DescendantNodes().Where(node => node switch
        {
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } =>
                member.Name.Identifier.ValueText is "Produce" or "ProduceAsync" or "Subscribe" or "CreateSender" or
                    "CreateProcessor" or "SendMessageAsync" or "SendMessagesAsync",
            AttributeSyntax attribute => SimpleName(attribute) is "Function" or "ServiceBusTrigger" or "ServiceBusOutput",
            _ => false
        });

    private static IEnumerable<string> ReferencePaths(bool kafkaAvailable, bool serviceBusAvailable, bool functionsAvailable)
    {
        foreach (var path in ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!kafkaAvailable && name.Equals(KafkaAssembly, StringComparison.OrdinalIgnoreCase)) continue;
            if (!serviceBusAvailable && (name.Equals(ServiceBusAssembly, StringComparison.OrdinalIgnoreCase) ||
                                         name.Equals("System.Memory.Data", StringComparison.OrdinalIgnoreCase))) continue;
            if (!functionsAvailable && (name.StartsWith(FunctionsAssemblyPrefix, StringComparison.OrdinalIgnoreCase) ||
                                        name.Equals("Microsoft.Azure.Functions.Worker.Extensions.Abstractions", StringComparison.OrdinalIgnoreCase))) continue;
            yield return path;
        }
        if (kafkaAvailable) yield return typeof(IProducer<,>).Assembly.Location;
        if (serviceBusAvailable)
        {
            yield return typeof(ServiceBusClient).Assembly.Location;
            yield return typeof(BinaryData).Assembly.Location;
        }
        if (functionsAvailable)
        {
            yield return typeof(FunctionAttribute).Assembly.Location;
            yield return typeof(ServiceBusTriggerAttribute).Assembly.Location;
            yield return typeof(ServiceBusOutputAttribute).BaseType!.Assembly.Location;
        }
    }

    private static string? FirstStringArgument(SemanticModel model, InvocationExpressionSyntax invocation, IMethodSymbol symbol) =>
        symbol.Parameters.Where(item => item.Type.SpecialType == SpecialType.System_String)
            .Select(item => DestinationStringArguments(model, invocation).GetValueOrDefault(item.Name)).FirstOrDefault();

    private static Dictionary<string, string?> DestinationStringArguments(SemanticModel model, InvocationExpressionSyntax invocation)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (model.GetOperation(invocation) is not IInvocationOperation operation) return values;
        foreach (var argument in operation.Arguments)
        {
            var parameter = argument.Parameter;
            if (parameter?.Type.SpecialType != SpecialType.System_String) continue;
            values[parameter.Name] = argument.Value.Syntax is ExpressionSyntax expression
                ? DestinationString(model, expression, invocation)
                : null;
        }
        return values;
    }

    private static string? DestinationString(SemanticModel model, ExpressionSyntax expression, SyntaxNode use)
    {
        var constant = model.GetConstantValue(expression);
        if (constant.HasValue && constant.Value is string text && !string.IsNullOrWhiteSpace(text)) return text;
        if (expression is ParenthesizedExpressionSyntax parenthesized)
            return DestinationString(model, parenthesized.Expression, use);
        if (expression is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } coalesce &&
            ConfigurationElement(model, coalesce.Left))
            return DestinationString(model, coalesce.Right, use);
        if (model.GetSymbolInfo(expression).Symbol is not ILocalSymbol local) return null;
        var declarations = expression.SyntaxTree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(item => item.SpanStart < use.SpanStart && item.Initializer is not null &&
                           SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(item), local)).ToArray();
        if (declarations.Length != 1 || expression.SyntaxTree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(item => item.SpanStart < use.SpanStart &&
                             SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(item.Left).Symbol, local))) return null;
        return DestinationString(model, declarations[0].Initializer!.Value, declarations[0]);
    }

    private static bool ConfigurationElement(SemanticModel model, ExpressionSyntax expression)
    {
        if (expression is not ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } element ||
            model.GetConstantValue(element.ArgumentList.Arguments[0].Expression) is not { HasValue: true, Value: string key } ||
            string.IsNullOrWhiteSpace(key)) return false;
        var type = model.GetTypeInfo(element.Expression).Type as INamedTypeSymbol;
        return type is not null && (type.ToDisplayString() == "Microsoft.Extensions.Configuration.IConfiguration" ||
                                    type.AllInterfaces.Any(item => item.ToDisplayString() == "Microsoft.Extensions.Configuration.IConfiguration"));
    }

    private static bool TryServiceBusDestination(SemanticModel model, InvocationExpressionSyntax invocation,
        IMethodSymbol symbol, out ServiceBusDestination destination)
    {
        destination = null!;
        var values = DestinationStringArguments(model, invocation);
        if (symbol.Name == "CreateProcessor" && symbol.Parameters.Any(item => item.Name == "subscriptionName"))
        {
            if (!values.TryGetValue("topicName", out var topic) || topic is null ||
                !values.TryGetValue("subscriptionName", out var subscription) || subscription is null) return false;
            destination = new($"{topic}/{subscription}", topic, subscription, "subscription");
            return true;
        }
        var parameterName = symbol.Name == "CreateSender" ? "queueOrTopicName" : "queueName";
        if (!values.TryGetValue(parameterName, out var name) || name is null) return false;
        destination = new(name, null, null, symbol.Name == "CreateProcessor" ? "queue" : "queue-or-topic");
        return true;
    }

    private static bool TryReachingSenderDestination(SyntaxNode root, SemanticModel model, ILocalSymbol receiver,
        InvocationExpressionSyntax send, out ServiceBusDestination destination)
    {
        destination = null!;
        var writes = new List<(SyntaxNode Node, ExpressionSyntax Expression, bool IsSimple)>();
        foreach (var variable in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            if (variable.SpanStart < send.SpanStart && variable.Initializer is not null &&
                SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(variable), receiver))
                writes.Add((variable, variable.Initializer.Value, true));
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            if (assignment.SpanStart < send.SpanStart &&
                SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, receiver))
                writes.Add((assignment, assignment.Right, assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)));
        var sendBlock = send.FirstAncestorOrSelf<BlockSyntax>();
        var writeIsDirect = sendBlock is not null && writes.Count == 1 &&
            IsDirectSenderWrite(writes[0].Node, writes[0].Expression, sendBlock);
        if (writes.Count != 1 || !writes[0].IsSimple || !writeIsDirect ||
            writes[0].Expression is not InvocationExpressionSyntax factory ||
            model.GetSymbolInfo(factory).Symbol is not IMethodSymbol symbol ||
            !KnownAssembly(symbol.ContainingAssembly, ServiceBusAssembly) ||
            symbol.ContainingType.ToDisplayString() != ServiceBusClient || symbol.Name != "CreateSender") return false;
        return TryServiceBusDestination(model, factory, symbol, out destination);
    }

    internal static bool IsDirectSenderWrite(SyntaxNode node, ExpressionSyntax expression, BlockSyntax sendBlock) => node switch
    {
        AssignmentExpressionSyntax assignment => assignment.Parent is ExpressionStatementSyntax statement &&
            statement.Expression == assignment && statement.Parent == sendBlock,
        VariableDeclaratorSyntax variable => variable.Initializer?.Value == expression &&
            variable.Parent is VariableDeclarationSyntax { Parent: LocalDeclarationStatementSyntax statement } &&
            statement.UsingKeyword.IsKind(SyntaxKind.None) && statement.Parent == sendBlock,
        _ => false
    };

    private static string? ServiceBusContract(SemanticModel model, ExpressionSyntax? expression)
    {
        if (expression is null) return null;
        foreach (var invocation in expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol symbol || symbol.Name != "FromObjectAsJson" ||
                symbol.ContainingType.ToDisplayString() != "System.BinaryData" || symbol.TypeArguments.Length != 1) continue;
            return MessageType(symbol.TypeArguments[0]);
        }
        return null;
    }

    private static string? MessageType(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named || type.TypeKind is not (TypeKind.Class or TypeKind.Struct) ||
            type.SpecialType is not SpecialType.None || named.IsUnboundGenericType ||
            named.TypeArguments.Any(item => !IsConcreteTypeArgument(item)) ||
            !type.Locations.Any(item => item.IsInSource)) return null;
        if (type.ContainingAssembly is null || KnownAssembly(type.ContainingAssembly, KafkaAssembly) ||
            KnownAssembly(type.ContainingAssembly, ServiceBusAssembly) ||
            type.ContainingAssembly.Name.StartsWith(FunctionsAssemblyPrefix, StringComparison.Ordinal)) return null;
        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal);
    }

    private static bool IsConcreteTypeArgument(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => false,
        IErrorTypeSymbol => false,
        IDynamicTypeSymbol => false,
        IArrayTypeSymbol array => IsConcreteTypeArgument(array.ElementType),
        INamedTypeSymbol named => !named.IsUnboundGenericType && named.TypeArguments.All(IsConcreteTypeArgument),
        _ => type.TypeKind is not (TypeKind.Pointer or TypeKind.FunctionPointer)
    };

    private static ITypeSymbol UnwrapTask(ITypeSymbol type) => type is INamedTypeSymbol named &&
        named.OriginalDefinition.ToDisplayString() == "System.Threading.Tasks.Task<TResult>" && named.TypeArguments.Length == 1
            ? named.TypeArguments[0] : type;

    private static bool TryBindingDestination(SemanticModel model, AttributeSyntax attribute,
        out string name, out string? topic, out string? subscription)
    {
        name = string.Empty;
        topic = subscription = null;
        if (model.GetOperation(attribute) is not IAttributeOperation { Operation: IObjectCreationOperation creation } ||
            creation.Constructor is not { } constructor) return false;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var argument in creation.Arguments)
        {
            var parameter = argument.Parameter;
            if (parameter?.Name is not ("queueName" or "queueOrTopicName" or "topicName" or "subscriptionName")) continue;
            var constant = argument.Value.ConstantValue;
            values[parameter.Name] = constant.HasValue && constant.Value is string text && !string.IsNullOrWhiteSpace(text) ? text : null;
        }
        if (constructor.Parameters.Any(item => item.Name == "subscriptionName"))
        {
            if (!values.TryGetValue("topicName", out topic) || topic is null ||
                !values.TryGetValue("subscriptionName", out subscription) || subscription is null) return false;
            name = $"{topic}/{subscription}";
            return true;
        }
        var parameterName = constructor.Parameters.Any(item => item.Name == "queueName") ? "queueName" : "queueOrTopicName";
        return values.TryGetValue(parameterName, out var value) && value is not null && (name = value).Length > 0;
    }

    private static bool TryOutputEntityKind(SemanticModel model, AttributeSyntax attribute, out string kind)
    {
        kind = string.Empty;
        if (model.GetOperation(attribute) is not IAttributeOperation { Operation: IObjectCreationOperation creation } ||
            creation.Constructor is not { } constructor) return false;
        object? value = constructor.Parameters.FirstOrDefault(item => item.Name == "entityType")?.ExplicitDefaultValue;
        foreach (var argument in creation.Arguments)
        {
            var parameter = argument.Parameter;
            if (parameter?.Name != "entityType") continue;
            value = argument.Value.ConstantValue.HasValue ? argument.Value.ConstantValue.Value : null;
        }
        foreach (var assignment in creation.Initializer?.Initializers.OfType<ISimpleAssignmentOperation>() ?? [])
            if (assignment.Target is IPropertyReferenceOperation { Property.Name: "EntityType" })
                value = assignment.Value.ConstantValue.HasValue ? assignment.Value.ConstantValue.Value : null;
        if (value is not int entityType || entityType is < 0 or > 1) return false;
        kind = entityType == 0 ? "queue" : "topic";
        return true;
    }

    private static bool IsAttribute(SemanticModel model, AttributeSyntax attribute, string metadataName) =>
        model.GetSymbolInfo(attribute).Symbol is IMethodSymbol constructor &&
        constructor.ContainingType.ToDisplayString() == metadataName &&
        (constructor.ContainingAssembly.Name == "Microsoft.Azure.Functions.Worker.Extensions.Abstractions" ||
         constructor.ContainingAssembly.Name.StartsWith(FunctionsAssemblyPrefix, StringComparison.Ordinal));

    private static bool KnownAssembly(IAssemblySymbol assembly, string name) => assembly.Name.Equals(name, StringComparison.Ordinal);

    private static IEnumerable<AttributeSyntax> Attributes(SyntaxList<AttributeListSyntax> lists) => lists.SelectMany(item => item.Attributes);

    private static string SimpleName(AttributeSyntax attribute)
    {
        var name = attribute.Name.ToString().Split('.').Last();
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    private static SourceRange Range(SyntaxTree tree, SyntaxNode node)
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

    private static string Stable(string value) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..12];

    private static StringComparer PathComparer() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
