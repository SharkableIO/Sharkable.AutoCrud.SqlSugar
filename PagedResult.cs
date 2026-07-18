namespace Sharkable.AutoCrud.SqlSugar;

/// <summary>
/// Paginated result from AutoCrud list endpoints.
/// </summary>
/// <typeparam name="T">Entity type.</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>Items in the current page.</summary>
    public List<T> Items { get; init; } = [];

    /// <summary>Total number of records across all pages.</summary>
    public int Total { get; init; }

    /// <summary>Current page number (1-based).</summary>
    public int Page { get; init; }

    /// <summary>Page size.</summary>
    public int PageSize { get; init; }

    /// <summary>Total number of pages.</summary>
    public int TotalPages { get; init; }
}
