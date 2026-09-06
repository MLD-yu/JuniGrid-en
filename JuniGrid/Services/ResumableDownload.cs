using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace JuniGrid.Services;

/// <summary>
/// v1.07: unified resumable streaming downloader. Previously both the Nexus and SMAPI download paths were raw streams:
/// a dropped connection mid-transfer (common on Nexus's free CDN) failed the whole task or restarted from 0 — the root cause of
/// "downloads jumping from ~3% back to 0% and starting over". Now a mid-transfer failure automatically
/// resumes with Range: bytes=written-, retrying up to maxAttempts-1 times; it only truly starts from 0
/// when the server does not support Range (returns 200 instead of 206).
/// Progress reporting is uniformly throttled to 0.4s (previously every 80KB chunk invoked a callback, hammering the UI thread on large files).
/// </summary>
public static class ResumableDownload
{
    public static async Task RunAsync(HttpClient http, string url, string destPath,
        Action<string, double?, double?> report, int maxAttempts = 5, CancellationToken ct = default,
        IEnumerable<string>? fallbackUrls = null)
    {
        // v1.08: mirror candidates — when the direct connection fails and no bytes have been written yet, switch to the next candidate
        // immediately instead of exhausting all retries on a dead link (the old logic burned 4 retries ≈ 80 idle seconds before trying a mirror).
        // With partial data already on disk, prefer resuming from the current host (candidate hosts serve identical bytes, and switching hosts mid-resume is always possible).
        var candidates = new List<string> { url };
        if (fallbackUrls is not null) candidates.AddRange(fallbackUrls);
        var candIndex = 0;

        long written = 0, totalBytes = 0;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, candidates[candIndex]);
                if (written > 0)
                    req.Headers.Range = new RangeHeaderValue(written, null);
                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                // v1.1.2: 4xx (except 408 request timeout / 429 rate limit) is a permanent error — 404 dead link, 403 no permission;
                // retrying all 5 times just wastes 15+ seconds, so hand the failure to the task center immediately
                if (!res.IsSuccessStatusCode && (int)res.StatusCode < 500
                    && (int)res.StatusCode != 408 && (int)res.StatusCode != 429)
                    throw new PermanentDownloadException($"HTTP {(int)res.StatusCode} (dead link or no permission, giving up retries)");
                res.EnsureSuccessStatusCode();

                // 206 = resume succeeded, keep writing; 200 = the server gives no resume point (or a fresh download) → write from the start
                var resumed = written > 0 && res.StatusCode == HttpStatusCode.PartialContent;
                if (!resumed)
                {
                    written = 0;
                    totalBytes = res.Content.Headers.ContentLength ?? 0;
                }
                else if (res.Content.Headers.ContentRange?.Length is long len)
                {
                    totalBytes = len;
                }

                await using var src = await res.Content.ReadAsStreamAsync();
                await using var dst = new FileStream(destPath, resumed ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                var lastReport = DateTime.UtcNow;
                var lastWritten = written;
                while (true)
                {
                    int read = await src.ReadAsync(buffer, ct);
                    if (read == 0) break;
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;

                    // v1.08.2: the throttle must wrap report itself — the old code applied the
                    // 1s threshold only to the speed calculation while report still fired once per 80KB chunk; a 12MB installer meant
                    // 150+ InvokeAsync calls flooding the UI thread (the root cause of the whole UI freezing during downloads),
                    // and every entry went into the task log, where >200 entries push the earlier backup/extract lines out of view.
                    var done = totalBytes > 0 && written >= totalBytes;
                    if (done || DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(1.0))
                    {
                        var percent = totalBytes <= 0 ? 0.0 : Math.Min(100.0, written * 100.0 / totalBytes);
                        var speed = (written - lastWritten)
                                    / Math.Max((DateTime.UtcNow - lastReport).TotalSeconds, 0.001)
                                    / 1024.0 / 1024.0;
                        lastReport = DateTime.UtcNow;
                        lastWritten = written;
                        report($"Downloading… {FormatBytes(written)} / {FormatBytes(totalBytes)}", percent, speed);
                    }
                }
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts
                && ex is not OperationCanceledException
                && ex is not PermanentDownloadException)
            {
                var pct = totalBytes > 0 ? Math.Min(99.0, written * 100.0 / totalBytes) : 0.0;
                if (written == 0 && candIndex < candidates.Count - 1)
                {
                    // not a single byte downloaded (connection unreachable) → switch mirrors immediately instead of waiting for retries to run out
                    candIndex++;
                    report($"Direct download failed, switching to mirror ({candIndex}/{candidates.Count - 1})…", 0, 0);
                    await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                }
                else
                {
                    report($"Connection lost ({ex.Message}), resuming from {FormatBytes(written)} (retry {attempt}/{maxAttempts - 1})…", pct, 0);
                    await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
                }
            }
        }
    }

    /// <summary>v1.1.2: permanent download errors such as 4xx — retrying is pointless, fail immediately (the task center shows the reason)</summary>
    public sealed class PermanentDownloadException(string message) : Exception(message);

    public static string FormatBytes(long bytes)
    {
        double value = bytes;
        string[] units = { "B", "KB", "MB", "GB" };
        int i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        return value.ToString(i == 0 ? "F0" : "F1") + " " + units[i];
    }
}
