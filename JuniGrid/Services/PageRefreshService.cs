namespace JuniGrid.Services;

/// <summary>
/// In-page refresh signal: raised when the top bar's nav icon for the page you're already on is clicked (mods / nexus);
/// the corresponding page subscribes and reloads its data (rescan / refetch / recheck updates).
/// Switching from a different page goes through normal NavLink navigation and loads naturally via component rebuild,
/// so no signal is raised and no requests are wasted.
/// </summary>
public sealed class PageRefreshService
{
    public event Action<string>? OnRefresh;

    /// <summary>pageKey: identifier of the page requesting the refresh ("mods" / "nexus").</summary>
    public void Request(string pageKey) => OnRefresh?.Invoke(pageKey);
}
