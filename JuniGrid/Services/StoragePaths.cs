using System.IO;

namespace JuniGrid.Services;

/// <summary>
/// v0.2.2: single source of truth for cache/storage paths. Previously the paths for
/// downloaded zips, the SMAPI installer, etc. were hardcoded Path.Combine calls
/// scattered across 5 services; they all now come from here.
/// When CacheRoot (the unified cache directory) is null, the historical default
/// locations are used; once set, install/update temp files, the SMAPI installer,
/// WebView2 data, and the Mods backup all live in subfolders of that directory.
/// Logs and config data stay in AppData (config must load before the cache location
/// is known, and logs must remain diagnosable after the cache directory is deleted).
/// </summary>
public static class StoragePaths
{
    /// <summary>Unified cache root directory; null = historical default location.
    /// Synced by ConfigService on Load/Save (SyncStoragePaths); changes take effect immediately.</summary>
    public static string? CacheRoot { get; internal set; }

    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JuniGrid");

    public static string LocalAppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JuniGrid");

    /// <summary>Unified root of the historical default locations for migratable items (%TEMP%\JuniGrid).</summary>
    public static string TempRoot => Path.Combine(Path.GetTempPath(), "JuniGrid");

    /// <summary>Download and install temp directory (zips for install/update/nxm, extraction temp). Follows the cache directory.</summary>
    public static string DownloadsDir => CacheRoot is null
        ? Path.Combine(TempRoot, "downloads")
        : Path.Combine(CacheRoot, "downloads");

    /// <summary>SMAPI installer download/extraction cache. Follows the cache directory.</summary>
    public static string SmapiInstallerDir => CacheRoot is null
        ? Path.Combine(TempRoot, "smapi-installer")
        : Path.Combine(CacheRoot, "smapi-installer");

    /// <summary>WebView2 user data directory (login state and network cache). Follows the cache
    /// directory; changes take effect after restart — MainWindow performs the legacy
    /// migration before setting WEBVIEW2_USER_DATA_FOLDER at startup.</summary>
    public static string WebView2Dir => CacheRoot is null
        ? Path.Combine(TempRoot, "webview2")
        : Path.Combine(CacheRoot, "webview2");

    /// <summary>Safety backup of Mods before SMAPI updates. Follows the cache directory.</summary>
    public static string ModsBackupDir => CacheRoot is null
        ? Path.Combine(TempRoot, "mods-backup")
        : Path.Combine(CacheRoot, "mods-backup");

    /// <summary>Recycle bin for uninstalled game mods (relative to the game folder; never migrated).</summary>
    public static string GameTrashDir(string gamePath) => Path.Combine(gamePath, "Mods", ".junigrid_trash");
}
