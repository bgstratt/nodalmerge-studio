using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Storage;
using IHostApplicationLifetime = Microsoft.Extensions.Hosting.IHostApplicationLifetime;

namespace NodalMerge.Studio.Host;

/// <summary>
/// Maintains an outbound WebSocket connection to a nodalmerge host room, presenting this
/// process as a named peer. Handles reconnection with exponential backoff. When HostUri is
/// null the client is a no-op — the process runs standalone with no room presence.
/// </summary>
public sealed class RoomPeerClient(
    HeadlessPeerOptions options,
    WorkspaceOptions workspaceOptions,
    IHostApplicationLifetime appLifetime,
    ILogger<RoomPeerClient> logger) : IHostedService, IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _runLoop;

    public bool IsConnected { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.HostUri))
        {
            logger.LogInformation("[RoomPeerClient] No HostUri configured — running standalone (no room presence)");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runLoop = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
            await _cts.CancelAsync();

        if (_runLoop is not null)
        {
            try { await _runLoop.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var peerId = ResolveOrCreatePeerId();
        var delayMs = 1000;

        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                var uri = BuildWebSocketUri();
                logger.LogInformation("[RoomPeerClient] Connecting to {Uri} as peer_id={PeerId} peer_type={PeerType}",
                    uri, peerId, options.PeerType);

                await ws.ConnectAsync(uri, ct);
                IsConnected = true;
                delayMs = 1000;

                await SendHelloAsync(ws, peerId, ct);
                await ReceiveLoopAsync(ws, peerId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[RoomPeerClient] Connection lost — reconnecting in {DelayMs}ms", delayMs);
            }
            finally
            {
                IsConnected = false;
                if (ws.State == WebSocketState.Open)
                {
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutting down", CancellationToken.None); }
                    catch { /* best effort */ }
                }
            }

            if (!ct.IsCancellationRequested)
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                delayMs = Math.Min(delayMs * 2, 30_000);
            }
        }

        logger.LogInformation("[RoomPeerClient] Disconnected");
    }

    private async Task SendHelloAsync(ClientWebSocket ws, string peerId, CancellationToken ct)
    {
        var hello = JsonSerializer.Serialize(new
        {
            type = "hello",
            room = options.RoomId,
            pubkey = peerId,
            peer_id = peerId,
            peer_type = options.PeerType,
            frontier = Array.Empty<string>()
        });

        var bytes = Encoding.UTF8.GetBytes(hello);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        logger.LogDebug("[RoomPeerClient] Sent hello room={Room}", options.RoomId);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, string peerId, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                logger.LogInformation("[RoomPeerClient] Server closed the connection peer_id={PeerId}", peerId);
                return;
            }

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

            string? msgType = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                doc.RootElement.TryGetProperty("type", out var typeProp);
                msgType = typeProp.GetString();
            }
            catch (Exception ex) { logger.LogDebug(ex, "[RoomPeerClient] Malformed JSON message — skipping"); }

            switch (msgType)
            {
                case "peer-joined":
                    logger.LogInformation("[RoomPeerClient] peer-joined broadcast received");
                    break;
                case "peer-left":
                    logger.LogInformation("[RoomPeerClient] peer-left broadcast received");
                    break;
                case "catch-up-pack":
                    logger.LogInformation("[RoomPeerClient] Received catch-up pack from server");
                    break;
                case "participant.stop":
                    try
                    {
                        using var doc2 = JsonDocument.Parse(json);
                        if (doc2.RootElement.TryGetProperty("peer_id", out var pidProp)
                            && pidProp.GetString() == peerId)
                        {
                            logger.LogInformation("[RoomPeerClient] Received stop signal — requesting application shutdown");
                            appLifetime.StopApplication();
                        }
                    }
                    catch (Exception ex) { logger.LogDebug(ex, "[RoomPeerClient] Malformed participant.stop payload — skipping"); }
                    break;
                default:
                    logger.LogDebug("[RoomPeerClient] Received message type={Type}", msgType ?? "(unknown)");
                    break;
            }
        }
    }

    private Uri BuildWebSocketUri()
    {
        var base_ = options.HostUri!.TrimEnd('/');
        var room = Uri.EscapeDataString(options.RoomId);
        return new Uri($"{base_}/ws/{room}");
    }

    private string ResolveOrCreatePeerId()
    {
        if (!string.IsNullOrWhiteSpace(options.PeerId))
            return options.PeerId;

        var dir = string.IsNullOrWhiteSpace(workspaceOptions.RootPath)
            ? Path.GetTempPath()
            : workspaceOptions.RootPath;

        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ".peer-id");

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                logger.LogInformation("[RoomPeerClient] Using persisted peer_id={PeerId}", existing);
                return existing;
            }
        }

        var peerId = Guid.NewGuid().ToString("N");
        File.WriteAllText(path, peerId);
        logger.LogInformation("[RoomPeerClient] Generated new peer_id={PeerId} persisted to {Path}", peerId, path);
        return peerId;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }
    }
}
