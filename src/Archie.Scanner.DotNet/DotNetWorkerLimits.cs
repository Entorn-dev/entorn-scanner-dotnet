namespace Archie.Scanner.DotNet;

public static class DotNetWorkerLimits
{
    public const int MaxRequestBytes = 1024 * 1024;
    public const int MaxProtocolMessageBytes = 1024 * 1024;
    public const long MaxSerializedOutputBytes = 128L * 1024 * 1024;
    public const int MaxObservations = 100_000;
}

internal static class DotNetWorkerProtocol
{
    public static bool FitsOutput(
        string ready,
        IReadOnlyList<string> lines,
        int observationCount,
        out string? errorCode,
        int maxObservations = DotNetWorkerLimits.MaxObservations,
        int maxMessageBytes = DotNetWorkerLimits.MaxProtocolMessageBytes,
        long maxOutputBytes = DotNetWorkerLimits.MaxSerializedOutputBytes)
    {
        if (observationCount > maxObservations)
        {
            errorCode = "DOTNET_WORKER_OBSERVATION_LIMIT_EXCEEDED";
            return false;
        }
        var total = System.Text.Encoding.UTF8.GetByteCount(ready) + 1L;
        foreach (var line in lines)
        {
            var bytes = System.Text.Encoding.UTF8.GetByteCount(line);
            if (bytes > maxMessageBytes || total + bytes + 1 > maxOutputBytes)
            {
                errorCode = "DOTNET_WORKER_OUTPUT_LIMIT_EXCEEDED";
                return false;
            }
            total += bytes + 1L;
        }
        errorCode = null;
        return true;
    }
}

internal sealed class DotNetWorkerLimitException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
