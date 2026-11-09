using System.Globalization;
using Meep2.Meeplang.Compilation;
using Meep2.Meeplang.Parsing;
using Meep2.Meeplang.Syntax;
using Meep2.Runtime;

namespace Meep2.Meeplang.Loading;

/// <summary>
/// The parts of an error policy a file sets, such as <c>log, stop-branch after 5 in 1s</c>. Parts it leaves out
/// come from the level above: a branch's from the module above it, the pipeline's from the system defaults.
/// </summary>
internal sealed class ErrorPolicyParts
{
    public bool? Log { get; set; }

    public bool SetsStopBranch { get; set; }

    public FailureLimit? StopBranch { get; set; }

    public bool SetsStopPipeline { get; set; }

    public double? StopPipelineAtPercent { get; set; }

    public bool SetsRestart { get; set; }

    public SourceRestart? Restart { get; set; }

    public bool SetsRestartPipeline { get; set; }

    public PipelineRestart? RestartPipeline { get; set; }

    public ErrorPolicy ApplyTo(ErrorPolicy basis) => basis with
    {
        LogUnhandled = Log ?? basis.LogUnhandled,
        StopBranch = SetsStopBranch ? StopBranch : basis.StopBranch,
        StopPipelineAtPercent = SetsStopPipeline ? StopPipelineAtPercent : basis.StopPipelineAtPercent,
        RestartSources = SetsRestart ? Restart : basis.RestartSources,
        RestartPipeline = SetsRestartPipeline ? RestartPipeline : basis.RestartPipeline,
    };
}

/// <summary>
/// Reads an error policy: parts separated by commas, each one of
/// <c>log</c> or <c>ignore</c>;
/// <c>stop-branch after 5 in 1s</c> or <c>stop-branch never</c>;
/// <c>stop-pipeline at 10%</c> or <c>stop-pipeline never</c>;
/// <c>restart after 100ms up to 3 times</c> or <c>restart never</c>;
/// <c>restart-pipeline after 1m up to 5 times in 1h</c> or <c>restart-pipeline never</c>.
/// </summary>
internal static class ErrorPolicyParser
{
    private const string Grammar =
        "An error policy is made of parts separated by commas: log or ignore, stop-branch after 5 in 1s, stop-pipeline at 10%, restart after 100ms up to 3 times, and restart-pipeline after 1m up to 5 times in 1h. Each of the last four can be never instead.";

    /// <summary>Reads a policy written at <paramref name="start"/>. Problems are reported there; returns null if there were any.</summary>
    /// <param name="forPipeline">Whether this is the pipeline's policy. Only the pipeline's can have stop-pipeline.</param>
    public static ErrorPolicyParts? Parse(string text, SourceSpan start, bool forPipeline, Diagnostics diagnostics)
    {
        var parts = new ErrorPolicyParts();
        var problems = false;
        var offset = 0;
        foreach (var raw in text.Split(','))
        {
            var lead = raw.Length - raw.TrimStart().Length;
            var part = raw.Trim();
            var span = new SourceSpan(start.Line, start.Column + offset + lead, Math.Max(part.Length, 1));
            offset += raw.Length + 1;

            if (part.Length == 0)
            {
                diagnostics.Error("This part of the error policy is empty. " + Grammar, span);
                problems = true;
                continue;
            }
            if (!ParsePart(part, span, forPipeline, parts, diagnostics))
                problems = true;
        }
        return problems ? null : parts;
    }

    private static bool ParsePart(string part, SourceSpan span, bool forPipeline, ErrorPolicyParts parts, Diagnostics diagnostics)
    {
        var words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (words)
        {
            case ["log"] or ["ignore"]:
                if (parts.Log is not null)
                    return Twice("log or ignore", span, diagnostics);
                parts.Log = words[0] == "log";
                return true;

            case ["stop-branch", ..]:
                if (parts.SetsStopBranch)
                    return Twice("stop-branch", span, diagnostics);
                parts.SetsStopBranch = true;
                if (words is ["stop-branch", "never"])
                    return true;
                if (words is ["stop-branch", "after", var count, "in", var within]
                    && int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var failures) && failures >= 1
                    && TryDuration(within, out var duration) && duration > TimeSpan.Zero)
                {
                    parts.StopBranch = new FailureLimit(failures, duration);
                    return true;
                }
                return Wrong("stop-branch needs a number of failures and a time, as in stop-branch after 5 in 1s, or never.", span, diagnostics);

            case ["stop-pipeline", ..]:
                if (!forPipeline)
                    return Wrong("stop-pipeline is for the whole pipeline, so it goes in the defaults block's on-error, not on a module.", span, diagnostics);
                if (parts.SetsStopPipeline)
                    return Twice("stop-pipeline", span, diagnostics);
                parts.SetsStopPipeline = true;
                if (words is ["stop-pipeline", "never"])
                    return true;
                if (Percentage(words) is { } percent and > 0 and <= 100)
                {
                    parts.StopPipelineAtPercent = percent;
                    return true;
                }
                return Wrong("stop-pipeline needs a percentage more than 0 and at most 100, as in stop-pipeline at 10%, or never.", span, diagnostics);

            case ["restart", ..]:
                if (parts.SetsRestart)
                    return Twice("restart", span, diagnostics);
                parts.SetsRestart = true;
                if (words is ["restart", "never"])
                    return true;
                if (words is ["restart", "after", var delay, "up", "to", var count2, "times" or "time"]
                    && TryDuration(delay, out var wait)
                    && int.TryParse(count2, NumberStyles.None, CultureInfo.InvariantCulture, out var times) && times >= 1)
                {
                    parts.Restart = new SourceRestart(wait, times);
                    return true;
                }
                return Wrong("restart needs a delay and a number of failures in a row, as in restart after 100ms up to 3 times, or never.", span, diagnostics);

            case ["restart-pipeline", ..]:
                if (!forPipeline)
                    return Wrong("restart-pipeline is for the whole pipeline, so it goes in the defaults block's on-error, not on a module.", span, diagnostics);
                if (parts.SetsRestartPipeline)
                    return Twice("restart-pipeline", span, diagnostics);
                parts.SetsRestartPipeline = true;
                if (words is ["restart-pipeline", "never"])
                    return true;
                if (words is ["restart-pipeline", "after", var pause, "up", "to", var count3, "times" or "time", .. var rest]
                    && TryDuration(pause, out var delay3)
                    && int.TryParse(count3, NumberStyles.None, CultureInfo.InvariantCulture, out var times3) && times3 >= 1)
                {
                    TimeSpan? counted = null;
                    if (rest is ["in", var window] && TryDuration(window, out var stretch) && stretch > TimeSpan.Zero)
                        counted = stretch;
                    else if (rest.Length > 0)
                        return Wrong("restart-pipeline can end with a stretch of time to count restarts in, as in restart-pipeline after 1m up to 5 times in 1h.", span, diagnostics);
                    parts.RestartPipeline = new PipelineRestart(delay3, times3, counted);
                    return true;
                }
                return Wrong("restart-pipeline needs a delay and a number of restarts, as in restart-pipeline after 1m up to 5 times, which can end with in 1h to count them within an hour. Or it can be never.", span, diagnostics);

            default:
                return Wrong($"'{part}' isn't part of an error policy.{Suggestions.For(words[0], ["log", "ignore", "stop-branch", "stop-pipeline", "restart", "restart-pipeline"])} {Grammar}", span, diagnostics);
        }
    }

    // "at 10%" or "at 10 %".
    private static double? Percentage(string[] words)
    {
        var text = words switch
        {
            ["stop-pipeline", "at", var number] when number.EndsWith('%') => number[..^1],
            ["stop-pipeline", "at", var number, "%"] => number,
            _ => null,
        };
        return text is not null && double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    // A duration as Meeplang writes them: 250ms, 5s, 1h30m, or 00:00:05.
    private static bool TryDuration(string word, out TimeSpan duration)
    {
        duration = default;
        var problems = new Diagnostics(path: null);
        var tokens = Lexer.Lex(word, 1, 1, problems);
        if (problems.HasErrors || tokens is not [{ Kind: TokenKind.Duration, Value: TimeSpan value }, { Kind: TokenKind.End }])
            return false;
        duration = value;
        return true;
    }

    private static bool Twice(string part, SourceSpan span, Diagnostics diagnostics) =>
        Wrong($"This error policy sets {part} twice.", span, diagnostics);

    private static bool Wrong(string message, SourceSpan span, Diagnostics diagnostics)
    {
        diagnostics.Error(message, span);
        return false;
    }
}
