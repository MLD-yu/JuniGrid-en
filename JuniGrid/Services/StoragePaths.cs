using System.IO;

namespace JuniGrid.Services;

/// <summary>
/// v0.2.2: single source of truth for cache/storage paths. Previously the download zip, SMAPI installer, etc.
/// paths were hardcoded Path.Combine calls scattered across 5 services; all of them now come from here.
/// When CacheRoot (the unified cache directory) is null, everything uses the historical default locations;
/// once set, downloads/install temp, SMAPI installer, WebView2 data, and Mods backups all live in subdirectories of it.
/// Logs and config data stay fixed in AppData (config must load before the cache location is known, and logs must remain diagnosable even if the cache directory is deleted).
/// </summary>
public static class StoragePaths
{
    /// <summary>Unified cache root; null = historical default locations.
    /// Kept in sync by ConfigService on Load/Save (SyncStoragePaths); a change takes effect immediately.</summary>
    public static string? CacheRoot { get; internal set; }

    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JuniGrid");

    public static string LocalAppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JuniGrid");

    /// <summary>Common root of the historical default locations for movable items (%TEMP%\JuniGrid).</summary>
    public static string TempRoot => Path.Combine(Path.GetTempPath(), "JuniGrid");

    /// <summary>Downloads & install temp directory (zips and extraction temp for direct install/update/nxm). Follows the cache directory.</summary>
    public static string DownloadsDir => CacheRoot is null
        ? Path.Combine(TempRoot, "downloads")
        : Path.Combine(CacheRoot, "downloads");

    /// <summary>SMAPI installer download & extraction cache. Follows the cache directory.</summary>
    public static string SmapiInstallerDir => CacheRoot is null
        ? Path.Combine(TempRoot, "smapi-installer")
        : Path.Combine(CacheRoot, "smapi-installer");

    /// <summary>App self-update installer cache (v1.0.8). Follows the cache directory — files are kept after a cancelled install so they can be resumed/reinstalled.</summary>
    public static string SelfUpdateDir => CacheRoot is null
        ? Path.Combine(TempRoot, "self-update")
        : Path.Combine(CacheRoot, "self-update");

    /// <summary>WebView2 user data directory (includes sign-in state and network cache). Follows the cache directory; changes take effect after restart —
    /// MainWindow performs the pending migration at startup before setting WEBVIEW2_USER_DATA_FOLDER.</summary>
    public static string WebView2Dir => CacheRoot is null
        ? Path.Combine(TempRoot, "webview2")
        : Path.Combine(CacheRoot, "webview2");

    /// <summary>Safety backup of Mods before SMAPI updates. Follows the cache directory.</summary>
    public static string ModsBackupDir => CacheRoot is null
        ? Path.Combine(TempRoot, "mods-backup")
        : Path.Combine(CacheRoot, "mods-backup");

    /// <summary>Recycle bin for uninstalled game Mods (relative to the game directory, never migrated).</summary>
    public static string GameTrashDir(string gamePath) => Path.Combine(gamePath, "Mods", ".junigrid_trash");
}
