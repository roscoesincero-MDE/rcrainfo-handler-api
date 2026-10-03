using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>
/// The two properties that decide whether the mirror has holes in it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coverage and non-overlap, and coverage is the one that matters.</b> A gap between two windows is a date
/// range nobody ever asks EPA about: the watermark advances past it because every window that <i>was</i>
/// asked for succeeded, and <c>/hd/sources/summaries</c> has no envelope to notice the omission with
/// afterwards. So the arithmetic below is checked exhaustively over a range of widths rather than at a few
/// convenient boundaries — an off-by-one in an inclusive-inclusive split is exactly the defect that produces
/// a hole, and it would not show up on a range that divides evenly.
/// </para>
/// <para>
/// Overlap is the cheaper failure — a duplicate version, absorbed by the walk's dictionary — but it is worth
/// asserting because a duplicate is also the <i>symptom</i> of G25, EPA filtering on a date field this loader
/// has assumed. Windows that overlapped by construction would make that signal unreadable.
/// </para>
/// </remarks>
public class DateWindowTests
{
    [Fact]
    public void BothEndsAreInclusiveSoASingleDayIsAOneDayWindow()
    {
        DateOnly day = new(2026, 9, 6);

        DateWindow window = Assert.Single(DateWindow.Split(day, day, 7));

        Assert.Equal(day, window.StartDate);
        Assert.Equal(day, window.EndDate);
        Assert.Equal(1, window.DayCount);
        Assert.True(window.IsValid);
    }

    [Fact]
    public void AWindowIsWindowDaysWideAndNotWindowDaysPlusOne()
    {
        // The inclusive-inclusive trap: end = start + windowDays would make a "7 day" window eight days
        // long, and the run would ask EPA for a day it also asks for in the next window.
        IReadOnlyList<DateWindow> windows = DateWindow.Split(new(2026, 1, 1), new(2026, 1, 14), 7);

        Assert.Equal(2, windows.Count);
        Assert.Equal(new DateOnly(2026, 1, 1), windows[0].StartDate);
        Assert.Equal(new DateOnly(2026, 1, 7), windows[0].EndDate);
        Assert.Equal(new DateOnly(2026, 1, 8), windows[1].StartDate);
        Assert.Equal(new DateOnly(2026, 1, 14), windows[1].EndDate);
        Assert.All(windows, window => Assert.Equal(7, window.DayCount));
    }

    [Fact]
    public void TheLastWindowIsClampedRatherThanRunningPastTheRequestedRange()
    {
        // Asking EPA for dates beyond the range would not fail -- it would return versions the run then
        // fetches while the watermark is set to the earlier date, so the next run fetches them again.
        IReadOnlyList<DateWindow> windows = DateWindow.Split(new(2026, 1, 1), new(2026, 1, 10), 7);

        Assert.Equal(2, windows.Count);
        Assert.Equal(new DateOnly(2026, 1, 10), windows[^1].EndDate);
        Assert.Equal(3, windows[^1].DayCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(366)]
    public void EveryDayInTheRangeIsCoveredByExactlyOneWindow(int windowDays)
    {
        // Exhaustive over a range that divides evenly by none of the widths above, which is the point: an
        // off-by-one hides on a range that divides evenly and produces a permanent hole on one that does not.
        DateOnly from = new(2025, 11, 17);
        DateOnly to = new(2026, 3, 2);

        IReadOnlyList<DateWindow> windows = DateWindow.Split(from, to, windowDays);

        List<DateOnly> covered = [];

        foreach (DateWindow window in windows)
        {
            Assert.True(window.IsValid);
            Assert.True(window.DayCount <= windowDays);

            for (DateOnly day = window.StartDate; day <= window.EndDate; day = day.AddDays(1))
            {
                covered.Add(day);
            }
        }

        // Coverage: every day, in order, once. A Distinct() count would prove non-overlap and hide a gap,
        // and a count alone would prove neither -- so the whole sequence is compared.
        List<DateOnly> expected = [];

        for (DateOnly day = from; day <= to; day = day.AddDays(1))
        {
            expected.Add(day);
        }

        Assert.Equal(expected, covered);
    }

    [Fact]
    public void WindowsAreContiguousAndNeverOverlap()
    {
        IReadOnlyList<DateWindow> windows = DateWindow.Split(new(2026, 1, 1), new(2026, 4, 15), 10);

        for (int index = 1; index < windows.Count; index++)
        {
            // Exactly one day on, not "at or after". A duplicated day is the symptom of G25 and must stay
            // readable as EPA's behaviour rather than as this loader's arithmetic.
            Assert.Equal(windows[index - 1].EndDate.AddDays(1), windows[index].StartDate);
        }
    }

    [Fact]
    public void AWindowWiderThanTheRangeIsOneWindowOverTheWholeRange()
    {
        IReadOnlyList<DateWindow> windows = DateWindow.Split(new(2026, 1, 1), new(2026, 1, 3), 366);

        DateWindow only = Assert.Single(windows);

        Assert.Equal(new DateOnly(2026, 1, 1), only.StartDate);
        Assert.Equal(new DateOnly(2026, 1, 3), only.EndDate);
    }

    [Fact]
    public void AnInvertedRangeThrowsRatherThanReturningNoWindows()
    {
        // The dangerous alternative is returning an empty list: every window succeeds, because there are
        // none, so MayAdvanceWatermark is true and the watermark moves over a range never asked for. EPA
        // answers an inverted range with 200 and an empty array, so nothing downstream would object.
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => DateWindow.Split(new(2026, 1, 10), new(2026, 1, 1), 7));

        Assert.Contains("watermark", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AWindowWidthBelowOneThrows(int windowDays) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DateWindow.Split(new(2026, 1, 1), new(2026, 1, 31), windowDays));

    [Fact]
    public void ToStringNamesBothDatesInEpasOwnFormat()
    {
        // The walk logs this string and nothing else about a window -- no relative URI, no handler. Its
        // format is therefore part of what a run summary means.
        DateWindow window = new(new(2026, 1, 1), new(2026, 1, 7));

        Assert.Equal("2026-01-01..2026-01-07", window.ToString());
    }
}
