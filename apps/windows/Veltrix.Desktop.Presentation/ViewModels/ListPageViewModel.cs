using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veltrix.Contracts;

namespace Veltrix.Desktop.Presentation.ViewModels;

/// <summary>Server-paged, searchable list.</summary>
public abstract partial class ListPageViewModel<T>(DesktopServices s) : PageViewModel(s)
{
    public const int PageSize = 50;

    public ObservableCollection<T> Items { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PageLabel), nameof(HasNext), nameof(HasPrevious))] private int _page = 1;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PageLabel), nameof(HasNext), nameof(PageCount))] private int _totalCount;

    public int PageCount => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public bool HasNext => Page < PageCount;
    public bool HasPrevious => Page > 1;
    public string PageLabel => $"{Page} / {PageCount} · {TotalCount}";

    protected abstract Task<PagedResponse<T>> FetchAsync(int page, string search, CancellationToken ct);

    protected override async Task<bool> LoadAsync(CancellationToken ct)
    {
        var r = await FetchAsync(Page, Search.Trim(), ct);
        Items.Clear();
        foreach (var i in r.Items) Items.Add(i);
        TotalCount = r.TotalCount;
        OnLoaded();
        return Items.Count > 0;
    }

    protected virtual void OnLoaded() { }

    [RelayCommand]
    public Task SearchNowAsync()
    {
        Page = 1;
        return ReloadAsync();
    }

    [RelayCommand]
    public Task NextPageAsync()
    {
        if (!HasNext) return Task.CompletedTask;
        Page++;
        return ReloadAsync();
    }

    [RelayCommand]
    public Task PreviousPageAsync()
    {
        if (!HasPrevious) return Task.CompletedTask;
        Page--;
        return ReloadAsync();
    }
}
