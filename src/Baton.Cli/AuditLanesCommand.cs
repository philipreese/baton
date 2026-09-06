using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Domain;
using Baton.Status;
using Baton.Store;

namespace Baton.Cli;

/// <summary>
/// One room's tool-step audit (#1921). Every count is nullable as a UNIT rather than field by field:
/// they come from <see cref="ToolStepTally.Snapshot"/>, which decides present-or-absent once for all
/// four, so a room can never report three measured counts and a fourth that defaulted.
/// </summary>
/// <param name="Room">The room directory's name — its identity, and the whole of it (the discarded prefix is the operator's home directory).</param>
/// <param name="Vendors">The adapters this room's counted executions resolved to, sorted. Empty when none was ever recorded.</param>
/// <param name="Executions">How many settled executions contributed. Never zero on a reported room.</param>
/// <param name="LastActivity">The room's flow-log last-write time in UTC — the clock <c>--since</c> filters on.</param>
public sealed record AuditLanesRoom(
    [property: JsonPropertyName("room")] string Room,
    [property: JsonPropertyName("vendors")] IReadOnlyList<string> Vendors,
    [property: JsonPropertyName("executions")] int Executions,
    [property: JsonPropertyName("lastActivity")] DateTime LastActivity,
    [property: JsonPropertyName("toolSteps")] int ToolSteps,
    [property: JsonPropertyName("refused")] int Refused,
    [property: JsonPropertyName("repeated")] int Repeated,
    [property: JsonPropertyName("emptyResults")] int EmptyResults);

/// <summary>One vendor's totals across every reported room (#1921).</summary>
public sealed record AuditLanesVendorTotal(
    [property: JsonPropertyName("vendor")] string Vendor,
    [property: JsonPropertyName("executions")] int Executions,
    [property: JsonPropertyName("toolSteps")] int ToolSteps,
    [property: JsonPropertyName("refused")] int Refused,
    [property: JsonPropertyName("repeated")] int Repeated,
    [property: JsonPropertyName("emptyResults")] int EmptyResults);

/// <summary>
/// What one <c>baton audit lanes</c> run found (#1921) — the JSON contract, and what the text view
/// renders from.
/// </summary>
/// <param name="RoomsWalked">
/// Every immediate child of the root that <see cref="AuditLanesOptions.Since"/> admitted, whether or not
/// it yielded counts. Reported so <see cref="Rooms"/>.Count being smaller is visibly a filter and not a
/// fleet that shrank.
/// </param>
/// <param name="RoomsWithoutCounts">
/// Rooms walked whose streams carried no tool activity this reader could parse — the honest denominator
/// for <see cref="Rooms"/>, and never a claim that those lanes ran no tools.
/// </param>
/// <param name="RoomsExcludedByVendor">
/// Rooms walked that are absent from <see cref="Rooms"/> because <see cref="AuditLanesOptions.Vendor"/>
/// removed their executions — the operator's own filter, not a stream this reader could not parse. Its
/// own bucket because the two have opposite readings: this one says nothing about the room's stream, and
/// counting it under <see cref="RoomsWithoutCounts"/> made the disclosure sentence false of every room a
/// narrow <c>--vendor</c> excluded (#1921 review). A room some of whose executions the filter removed and
/// the rest of whose executions carried no readable activity is counted HERE, because the filter is the
/// explanation the operator can act on.
/// </param>
public sealed record AuditLanesReport(
    [property: JsonPropertyName("roomsWalked")] int RoomsWalked,
    [property: JsonPropertyName("roomsWithoutCounts")] int RoomsWithoutCounts,
    [property: JsonPropertyName("roomsExcludedByVendor")] int RoomsExcludedByVendor,
    [property: JsonPropertyName("rooms")] IReadOnlyList<AuditLanesRoom> Rooms,
    [property: JsonPropertyName("byVendor")] IReadOnlyList<AuditLanesVendorTotal> ByVendor);

/// <summary>
/// <c>baton audit lanes</c> (#1921, operator scope addition 2026-09-05): per room and per vendor — tool
/// calls, refused, identical repeats, empty results. The room-level read of the same figures
/// <c>Accounting.CostLedgerEntry.ToolSteps</c> carries per execution.
/// </summary>
/// <remarks>
/// <para>
/// <b>It reads through <see cref="ExecutionUsageProjector"/> and derives nothing of its own</b>, which
/// is the whole design: the numbers this prints and the numbers a cost-ledger row carries come from one
/// reader over one stream, so a lane's audit and its row cannot disagree. This replaces a conductor's
/// one-off Python scanner that parsed <c>.stdout.log</c> by hand — the shape #1921 exists to retire.
/// </para>
/// <para>
/// <b>Fail-open per room, never fatal.</b> An unreadable flow log, a room mid-write, a room with no
/// journal at all: the room contributes nothing and the walk continues. Exit code 0 unless the
/// invocation itself was malformed — this verb reports on a fleet's waste and must never itself become
/// a reason a script stops.
/// </para>
/// <para>
/// Not a <see cref="CommandResult"/>/<c>FlowStateReporter</c> command, for the reason
/// <see cref="MemoryAuditCommand"/> is not: there is no workflow pump here to report on.
/// </para>
/// </remarks>
public static class AuditLanesCommand
{
    public static async Task<int> ExecuteAsync(
        AuditLanesOptions options,
        TextWriter output,
        DateTime? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        if (options.Help)
        {
            output.WriteLine(AuditLanesOptionsParser.Usage);
            foreach (var line in AuditLanesOptionsParser.HelpLines)
            {
                output.WriteLine(line);
            }

            return 0;
        }

        var report = await BuildReportAsync(options, now ?? DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        if (options.Format == AuditLanesOutputFormat.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return 0;
        }

        WriteText(report, options, output);
        return 0;
    }

    /// <summary>
    /// The walk. Separated from the rendering so a test asserts hand-counted numbers against the report
    /// rather than against a string it would have to re-parse.
    /// </summary>
    internal static async Task<AuditLanesReport> BuildReportAsync(
        AuditLanesOptions options,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var roomsRoot = options.RoomsRoot is { Length: > 0 } root ? root : BatonPaths.Rooms;
        var cutoff = options.Since is { } since ? now - since : (DateTime?)null;

        List<AuditLanesRoom> rooms = [];
        var roomsWalked = 0;
        var roomsWithoutCounts = 0;
        var roomsExcludedByVendor = 0;

        foreach (var roomDirectoryPath in EnumerateRoomDirectories(roomsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var flowLogPath = Path.Combine(roomDirectoryPath, BatonPaths.FlowLogFileName);
            if (!File.Exists(flowLogPath))
            {
                continue;
            }

            DateTime lastActivity;
            try
            {
                lastActivity = File.GetLastWriteTimeUtc(flowLogPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (cutoff is { } floor && lastActivity < floor)
            {
                continue;
            }

            roomsWalked++;

            IReadOnlyList<LogEntry> entries;
            try
            {
                entries = await new FlowEventLogReader(flowLogPath)
                    .ReadAllEntriesWithTimestampsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BatonFlowException)
            {
                // Fail-open per room: a journal being written right now, or one this reader cannot
                // parse, costs this room's counts and not the walk.
                roomsWithoutCounts++;
                continue;
            }

            var artifactsRootPath = Path.Combine(roomDirectoryPath, Baton.Artifacts.ArtifactManager.ArtifactsDirectoryName);
            var usageByExecutionId = ExecutionUsageProjector.BuildByExecutionId(
                entries, artifactsRootPath, roomDirectoryPath: roomDirectoryPath);
            var bindings = ExecutionBindingResolver.Resolve(entries);

            var vendors = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var executions = 0;
            var toolSteps = 0;
            var refused = 0;
            var repeated = 0;
            var emptyResults = 0;
            var excludedByVendor = 0;

            foreach (var (executionId, usage) in usageByExecutionId)
            {
                bindings.TryGetValue(executionId, out var binding);
                if (options.Vendor is { Length: > 0 } wanted
                    && !string.Equals(binding.Adapter, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    excludedByVendor++;
                    continue;
                }

                // The all-four-or-none gate, read once. An execution whose stream carried no readable
                // tool activity contributes nothing rather than four zeros, so a room's totals are a sum
                // over executions that were actually measured.
                if (usage.ToolSteps is not { } executionToolSteps)
                {
                    continue;
                }

                executions++;
                toolSteps += executionToolSteps;
                refused += usage.RefusedToolSteps ?? 0;
                repeated += usage.RepeatedToolSteps ?? 0;
                emptyResults += usage.EmptyToolResults ?? 0;
                if (binding.Adapter is { Length: > 0 } adapter)
                {
                    vendors.Add(adapter);
                }
            }

            if (executions == 0)
            {
                // Which of the two buckets: the operator's filter is the explanation whenever it
                // removed anything here, and only a room it removed nothing from is a room this reader
                // found nothing in.
                if (excludedByVendor > 0)
                {
                    roomsExcludedByVendor++;
                }
                else
                {
                    roomsWithoutCounts++;
                }

                continue;
            }

            rooms.Add(new AuditLanesRoom(
                Path.GetFileName(roomDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                [.. vendors],
                executions,
                lastActivity,
                toolSteps,
                refused,
                repeated,
                emptyResults));
        }

        // Worst first, then by room name so the order is total rather than "whatever the filesystem
        // enumerated": the reading this verb exists for is "which lane wasted most", and a report whose
        // order changes between two runs over an unchanged fleet cannot be diffed.
        rooms.Sort(static (left, right) =>
        {
            var byRefused = right.Refused.CompareTo(left.Refused);
            if (byRefused != 0)
            {
                return byRefused;
            }

            var byRepeated = right.Repeated.CompareTo(left.Repeated);
            return byRepeated != 0 ? byRepeated : string.CompareOrdinal(left.Room, right.Room);
        });

        return new AuditLanesReport(
            roomsWalked, roomsWithoutCounts, roomsExcludedByVendor, rooms, BuildVendorTotals(rooms));
    }

    /// <summary>
    /// Per-vendor totals.
    /// <para>
    /// <b>A room that ran more than one vendor is counted into each of them</b>, which is a real
    /// double-count of that room's steps across the breakdown and is why the breakdown's rows do not sum
    /// to the fleet total. The alternative — attributing a mixed room to nothing, or to its first vendor
    /// — would make the mixed rooms invisible in exactly the view a vendor comparison is read from. The
    /// text view says so where it is read; nothing here silently reconciles the two.
    /// </para>
    /// </summary>
    private static IReadOnlyList<AuditLanesVendorTotal> BuildVendorTotals(IReadOnlyList<AuditLanesRoom> rooms)
    {
        Dictionary<string, AuditLanesVendorTotal> totals = new(StringComparer.OrdinalIgnoreCase);
        foreach (var room in rooms)
        {
            foreach (var vendor in room.Vendors)
            {
                var running = totals.TryGetValue(vendor, out var existing)
                    ? existing
                    : new AuditLanesVendorTotal(vendor, 0, 0, 0, 0, 0);
                totals[vendor] = running with
                {
                    Executions = running.Executions + room.Executions,
                    ToolSteps = running.ToolSteps + room.ToolSteps,
                    Refused = running.Refused + room.Refused,
                    Repeated = running.Repeated + room.Repeated,
                    EmptyResults = running.EmptyResults + room.EmptyResults,
                };
            }
        }

        return [.. totals.Values.OrderBy(total => total.Vendor, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The room directories to consider. Absent or unreadable root yields none rather than throwing: a
    /// machine that has never run a lane is not a malformed invocation.
    /// </summary>
    private static IReadOnlyList<string> EnumerateRoomDirectories(string roomsRoot)
    {
        try
        {
            return Directory.Exists(roomsRoot)
                ? [.. Directory.EnumerateDirectories(roomsRoot).OrderBy(path => path, StringComparer.Ordinal)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void WriteText(AuditLanesReport report, AuditLanesOptions options, TextWriter output)
    {
        output.WriteLine(
            $"Lane tool-step audit — {Number(report.RoomsWalked)} room(s) walked"
            + (options.Since is { } since ? $", within {DescribeDuration(since)}" : string.Empty)
            + (options.Vendor is { Length: > 0 } vendor ? $", vendor {vendor}" : string.Empty));

        if (report.Rooms.Count == 0)
        {
            // The narrow-filter case reaches HERE, not the disclosure at the bottom: `--vendor` naming
            // an adapter no walked room ran empties the report entirely, and saying "no room carried
            // tool activity" of rooms nobody looked inside is the false reading this verb exists to
            // prevent.
            output.WriteLine(report.RoomsExcludedByVendor > 0
                ? $"  no room reported counts under --vendor {options.Vendor}."
                : "  no room carried tool activity this reader could parse. That is not a claim that no "
                    + "lane ran tools.");
            WriteUncountedDisclosure(report, options, output);
            return;
        }

        foreach (var room in report.Rooms)
        {
            output.WriteLine(
                $"  {room.Room}  {(room.Vendors.Count > 0 ? string.Join(",", room.Vendors) : "(unknown vendor)")}"
                + $"  {Number(room.Executions)} exec"
                + $"  steps {Number(room.ToolSteps)}"
                + $"  refused {Number(room.Refused)}"
                + $"  repeated {Number(room.Repeated)}"
                + $"  empty {Number(room.EmptyResults)}");
        }

        output.WriteLine();
        output.WriteLine("By vendor (a room that ran two vendors counts into both, so these do not sum to the fleet):");
        foreach (var total in report.ByVendor)
        {
            output.WriteLine(
                $"  {total.Vendor}  {Number(total.Executions)} exec"
                + $"  steps {Number(total.ToolSteps)}"
                + $"  refused {Number(total.Refused)}"
                + $"  repeated {Number(total.Repeated)}"
                + $"  empty {Number(total.EmptyResults)}");
        }

        WriteUncountedDisclosure(report, options, output);
    }

    /// <summary>
    /// Why a walked room is absent from the table, one sentence per cause and never one sentence for
    /// both: a stream this reader could not read is a fact about the room, and a <c>--vendor</c>
    /// exclusion is a fact about the invocation.
    /// </summary>
    private static void WriteUncountedDisclosure(
        AuditLanesReport report, AuditLanesOptions options, TextWriter output)
    {
        if (report.RoomsWithoutCounts > 0)
        {
            output.WriteLine();
            output.WriteLine(
                $"{Number(report.RoomsWithoutCounts)} walked room(s) reported no counts — their streams "
                + "carried no tool activity this reader could parse, were never captured, or were not "
                + "whole. Not a measurement of zero tools.");
        }

        if (report.RoomsExcludedByVendor > 0)
        {
            output.WriteLine();
            output.WriteLine(
                $"{Number(report.RoomsExcludedByVendor)} walked room(s) ran no execution --vendor "
                + $"{options.Vendor} admitted. Nothing was read about those rooms' streams, and this is "
                + "not a claim that they carried no tool activity.");
        }
    }

    private static string DescribeDuration(TimeSpan since) =>
        since.TotalDays >= 1 && since == TimeSpan.FromDays(Math.Round(since.TotalDays))
            ? $"{Number((int)since.TotalDays)}d"
            : since.TotalHours >= 1 && since == TimeSpan.FromHours(Math.Round(since.TotalHours))
                ? $"{Number((int)since.TotalHours)}h"
                : $"{Number((int)since.TotalMinutes)}m";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
