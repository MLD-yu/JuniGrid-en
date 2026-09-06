using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace JuniGrid.Services;

/// <summary>
/// v1.08: local cache for Nexus images — the image CDN (staticdelivery.nexusmods.com) can be very slow to
/// reach directly, and WebView2 loading remote URLs directly leaves list/Nexus page images blank for a long time.
/// This downloads each cover once in the background to disk (LocalAppData/JuniGrid/covers/&lt;sha1&gt;.img),
/// after which <see cref="Get"/> returns a local data URI and image display no longer depends on the Nexus network.
/// A failed download is only abandoned for the current session (no repeated retries slowing rendering); it retries on the next launch.
/// </summary>
public sealed class CoverCacheService
{
    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JuniGrid-Launcher");
        h.Timeout = TimeSpan.FromSeconds(20);
        return h;
    }

    private static string CacheDir => Path.Combine(StoragePaths.LocalAppDataDir, "covers");

    /// <summary>url → data URI (null = download failed for this session, no more retries).
    /// v1.1.6: capped the memory cache entry count — it used to be unbounded, and after browsing a few dozen
    /// detail pages the base64 large images (33% bigger than the originals) could permanently occupy hundreds of MB;
    /// disk copies already exist, so eviction costs nothing.</summary>
    private const int MemoryCap = 256;
    private readonly ConcurrentDictionary<string, string?> _memory = new();
    private readonly ConcurrentQueue<string> _memoryOrder = new();
    private readonly ConcurrentDictionary<string, byte> _downloading = new();
    /// <summary>Concurrency gate: the link to the Nexus CDN is fragile, so image downloads are capped at 8 concurrent requests.</summary>
    private static readonly SemaphoreSlim DownloadGate = new(8, 8);

    /// <summary>Raised whenever a new image finishes downloading (the UI subscribes to refresh rendering).
    /// v1.08: debounced into 500ms batches — on first page load dozens of images download in parallel, and
    /// re-rendering the whole page for each one would lock it up; merged to at most one refresh per 500ms.</summary>
    public event Action? Changed;
    private int _notifyPending;

    private void NotifyChanged()
    {
        if (Interlocked.Exchange(ref _notifyPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            Interlocked.Exchange(ref _notifyPending, 0);
            Changed?.Invoke();
        });
    }

    /// <summary>
    /// Called during rendering (small list icons): returns a 240px thumbnail data URI; when not cached →
    /// triggers a background download and returns null.
    /// Non-http URLs (local paths/data URIs) are returned as-is. For large images use <see cref="GetFull"/>.
    /// </summary>
    public string? Get(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;
        if (_memory.TryGetValue(url, out var cached)) return cached;
        _ = DownloadAsync(url);
        return null;
    }

    /// <summary>v1.08.2: large-image scenarios (Nexus page banner, mod detail cover) — returns the original image
    /// as a data URI, cached separately from thumbnails (<orig-sha>.img vs w240-<sha>.img). Falls back to the thumbnail on failure.</summary>
    public string? GetFull(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;
        if (_fullMemory.TryGetValue(url, out var cached)) return cached;
        _ = DownloadFullAsync(url);
        return null;
    }

    /// <summary>url → original-image data URI.</summary>
    private readonly ConcurrentDictionary<string, string?> _fullMemory = new();
    private readonly ConcurrentQueue<string> _fullMemoryOrder = new();
    private readonly ConcurrentDictionary<string, byte> _downloadingFull = new();

    /// <summary>Writes to the memory cache with FIFO eviction (drops the earliest entry once over the cap).</summary>
    private static void Remember(
        ConcurrentDictionary<string, string?> store, ConcurrentQueue<string> order,
        string url, string? value)
    {
        store[url] = value;
        order.Enqueue(url);
        while (store.Count > MemoryCap && order.TryDequeue(out var oldest))
            store.TryRemove(oldest, out _);
    }

    private async Task DownloadFullAsync(string url)
    {
        if (!_downloadingFull.TryAdd(url, 0)) return;
        try
        {
            Directory.CreateDirectory(CacheDir);
            var file = Path.Combine(CacheDir, Sha1(url) + ".img");
            byte[] bytes;
            if (File.Exists(file)) bytes = await File.ReadAllBytesAsync(file);
            else
            {
                await DownloadGate.WaitAsync();
                try { bytes = await Http.GetByteArrayAsync(url); }
                finally { DownloadGate.Release(); }
                if (bytes.Length == 0) throw new InvalidOperationException("Empty image");
                await File.WriteAllBytesAsync(file, bytes);
            }
            Remember(_fullMemory, _fullMemoryOrder, url, ToDataUri(bytes));
            NotifyChanged();
        }
        catch
        {
            // Original failed → fall back to the thumbnail (something is better than blurry/blank)
            _ = await Task.Run(async () => { await DownloadAsync(url); return true; });
            Remember(_fullMemory, _fullMemoryOrder, url, _memory.TryGetValue(url, out var t) ? t : null);
        }
        finally { _downloadingFull.TryRemove(url, out _); }
    }

    /// <summary>
    /// v1.08: batch prewarm — starts downloading known covers in the background as soon as a page opens, so
    /// they are ready locally by the time the user scrolls to them (first-load responsiveness: a head start + download only once).
    /// Already-cached URLs short-circuit immediately; no duplicate requests are sent.
    /// </summary>
    public void Prewarm(IEnumerable<string?> urls)
    {
        foreach (var url in urls)
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            if (_memory.ContainsKey(url)) continue;
            _ = DownloadAsync(url);
        }
    }

    private async Task DownloadAsync(string url)
    {
        if (!_downloading.TryAdd(url, 0)) return;   // already downloading
        try
        {
            Directory.CreateDirectory(CacheDir);

            // v1.08: thumbnail on download — prefer the weserv proxy to squeeze images down to 240px (~10 KB,
            // an order of magnitude less rendering/decode cost for lists); fall back to the direct original when the proxy fails.
            var file = Path.Combine(CacheDir, "w240-" + Sha1(url) + ".img");
            byte[] bytes;
            if (File.Exists(file))
            {
                bytes = await File.ReadAllBytesAsync(file);
            }
            else
            {
                await DownloadGate.WaitAsync();
                try
                {
                    var thumbUrl = "https://images.weserv.nl/?url=" +
                                   Uri.EscapeDataString(url) + "&w=240&output=jpg";
                    try { bytes = await Http.GetByteArrayAsync(thumbUrl); }
                    catch { bytes = await Http.GetByteArrayAsync(url); }   // fall back to the original image
                }
                finally { DownloadGate.Release(); }
                if (bytes.Length == 0) throw new InvalidOperationException("Empty image");
                await File.WriteAllBytesAsync(file, bytes);
            }

            Remember(_memory, _memoryOrder, url, ToDataUri(bytes));
            NotifyChanged();
        }
        catch
        {
            Remember(_memory, _memoryOrder, url, null);   // give up for this session; the placeholder block covers it
        }
        finally
        {
            _downloading.TryRemove(url, out _);
        }
    }

    private static string Sha1(string s)
    {
        using var sha = SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
    }

    /// <summary>Detects the image type from the file header (Nexus covers are mostly webp/jpeg/png/gif).</summary>
    private static string ToDataUri(byte[] b)
    {
        string type = "image/jpeg";
        if (b.Length > 12 && b[0] == 0x89 && b[1] == 0x50) type = "image/png";
        else if (b.Length > 3 && b[0] == 0x47 && b[1] == 0x49) type = "image/gif";
        else if (b.Length > 12 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) type = "image/webp";
        return $"data:{type};base64,{Convert.ToBase64String(b)}";
    }
}
