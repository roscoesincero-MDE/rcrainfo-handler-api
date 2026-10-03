using System.Globalization;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// One window an operator asked the probe about, and whether it is a window worth asking for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own type rather than two <see cref="DateOnly"/> parameters, for
/// <see cref="TargetedLoadRequest"/>'s reason:</b> the refusals belong beside the values. The probe itself
/// throws for an inverted range, but a throw out of a stage is reported by <c>Program</c> as unusable
/// <i>configuration</i> — and a range typed into a Task Scheduler action is not configuration. Validating here
/// makes it exit code 9 with the switch named, which sends the operator to the argument list.
/// </para>
/// <para>
/// <b>The date format is fixed at <c>yyyy-MM-dd</c> and nothing else is accepted.</b> Not pedantry:
/// <c>06/09/2026</c> is the sixth of September in one reading and the ninth of June in another, and the probe
/// reports what a window <i>cost</i> — so a range silently three months from the one intended would produce a
/// number that looks like an answer and sizes F2 wrongly. Refusing the ambiguous form is the only way the
/// number can be trusted, and it is the same form <c>logs.LoadRun</c> and the walk's log messages use.
/// </para>
/// </remarks>
/// <param name="FromDate">First day of the window, inclusive.</param>
/// <param name="ToDate">Last day of the window, inclusive.</param>
public sealed record SummaryProbeRequest(DateOnly FromDate, DateOnly ToDate)
{
    /// <summary>The one accepted date format.</summary>
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// How many days a probed window may span before it is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Capped because this is one request with no paging.</b> <c>/hd/sources/summaries</c> takes no
    /// <c>offset</c> and no <c>limit</c>, so the window is the only lever on the response size — that being
    /// the very thing the probe measures. A range of years would be a single request asking EPA for every
    /// version of every handler in the state, which is discourteous to a shared service and would very likely
    /// come back as a timeout that measures nothing.
    /// </para>
    /// <para>
    /// Ninety-one days rather than a round number: a quarter, plus the day the inclusive range adds. A quarter
    /// is the widest window that answers "is one window enough" for a monthly catch-up, which is the question
    /// F2 will actually ask.
    /// </para>
    /// </remarks>
    public const int MaxWindowDays = 91;

    /// <summary>Parses a switch's two values.</summary>
    /// <param name="fromText">The first value, or <see langword="null"/> if it was not given.</param>
    /// <param name="toText">The second value, or <see langword="null"/> if it was not given.</param>
    /// <param name="switchName">The switch being parsed, so a refusal names it.</param>
    /// <param name="request">The request, or <see langword="null"/> when anything was refused.</param>
    /// <returns>Everything wrong with the pair, in a form safe to print. Empty means usable.</returns>
    public static IReadOnlyList<string> TryParse(
        string? fromText,
        string? toText,
        string switchName,
        out SummaryProbeRequest? request)
    {
        List<string> problems = [];
        request = null;

        DateOnly? from = ParseOne(fromText, switchName, "first", problems);
        DateOnly? to = ParseOne(toText, switchName, "second", problems);

        if (from is null || to is null)
        {
            return problems;
        }

        SummaryProbeRequest candidate = new(from.Value, to.Value);
        problems.AddRange(candidate.Validate());

        if (problems.Count == 0)
        {
            request = candidate;
        }

        return problems;
    }

    /// <summary>Everything wrong with this window.</summary>
    /// <returns>Zero or more messages, safe to print. Empty means the probe may be asked for it.</returns>
    public IReadOnlyList<string> Validate()
    {
        List<string> problems = [];

        if (ToDate < FromDate)
        {
            problems.Add(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The probed window {0:" + DateFormat + "} to {1:" + DateFormat + "} ends before it "
                    + "starts. EPA answers an inverted range with 200 and an empty array, so this would be "
                    + "reported as a quiet window rather than as a question nobody could have meant.",
                    FromDate,
                    ToDate));
        }
        else if (DayCount > MaxWindowDays)
        {
            problems.Add(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The probed window {0:" + DateFormat + "} to {1:" + DateFormat + "} spans {2} day(s), and "
                    + "{3} is the most this makes one request for. The summaries feed takes no offset and no "
                    + "limit, so a window is a single unpaged response -- a wider one asks a shared service "
                    + "for every version in the state at once, and would most likely time out and measure "
                    + "nothing.",
                    FromDate,
                    ToDate,
                    DayCount,
                    MaxWindowDays));
        }

        return problems;
    }

    /// <summary>How many days the window covers, counting both ends.</summary>
    public int DayCount => ToDate.DayNumber - FromDate.DayNumber + 1;

    /// <summary>The window, for a console line.</summary>
    /// <returns>Both dates in the one accepted format. Contains nothing else.</returns>
    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0:" + DateFormat + "} to {1:" + DateFormat + "} inclusive ({2} day(s))",
            FromDate,
            ToDate,
            DayCount);

    private static DateOnly? ParseOne(
        string? text,
        string switchName,
        string position,
        List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            problems.Add(
                $"{switchName} needs two dates and the {position} one is missing. Both ends are required and "
                + $"neither has a default: '{switchName} {DateFormat} {DateFormat}'.");

            return null;
        }

        // Exact, invariant, DateTimeStyles.None. TryParse would accept the machine's short date pattern and
        // therefore accept an ambiguous value on a machine configured one way and refuse it on another --
        // and a scheduled task's arguments outlive the workstation they were typed on.
        if (!DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None,
                out DateOnly value))
        {
            problems.Add(
                $"'{text}' is not a date {switchName} can use. The format is {DateFormat} and it is the only "
                + "one accepted, because 06/09/2026 means two different days depending on who reads it and "
                + "this switch reports what a window costs.");

            return null;
        }

        return value;
    }
}
