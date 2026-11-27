using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexAgentPlatform;

internal static class PlatformUpdater
{
    internal static async Task<UpdateState?> RequestAsync(string command, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint("/run/platform-update/update.sock"), deadline.Token);
            using var stream = new NetworkStream(socket);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(command + "\n"), deadline.Token);
            using var reader = new StreamReader(stream, Encoding.ASCII);
            var response = await reader.ReadLineAsync(deadline.Token);
            return response is null ? null : JsonSerializer.Deserialize<UpdateState>(response);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    internal sealed record UpdateState(
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("version")] string? Version);
}
