using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// Nexus Mods Public API v1 (https://api.nexusmods.com).
///  - Version checks work with any free personal API key.
///  - Direct download links are Premium-only (Nexus policy): free accounts
///    get HTTP 403 on download_link — surfaced as NeedsPremium.
/// Rate limits: ~100 req/day free, 2500/day premium (X-RL-* headers).
/// </summary>
public sealed class NexusService
{
    private const string Base = "https://api.nexusmods.com/v1/games/stardewvalley";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JuniGrid-Launcher");
        // App identification headers required by the Nexus API terms of use
        h.DefaultRequestHeaders.TryAddWithoutValidation("Application-Name", "JuniGrid");
        h.DefaultRequestHeaders.TryAddWithoutValidation("Application-Version", "0.2.0");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        h.Timeout = TimeSpan.FromSeconds(15);   // v1.06.8: faster update checks — slow requests fail fast at 15s instead of stalling the whole batch
        return h;
    }

    private static HttpRequestMessage Req(string? apiKey, string url)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(apiKey))
            r.Headers.TryAddWithoutValidation("apikey", apiKey);
        return r;
    }

    /// <summary>Mod metadata (name + current version + cover). null on error.</summary>
    /// <summary>v0.46.0: fetches the game's official category table (category_id → name); the caller caches it in config.</summary>
    public async Task<Dictionary<int, string>?> GetCategoriesAsync(string apiKey)
    {
        using var res = await Http.SendAsync(Req(apiKey, Base + ".json"));
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("categories", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        var dict = new Dictionary<int, string>();
        foreach (var c in arr.EnumerateArray())
        {
            var id = c.TryGetProperty("category_id", out var e1) && e1.ValueKind == JsonValueKind.Number ? e1.GetInt32()
                   : c.TryGetProperty("id", out var e2) && e2.ValueKind == JsonValueKind.Number ? e2.GetInt32() : -1;
            var name = c.TryGetProperty("name", out var ne) && ne.ValueKind == JsonValueKind.String ? ne.GetString() : null;
            if (id >= 0 && !string.IsNullOrWhiteSpace(name)) dict[id] = name;
        }
        return dict;
    }

    public async Task<NexusModInfo?> GetModAsync(string apiKey, int modId)
    {
        using var res = await Http.SendAsync(Req(apiKey, $"{Base}/mods/{modId}.json"));
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        return new NexusModInfo(
            modId,
            GetStr(root, "name"),
            GetStr(root, "version"),
            GetStr(root, "picture_url"),
            root.TryGetProperty("category_id", out var cid) && cid.ValueKind == JsonValueKind.Number
                ? cid.GetInt32() : (int?)null);
    }

    /// <summary>Full mod detail for the in-app detail page (incl. HTML description + cover).</summary>
    public async Task<NexusModDetail?> GetModDetailAsync(string apiKey, int modId)
    {
        using var res = await Http.SendAsync(Req(apiKey, $"{Base}/mods/{modId}.json"));
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        var updatedTs = GetInt64(root, "updated_timestamp");
        var endorsements = GetInt64(root, "endorsement_count");
        if (endorsements == 0) endorsements = GetInt64(root, "endorsements");

        var deps = new List<NexusModDependency>();
        if (root.TryGetProperty("dependencies", out var depArr) && depArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in depArr.EnumerateArray())
            {
                var did = d.TryGetProperty("mod_id", out var di) && di.ValueKind == JsonValueKind.Number
                    ? di.GetInt32() : 0;
                var dname = GetStr(d, "name");
                var davail = GetStr(d, "availability");
                if (did > 0)
                    deps.Add(new NexusModDependency(did, dname, davail));
            }
        }

        // Gallery: primary picture first, then the images array in order.
        // In Nexus v1, each images entry may use picture_url / original_url / thumbnail_url;
        // prefer by sharpness: original_url > picture_url > thumbnail_url.
        var primary = GetStr(root, "picture_url");
        var images = new List<string>();
        if (!string.IsNullOrWhiteSpace(primary)) images.Add(primary);
        if (root.TryGetProperty("images", out var imgArr) && imgArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var img in imgArr.EnumerateArray())
            {
                var u = GetStr(img, "original_url");
                if (string.IsNullOrEmpty(u)) u = GetStr(img, "picture_url");
                if (string.IsNullOrEmpty(u)) u = GetStr(img, "thumbnail_url");
                if (!string.IsNullOrEmpty(u) && !images.Contains(u, StringComparer.OrdinalIgnoreCase))
                    images.Add(u);
            }
        }
        if (images.Count == 0 && !string.IsNullOrEmpty(primary)) images.Add(primary);

        return new NexusModDetail(
            modId,
            GetStr(root, "name"),
            GetStr(root, "author"),
            GetStr(root, "version"),
            GetStr(root, "summary"),
            SanitizeHtml(GetStr(root, "description")),
            primary,
            GetInt64(root, "mod_downloads"),
            endorsements,
            updatedTs > 0
                ? DateTimeOffset.FromUnixTimeSeconds(updatedTs).LocalDateTime.ToString("yyyy-MM-dd")
                : "",
            deps,
            images);
    }

    // ---- JSON helpers ----
    private static string GetStr(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static long GetInt64(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var n) ? n : 0;

    /// <summary>
    /// Nexus's description is BBCode-style ([b]…[/b], [url=…]…[/url], [list]…[/list]),
    /// not HTML — a raw (MarkupString) would render those as literal text.
    /// This converts common BBCode into real HTML so the detail page can render bold/links/lists/colors.
    /// </summary>
    private static string SanitizeHtml(string html)
{
    if (string.IsNullOrEmpty(html)) return "";
    var s = html;
    // First strip dangerous scripts (defensive)
    s = Regex.Replace(s, "<script.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    // If the block is HTML-escaped entities (&lt;), decode once first
    if (s.Contains("&lt;", StringComparison.OrdinalIgnoreCase))
        s = System.Net.WebUtility.HtmlDecode(s);

    // ── BBCode → HTML ──
    s = Regex.Replace(s, @"\[b\](.*?)\[/b\]", "<b>$1</b>", RegexOptions.Singleline);
    s = Regex.Replace(s, @"\[i\](.*?)\[/i\]", "<i>$1</i>", RegexOptions.Singleline);
    s = Regex.Replace(s, @"\[u\](.*?)\[/u\]", "<u>$1</u>", RegexOptions.Singleline);
    s = Regex.Replace(s, @"\[s\](.*?)\[/s\]", "<s>$1</s>", RegexOptions.Singleline);
    // Color / font size (Nexus size is 1–7 tiers, mapped to readable pixel sizes)
    s = Regex.Replace(s, @"\[color\s*=\s*([^\]]+)\](.*?)\[/color\]",
        "<span style=\"color:$1\">$2</span>", RegexOptions.Singleline);
    s = Regex.Replace(s, @"\[size\s*=\s*([^\]]+)\](?<c>.*?)\[/size\]", m =>
    {
        var n = int.TryParse(m.Groups[1].Value.Trim(), out var v) ? v : 3;
        var px = n switch
        {
            <= 1 => 11, 2 => 12, 3 => 14, 4 => 17, 5 => 20,
            6 => 24, _ => 28
        };
        return $"<span style=\"font-size:{px}px\">{m.Groups["c"].Value}</span>";
    }, RegexOptions.Singleline);
    // Links: [url=external]text[/url] or [url]external[/url]
    s = Regex.Replace(s, @"\[url\s*=\s*[^\]]+?\](.*?)\[/url\]", m =>
    {
        var tag = m.Value;
        var url = Regex.Match(tag, @"\[url\s*=\s*(?<u>[^\]]+?)\]").Groups["u"].Value.Trim('"', '\'', ' ');
        var text = Regex.Replace(m.Value, @"^\[url\s*=[^\]]*\]|\[/url\]$", "");
        return $"<a href=\"{System.Net.WebUtility.HtmlEncode(url)}\" target=\"_blank\" rel=\"noopener\">{text}</a>";
    }, RegexOptions.Singleline);
    s = Regex.Replace(s, @"\[url\](.*?)\[/url\]",
        "<a href=\"$1\" target=\"_blank\" rel=\"noopener\">$1</a>", RegexOptions.Singleline);
    // Images
    s = Regex.Replace(s, @"\[img\s*(?:=\s*([^\]]+))?\](.*?)\[/img\]",
        "<img src=\"$2\" alt=\"\" loading=\"lazy\" class=\"jg-desc-img\"/>", RegexOptions.Singleline);
    // v0.56.0: HTML <img> tags also get the class uniformly, so Flip can zoom them
    s = Regex.Replace(s, @"<img\s+([^>]*?)src=""([^""]+)""([^>]*?)>",
        @"<img src=""$2"" alt="""" loading=""lazy"" class=""jg-desc-img""/>", RegexOptions.IgnoreCase);
    // v0.57.0: bare image URLs (wrapped in neither [img] nor <img>) are rendered as images too.
    // A negative lookbehind excludes URLs already inside generated tag attributes (quotes before href="/src=", > etc.).
    s = Regex.Replace(s, @"(?<![""'>=])(https?://[^\s<""'\]\[]+?\.(?:png|jpe?g|gif|webp)(?:\?[a-zA-Z0-9=&_%\-]*)?)",
        "<img src=\"$1\" alt=\"\" loading=\"lazy\" class=\"jg-desc-img\"/>", RegexOptions.IgnoreCase);
    // Quotes / code
    s = Regex.Replace(s, @"\[quote\](.*?)\[/quote\]",
        "<blockquote>$1</blockquote>", RegexOptions.Singleline);
    s = Regex.Replace(s, @"\[code\](.*?)\[/code\]",
        "<pre>$1</pre>", RegexOptions.Singleline);

    // Lists and list items: a stack tracks open <ul>/<ol> so [/list] closes correctly and nesting is preserved
    var listStack = new Stack<string>();
    s = Regex.Replace(s, @"\[(/)?list(=([^\]]+))?\]", match =>
    {
        if (match.Value == "[/list]")
        {
            if (listStack.Count == 0) return "";
            return "</" + listStack.Pop() + ">";
        }
        var kindAttr = match.Groups[2].Success ? match.Groups[3].Value.Trim() : "";
        var tag = (!string.IsNullOrEmpty(kindAttr) && kindAttr is "1" or "a" or "o") ? "ol" : "ul";
        listStack.Push(tag);
        return "<" + tag + ">";
    }, RegexOptions.IgnoreCase);
    s = Regex.Replace(s, @"\[item\]", "<li>", RegexOptions.IgnoreCase);
    s = Regex.Replace(s, @"\[\*\]", "<li>", RegexOptions.IgnoreCase);

    // Common subset: font, horizontal rule, float — keep as helper styles
    s = Regex.Replace(s, @"\[font\s*=\s*([^\]]+)\](.*?)\[/font\]",
        "<span style=\"font-family:$1\">$2</span>", RegexOptions.Singleline);
    s = Regex.Replace(s, @"\[hr\]", "<hr/>", RegexOptions.IgnoreCase);

    // Remaining unknown BBCode is stripped as plain text to avoid bracket syntax leaking through.
    // v0.58.0: the list-item closing tags [/ *] / [/*] must also be removed (* is not a letter, the old rule missed them)
    s = Regex.Replace(s, @"\[/?\*\]", "", RegexOptions.Singleline);
    s = Regex.Replace(s, @"\[/?[a-zA-Z][a-zA-Z0-9_=:,/\.# -]*?\]", "", RegexOptions.Singleline);

    return s;
}

    /// <summary>Newest MAIN file (fallback: newest file of any category).</summary>
    public async Task<NexusFileInfo?> GetLatestMainFileAsync(string apiKey, int modId)
    {
        using var res = await Http.SendAsync(Req(apiKey, $"{Base}/mods/{modId}/files.json"));
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("files", out var files)) return null;

            NexusFileInfo? bestMain = null, bestAny = null;
            long bestMainTs = -1, bestAnyTs = -1;
            foreach (var f in files.EnumerateArray())
            {
                var id = f.TryGetProperty("file_id", out var fi) ? fi.GetInt64() : 0;
                var name = f.TryGetProperty("name", out var fn) ? fn.GetString() ?? "" : "";
                var ver = f.TryGetProperty("version", out var fv) ? fv.GetString() ?? "" : "";
                var cat = f.TryGetProperty("category_name", out var fc) ? fc.GetString() ?? "" : "";
                var ts = f.TryGetProperty("uploaded_timestamp", out var ft) ? ft.GetInt64() : 0;
                // v1.04.0: main file size (bytes) — used by the detail page's "Size" stat row; treated as 0 when missing (the page still shows 0 B)
                var size = f.TryGetProperty("size", out var fz) && fz.ValueKind == JsonValueKind.Number
                    && fz.TryGetInt64(out var szl) ? szl : 0;
                var info = new NexusFileInfo(id, name, ver, cat, size);
                if (ts > bestAnyTs) { bestAnyTs = ts; bestAny = info; }
                if (cat.Equals("MAIN", StringComparison.OrdinalIgnoreCase) && ts > bestMainTs)
                { bestMainTs = ts; bestMain = info; }
            }
            return bestMain ?? bestAny;
    }

    /// <summary>
    /// vNext: "fingerprint batch fast path" for update checks — one GraphQL legacyModsByDomain call
    /// fetches { modId, version, updatedAt, pictureUrl } for a batch of mods (50 per batch, no API key,
    /// same channel as covers/browse lists).
    /// Core rationale (verified in testing, 2026-09): a mod's updatedAt follows file uploads (SVE's latest
    /// MAIN file uploaded at 23:17:39, updatedAt=23:19:24) — updatedAt unchanged ⟹ file list unchanged ⟹
    /// the "latest MAIN file version" found by the previous files.json query is still valid, so the whole
    /// batch can skip per-mod detail checks.
    /// Update checks for hundreds of mods collapse from N requests to ~N/50 (after restart / past the
    /// 60-minute TTL, entering the page drops from tens of seconds to about 1 second).
    /// Returns null if any batch fails (the caller falls back to per-mod detail checks — slow but never wrong).
    /// </summary>
    public async Task<Dictionary<int, NexusModFingerprint>?> GetModFingerprintsBatchAsync(
        IEnumerable<int> modIds, string gameDomain = "stardewvalley")
    {
        try
        {
            var ids = modIds.Distinct().ToList();
            if (ids.Count == 0) return new Dictionary<int, NexusModFingerprint>();
            const int CHUNK = 50;   // same size as the browse page's FetchChunkedAsync (a batch of 50 is reliably stable)
            var result = new Dictionary<int, NexusModFingerprint>();
            // Fetch batches in parallel (even hundreds of mods means only a few concurrent POSTs — no key, no rate-limit pressure)
            var chunks = new List<Task<Dictionary<int, NexusModFingerprint>?>>();
            for (var i = 0; i < ids.Count; i += CHUNK)
            {
                var slice = ids.Skip(i).Take(CHUNK).ToList();
                chunks.Add(Task.Run(async () =>
                {
                    var idArgs = string.Join(",", slice.Select(id =>
                        "{gameDomain:\"" + gameDomain + "\", modId:" + id + "}"));
                    var d = await GraphQlAsync(
                        "{ legacyModsByDomain(ids:[" + idArgs + "]) { nodes { modId version updatedAt pictureUrl } } }");
                    if (d is null) return null;
                    var root = d.Value;
                    if (!root.TryGetProperty("legacyModsByDomain", out var lb)
                        || lb.ValueKind != JsonValueKind.Object
                        || !lb.TryGetProperty("nodes", out var nodes)
                        || nodes.ValueKind != JsonValueKind.Array)
                        return null;
                    var dict = new Dictionary<int, NexusModFingerprint>();
                    foreach (var n in nodes.EnumerateArray())
                    {
                        var mid = n.TryGetProperty("modId", out var m1) && m1.ValueKind == JsonValueKind.Number
                            ? m1.GetInt32()
                            : int.TryParse(GetStr(n, "modId"), out var p) ? p : 0;
                        if (mid <= 0) continue;
                        dict[mid] = new NexusModFingerprint(
                            mid, GetStr(n, "version"), GetStr(n, "updatedAt"), GetStr(n, "pictureUrl"));
                    }
                    return dict;
                }));
            }
            foreach (var t in chunks)
            {
                var dict = await t;
                if (dict is null) return null;
                foreach (var kv in dict) result[kv.Key] = kv.Value;
            }
            return result;
        }
        catch { return null; }
    }

    /// <summary>v0.69.0: a mod's changelogs (version → change lines). Corresponds to the website's LOGS tab → Changelogs.</summary>
    public async Task<List<NexusChangelog>?> GetChangelogsAsync(string apiKey, int modId)
    {
        using var res = await Http.SendAsync(Req(apiKey, $"{Base}/mods/{modId}/changelogs.json"));
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var list = new List<NexusChangelog>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return list;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var lines = new List<string>();
            if (prop.Value.ValueKind == JsonValueKind.Array)
                foreach (var l in prop.Value.EnumerateArray())
                    if (l.ValueKind == JsonValueKind.String) lines.Add(l.GetString() ?? "");
            list.Add(new NexusChangelog(prop.Name, lines));
        }
        return list;
    }

    /// <summary>
    /// v0.69.2: scrapes the mod's images page (?tab=images) to complete the full gallery.
    /// Root cause: the images field in v1 mods.json essentially only contains the primary image
    /// (the website's 25 images are not included); the full gallery exists only in the images page
    /// HTML. staticdelivery direct links are scraped and deduplicated by file name.
    /// Any failure returns null (the caller keeps the original primary image) — it never breaks the detail page.
    /// </summary>
    public async Task<List<string>?> GetModImagesAsync(int modId)
    {
        try
        {
            using var res = await Http.GetAsync(
                $"https://www.nexusmods.com/stardewvalley/mods/{modId}?tab=images");
            if (!res.IsSuccessStatusCode) return null;
            var html = await res.Content.ReadAsStringAsync();
            var urls = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                html, @"https://staticdelivery\.nexusmods\.com/[^""'\s\\]+?\.(?:png|jpe?g|webp)(?:\?[^""'\s\\]*)?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var u = m.Value;
                // Exclude small avatar/icon images
                if (u.Contains("/avatars/", StringComparison.OrdinalIgnoreCase)) continue;
                // Deduplicate by base URL (query string removed) — keep one copy of the same image with different query strings
                var baseU = u.Split('?')[0];
                if (!urls.Any(x => x.Split('?')[0].Equals(baseU, StringComparison.OrdinalIgnoreCase)))
                    urls.Add(u);
            }
            return urls.Count > 1 ? urls : null;
        }
        catch { return null; }
    }


    // ══════════════════════════════════════════════════════════════════
    // v0.69.5: Requirements via GraphQL v2 (fixes the v0.69.3 bug where hand-built JSON had unescaped
    // inner quotes, producing an invalid request body, HTTP 400, and a UI stuck on shimmer — now uses JsonSerializer).
    // ══════════════════════════════════════════════════════════════════
    // v0.96.0: switched to the website's own api-router endpoint — on the old api.nexusmods.com/v2,
    // modId EQUALS (numeric ID search) always returned empty, while api-router works; all other
    // fields/shape are fully compatible (the website frontend uses this route too).
    private const string GraphQlEndpoint = "https://api-router.nexusmods.com/graphql";
    /// <summary>v0.79.0: adult-content master switch — false = browse/search GraphQL appends an adultContent:false filter.
    /// Synced by ConfigService from the "Settings → Filter adult content" toggle (FilterAdultContent=true ⇒ this=false).</summary>
    public static bool IncludeAdultContent = true;
    /// <summary>v0.81.0: the totalCount reported by the server for the latest browse query (data source for the paginator's "Page x / N · X items").</summary>
    public int? LastBrowseTotalCount;
    /// <summary>v0.96.0: server-side random sort seed for the Surprise list — kept constant while paging so page order stays continuous; a new seed is drawn on "Shuffle".</summary>
    public int SurpriseSeed { get; private set; } = Random.Shared.Next();
    public void ReshuffleSurprise() => SurpriseSeed = Random.Shared.Next();
    private static readonly ConcurrentDictionary<string, int> GameIdCache = new();

    private async Task<JsonElement?> GraphQlAsync(string query)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { query });
            using var res = await Http.PostAsync(GraphQlEndpoint,
                new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            // v0.76.0: on GraphQL syntax/argument errors the response is 200 + {"errors":[...],"data":null} —
            // a data field that [exists but is null] must also be treated as failure, otherwise the parsing
            // below throws and the downgrade-retry never triggers
            // (this is the root cause of "fetch failed" whenever a time range/Trending was selected).
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
                return null;
            return data.Clone();
        }
        catch { return null; }
    }

    /// <summary>Fetches the website's Requirements (requirements/dependencies table). Returns null on failure; the UI falls back to a "view on the website" link.</summary>
    public async Task<List<NexusRequirement>?> GetModRequirementsAsync(int modId, string gameDomain = "stardewvalley")
    {
        if (!GameIdCache.TryGetValue(gameDomain, out var gameId))
        {
            var g = await GraphQlAsync("{ game(domainName:\"" + gameDomain + "\") { id } }");
            if (g is null) return null;
            var gv = g.Value;
            if (!gv.TryGetProperty("game", out var gg) || gg.ValueKind != JsonValueKind.Object) return null;
            var gidEl = gg.TryGetProperty("id", out var tmp) ? tmp : default;
            gameId = gidEl.ValueKind == JsonValueKind.Number ? gidEl.GetInt32()
                   : int.TryParse(gidEl.ToString(), out var p) ? p : 0;
            if (gameId <= 0) return null;
            GameIdCache[gameDomain] = gameId;
        }

        var d = await GraphQlAsync(
            "{ mod(gameId:\"" + gameId + "\", modId:\"" + modId + "\") { modRequirements { nexusRequirements { nodes { modName notes url modId externalRequirement } } } } }");
        if (d is null) return null;
        var root = d.Value;
        if (!root.TryGetProperty("mod", out var mod) || mod.ValueKind != JsonValueKind.Object ||
            !mod.TryGetProperty("modRequirements", out var mreq) || mreq.ValueKind != JsonValueKind.Object ||
            !mreq.TryGetProperty("nexusRequirements", out var nreq) || nreq.ValueKind != JsonValueKind.Object ||
            !nreq.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<NexusRequirement>();
        foreach (var n in nodes.EnumerateArray())
            list.Add(new NexusRequirement(
                GetStr(n, "modName"), GetStr(n, "notes"), GetStr(n, "url"),
                int.TryParse(GetStr(n, "modId"), out var rid) ? rid : 0,
                n.TryGetProperty("externalRequirement", out var ex) && ex.ValueKind == JsonValueKind.True));
        return list;
    }

    /// <summary>v0.69.7: Requirements + covers (fetch requirements first, then batch-fill pictureUrl by modId).</summary>
    public async Task<List<NexusRequirementEx>?> GetModRequirementsWithCoversAsync(int modId, string gameDomain = "stardewvalley")
    {
        var base_ = await GetModRequirementsAsync(modId, gameDomain);
        if (base_ is null) return null;
        var withId = base_.Where(r => r.ModId > 0 && !r.External).ToList();
        var covers = new Dictionary<int, string?>();
        if (withId.Count > 0)
        {
            try
            {
                var ids = string.Join(",", withId.Select(r =>
                    "{gameDomain:\"" + gameDomain + "\", modId:" + r.ModId + "}"));
                var d = await GraphQlAsync("{ legacyModsByDomain(ids:[" + ids + "]) { nodes { modId pictureUrl } } }");
                if (d is not null && d.Value.TryGetProperty("legacyModsByDomain", out var lm)
                    && lm.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                    foreach (var n in nodes.EnumerateArray())
                    {
                        var mid = n.TryGetProperty("modId", out var m1) && m1.ValueKind == JsonValueKind.Number ? m1.GetInt32()
                                : int.TryParse(GetStr(n, "modId"), out var p) ? p : 0;
                        var pic = GetStr(n, "pictureUrl");
                        if (mid > 0) covers[mid] = string.IsNullOrWhiteSpace(pic) ? null : pic;
                    }
            }
            catch { /* missing covers are non-blocking */ }
        }
        return base_.Select(r => new NexusRequirementEx(
            r.ModName, r.Notes, r.Url, r.ModId, r.External,
            covers.TryGetValue(r.ModId, out var pu) ? pu : null)).ToList();
    }

    /// <summary>v0.69.7: translations — finds translated versions via mods search on the main mod's name (the website HTML scrape is blocked by 403 anti-scraping).</summary>
    public async Task<List<NexusTranslationItem>?> GetModTranslationsAsync(int modId, string modName, string gameDomain = "stardewvalley")
    {
        try
        {
            if (!GameIdCache.TryGetValue(gameDomain, out var gameId))
            {
                var g = await GraphQlAsync("{ game(domainName:\"" + gameDomain + "\") { id } }");
                if (g is null) return null;
                var gv = g.Value;
                if (!gv.TryGetProperty("game", out var gg) || gg.ValueKind != JsonValueKind.Object) return null;
                var gidEl = gg.TryGetProperty("id", out var tmp) ? tmp : default;
                gameId = gidEl.ValueKind == JsonValueKind.Number ? gidEl.GetInt32()
                       : int.TryParse(gidEl.ToString(), out var p) ? p : 0;
                if (gameId <= 0) return null;
                GameIdCache[gameDomain] = gameId;
            }
            // Wildcard search on the main name; exclude the mod itself, keep only results with translation semantics
            var safeName = new string((modName ?? "")
                .Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_').ToArray()).Trim();
            if (string.IsNullOrEmpty(safeName)) return new List<NexusTranslationItem>();
            var q = "{ mods(filter:{gameId:{value:\"" + gameId + "\"}, name:{value:\"" + safeName
                + "\", op:WILDCARD}}, count:30) { nodes { modId name } } }";
            var d = await GraphQlAsync(q);
            if (d is null) return null;
            // v0.81.0: capture totalCount (debug feature: the frontend shows "server total vs loaded count")
            try
            {
                var rv0 = d.Value;
                if (rv0.ValueKind == JsonValueKind.Object
                    && rv0.TryGetProperty("mods", out var mv0) && mv0.ValueKind == JsonValueKind.Object
                    && mv0.TryGetProperty("totalCount", out var tv0) && tv0.ValueKind == JsonValueKind.Number)
                    LastBrowseTotalCount = tv0.GetInt32();
            }
            catch { }
            var root = d.Value;
            if (!root.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Object ||
                !mods.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                return null;
            var keywords = new[] { "translation", "chinese", "japanese", "korean", "francais", "french",
                "german", "deutsch", "spanish", "espanol", "portuguese", "russian", "italian", "polish",
                "czech", "turkish", "mandarin", "kor", "中文", "翻译", "汉化", "简体", "繁体" };
            var list = new List<NexusTranslationItem>();
            foreach (var n in nodes.EnumerateArray())
            {
                var mid = n.TryGetProperty("modId", out var m1) && m1.ValueKind == JsonValueKind.Number ? m1.GetInt32()
                        : int.TryParse(GetStr(n, "modId"), out var p) ? p : 0;
                var nm = GetStr(n, "name");
                if (mid <= 0 || mid == modId || string.IsNullOrWhiteSpace(nm)) continue;
                var low = nm.ToLowerInvariant();
                if (keywords.Any(k => low.Contains(k.ToLowerInvariant())))
                    list.Add(new NexusTranslationItem(nm, mid, null));
            }

            // v0.69.9: batch-fill covers (same logic as Requirements)
            if (list.Count > 0)
            {
                try
                {
                    var ids = string.Join(",", list.Select(t =>
                        "{gameDomain:\"" + gameDomain + "\", modId:" + t.ModId + "}"));
                    var dc = await GraphQlAsync("{ legacyModsByDomain(ids:[" + ids + "]) { nodes { modId pictureUrl } } }");
                    if (dc is not null && dc.Value.TryGetProperty("legacyModsByDomain", out var lm)
                        && lm.TryGetProperty("nodes", out var cnodes) && cnodes.ValueKind == JsonValueKind.Array)
                    {
                        var picMap = new Dictionary<int, string>();
                        foreach (var n in cnodes.EnumerateArray())
                        {
                            var mid2 = n.TryGetProperty("modId", out var m2) && m2.ValueKind == JsonValueKind.Number ? m2.GetInt32()
                                     : int.TryParse(GetStr(n, "modId"), out var p2) ? p2 : 0;
                            var pic = GetStr(n, "pictureUrl");
                            if (mid2 > 0 && !string.IsNullOrWhiteSpace(pic)) picMap[mid2] = pic;
                        }
                        list = list.Select(t => picMap.TryGetValue(t.ModId, out var pu)
                            ? new NexusTranslationItem(t.Name, t.ModId, pu) : t).ToList();
                    }
                }
            catch { /* missing covers are non-blocking */ }
            }
            return list;
        }
        catch { return null; }
    }

    /// <summary>
    /// v0.69.2: scrapes the mod detail page HTML and parses three blocks — "Permissions and credits /
    /// Translations / Collections containing this mod"
    /// (none of these have endpoints in the v1 REST or public GraphQL docs; the website page is
    /// server-rendered, so it can be parsed directly).
    /// Any block that fails to parse becomes empty/null; the UI falls back to a "view on the website" link.
    /// </summary>
    public async Task<NexusModExtras?> GetModPageExtrasAsync(int modId)
    {
        try
        {
            using var res = await Http.GetAsync(
                $"https://www.nexusmods.com/stardewvalley/mods/{modId}");
            if (!res.IsSuccessStatusCode) return null;
            var html = await res.Content.ReadAsStringAsync();

            // ── Translations: mod links inside the Translations block ──
            var translations = new List<NexusLinkItem>();
            var tRegion = ExtractRegion(html, "Translations",
                "Changelogs", "Mods using this mod", "Collections containing this mod", "Posts");
            if (tRegion is not null)
                foreach (var (u, t) in ExtractModLinks(tRegion))
                    translations.Add(new NexusLinkItem(t, "https://www.nexusmods.com" + u, ""));

            // ── Collections: Collections containing this mod / Included in N collections block ──
            var collections = new List<NexusLinkItem>();
            var cRegion = ExtractRegion(html, "Collections containing this mod",
                "Posts", "Bug reports", "Activity logs", "Mod statistics", "</footer");
            if (cRegion is null)
                cRegion = ExtractRegion(html, "Included in", "Posts", "</footer");
            if (cRegion is not null)
            {
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                    cRegion, @"href=""(?<u>/stardewvalley/collections/[a-zA-Z0-9]+)""[^>]*>(?<t>[^<]{1,80})<"))
                {
                    var t = m.Groups["t"].Value.Trim();
                    if (t.Length == 0) continue;
                    // Look for an "N mods" count near the link
                    var tail = cRegion.Substring(m.Index, Math.Min(400, cRegion.Length - m.Index));
                    var cm = System.Text.RegularExpressions.Regex.Match(tail, @"(\d[\d,]*)\s*mods");
                    var sub = cm.Success ? cm.Groups[1].Value + " mods" : "";
                    collections.Add(new NexusLinkItem(t, "https://www.nexusmods.com" + m.Groups["u"].Value, sub));
                }
            }

            // ── Permissions and credits: strip tags within the block for plain text (truncate if too long) ──
            string? permissions = null;
            var pRegion = ExtractRegion(html, "Permissions and credits",
                "Translations", "Changelogs", "Mods using this mod", "Collections containing this mod");
            if (pRegion is not null)
            {
                var txt = System.Text.RegularExpressions.Regex.Replace(pRegion, "<[^>]+>", " ");
                txt = System.Text.RegularExpressions.Regex.Replace(
                    System.Net.WebUtility.HtmlDecode(txt), "\\s+", " ").Trim();
                // Remove the block heading itself from the start
                txt = System.Text.RegularExpressions.Regex.Replace(txt, "^Permissions and credits\\s*", "");
                if (txt.Length > 30) permissions = txt.Length > 900 ? txt[..900] + "…" : txt;
            }

            return new NexusModExtras(permissions, translations, collections);
        }
        catch { return null; }
    }

    /// <summary>Extracts the HTML region between startMarker and any endMarker (null if not found).</summary>
    private static string? ExtractRegion(string html, string startMarker, params string[] endMarkers)
    {
        var i = html.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var end = html.Length;
        foreach (var em in endMarkers)
        {
            var j = html.IndexOf(em, i + startMarker.Length, StringComparison.OrdinalIgnoreCase);
            if (j > i && j < end) end = j;
        }
        var len = Math.Min(end - i, 200000);   // defensive: truncate absurdly large regions
        return html.Substring(i, len);
    }

    /// <summary>Extracts (mod link, display text) pairs from an HTML region.</summary>
    private static IEnumerable<(string Url, string Text)> ExtractModLinks(string region)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            region, @"href=""(?<u>/stardewvalley/mods/\d+)[^""]*""[^>]*>(?<t>[^<]{1,80})<"))
        {
            var t = m.Groups["t"].Value.Trim();
            if (t.Length > 0) yield return (m.Groups["u"].Value, t);
        }
    }

    /// <summary>
    /// v0.69.0: user download history (modId → last download date yyyy-MM-dd).
    /// This is a legacy v1 endpoint that could be taken offline at any time — any failure returns
    /// null and the caller falls back silently; it must never prevent the detail page from opening.
    /// </summary>
    public async Task<Dictionary<int, string>?> GetDownloadHistoryAsync(string apiKey)
    {
        try
        {
            using var res = await Http.SendAsync(Req(apiKey,
                "https://api.nexusmods.com/v1/user/download_history.json"));
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var map = new Dictionary<int, string>();
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return map;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                // Defensive parsing: mod_id may be flat or nested inside a "mod" object
                var mid = e.TryGetProperty("mod_id", out var m1) && m1.ValueKind == JsonValueKind.Number ? m1.GetInt32()
                        : e.TryGetProperty("mod", out var mo) && mo.ValueKind == JsonValueKind.Object
                            && mo.TryGetProperty("mod_id", out var m2) && m2.ValueKind == JsonValueKind.Number ? m2.GetInt32() : 0;
                if (mid <= 0) continue;
                // Date field names vary across versions: date / downloaded_at / time; both ISO strings and unix seconds are handled
                string? iso = null;
                foreach (var key in new[] { "date", "downloaded_at", "time" })
                {
                    if (!e.TryGetProperty(key, out var dv)) continue;
                    if (dv.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(dv.GetString(), out var dt)) { iso = dt.ToString("yyyy-MM-dd"); break; }
                    if (dv.ValueKind == JsonValueKind.Number && dv.TryGetInt64(out var unix) && unix > 0)
                    { iso = DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("yyyy-MM-dd"); break; }
                }
                if (iso is null) continue;
                if (!map.TryGetValue(mid, out var cur) || string.Compare(iso, cur, StringComparison.Ordinal) > 0)
                    map[mid] = iso;
            }
            return map;
        }
        catch { return null; }
    }

    /// <summary>CDN download URL for a file. NeedsPremium=true on free accounts (HTTP 403).</summary>
    public async Task<NexusDownloadResult> GetDownloadUrlAsync(string apiKey, int modId, long fileId)
    {
        using var res = await Http.SendAsync(Req(apiKey,
            $"{Base}/mods/{modId}/files/{fileId}/download_link.json"));
        if ((int)res.StatusCode == 403) return NexusDownloadResult.PremiumRequired;
        if (!res.IsSuccessStatusCode) return NexusDownloadResult.Fail($"HTTP {(int)res.StatusCode}");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        foreach (var server in doc.RootElement.EnumerateArray())
            if (server.TryGetProperty("URI", out var u) && u.GetString() is { } uri)
                return NexusDownloadResult.Ok(uri);
        return NexusDownloadResult.Fail("No download URL in the response");
    }

    // Large downloads use a separate long-timeout client (free accounts are throttled to ~1MB/s,
    // and the 30s-timeout API client would cut off multi-hundred-MB collection downloads).
    private static readonly HttpClient DownloadHttp = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JuniGrid-Launcher");
        h.Timeout = TimeSpan.FromMinutes(30);
        return h;
    }

    /// <summary>Streaming download: writes to disk as it downloads instead of reading everything into memory like GetByteArrayAsync.</summary>
    public Task DownloadFileAsync(string url, string destPath) =>
        DownloadFileAsync(url, destPath, null);

    /// <summary>
    /// Streaming download with a live progress callback, for the task center to show percent and speed.
    /// When progress is null, degrades to a plain download.
    /// v1.07: resumable downloads / automatic retries go through ResumableDownload (a dropped
    /// connection no longer restarts from zero).
    /// </summary>
    public Task DownloadFileAsync(string url, string destPath,
        IProgress<NexusDownloadProgress>? progress)
    {
        return ResumableDownload.RunAsync(DownloadHttp, url, destPath,
            (msg, pct, spd) => progress?.Report(new NexusDownloadProgress(msg, pct ?? 0, spd)));
    }

    private static string FormatBytes(long bytes)
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

    // ------------------------------------------------------------------
    // nxm:// one-time links (free accounts OK — the key+expires come from
    // the user clicking "Mod Manager Download" on the website)
    // ------------------------------------------------------------------
    public async Task<NexusDownloadResult> GetNxmDownloadUrlAsync(
        string? apiKey, int modId, long fileId, string key, string expires)
    {
        var url = $"{Base}/mods/{modId}/files/{fileId}/download_link.json"
                + $"?key={Uri.EscapeDataString(key)}&expires={Uri.EscapeDataString(expires)}";
        using var res = await Http.SendAsync(Req(apiKey, url));
        if (!res.IsSuccessStatusCode)
        {
            var code = (int)res.StatusCode;
            return NexusDownloadResult.Fail(code is 400 or 401 or 403
                ? $"HTTP {code} (the download credentials don't match the account of the API Key in Settings, or the link has expired — make sure the app and the browser are logged into the same Nexus account, then click Mod Manager Download on the website again)"
                : $"HTTP {code} (the link may have expired; click the download on the website again)");
        }

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        foreach (var server in doc.RootElement.EnumerateArray())
            if (server.TryGetProperty("URI", out var u) && u.GetString() is { } uri)
                return NexusDownloadResult.Ok(uri);
        return NexusDownloadResult.Fail("No download URL in the response");
    }

    // ------------------------------------------------------------------
    // Browse lists (no full-text search in API v1 — that's v2/OAuth only)
    // ------------------------------------------------------------------
    /// <summary>kind: trending | latest_added | latest_updated</summary>
    public async Task<IReadOnlyList<NexusModListEntry>> GetModListAsync(string apiKey, string kind)
    {
        using var res = await Http.SendAsync(Req(apiKey, $"{Base}/mods/{kind}.json"));
        if (!res.IsSuccessStatusCode) return Array.Empty<NexusModListEntry>();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var list = new List<NexusModListEntry>();
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            list.Add(new NexusModListEntry(
                m.TryGetProperty("mod_id", out var i) && i.ValueKind == JsonValueKind.Number
                    ? i.GetInt32() : 0,
                GetStr(m, "name"),
                GetStr(m, "summary"),
                GetStr(m, "version"),
                GetInt64(m, "mod_downloads"),
                GetStr(m, "picture_url")));
        }
        return list;
    }

    /// <summary>
    /// v0.77.0: public entry point for browse lists.
    /// </summary>
    /// <summary>
    /// v0.96.0: time filtering fully rewritten — dropped the "crawl the time stream + client-side sort"
    /// simulation in favor of server-side filtering identical to the website: the GraphQL
    /// createdAt/updatedAt filter values use Unix timestamps in seconds (ISO strings always match 0 in
    /// the website's ES — the real reason the old comment said "date filtering broke as soon as it was added").
    /// Verified semantics (mirroring the website's games/{domain}/mods?sort=&timeRange=):
    /// New      = createdAt filter + createdAt sort
    /// Updated  = updatedAt filter + updatedAt sort
    /// Trending = createdAt filter + endorsements sort (website default timeRange=7, still used by the homepage Trending block)
    /// Downloads= downloads sort (the former Trending slot, sorted by download count)
    /// Popular  = createdAt filter + downloads sort (the website's popular is download count, not endorsements)
    /// Surprise = no time filter + server-side random{seed} sort (a stable seed keeps paging continuous; "Shuffle" draws a new seed)
    /// With filtering moved server-side, every page is a single precise query: totalCount is available at
    /// all times (the paginator's time filter can also show the last page).
    /// v1.04.0: removed the "custom time range" (already removed from the UI); added direction (ASC/DESC order dropdown).
    /// </summary>
    public async Task<List<NexusModListEntry>?> BrowseModsAsync(string kind, int offset, int count,
        string gameDomain = "stardewvalley", string? searchText = null, string? categoryName = null,
        string timeRange = "all", string direction = "DESC")
    {
        // ─── Surprise special path: the website's own server-side random sort (seed controlled by the UI's "Shuffle") ───
        if (kind == "surprise")
            return await FetchChunkedAsync(kind, offset, count, gameDomain, searchText, categoryName,
                randomSeed: SurpriseSeed);

        // ─── Time window → epoch filter conditions (the Updated tab filters by update time, the rest by publish time, mirroring the website) ───
        long? sinceEpoch = null, untilEpoch = null;
        var dateOnUpdatedAt = kind == "updated";
        if (timeRange != "all")
        {
            var days = timeRange switch { "day" => 1, "week" => 7, "2week" => 14, "month" => 28, "year" => 365, _ => 0 };
            if (days > 0) sinceEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - days * 86400;
        }

        return await FetchChunkedAsync(kind, offset, count, gameDomain, searchText, categoryName,
            sinceEpoch: sinceEpoch, untilEpoch: untilEpoch, dateOnUpdatedAt: dateOnUpdatedAt,
            direction: direction);
    }

    /// <summary>v0.77.0: loops in CHUNK steps until count entries are reached (keeps pulling if the server truncates).</summary>
    private async Task<List<NexusModListEntry>?> FetchChunkedAsync(string kind, int offset, int count,
        string gameDomain, string? searchText, string? categoryName,
        long? sinceEpoch = null, long? untilEpoch = null, bool dateOnUpdatedAt = false,
        string direction = "DESC", int? randomSeed = null)
    {
        var all = new List<NexusModListEntry>();
        var seen = new HashSet<int>();
        const int CHUNK = 50;
        var cur = offset;
        var emptyStreak = 0;
        for (var guard = 0; guard < 10 && all.Count < count; guard++)
        {
            var want = Math.Min(CHUNK, count - all.Count);   // v0.88.0: don't over-fetch — exact per-page counts, so the last page is no longer short
            var batch = await BrowseModsChunkAsync(kind, cur, want, gameDomain, searchText, categoryName,
                sinceEpoch: sinceEpoch, untilEpoch: untilEpoch, dateOnUpdatedAt: dateOnUpdatedAt,
                direction: direction, randomSeed: randomSeed);
            if (batch is null)
            {
                await Task.Delay(400);
                batch = await BrowseModsChunkAsync(kind, cur, want, gameDomain, searchText, categoryName,
                    sinceEpoch: sinceEpoch, untilEpoch: untilEpoch, dateOnUpdatedAt: dateOnUpdatedAt,
                    direction: direction, randomSeed: randomSeed);
                if (batch is null) { await Task.Delay(900); batch = await BrowseModsChunkAsync(kind, cur, want, gameDomain, searchText, categoryName,
                    sinceEpoch: sinceEpoch, untilEpoch: untilEpoch, dateOnUpdatedAt: dateOnUpdatedAt,
                    direction: direction, randomSeed: randomSeed); }
            }
            if (batch is null) return all.Count > 0 ? all : null;
            var added = 0;
            foreach (var e in batch)
                if (seen.Add(e.Id)) { all.Add(e); added++; }
            cur += Math.Max(batch.Count, 1);
            // v0.77.0: a single batch with 0 additions doesn't immediately mean the end (the server occasionally
            // returns an overlapping page); only 2 consecutive batches with 0 additions is truly the end
            if (added == 0) { if (++emptyStreak >= 2) break; }
            else emptyStreak = 0;
        }
        return all;
    }

    /// <summary>Single-batch GraphQL fetch (the original BrowseModsAsync implementation, internal only).</summary>
    private async Task<List<NexusModListEntry>?> BrowseModsChunkAsync(string kind, int offset, int count,
        string gameDomain = "stardewvalley", string? searchText = null, string? categoryName = null,
        long? sinceEpoch = null, long? untilEpoch = null, bool dateOnUpdatedAt = false,
        string direction = "DESC", int? randomSeed = null)
    {
        try
        {
            if (!GameIdCache.TryGetValue(gameDomain, out var gameId))
            {
                var g = await GraphQlAsync("{ game(domainName:\"" + gameDomain + "\") { id } }");
                if (g is null) return null;
                var gv = g.Value;
                if (!gv.TryGetProperty("game", out var gg) || gg.ValueKind != JsonValueKind.Object) return null;
                var gidEl = gg.TryGetProperty("id", out var tmp) ? tmp : default;
                gameId = gidEl.ValueKind == JsonValueKind.Number ? gidEl.GetInt32()
                       : int.TryParse(gidEl.ToString(), out var p) ? p : 0;
                if (gameId <= 0) return null;
                GameIdCache[gameDomain] = gameId;
            }

            // v0.96.0: sort mapping mirroring the website frontend (new=createdAt / updated=updatedAt / trending=endorsements
            // / downloads=downloads / popular=downloads / surprise=random). random only honors seed, not direction.
            // v1.04.0: ascending/descending dropdown → direction (ASC/DESC); random ignores direction.
            var dir = string.Equals(direction, "ASC", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
            string sort = randomSeed is int rs
                ? "random:{seed:" + rs + "}"
                : kind switch
                {
                    "updated"     => $"updatedAt:{{direction:{dir}}}",
                    "trending"    => $"endorsements:{{direction:{dir}}}",
                    "downloads"   => $"downloads:{{direction:{dir}}}",
                    "popular"     => $"downloads:{{direction:{dir}}}",
                    _             => $"createdAt:{{direction:{dir}}}"
                };

            // v0.71.0: filter conditions — always filter adult content; category; search (pure number = exact Nexus mod ID, otherwise name/author OR fuzzy)
            var conds = new List<string>
            {
                "{gameId:{value:\"" + gameId + "\"}}"
            };
            // v0.79.0: adult content filtered per the master switch (the filter condition is added only when IncludeAdultContent=false)
            if (!IncludeAdultContent)
                conds.Add("{adultContent:{value:false, op:EQUALS}}");
            if (!string.IsNullOrWhiteSpace(categoryName))
                conds.Add("{categoryName:{value:\"" + categoryName.Replace("\"", "") + "\", op:EQUALS}}");
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                var st = searchText.Trim();
                if (int.TryParse(st, out var idNum))
                    // v0.96.0: value must be a string literal (BaseFilterValue.value: String!) — a bare number fails the whole query;
                    // and modId EQUALS only works on the api-router endpoint, the old v2 mirror always returns empty.
                    conds.Add("{modId:{value:\"" + idNum + "\", op:EQUALS}}");
                else
                {
                    var safe = new string(st.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '&').ToArray());
                    if (safe.Length > 0)
                        // v0.98.1: three-way OR over name/author/uploader — searching an author's or uploader's name lists their mods
                        conds.Add("{filter:[{name:{value:\"" + safe + "\", op:WILDCARD}},"
                                + "{author:{value:\"" + safe + "\", op:WILDCARD}},"
                                + "{uploader:{value:\"" + safe + "\", op:WILDCARD}}], op:OR}");
                }
            }
            // v0.96.0: server-side time filtering (mirroring the website) — filter values use Unix timestamps in seconds.
            // Note ES builds this into a Lucene query of the form date:>=<value>: colons inside ISO strings break
            // Lucene syntax, plain date strings always match 0 — only a pure numeric epoch truly hits.
            var dateField = dateOnUpdatedAt ? "updatedAt" : "createdAt";
            if (sinceEpoch is long se)
                conds.Add("{" + dateField + ":{value:\"" + se + "\", op:GTE}}");
            if (untilEpoch is long ue)
                conds.Add("{" + dateField + ":{value:\"" + ue + "\", op:LT}}");
            var sel = "modId name summary version thumbnailUrl pictureUrl downloads endorsements createdAt updatedAt "
                    + "uploader { name avatar } modCategory { name } fileSize";
            var q = "{ mods(filter:{filter:[" + string.Join(",", conds) + "], op:AND}, sort:{" + sort
                + "}, offset:" + offset + ", count:" + count + ") { totalCount nodes { " + sel + " } } }";
            var d = await GraphQlAsync(q);
            if (d is null) return null;
            var root = d.Value;
            if (!root.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Object ||
                !mods.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                return null;

            // v0.95.0: capture the server-side totalCount — the data source for the paginator's "Page x / N · X items".
            // Within one query, totalCount doesn't vary with offset, so any batch's captured value equals the full result-set size.
            // v0.96.0: with time filtering moved server-side, time filters also get an exact totalCount (last page always available).
            if (mods.TryGetProperty("totalCount", out var tv) && tv.ValueKind == JsonValueKind.Number
                && tv.TryGetInt32(out var tcv))
                LastBrowseTotalCount = tcv;

            var list = new List<NexusModListEntry>();
            foreach (var n in nodes.EnumerateArray())
            {
                var mid = n.TryGetProperty("modId", out var m1) && m1.ValueKind == JsonValueKind.Number ? m1.GetInt32()
                        : int.TryParse(GetStr(n, "modId"), out var p) ? p : 0;
                string? up = null, upAv = null;
                if (n.TryGetProperty("uploader", out var uo) && uo.ValueKind == JsonValueKind.Object)
                { up = GetStr(uo, "name"); upAv = GetStr(uo, "avatar"); }
                long fsize = n.TryGetProperty("fileSize", out var fs) && fs.ValueKind == JsonValueKind.Number
                    && fs.TryGetInt64(out var fsl) ? fsl : 0;
                string? cat = null;
                if (n.TryGetProperty("modCategory", out var mc) && mc.ValueKind == JsonValueKind.Object) cat = GetStr(mc, "name");
                long downloads = n.TryGetProperty("downloads", out var dl) && dl.ValueKind == JsonValueKind.Number && dl.TryGetInt64(out var dln) ? dln : 0;
                int endor = n.TryGetProperty("endorsements", out var en) && en.ValueKind == JsonValueKind.Number ? en.GetInt32() : 0;
                list.Add(new NexusModListEntry(
                    mid, GetStr(n, "name"), GetStr(n, "summary"), GetStr(n, "version"), downloads, GetStr(n, "pictureUrl"),
                    GetStr(n, "thumbnailUrl"), up, endor,
                    GetStr(n, "createdAt"), GetStr(n, "updatedAt"), cat, upAv, fsize));
            }
            return list;
        }
        catch { return null; }
    }

    /// <summary>v0.71.0: fetches category facets (data source for the Showcase's 4 category pills, with per-category mod counts).</summary>
    public async Task<Dictionary<string, int>?> GetCategoryFacetsAsync(string gameDomain = "stardewvalley")
    {
        try
        {
            if (!GameIdCache.TryGetValue(gameDomain, out var gameId))
            {
                var g = await GraphQlAsync("{ game(domainName:\"" + gameDomain + "\") { id } }");
                if (g is null) return null;
                var gv = g.Value;
                if (!gv.TryGetProperty("game", out var gg) || gg.ValueKind != JsonValueKind.Object) return null;
                var gidEl = gg.TryGetProperty("id", out var tmp) ? tmp : default;
                gameId = gidEl.ValueKind == JsonValueKind.Number ? gidEl.GetInt32()
                       : int.TryParse(gidEl.ToString(), out var p) ? p : 0;
                if (gameId <= 0) return null;
                GameIdCache[gameDomain] = gameId;
            }
            var d = await GraphQlAsync("{ mods(filter:{gameId:{value:\"" + gameId
                + "\"}}, count:1, facets:{categoryName:[]}) { facetsData } }");
            if (d is null) return null;
            var root = d.Value;
            if (!root.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Object ||
                !mods.TryGetProperty("facetsData", out var fd) || fd.ValueKind != JsonValueKind.Object ||
                !fd.TryGetProperty("categoryName", out var cn) || cn.ValueKind != JsonValueKind.Object)
                return null;
            var dict = new Dictionary<string, int>();
            foreach (var p in cn.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n))
                    dict[p.Name] = n;
            return dict;
        }
        catch { return null; }
    }

    /// <summary>Calls /v1/users/validate.json for the account info tied to the current API Key.
    /// Verified as of v1.08.0: this endpoint no longer has avatar / member_id fields — the user id is
    /// called user_id (reading member_id always yields 0, so the GraphQL extras + avatar backfill chain
    /// never worked); the avatar direct link can be constructed as avatars.nexusmods.com/{user_id}/100
    /// (the endpoint currently misplaces it in the profile_url field — fixed along the way).</summary>
    public async Task<NexusUser?> ValidateAsync(string apiKey)
    {
        try
        {
            using var res = await Http.SendAsync(Req(apiKey, "https://api.nexusmods.com/v1/users/validate.json"));
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var r = doc.RootElement;
            var memberId = r.TryGetProperty("user_id", out var ui) && ui.ValueKind == JsonValueKind.Number ? ui.GetInt32()
                         : r.TryGetProperty("member_id", out var mi) && mi.ValueKind == JsonValueKind.Number ? mi.GetInt32() : 0;
            var avatar = GetStr(r, "avatar") ?? "";
            if (string.IsNullOrEmpty(avatar) && memberId > 0)
                avatar = $"https://avatars.nexusmods.com/{memberId}/100";
            var profileUrl = GetStr(r, "profile_url") ?? "";
            if (profileUrl.Contains("avatars.nexusmods.com", StringComparison.OrdinalIgnoreCase))
                profileUrl = $"https://www.nexusmods.com/users/{memberId}";
            return new NexusUser(
                GetStr(r, "name") ?? "",
                GetStr(r, "email") ?? "",
                profileUrl,
                avatar,
                r.TryGetProperty("is_premium", out var pp) && pp.ValueKind == JsonValueKind.True,
                memberId);
        }
        catch { return null; }
    }

    /// <summary>v0.70.1: GraphQL user(id) fetches extended user info (for the tooltip card). Returns null on failure.</summary>
    public async Task<NexusUserExtras?> GetUserExtrasAsync(int memberId)
    {
        if (memberId <= 0) return null;
        // v1.08.0: user.id is Int! — the old user(id:"123") with quotes was rejected outright by the server
        // (Expected type 'Int!'), so this query (including avatar backfill and hover-card stats) never succeeded
        var d = await GraphQlAsync(
            "{ user(id:" + memberId + ") { avatar about joined country modCount uniqueModDownloads endorsementsGiven kudos recognizedAuthor verifiedCurator } }");
        if (d is null) return null;
        var u = d.Value;
        if (!u.TryGetProperty("user", out var uu) || uu.ValueKind != JsonValueKind.Object) return null;
        long GetLong(string k) => uu.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
        bool GetBool(string k) => uu.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        return new NexusUserExtras(
            GetStr(uu, "avatar"),
            GetStr(uu, "about"), GetStr(uu, "joined"), GetStr(uu, "country"),
            (int)GetLong("modCount"), GetLong("uniqueModDownloads"),
            (int)GetLong("endorsementsGiven"), (int)GetLong("kudos"),
            GetBool("recognizedAuthor"), GetBool("verifiedCurator"));
    }

    /// <summary>v1.04.0: queries a single mod's uploader (for the detail page's hover avatar preview next to the author name).
    /// v1.06.1: switched to a legacyModsByDomain single-mod exact query — the old mods(filter modId EQUALS)
    /// has been blocked server-side (even with gameId it always reports "gameId is required when filtering by modId"),
    /// so the avatar never resolved. legacyModsByDomain reliably returns uploader.name/avatar in testing
    /// (avatar is a real direct link like https://avatars.nexusmods.com/<memberId>/100). Returns null on failure.</summary>
    public async Task<(string? Name, string? Avatar)?> GetUploaderInfoAsync(int modId, string gameDomain = "stardewvalley")
    {
        try
        {
            var d = await GraphQlAsync("{ legacyModsByDomain(ids:[{gameDomain:\"" + gameDomain
                + "\", modId:" + modId + "}]) { nodes { uploader { name avatar } } } }");
            if (d is null) return null;
            var root = d.Value;
            if (!root.TryGetProperty("legacyModsByDomain", out var lb) || lb.ValueKind != JsonValueKind.Object ||
                !lb.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var n in nodes.EnumerateArray())
            {
                if (n.TryGetProperty("uploader", out var uo) && uo.ValueKind == JsonValueKind.Object)
                    return (GetStr(uo, "name"), GetStr(uo, "avatar"));
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>v1.05.1: detail page author avatar.
    /// v1.06.1: GraphQL legacyModsByDomain promoted to the primary channel — the website HTML scrape is
    /// blocked by Cloudflare 403 (HttpClient/curl blocked regardless of UA), while GraphQL's avatar field
    /// returns real direct links in testing; previously it failed because the query itself was rejected by
    /// the server (see the GetUploaderInfoAsync comment). HTML scraping kept as fallback.</summary>
    public async Task<string?> GetUploaderAvatarAsync(int modId, string gameDomain = "stardewvalley")
    {
        // ① GraphQL legacyModsByDomain → uploader.avatar (currently the only stable channel)
        try
        {
            var up = await GetUploaderInfoAsync(modId, gameDomain);
            var gav = up?.Avatar;
            if (!string.IsNullOrWhiteSpace(gav) && !gav.Contains("/missing", StringComparison.OrdinalIgnoreCase))
                return gav;
        }
        catch { }
        // ② Fallback: scrape the avatar from the website page HTML (only works when Cloudflare lets it through)
        try
        {
            using var res = await Http.GetAsync($"https://www.nexusmods.com/{gameDomain}/mods/{modId}");
            if (res.IsSuccessStatusCode)
            {
                var html = await res.Content.ReadAsStringAsync();
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                    html, @"https://avatars\.nexusmods\.com/[^""'\s\\]+"))
                {
                    var u = m.Value;
                    if (u.Contains("/missing", StringComparison.OrdinalIgnoreCase)) continue;
                    return u;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Returns the avatar image as a data URI (avoids WebView2 cross-origin and Referer restrictions).
    /// v1.05.0: filters out the avatars.nexusmods.com/missing placeholder — users without an avatar get
    /// this placeholder URL in the GraphQL avatar field, and displaying it would show a wrong
    /// "Nexus placeholder avatar"; the initial-letter fallback is used instead.</summary>
    public async Task<string?> FetchAvatarAsync(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (url.Contains("/missing", StringComparison.OrdinalIgnoreCase)) return null;
            using var res = await Http.GetAsync(url);
            if (!res.IsSuccessStatusCode) return null;
            // v1.08.0: the ID direct link for users without an avatar 307-redirects to the missing placeholder —
            // the final redirected URL must be checked again (the original URL doesn't contain /missing, the landing one does)
            var finalUrl = res.RequestMessage?.RequestUri?.ToString() ?? url;
            if (finalUrl.Contains("/missing", StringComparison.OrdinalIgnoreCase)) return null;
            var bytes = await res.Content.ReadAsByteArrayAsync();
            // The avatars CDN is observed to return application/octet-stream — data URIs are uniformly labeled as image type
            var ct = res.Content.Headers.ContentType?.MediaType ?? "image/png";
            if (ct is null || ct == "application/octet-stream") ct = "image/png";
            return $"data:{ct};base64,{Convert.ToBase64String(bytes)}";
        }
        catch { return null; }
    }
}

public sealed record NexusUser(string Name, string Email, string ProfileUrl, string Avatar, bool IsPremium, int MemberId);

/// <summary>v0.70.1: extended user info (GraphQL user(id), for the tooltip card).
/// v1.07.0: added Avatar — a real GraphQL avatar direct link, the backfill source when validate.json lacks an avatar.</summary>
public sealed record NexusUserExtras(string? Avatar, string? About, string? Joined, string? Country,
    int ModCount, long UniqueModDownloads, int EndorsementsGiven, int Kudos, bool RecognizedAuthor, bool VerifiedCurator);

public sealed record NexusModInfo(int Id, string Name, string Version, string PictureUrl, int? CategoryId);

/// <summary>vNext: update-check fingerprint (batch-fetched via GraphQL legacyModsByDomain).
/// UpdatedAt is the mod's last update time (ISO-8601; uploading new files necessarily changes it) —
/// fingerprint unchanged ⟹ file list unchanged ⟹ the latest MAIN file version found by the last detail check is still valid.
/// Version is the mod's top-level version (hand-entered by the author, may lag; diagnostic only — update detection still relies on file versions).</summary>
public sealed record NexusModFingerprint(int ModId, string Version, string UpdatedAt, string? PictureUrl);

public sealed record NexusFileInfo(long FileId, string Name, string Version, string Category, long Size = 0);

/// <summary>v0.69.0: changelog for one version.</summary>
public sealed record NexusChangelog(string Version, List<string> Lines);

/// <summary>v0.69.2: one link from the page extras data (shared by translations/collections). Sub is extra info (e.g. "553 mods").</summary>
public sealed record NexusLinkItem(string Name, string Url, string Sub);

/// <summary>v0.69.2: mod page extras (permissions & credits text / translations list / collections list). Null when scraping fails; the UI falls back to a website link.</summary>
public sealed record NexusModExtras(string? PermissionsText, List<NexusLinkItem> Translations, List<NexusLinkItem> Collections);

public sealed record NexusModListEntry(
    int Id, string Name, string Summary, string Version, long Downloads, string PictureUrl,
    string? ThumbnailUrl = null, string? Uploader = null, int Endorsements = 0,
    string? CreatedAt = null, string? UpdatedAt = null, string? Category = null,
    string? UploaderAvatar = null, long FileSize = 0);

public sealed record NexusModDetail(
    int Id, string Name, string Author, string Version, string Summary,
    string DescriptionHtml, string PictureUrl, long Downloads, long Endorsements,
    string UpdatedAt, IReadOnlyList<NexusModDependency>? Dependencies,
    IReadOnlyList<string> ImageUrls);

/// <summary>"Other mods that depend on this mod" as returned by Nexus. availability: published/removed etc.</summary>
public sealed record NexusModDependency(int ModId, string Name, string Availability);

/// <summary>v0.69.3: one row of the website's Requirements table (GraphQL v2 data source).</summary>
public sealed record NexusRequirement(string ModName, string Notes, string Url, int ModId, bool External);

/// <summary>v0.69.7: Requirements row with cover (covers batch-filled via GraphQL legacyModsByDomain).</summary>
public sealed record NexusRequirementEx(string ModName, string Notes, string Url, int ModId, bool External, string? PictureUrl);

/// <summary>v0.69.7: translation entry (from the GraphQL mods search).</summary>
public sealed record NexusTranslationItem(string Name, int ModId, string? PictureUrl);

public sealed record NexusDownloadResult(string? Url, string? Error, bool NeedsPremium)
{
    public static NexusDownloadResult Ok(string url) => new(url, null, false);
    public static NexusDownloadResult Fail(string err) => new(null, err, false);

    public static readonly NexusDownloadResult PremiumRequired =
        new(null, "Free Nexus accounts cannot download directly via the API (Premium membership required)", true);
}

/// <summary>Download progress snapshot: text + optional percent + optional instantaneous speed (MB/s).</summary>
public sealed record NexusDownloadProgress(string Message, double Percent, double? SpeedMBps);
