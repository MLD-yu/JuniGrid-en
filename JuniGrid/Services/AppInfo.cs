namespace JuniGrid.Services;

/// <summary>
/// Basic info about the app itself. When releasing a new version, only change Version here;
/// the About page display and self-update comparison both read from here, so the two version numbers can't diverge.
/// </summary>
public static class AppInfo
{
    /// <summary>Current app version (without the v prefix).</summary>
    public const string Version = "1.1.3";

    public const string RepoOwner = "MLD-yu";
    public const string RepoName  = "JuniGrid-en";

    /// <summary>Releases download page (opened when a new version is found).</summary>
    public static string ReleasesUrl => $"https://github.com/{RepoOwner}/{RepoName}/releases";

    /// <summary>GitHub API: latest stable Release.</summary>
    public static string LatestApiUrl => $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
}
