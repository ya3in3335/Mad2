namespace Veltrix.SharedKernel;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed record PageQuery(int Page = 1, int PageSize = 50, string? Search = null, string? Sort = null)
{
    public int SafePage => Page < 1 ? 1 : Page;
    public int SafePageSize => PageSize is < 1 or > 500 ? 50 : PageSize;
    public bool Descending => Sort?.StartsWith('-') == true;
    public string? SortField => Sort?.TrimStart('-', '+');
}

public static class PagingExtensions
{
    public static async Task<PagedResult<T>> ToPagedAsync<T>(this IQueryable<T> query, PageQuery page, CancellationToken ct = default)
    {
        var total = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(query, ct);
        var items = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            query.Skip((page.SafePage - 1) * page.SafePageSize).Take(page.SafePageSize), ct);
        return new PagedResult<T>(items, page.SafePage, page.SafePageSize, total);
    }
}
