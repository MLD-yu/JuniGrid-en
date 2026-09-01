using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>
/// OAuth2 Authorization Code flow (with PKCE) for registered JuniGrid applications.
/// Callback: a temporary local loopback HTTP listener at http://localhost:49162/auth/callback
/// — standard for desktop apps; nothing is exposed on the network (listener binds to
/// localhost only, shuts down after a single callback or 5 minutes).
/// Flow: start listener → open the authorize page in the system browser → user approves →
/// Nexus redirects to the callback with ?code=…&state=… → exchange the code for tokens at
/// https://users.nexusmods.com/oauth/token (Basic auth with client_id:client_secret).
/// The resulting access_token authenticates API calls as the logged-in user
/// (Authorization: Bearer …), alongside the existing API-key and SSO login paths.
/// ClientId/ClientSecret are issued by Nexus Mods when the application is registered —
/// they are NOT hardcoded; the user supplies them in Settings (stored only in the
/// local per-user config file).
/// </summary>
public class NexusOAuthService
{
    public const string CallbackUrl = "http://localhost:49162/auth/callback";
    private const string AuthorizeUrl = "https://users.nexusmods.com/oauth/authorize";
    private const string TokenUrl = "https://users.nexusmods.com/oauth/token";

    public string? LastError { get; private set; }

    public async Task<string?> LoginAsync(string clientId, string clientSecret, CancellationToken ct = default)
    {
        LastError = null;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            LastError = "OAuth2 is not configured yet: enter the Client ID / Client Secret issued by Nexus Mods in Settings first.";
            return null;
        }

        var state = Guid.NewGuid().ToString("N");
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var listener = new HttpListener();
        listener.Prefixes.Add(CallbackUrl + "/");
        HttpListenerContext? ctx = null;
        try
        {
            listener.Start();
            var authorize = $"{AuthorizeUrl}?client_id={Uri.EscapeDataString(clientId)}" +
                            $"&redirect_uri={Uri.EscapeDataString(CallbackUrl)}" +
                            "&response_type=code&scope=public" +
                            $"&state={state}&code_challenge={challenge}&code_challenge_method=S256";
            AppLog.Warn("NOAuth", "Opening OAuth2 authorize page, callback=" + CallbackUrl);
            Process.Start(new ProcessStartInfo(authorize) { UseShellExecute = true });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var awaitTask = listener.GetContextAsync();
            var done = await Task.WhenAny(awaitTask, Task.Delay(Timeout.Infinite, timeout.Token));
            if (done != awaitTask) { LastError = "Timed out waiting for the authorization callback."; return null; }
            ctx = await awaitTask;

            var query = ctx.Request.Url!.Query;
            var qs = System.Web.HttpUtility.ParseQueryString(query);
            if (qs["state"] != state || qs["code"] is not { Length: > 0 } code)
            {
                LastError = string.IsNullOrEmpty(qs["error"]) ? "Callback state mismatch." : "Authorization page returned an error: " + qs["error"];
                await Respond(ctx, LastError!);
                return null;
            }
            await Respond(ctx, "Authorization complete. You can close this window and return to JuniGrid.");
        }
        catch (Exception ex) { LastError = ex.Message; return null; }
        finally
        {
            try { listener.Stop(); } catch { /* already stopped */ }
        }

        try
        {
            using var http = new HttpClient();
            var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = CallbackUrl,
                ["code_verifier"] = verifier,
            });
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!resp.IsSuccessStatusCode || !root.TryGetProperty("access_token", out var at))
            {
                LastError = root.TryGetProperty("error_description", out var ed) ? ed.GetString() : "Token exchange failed (HTTP " + (int)resp.StatusCode + ").";
                AppLog.Warn("NOAuth", "Token exchange failed: " + body[..Math.Min(body.Length, 200)]);
                return null;
            }
            AppLog.Warn("NOAuth", "OAuth2 login succeeded, access_token received.");
            return at.GetString();
        }
        catch (Exception ex) { LastError = ex.Message; return null; }
    }

    private static async Task Respond(HttpListenerContext ctx, string message)
    {
        var html = $"<!doctype html><meta charset=\"utf-8\"><title>JuniGrid</title>" +
                   $"<body style=\"font-family:sans-serif;text-align:center;padding-top:80px;color:#1e1e1e\"><h2>JuniGrid</h2><p>{WebUtility.HtmlEncode(message)}</p>";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
