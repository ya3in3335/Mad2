using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Desktop.Core.Api;

namespace Veltrix.Desktop.Presentation.ViewModels;

public enum LoadState { Idle, Loading, Ready, Empty, Error }

/// <summary>Base for every screen: loading / empty / error state, cancellation of stale loads and user-facing error text.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;
    private bool _loadedOnce;

    protected PageViewModel(DesktopServices services) => S = services;

    protected DesktopServices S { get; }
    protected ILocalizer Loc => S.Loc;

    public abstract string TitleKey { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsEmpty), nameof(IsError), nameof(IsReady))]
    private LoadState _state = LoadState.Idle;

    [ObservableProperty] private string? _errorMessage;
    /// <summary>Data came from the local cache because the server could not be reached.</summary>
    [ObservableProperty] private bool _isOfflineData;
    /// <summary>Transient feedback after an action (saved, queued offline…).</summary>
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isBusy;

    public bool IsLoading => State == LoadState.Loading;
    public bool IsEmpty => State == LoadState.Empty;
    public bool IsError => State == LoadState.Error;
    public bool IsReady => State == LoadState.Ready;

    /// <summary>Called by the shell when the page is shown. Loads once; use Refresh to reload.</summary>
    public Task OnNavigatedToAsync()
    {
        if (_loadedOnce) return Task.CompletedTask;
        _loadedOnce = true;
        return ReloadAsync();
    }

    [RelayCommand]
    public Task RefreshAsync() => ReloadAsync();

    /// <summary>Loads the page data. Return true when there is something to show, false for the empty state.</summary>
    protected abstract Task<bool> LoadAsync(CancellationToken ct);

    protected async Task ReloadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        State = LoadState.Loading;
        ErrorMessage = null;
        try
        {
            var any = await LoadAsync(cts.Token);
            if (cts.IsCancellationRequested) return;
            State = any ? LoadState.Ready : LoadState.Empty;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (cts.IsCancellationRequested) return;
            S.Log.Warn($"{GetType().Name} load failed", e);
            ErrorMessage = Describe(e);
            State = LoadState.Error;
        }
    }

    /// <summary>Runs a user action, mapping failures to <see cref="StatusMessage"/>. Returns true on success.</summary>
    protected async Task<bool> TryActionAsync(Func<Task> action, string? successKey = null)
    {
        if (IsBusy) return false;
        IsBusy = true;
        StatusMessage = null;
        try
        {
            await action();
            if (successKey is not null) StatusMessage = Loc.Get(successKey);
            return true;
        }
        catch (Exception e)
        {
            S.Log.Warn($"{GetType().Name} action failed", e);
            StatusMessage = Describe(e);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public string Describe(Exception e) => ErrorText.Describe(Loc, e);

    protected bool Can(string permission) => S.Session.HasPermission(permission);
}

public static class ErrorText
{
    public static string Describe(ILocalizer loc, Exception e) => e switch
    {
        ApiOfflineException => loc.Get("err.network"),
        ApiException { Status: 403 } => loc.Get("err.permission"),
        ApiException { Status: 401 } => loc.Get("dk.err.sessionExpired"),
        ApiException { Code: "client.no_server" } => loc.Get("dk.err.noServer"),
        ApiException a when !string.IsNullOrWhiteSpace(a.Message) => a.Message,
        Core.Workforce.WorkforceAccessDeniedException => loc.Get("err.permission"),
        // Local ERP rules report a localization key (erp.err.*).
        InvalidOperationException io when io.Message.StartsWith("erp.", StringComparison.Ordinal) || io.Message.StartsWith("cam.", StringComparison.Ordinal)
                                           || io.Message.StartsWith("wf.", StringComparison.Ordinal) || io.Message.StartsWith("sec.", StringComparison.Ordinal)
                                           || io.Message.StartsWith("link.", StringComparison.Ordinal)
                                           || io.Message == "auth.invalid" => loc.Get(io.Message),
        InvalidOperationException io => io.Message,
        _ => loc.Get("err.generic"),
    };
}
