using System.Diagnostics.CodeAnalysis;

namespace RCRAInfo.Data;

/// <summary>One page of a paged read, and the size of the set it came from.</summary>
/// <typeparam name="T">The row type from <c>RCRAInfo.Data.Results</c>.</typeparam>
/// <param name="Items">The rows on this page, in the order the procedure returned them.</param>
/// <param name="TotalRows">
/// How many rows match the filter, ignoring paging. Zero when <paramref name="Items"/> is empty.
/// </param>
/// <param name="Skip">The offset that was requested.</param>
/// <param name="Take">The page size that was requested.</param>
/// <remarks>
/// <para>
/// Every paged procedure in this database returns its total as a column repeated on each row, which
/// is a deliberate choice — a second round trip for a count can disagree with the page it labels.
/// Lifting it here means callers do not each reimplement "read it off the first row, and zero if
/// there is no first row".
/// </para>
/// <para>
/// The count is taken from the first row only. Every procedure computes it once into a variable and
/// projects that same variable on every row, so the values cannot differ; reading one is not an
/// assumption about the others.
/// </para>
/// </remarks>
public sealed record Page<T> (IReadOnlyList<T> Items, int TotalRows, int Skip, int Take)
{
    /// <summary>An empty page, for a filter that matched nothing.</summary>
    /// <param name="skip">The offset that was requested.</param>
    /// <param name="take">The page size that was requested.</param>
    /// <returns>A page with no rows and a total of zero.</returns>
    [SuppressMessage (
        "Design",
        "CA1000:Do not declare static members on generic types",
        Justification =
            "CA1000 exists because a static member on a generic type forces the caller to name the " +
            "type argument. Here the caller is naming it anyway -- Page<HandlerSourceGridRow>.Empty " +
            "reads exactly as Array.Empty<HandlerSourceGridRow> does, which is the framework's own " +
            "precedent for this shape. The alternative, a non-generic Page.Empty<T>, moves the type " +
            "argument rather than removing it.")]
    public static Page<T> Empty (int skip, int take) => new (Array.Empty<T> (), 0, skip, take);

    /// <summary>
    /// Whether a further page exists. Derived rather than returned by the procedures, because
    /// <see cref="Skip"/> plus the row count against <see cref="TotalRows"/> already answers it.
    /// </summary>
    public bool HasMore => Skip + Items.Count < TotalRows;
}
