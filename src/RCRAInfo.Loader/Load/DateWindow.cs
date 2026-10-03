using System.Globalization;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// One inclusive <c>startDate</c>/<c>endDate</c> pair to ask <c>/hd/sources/summaries</c> for.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type exists because the summaries endpoint has no paging.</b> Its three siblings under
/// <c>/ce/</c> take <c>offset</c> and <c>limit</c>; <c>/hd/sources/summaries</c> takes neither (plan
/// [R28]). So the window <i>is</i> the paging mechanism, and every property a page would have had has to
/// come from somewhere else: the response-size lever is <see cref="DayCount"/>, the resume point is
/// <c>logs.HandlerLoadStatus</c>, and the progress marker is <c>config.LoadWatermark</c>.
/// </para>
/// <para>
/// <b>Both ends are inclusive, because EPA's are.</b> That is the entire reason <see cref="Split"/> starts
/// each window on the day <i>after</i> the previous one ends rather than on the previous end. Half-open
/// arithmetic here would ask for every boundary day twice, which is merely wasteful — but the same
/// off-by-one in the other direction skips a day, and a skipped day in an initial load is a permanent hole:
/// the watermark advances past it and no later run asks for it again.
/// </para>
/// </remarks>
/// <param name="StartDate">The first day in the window, inclusive.</param>
/// <param name="EndDate">The last day in the window, inclusive.</param>
public readonly record struct DateWindow(DateOnly StartDate, DateOnly EndDate)
{
    /// <summary>How many days the window covers, counting both ends.</summary>
    public int DayCount => EndDate.DayNumber - StartDate.DayNumber + 1;

    /// <summary>Whether the window is the right way round.</summary>
    public bool IsValid => EndDate >= StartDate;

    /// <summary>
    /// Splits an inclusive date range into consecutive, non-overlapping windows of at most
    /// <paramref name="windowDays"/> days each.
    /// </summary>
    /// <param name="fromDate">The first day to cover, inclusive.</param>
    /// <param name="toDate">The last day to cover, inclusive.</param>
    /// <param name="windowDays">The widest window to produce. At least 1.</param>
    /// <returns>
    /// The windows, in ascending date order. Always at least one; the last one may be narrower than
    /// <paramref name="windowDays"/> and never wider.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="toDate"/> is before <paramref name="fromDate"/>, or <paramref name="windowDays"/>
    /// is less than 1.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>An inverted range throws rather than returning nothing</b>, for the reason
    /// <c>RcraInfoDataRequest.Summaries</c> gives about the same condition: EPA would answer <c>200</c> with
    /// an empty array, which is indistinguishable from a genuinely quiet week, and the run would then
    /// advance the watermark over data it never fetched. An empty list here would produce the same outcome
    /// one layer earlier. There is no arrangement of the two dates that makes "no windows" the right
    /// answer, so it is not an answer this method can give.
    /// </para>
    /// <para>
    /// The two properties worth asserting about the result, and the two the tests assert: the windows
    /// <b>cover</b> <c>[fromDate, toDate]</c> with no gap, and they <b>do not overlap</b>. Coverage is what
    /// keeps the mirror complete; non-overlap is what keeps a version from being counted twice in a run
    /// summary that an operator reads as a row count.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<DateWindow> Split(DateOnly fromDate, DateOnly toDate, int windowDays)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowDays, 1);

        if (toDate < fromDate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toDate),
                toDate,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The load window ends before it starts ({0:yyyy-MM-dd} to {1:yyyy-MM-dd}), which "
                    + "cannot be split into windows. EPA answers an inverted range with 200 and an empty "
                    + "array, so a run that got here would report success and advance its watermark over "
                    + "data it never asked for.",
                    fromDate,
                    toDate));
        }

        int totalDays = toDate.DayNumber - fromDate.DayNumber + 1;
        List<DateWindow> windows = new((totalDays / windowDays) + 1);

        DateOnly start = fromDate;

        while (start <= toDate)
        {
            // windowDays - 1 because both ends are inclusive: a one-day window ends on the day it starts.
            DateOnly end = start.AddDays(windowDays - 1);

            if (end > toDate)
            {
                end = toDate;
            }

            windows.Add(new DateWindow(start, end));

            start = end.AddDays(1);
        }

        return windows;
    }

    /// <summary>The window as one short token for a log message. Two dates and nothing else.</summary>
    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd}..{1:yyyy-MM-dd}", StartDate, EndDate);
}
