using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>
/// Official Nexus Mods SSO login (wss://sso.nexusmods.com, protocol 2).
/// Flow: connect WebSocket → send {id:uuid} → open the authorization page in the
/// system browser → receive connection_token and send it back → receive api_key, done.
/// v0.68.4: per the official protocol, added "a WebSocket ping every 30 seconds" as
/// keep-alive — the official spec requires continuous pinging from connection to close,
/// otherwise if the user lingers on the authorization page the server considers the
/// session idle and disconnects (symptom: user approves but login fails).
/// No hand-written ping loop needed on the .NET side: KeepAliveInterval makes the
/// underlying stack send ping frames automatically.
/// </summary>
public class NexusSsoService
{
    public string? LastError { get; private set; }

    public async Task<string?> LoginAsync(string applicationSlug, CancellationToken ct = default)
    {
        LastError = null;
        var uuid = Guid.NewGuid().ToString();
        try
        {
            using var ws = new ClientWebSocket();
            // v0.68.4: the official SSO protocol strictly requires a ping every 30 seconds as keep-alive.
            // KeepAliveInterval makes the .NET underlying stack send WebSocket ping frames
            // automatically at that interval, equivalent to the ws.ping() timer in the official
            // Node example (works on .NET 6+).
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            await ws.ConnectAsync(new Uri("wss://sso.nexusmods.com"), ct);

            var hello = JsonSerializer.Serialize(new { id = uuid, token = (string?)null, protocol = 2 });
            await ws.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, ct);

            AppLog.Warn("NSSO", "SSO connection established (30s keep-alive), waiting for user authorization: " + uuid);   // v0.69.0: AppLog only has Warn/Error, no Info
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                $"https://www.nexusmods.com/sso?id={uuid}&application={applicationSlug}")
            { UseShellExecute = true });

            var buffer = new byte[16384];
            var sb = new StringBuilder();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));

            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;   // fragmented message: keep appending until complete
                var msg = sb.ToString(); sb.Clear();

            try
            {
                using var doc = JsonDocument.Parse(msg);
                var root = doc.RootElement;
                if (root.TryGetProperty("success", out var ok) && ok.GetBoolean()
                    && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    if (data.TryGetProperty("api_key", out var key))
                        return key.GetString();
                    if (data.TryGetProperty("connection_token", out var token))
                    {
                        var auth = JsonSerializer.Serialize(new { id = uuid, token = token.GetString(), protocol = 2 });
                        await ws.SendAsync(Encoding.UTF8.GetBytes(auth), WebSocketMessageType.Text, true, ct);
                    }
                }
                else if (root.TryGetProperty("error", out var err))
                {
                    LastError = err.GetString();
                    AppLog.Warn("NSSO", "Authorization page returned an error: " + err.GetString());
                }
            }
            catch (JsonException)
            {
                // Non-JSON (possibly keep-alive pings etc.) — ignore and keep reading. To avoid log
                // spam if every message fails, only log once
                AppLog.Warn("NSSO", "Received an unparseable message: " + msg[..Math.Min(msg.Length, 120)]);
            }
            }
        }
        catch (Exception ex) { LastError = ex.Message; }
        return null;
    }
}
