namespace ReeveWorkflow.Core.Catalog;

/// <summary>Overflow-safe bounds for the library's own list reads: an unbounded page multiplies out past
/// int.MaxValue and reaches SQL as a NEGATIVE offset, which reads as an empty page, not as an error.</summary>
public static class WorkflowPaging
{
    /// <summary>The highest page number that can be asked for. Under any page size below roughly two
    /// thousand, (page - 1) * pageSize stays inside an int.</summary>
    public const int MaxPage = 1_000_000;

    /// <summary>Clamps a one-based page and its size into the safe range.</summary>
    public static (int Page, int PageSize) ClampPage(int page, int pageSize, int maxPageSize)
        => (Math.Clamp(page, 1, MaxPage), Math.Clamp(pageSize, 1, maxPageSize));

    /// <summary>Clamps a raw offset and count into the safe range.</summary>
    public static (int Skip, int Take) ClampSkipTake(int skip, int take, int maxTake)
    {
        var maxSkip = (int)Math.Min(int.MaxValue, (long)MaxPage * maxTake);
        return (Math.Clamp(skip, 0, maxSkip), Math.Clamp(take, 1, maxTake));
    }

    /// <summary>Applies one-based paging to a query, clamping first. Deliberately not an extension:
    /// Reeve's own Paging has the same signature, and both in scope is an ambiguity, not an override.</summary>
    public static IQueryable<T> ApplyPage<T>(IQueryable<T> source, int page, int pageSize, int maxPageSize)
    {
        var (p, ps) = ClampPage(page, pageSize, maxPageSize);
        return source.Skip((p - 1) * ps).Take(ps);
    }

    /// <summary>Applies a raw offset and count to a query, clamping first. Not an extension, for the
    /// reason above.</summary>
    public static IQueryable<T> ApplySkipTake<T>(IQueryable<T> source, int skip, int take, int maxTake)
    {
        var (s, t) = ClampSkipTake(skip, take, maxTake);
        return source.Skip(s).Take(t);
    }
}
