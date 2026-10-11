namespace Veltrix.Desktop.Presentation;

/// <summary>
/// One running copy per Windows session: the first launch owns a named mutex and listens on a named event; a second launch
/// (double-click, Start Menu, Ctrl+Alt+V hotkey) signals that event so the running window comes to front, then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _activate;
    private readonly RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle? activate, Action? onActivate)
    {
        _mutex = mutex;
        _activate = activate;
        if (activate is not null && onActivate is not null)
            _registration = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Session-local kernel object names (Local\ = per logged-on user session, so fast user switching keeps one copy each).</summary>
    public static (string Mutex, string Event) Names(string appId) => ($@"Local\{appId}", $@"Local\{appId}-Activate");

    /// <summary>
    /// Claims the instance. Returns the owner (keep it alive until exit) when this is the first copy; otherwise signals the
    /// running copy to come to front and returns null. <paramref name="onActivate"/> runs on a thread-pool thread.
    /// </summary>
    public static SingleInstance? TryClaim(string appId, Action? onActivate)
    {
        var (mutexName, eventName) = Names(appId);
        var mutex = new Mutex(true, mutexName, out var first);
        if (!first)
        {
            mutex.Dispose();
            Signal(eventName);
            return null;
        }
        EventWaitHandle? activate = null;
        if (OperatingSystem.IsWindows()) activate = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        return new SingleInstance(mutex, activate, onActivate);
    }

    private static void Signal(string eventName)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (EventWaitHandle.TryOpenExisting(eventName, out var h)) using (h) h.Set();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException) { }
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate?.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
