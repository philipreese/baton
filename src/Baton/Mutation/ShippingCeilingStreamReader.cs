using Baton.Dispatch;
using Baton.Status;

namespace Baton.Mutation;

/// <summary>
/// #1998: reads a finished execution's captured stream for the one question the delivery check needs —
/// <b>was the last command this lane ran a shipping-class command that Baton killed at its ceiling?</b>
/// If it was, and the branch is not on origin, the room's <c>branch-not-pushed</c> refusal can say why
/// instead of stating the symptom.
/// <para>
/// The vendor decides what a line means (<see cref="IWorkerUsageParser.ReportsShippingCeilingTimeout"/>,
/// whose own doc states the tri-state and the anchoring rule); this class only walks the stream in
/// order and keeps the last answer that was not null. Reads the rolled-out segment before the live one
/// for the same reason <c>MutationInterface.CountToolCallsFromStdoutLog</c> does — a long run's stream
/// is two files, and "last" is only meaningful across both.
/// </para>
/// </summary>
public static class ShippingCeilingStreamReader
{
    /// <summary>
    /// <see langword="true"/> only when the FINAL run-command result in the captured stream is a
    /// shipping-class ceiling kill. A parser that answers nothing (no parser at all, or a vendor Baton
    /// enforces no ceiling on) gives <see langword="false"/> — the pre-#1998 reading, never a guess.
    /// </summary>
    public static bool FinalRunCommandHitShippingCeiling(IWorkerUsageParser? usageParser, string outputDirectory)
    {
        if (usageParser is null || string.IsNullOrWhiteSpace(outputDirectory))
        {
            return false;
        }

        var lastAnswer = false;
        foreach (var fileName in new[] { ExecutionStreamLogger.StdoutRolloverFileName, ExecutionStreamLogger.StdoutLogFileName })
        {
            var path = Path.Combine(outputDirectory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            foreach (var line in File.ReadLines(path))
            {
                if (usageParser.ReportsShippingCeilingTimeout(line) is { } answer)
                {
                    lastAnswer = answer;
                }
            }
        }

        return lastAnswer;
    }
}
