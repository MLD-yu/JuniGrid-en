namespace JuniGrid.Services;

/// <summary>
/// In-page refresh signal: raised when the top bar's navigation icon for the page you're already on is clicked (mods / nexus);
/// the corresponding page subscribes and reloads its data (rescan / refetch / recheck for updates).
/// When switching over from another page, normal NavLink navigation happens and the component rebuild loads data naturally — no signal raised, no wasted requests.
/// </summary>
public sealed class PageRefreshService
{
    public event Action<string>? OnRefresh;

    /// <summary>pageKey: identifier of the page requesting a refresh ("mods" / "nexus").</summary>
    public void Request(string pageKey) => OnRefresh?.Invoke(pageKey);
}
