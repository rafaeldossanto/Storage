using Storage.Application.Errors;

namespace Storage.Application.Abstractions;

/// <summary>Which slice of a long list to read. Pages count from 1.</summary>
public sealed record PageRequest
{
    public const int DefaultSize = 50;
    public const int MaxSize = 200;

    private PageRequest(int page, int pageSize)
    {
        Page = page;
        PageSize = pageSize;
    }

    public int Page { get; }

    public int PageSize { get; }

    public int Skip => (Page - 1) * PageSize;

    /// <summary>The first page, for code that wants a bounded sample rather than a screenful.</summary>
    public static PageRequest First(int pageSize = DefaultSize) => Of(1, pageSize);

    /// <summary>
    /// Validates what came in the query string. Left out, a value takes its default; out of
    /// range, it is refused rather than quietly adjusted, so a screen asking for 500 rows
    /// learns at once that it gets at most <see cref="MaxSize"/>.
    /// </summary>
    public static PageRequest Of(int? page, int? pageSize)
    {
        var number = page ?? 1;
        var size = pageSize ?? DefaultSize;

        // The skip must fit an int, which is what the database driver takes.
        if (number < 1 || size is < 1 or > MaxSize || (long)(number - 1) * size > int.MaxValue)
        {
            throw UseCaseException.Invalid(
                ErrorCodes.PageInvalid, $"Pages start at 1 and hold from 1 to {MaxSize} items.");
        }

        return new PageRequest(number, size);
    }
}

/// <summary>
/// One page of a list, with what a screen needs to draw the rest: how many items there are
/// in all and how many pages that makes.
/// </summary>
public sealed record Paged<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total)
{
    public int TotalPages => (int)((Total + PageSize - 1) / PageSize);

    public static Paged<T> Empty(PageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new Paged<T>([], request.Page, request.PageSize, 0);
    }

    /// <summary>A page cut from a list already held in memory, in the order it came.</summary>
    public static Paged<T> Slice(IReadOnlyList<T> all, PageRequest request)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(request);

        return new Paged<T>(
            all.Skip(request.Skip).Take(request.PageSize).ToArray(), request.Page, request.PageSize, all.Count);
    }

    public Paged<TResult> Map<TResult>(Func<T, TResult> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return new Paged<TResult>(Items.Select(map).ToArray(), Page, PageSize, Total);
    }
}
