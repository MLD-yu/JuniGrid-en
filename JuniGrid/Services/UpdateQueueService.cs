namespace JuniGrid.Services;

/// <summary>
/// Placeholder implementation left after the built-in browser was removed:
/// keeps the old interface to avoid a cascade of changes. The auto-update queue is disabled (the "Update" button now opens the system browser).
/// </summary>
public sealed class UpdateQueueService
{
    public int? CurrentModId => null;
    public int Done => 0;
    public int Total => 0;
    public bool IsRunning => false;
    public event Action? OnAdvanced;
    public void Start(IEnumerable<int> modIds) { }
    public void Stop() { }
    public void NotifyFailed(int modId) { }
    public void NotifyInstalled(int modId) { }
    public void Skip() { }
    public void Advance() => OnAdvanced?.Invoke();
}
