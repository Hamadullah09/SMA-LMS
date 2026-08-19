using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

using Library_Management_system.Rfid.Abstractions;

namespace Library_Management_system.Rfid.Bridge;

/// <summary>
/// The public site's end of the relay: accepts a bridge from a library PC and feeds the reads it
/// sends into the ordinary scan pipeline.
/// </summary>
/// <remarks>
/// Reads arriving here are indistinguishable downstream from ones this process read itself — they
/// go through <see cref="IRfidObservationSink"/>, so debounce, persistence, the live feed and the
/// kiosk state machine all work unchanged. That is the point: the kiosk does not know or care
/// which machine is holding the antenna.
/// </remarks>
public static class RfidBridgeEndpoint
{
    public static IEndpointRouteBuilder MapRfidBridge(this IEndpointRouteBuilder endpoints)
    {
        endpoints.Map("/rfid/bridge", HandleAsync).WithDisplayName("RFID bridge");
        return endpoints;
    }

    private static async Task HandleAsync(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<RfidBridgeOptions>>().Value;
        var registry = context.RequestServices.GetRequiredService<IRfidBridgeRegistry>();
        var sink = context.RequestServices.GetRequiredService<IRfidObservationSink>();
        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>().CreateLogger("RfidBridge");

        if (!options.ServerEnabled)
        {
            // No secret configured means no bridge is expected here. Refusing outright is better
            // than accepting one nobody set up.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("This endpoint expects a WebSocket connection.");
            return;
        }

        var presented = context.Request.Headers["X-Bridge-Secret"].ToString();

        // Fixed-time comparison: a plain string compare leaks the secret one character at a time to
        // anyone willing to measure how long the rejection takes.
        if (!FixedTimeEquals(presented, options.Secret!))
        {
            logger.LogWarning(
                "Rejected a bridge from {Ip}: wrong or missing secret.",
                context.Connection.RemoteIpAddress);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var readerId = options.ReaderId;

        logger.LogInformation(
            "Bridge accepted from {Ip}.", context.Connection.RemoteIpAddress);

        var buffer = new byte[8 * 1024];

        try
        {
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(buffer, context.RequestAborted);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (received.MessageType != WebSocketMessageType.Text || received.Count == 0)
                {
                    continue;
                }

                RfidBridgeMessage? message;

                try
                {
                    message = JsonSerializer.Deserialize<RfidBridgeMessage>(
                        buffer.AsSpan(0, received.Count));
                }
                catch (JsonException)
                {
                    // A malformed frame is not worth dropping the connection over.
                    continue;
                }

                if (message is null)
                {
                    continue;
                }

                if (message.ReaderId > 0)
                {
                    readerId = message.ReaderId;
                }

                switch (message.Type)
                {
                    case RfidBridgeMessage.Types.Hello:
                    case RfidBridgeMessage.Types.Status:
                    case RfidBridgeMessage.Types.Heartbeat:
                        registry.Report(readerId, message.ReaderName, message.Online);
                        break;

                    case RfidBridgeMessage.Types.Observation
                        when !string.IsNullOrWhiteSpace(message.Epc):

                        // Marked seen as well: a stream of reads is the strongest evidence there is
                        // that the far end and its reader are both alive.
                        registry.Report(readerId, message.ReaderName, true);

                        sink.Submit(new RfidObservation(
                            ReaderId: readerId,
                            Epc: message.Epc!,
                            ObservedUtc: message.ObservedUtc ?? DateTime.UtcNow,
                            Rssi: message.Rssi,
                            Antenna: message.Antenna));
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown, or the client went away.
        }
        catch (WebSocketException ex)
        {
            logger.LogInformation("Bridge for reader {ReaderId} dropped: {Message}", readerId, ex.Message);
        }
        finally
        {
            registry.Disconnected(readerId);
        }
    }

    private static bool FixedTimeEquals(string presented, string expected)
    {
        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(expected);

        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
