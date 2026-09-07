using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>
/// Nexus Mods OAuth2 sign-in — authorization-code flow with PKCE, per the official
/// OAuth2 guide (https://modding.wiki/en/api/oauth2-guide).
/// Flow: open the system browser on users.nexusmods.com/oauth/authorize → the user signs in
/// and authorizes → Nexus redirects to the registered loopback callback → the temporary
/// local listener captures the single callback and shuts down → the code is exchanged for
/// access/refresh tokens at /oauth/token → API v1 calls attach "Authorization: Bearer …".
/// This is the only sign-in path in the app: personal API keys are never used or requested
/// (Nexus AUP requirement for distributed applications).
/// The ClientId is assigned by Nexus Mods together with the application registration; until it
/// is issued the sign-in button reports "pending registration" (same placeholder approach as
/// the SSO application slug had). Tokens persist in the local config file on this machine only.
/// </summary>
public sealed class NexusOAuthService
{
    /// <summary>OAuth2 client id — filled in once Nexus Mods issues the application registration.</summary>
    public const string ClientId = "";

    private const string AuthorizeUrl = "https://users.nexusmods.com/oauth/authorize";
    private const string TokenUrl = "https://users.nexusmods.com/oauth/token";
    private const int CallbackPort = 49162;   // registered callback port
    public const string CallbackUrl = "http://localhost:49162/auth/callback";
    private const int LoginTimeoutMinutes = 5;

    public string? LastError { get; private set; }

    private readonly ConfigService _cfg;
    private static readonly HttpClient Http = CreateClient();

    public NexusOAuthService(ConfigService cfg) => _cfg = cfg;

    private static HttpClient CreateClient()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JuniGrid-Launcher");
        h.Timeout = TimeSpan.FromSeconds(20);
        return h;
    }

    /// <summary>True when an access token is present (persisted session restored at startup).</summary>
    public bool IsSignedIn => !string.IsNullOrEmpty(NexusService.BearerToken);

    /// <summary>Startup restore: load the persisted tokens, refresh them when expired, and attach
    /// the access token to NexusService. Never throws — a failed restore just means logged out.</summary>
    public void RestoreSession()
    {
        var c = _cfg.Current;
        if (string.IsNullOrEmpty(c.NexusRefreshToken)) return;
        NexusService.BearerToken = c.NexusAccessToken;
        if (TokenExpired(c)) _ = RefreshAsync();   // background refresh; requests meanwhile may 401 once and degrade gracefully
    }

    private static bool TokenExpired(JuniGridConfig c) =>
        c.NexusTokenExpiresAt is null || c.NexusTokenExpiresAt <= DateTime.Now.AddMinutes(5);

    /// <summary>True when the stored token is (near) expiry and a refresh is due.</summary>
    public bool NeedsRefresh => TokenExpired(_cfg.Current);

    /// <summary>Proactively refreshes an expired access token. Returns false when the session is gone
    /// (a 4xx on refresh means the user revoked the app — per the guide, treat as logged out).</summary>
    public async Task<bool> RefreshAsync()
    {
        LastError = null;
        var c = _cfg.Current;
        var rt = c.NexusRefreshToken;
        if (string.IsNullOrEmpty(ClientId) || string.IsNullOrEmpty(rt)) return false;
        try
        {
            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = rt,
                ["client_id"] = ClientId,
            });
            using var res = await Http.PostAsync(TokenUrl, body);
            if ((int)res.StatusCode is >= 400 and < 500)
            {
                // 4xx = the grant is gone (user revoked the app / refresh token expired) — treat as logged out.
                // 5xx/network failures keep the session; the next attempt may succeed.
                Logout();
                return false;
            }
            if (!res.IsSuccessStatusCode) return false;
            SaveTokens(await res.Content.ReadAsStringAsync());
            return NexusService.BearerToken is not null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>Runs the full browser authorization. Returns the signed-in user, or null on
    /// failure / cancellation (LastError carries the reason).</summary>
    public async Task<NexusUser?> LoginAsync(CancellationToken ct = default)
    {
        LastError = null;
        if (string.IsNullOrEmpty(ClientId))
        {
            LastError = "OAuth2 registration is pending at Nexus Mods — sign-in will open automatically once the client id is issued";
            return null;
        }

        // PKCE (S256): cryptographically random verifier (≥43 chars), challenge = BASE64URL(SHA256(verifier))
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = new TcpListener(IPAddress.Loopback, CallbackPort);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            LastError = "Could not open the local callback port " + CallbackPort + ": " + ex.Message;
            return null;
        }

        try
        {
            var auth = AuthorizeUrl + "?client_id=" + Uri.EscapeDataString(ClientId)
                     + "&redirect_uri=" + Uri.EscapeDataString(CallbackUrl)
                     + "&response_type=code&scope="   // empty scope per the official guide (user info comes from validate.json, not the JWT)
                     + "&state=" + Uri.EscapeDataString(state)
                     + "&code_challenge=" + Uri.EscapeDataString(challenge)
                     + "&code_challenge_method=S256";
            AppLog.Warn("NOAUTH", "Opening browser for OAuth2 authorization");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(auth) { UseShellExecute = true });

            // Capture the single redirect: parse GET /auth/callback?code=…&state=…, answer once, shut down.
            var (code, gotState, error) = await CaptureCallbackAsync(listener, ct);
            if (!string.IsNullOrEmpty(error)) { LastError = error; return null; }
            if (string.IsNullOrEmpty(code)) { LastError = "Authorization was not completed"; return null; }
            if (gotState != state) { LastError = "State mismatch — the callback did not come from this sign-in attempt"; return null; }

            using var tokenBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = ClientId,
                ["redirect_uri"] = CallbackUrl,
                ["code_verifier"] = verifier,
            });
            using var tokenRes = await Http.PostAsync(TokenUrl, tokenBody, ct);
            if (!tokenRes.IsSuccessStatusCode)
            {
                LastError = "Token exchange failed: HTTP " + (int)tokenRes.StatusCode;
                return null;
            }
            SaveTokens(await tokenRes.Content.ReadAsStringAsync());
            if (NexusService.BearerToken is null) { LastError = "Token response contained no access_token"; return null; }

            var user = await _nexus.ValidateAsync();
            if (user is null) { LastError = "Signed in, but fetching the account info failed"; return null; }

            var c = _cfg.Current;
            c.NexusUserName = user.Name ?? "";
            c.NexusUserEmail = user.Email ?? "";
            c.NexusProfileUrl = user.ProfileUrl ?? "";
            c.NexusIsPremium = user.IsPremium;
            _cfg.Save(c);
            return user;
        }
        catch (OperationCanceledException)
        {
            LastError = "Cancelled";
            return null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    private static readonly NexusService _nexus = new();   // stateless API facade — only used for the post-login validate call

    /// <summary>Signs out: clears the persisted tokens and the attached Bearer token.
    /// (Server-side revocation is available to the user at users.nexusmods.com/oauth/authorized_applications.)</summary>
    public void Logout()
    {
        NexusService.BearerToken = null;
        var c = _cfg.Current;
        c.NexusAccessToken = "";
        c.NexusRefreshToken = "";
        c.NexusTokenExpiresAt = null;
        c.NexusUserName = "";
        c.NexusUserEmail = "";
        c.NexusProfileUrl = "";
        c.NexusIsPremium = false;
        c.NexusAvatarDataUri = "";
        _cfg.Save(c);
    }

    private void SaveTokens(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var access = r.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        var refresh = r.TryGetProperty("refresh_token", out var rf) ? rf.GetString() : null;
        var expiresIn = r.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number ? ei.GetDouble() : 0;
        if (string.IsNullOrEmpty(access)) return;
        var c = _cfg.Current;
        c.NexusAccessToken = access;
        if (!string.IsNullOrEmpty(refresh)) c.NexusRefreshToken = refresh;   // refresh may be omitted on re-grants — keep the old one then
        c.NexusTokenExpiresAt = DateTime.Now.AddSeconds(expiresIn > 0 ? expiresIn - 60 : 3600);
        _cfg.Save(c);
        NexusService.BearerToken = access;
    }

    /// <summary>Accepts exactly one HTTP request on the loopback listener and returns the OAuth
    /// callback parameters. A raw TcpListener is used (no URL ACL / admin rights needed) and the
    /// listener is disposed right after, per the "single callback, then closed" behavior the
    /// callback registration describes.</summary>
    private static async Task<(string? Code, string? State, string? Error)> CaptureCallbackAsync(
        TcpListener listener, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(LoginTimeoutMinutes));
        using var client = await listener.AcceptTcpClientAsync(timeout.Token);
        using var stream = client.GetStream();
        var buffer = new byte[8192];
        var sb = new StringBuilder();
        while (!sb.ToString().Contains("\r\n\r\n"))
        {
            var n = await stream.ReadAsync(buffer, timeout.Token);
            if (n == 0) break;
            sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
            if (sb.Length > 65536) break;   // defensive: a callback request is tiny
        }
        try
        {
            // Minimal valid response so the browser tab shows a friendly "you can close this window"
            var page = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>JuniGrid</title></head>" +
                       "<body style=\"font-family:system-ui;background:#1b1d22;color:#e7e9ee;display:flex;" +
                       "align-items:center;justify-content:center;height:100vh;margin:0\">" +
                       "<div style=\"text-align:center\"><div style=\"font-size:22px;font-weight:600\">" +
                       (sb.ToString().Contains("error=") ? "Authorization failed" : "Authorization complete") +
                       "</div><div style=\"opacity:.6;margin-top:6px\">You can close this window and return to JuniGrid.</div></div></body></html>";
            var head = sb.ToString();
            var line = head.Split('\n')[0];   // e.g. "GET /auth/callback?code=…&state=… HTTP/1.1"
            var target = line.Split(' ')[^2] is { } t && t.StartsWith('/') ? t : "/";
            var resp = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\nContent-Length: "
                     + Encoding.UTF8.GetByteCount(page) + "\r\n\r\n" + page;
            await stream.WriteAsync(Encoding.UTF8.GetBytes(resp), timeout.Token);

            var query = new Uri("http://localhost" + target).Query;
            var args = System.Web.HttpUtility.ParseQueryString(query);
            var err = args["error"];
            if (!string.IsNullOrEmpty(err)) return (null, null, "Authorization page returned: " + err);
            return (args["code"], args["state"], null);
        }
        catch (Exception ex)
        {
            return (null, null, "Failed to read the callback: " + ex.Message);
        }
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
