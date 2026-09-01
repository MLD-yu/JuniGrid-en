using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>
/// Locates the Stardew Valley install and probes SMAPI metadata.
/// </summary>
public sealed class GameService
{
    public string DetectGamePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley",
            @"C:\Program Files\Steam\steamapps\common\Stardew Valley",
            @"D:\Steam\steamapps\common\Stardew Valley",
            @"D:\SteamLibrary\steamapps\common\Stardew Valley",
            @"E:\Steam\steamapps\common\Stardew Valley",
            @"E:\SteamLibrary\steamapps\common\Stardew Valley",
            @"F:\SteamLibrary\steamapps\common\Stardew Valley",
            Path.Combine(home, @"AppData\Local\Programs\Stardew Valley"),
            @"C:\GOG Games\Stardew Valley"
        };

        foreach (var p in candidates)
            if (Directory.Exists(p)) return p;
        return "";
    }

    public string? ProbeSmapiVersion(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            return null;

        var exe = Path.Combine(gamePath, "StardewModdingAPI.exe");
        if (!File.Exists(exe)) return null;

        // 1) Most reliable: the SMAPI exe carries its own assembly version.
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            var raw = info.FileVersion ?? info.ProductVersion;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var parts = raw.Split('.');
                return parts.Length >= 3 ? string.Join('.', parts.Take(3)) : raw;
            }
        }
        catch (Exception __ex) { AppLog.Warn("GameService", __ex.Message); }

        // 2) Fallback: smapi-internal metadata JSON (name varies by SMAPI build).
        foreach (var metaName in new[] { "SMAPI.metadata.json", "metadata.json" })
        {
            var metaPath = Path.Combine(gamePath, "smapi-internal", metaName);
            if (!File.Exists(metaPath)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                if (doc.RootElement.TryGetProperty("Version", out var v))
                {
                    var s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            catch (Exception __ex) { AppLog.Warn("GameService", __ex.Message); }
        }

        // 3) Last resort: we can only confirm SMAPI is installed but can't read the version.
        // Return a placeholder that Version.TryParse will definitely reject as a valid version,
        // and guarantee CheckSmapiAsync recognizes "installed locally but version unknown".
        return null;   // version unreadable → treated as unrecognized, avoiding flapping when compared against the GitHub version
    }

    // ------------------------------------------------------------------
    // SMAPI's own icon (extracted from the exe, converted to a data URI for the UI)
    // ------------------------------------------------------------------
    private string? _smapiIconCache;
    private string? _smapiIconForPath;

    public string? GetSmapiIconDataUri(string gamePath)
    {
        if (_smapiIconForPath == gamePath) return _smapiIconCache;
        _smapiIconForPath = gamePath;
        _smapiIconCache = null;
        try
        {
            var exe = Path.Combine(gamePath, "StardewModdingAPI.exe");
            if (!File.Exists(exe)) return null;

            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon is null) return null;
            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            _smapiIconCache = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception __ex) { AppLog.Warn("GameService", __ex.Message); }
        return _smapiIconCache;
    }
}
