using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Archie.Contracts;
using Archie.Scanner.DotNet;

const string protocolVersion = "scanner/v1";
var scanner = new ScannerIdentity("archie.dotnet", "1.2.0");
var json = new JsonSerializerOptions(ContractJson.Options) { WriteIndented = false };
using var cancellation = new CancellationTokenSource();
using var terminateSignal = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    cancellation.Cancel();
});
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var ready = Serialize(new ReadyMessage(protocolVersion, scanner));
await WriteLineAsync(ready, cancellation.Token);

try
{
    var line = await ReadBoundedLineAsync(Console.OpenStandardInput(), DotNetWorkerLimits.MaxRequestBytes, cancellation.Token);
    if (line is null) throw new InvalidDataException("Scan request was not provided.");
    var message = JsonSerializer.Deserialize<ProtocolMessage>(line, ContractJson.Options);
    if (message is not ScanRequestMessage request || request.ProtocolVersion != protocolVersion)
        throw new InvalidDataException("Expected one scanner/v1 scan-request message.");

    var result = await new DotNetScanner().ScanAsync(request.Context.CheckoutPath, cancellation.Token);
    var messages = new List<ProtocolMessage>();
    messages.AddRange(result.Diagnostics.Select(item => (ProtocolMessage)new DiagnosticMessage(protocolVersion, item)));
    if (result.Succeeded)
        messages.AddRange(result.Observations.Select(item => (ProtocolMessage)new ObservationMessage(protocolVersion, item)));
    messages.Add(new CompletedMessage(protocolVersion, new(result.Succeeded ? result.Observations.Count : 0)));

    var lines = messages.Select(Serialize).ToArray();
    if (!DotNetWorkerProtocol.FitsOutput(ready, lines, result.Observations.Count, out var limitCode))
    {
        await SendFatalAsync(limitCode!,
            "The .NET worker exceeded its bounded observation, protocol-message, or serialized-output budget.", cancellation.Token);
        return 0;
    }

    foreach (var output in lines) await WriteLineAsync(output, cancellation.Token);
    return 0;
}
catch (OperationCanceledException)
{
    return 130;
}
catch (DotNetWorkerLimitException exception)
{
    await SendFatalAsync(exception.Code, exception.Message, CancellationToken.None);
    return 0;
}
catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
{
    await SendFatalAsync("DOTNET_SCAN_REQUEST_INVALID",
        "The .NET scanner could not read one valid bounded scan request.", CancellationToken.None);
    await Console.Error.WriteLineAsync(exception.GetType().Name);
    return 0;
}

string Serialize(ProtocolMessage message) => JsonSerializer.Serialize(message, json);

async Task SendFatalAsync(string code, string message, CancellationToken cancellationToken)
{
    var diagnostic = new Diagnostic(
        $"diagnostic:archie.dotnet:{code.ToLowerInvariant()}", code, "error", message, "archie.dotnet");
    await WriteLineAsync(Serialize(new DiagnosticMessage(protocolVersion, diagnostic)), cancellationToken);
    await WriteLineAsync(Serialize(new CompletedMessage(protocolVersion, new(0))), cancellationToken);
}

static async Task<string?> ReadBoundedLineAsync(Stream input, int limit, CancellationToken cancellationToken)
{
    using var buffer = new MemoryStream();
    var value = new byte[1];
    while (true)
    {
        var read = await input.ReadAsync(value, cancellationToken);
        if (read == 0) return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
        if (value[0] == (byte)'\n') return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
        buffer.WriteByte(value[0]);
        if (buffer.Length > limit)
            throw new DotNetWorkerLimitException("DOTNET_WORKER_REQUEST_LIMIT_EXCEEDED", $"The .NET worker request exceeded {limit} bytes.");
    }
}

static async Task WriteLineAsync(string line, CancellationToken cancellationToken)
{
    await Console.Out.WriteLineAsync(line.AsMemory(), cancellationToken);
    await Console.Out.FlushAsync(cancellationToken);
}
