using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace JuniGrid.Services;

/// <summary>
/// v1.07: unified resumable streaming downloader. Previously both the Nexus and SMAPI download paths were bare
/// streaming downloads — a mid-transfer disconnect (common on Nexus's free CDN) failed the whole task or restarted
/// from 0 — the root cause of "download jumps from ~3% back to 0% and starts over". Now a mid-transfer failure
/// automatically resumes with Range: bytes=written-, retrying up to maxAttempts-1 times; it only truly restarts
/// from 0 when the server doesn't support Range (returns 200 instead of 206).
/// Progress reporting is throttled to 0.4s (previously every 80KB chunk triggered a callback, flooding the UI thread on large files).
/// </summary>
public static class ResumableDownload
{
    public static async Task RunAsync(HttpClient http, string url, string destPath,
        Action<string, double?, double?> report, int maxAttempts = 5, CancellationToken ct = default)
    {
        long written = 0, totalBytes = 0;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (written > 0)
                    req.Headers.Range = new RangeHeaderValue(written, null);
                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();

                // 206 = resumed successfully, keep writing; 200 = server doesn't honor ranges (or a fresh download) → write from the start
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

                    var percent = totalBytes <= 0 ? 0.0 : Math.Min(100.0, written * 100.0 / totalBytes);
                    double? speed = null;
                    if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(0.4)
                        || (totalBytes > 0 && written >= totalBytes))
                    {
                        speed = (written - lastWritten) / (DateTime.UtcNow - lastReport).TotalSeconds / 1024.0 / 1024.0;
                        lastReport = DateTime.UtcNow;
                        lastWritten = written;
                    }
                    report($"Downloading… {FormatBytes(written)} / {FormatBytes(totalBytes)}", percent, speed);
                }
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && ex is not OperationCanceledException)
            {
                var pct = totalBytes > 0 ? Math.Min(99.0, written * 100.0 / totalBytes) : 0.0;
                report($"Connection lost ({ex.Message}), resuming from {FormatBytes(written)} (retry {attempt}/{maxAttempts - 1})…", pct, 0);
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
        }
    }

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
