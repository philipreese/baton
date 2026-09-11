using System.Text.Json;
using Baton.Cli.Daemon;
using Baton.Domain;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests.Daemon;

public sealed class FleetEventLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"baton-fleet-events-{Guid.NewGuid():N}");
    private string Live => Path.Combine(_root, "events.jsonl");
    private string Rollover => Path.Combine(_root, "events.1.jsonl");

    public FleetEventLogTests() => Directory.CreateDirectory(_root);

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_root);

    [Fact]
    public async Task Ids_remain_monotonic_across_restart_and_rotation_while_replay_reads_only_live_file()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var attempt = new FleetAttemptId("attempt-a");
        var firstLog = new FleetEventLog(Live, Rollover, maxLiveBytes: 1);
        var first = await firstLog.Append(Draft(FleetEventKind.AdmissionDecided, "admission:a", attempt), cancellationToken);
        var second = await firstLog.Append(Draft(FleetEventKind.AttemptStarted, "started:a", attempt), cancellationToken);

        var restarted = new FleetEventLog(Live, Rollover, maxLiveBytes: 1);
        var third = await restarted.Append(Draft(FleetEventKind.AttemptSettled, "settled:a", attempt), cancellationToken);

        Assert.Equal(1, first!.Id);
        Assert.Equal(2, second!.Id);
        Assert.Equal(3, third!.Id);
        Assert.True(File.Exists(Rollover));
        Assert.Equal([third], await restarted.ReadAfter(0, cancellationToken));
    }

    [Fact]
    public async Task A_replayed_producer_fact_is_deduplicated_after_restart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstLog = new FleetEventLog(Live, Rollover, maxLiveBytes: 100_000);
        var draft = Draft(FleetEventKind.AttemptSettled, "settled:attempt-a", new FleetAttemptId("attempt-a"));
        Assert.NotNull(await firstLog.Append(draft, cancellationToken));

        var restarted = new FleetEventLog(Live, Rollover, maxLiveBytes: 100_000);
        Assert.Null(await restarted.Append(draft, cancellationToken));
        Assert.Single(await restarted.ReadAfter(0, cancellationToken));
    }

    [Fact]
    public async Task Missing_identity_and_usage_values_stay_absent_on_the_wire()
    {
        var log = new FleetEventLog(Live, Rollover, maxLiveBytes: 100_000);
        await log.Append(new FleetEventDraft(
            FleetEventKind.DaemonStarted, "daemon:start:one", DateTimeOffset.Parse("2026-09-11T12:00:00-04:00")),
            TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(Assert.Single(File.ReadAllLines(Live)));
        Assert.Equal("2026-09-11T16:00:00+00:00", json.RootElement.GetProperty("at").GetString());
        Assert.False(json.RootElement.TryGetProperty("attemptId", out _));
        Assert.False(json.RootElement.TryGetProperty("roomId", out _));
        Assert.False(json.RootElement.TryGetProperty("executionId", out _));
        Assert.False(json.RootElement.TryGetProperty("usage", out _));
    }

    [Fact]
    public void The_named_vocabulary_contains_every_kind_a_producer_can_write()
    {
        Assert.Equal(Enum.GetValues<FleetEventKind>().Length, FleetEventKinds.All.Count);
        Assert.All(FleetEventKinds.All, kind => Assert.True(Enum.IsDefined(kind)));
    }

    [Theory]
    [InlineData(FleetRevisionKind.Implementation, "implementation")]
    [InlineData(FleetRevisionKind.Repair, "repair")]
    public void Revision_kinds_use_the_specified_lowercase_wire_tokens(
        FleetRevisionKind revisionKind, string expectedToken)
    {
        var draft = new FleetEventDraft(
            FleetEventKind.RevisionProduced,
            $"revision:{expectedToken}",
            DateTimeOffset.Parse("2026-09-11T16:00:00Z"),
            RevisionKind: revisionKind);

        using var json = JsonDocument.Parse(FleetEventLog.Serialize(FleetEvent.From(1, draft)));

        Assert.Equal(expectedToken, json.RootElement.GetProperty("revisionKind").GetString());
    }

    [Fact]
    public async Task A_numeric_value_outside_the_named_vocabulary_never_reaches_disk()
    {
        var log = new FleetEventLog(Live, Rollover, maxLiveBytes: 100_000);
        var draft = new FleetEventDraft(
            (FleetEventKind)999,
            "invalid-kind",
            DateTimeOffset.Parse("2026-09-11T16:00:00Z"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => log.Append(draft, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Live));
    }

    [Fact]
    public async Task An_incomplete_final_row_is_tolerated_and_removed_before_the_next_append()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var log = new FleetEventLog(Live, Rollover, maxLiveBytes: 100_000);
        var attempt = new FleetAttemptId("attempt-torn");
        var first = await log.Append(Draft(FleetEventKind.AttemptStarted, "started:torn", attempt), cancellationToken);
        await File.AppendAllTextAsync(Live, "{\"id\":2,\"kind\":\"attemptSettled\"", cancellationToken);

        Assert.Equal([first!], await log.ReadAfter(0, cancellationToken));

        var second = await log.Append(
            Draft(FleetEventKind.AttemptSettled, "settled:torn", attempt), cancellationToken);

        Assert.Equal(2, second!.Id);
        Assert.Equal([first!, second], await log.ReadAfter(0, cancellationToken));
        Assert.Equal(2, File.ReadAllLines(Live).Length);
    }

    [Fact]
    public async Task A_malformed_complete_mid_file_row_is_reported_with_its_location()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var log = new FleetEventLog(Live, Rollover, maxLiveBytes: 100_000);
        var attempt = new FleetAttemptId("attempt-corrupt");
        await log.Append(Draft(FleetEventKind.AttemptStarted, "started:corrupt", attempt), cancellationToken);
        var later = FleetEvent.From(
            2, Draft(FleetEventKind.AttemptSettled, "settled:corrupt", attempt));
        await File.AppendAllTextAsync(
            Live, $"not-json\n{FleetEventLog.Serialize(later)}\n", cancellationToken);

        var replayError = await Assert.ThrowsAsync<FleetEventLogReadException>(
            () => log.ReadAfter(0, cancellationToken));
        Assert.Equal(Live, replayError.FilePath);
        Assert.Equal(2, replayError.LineNumber);

        var appendError = await Assert.ThrowsAsync<FleetEventLogReadException>(
            () => log.Append(Draft(FleetEventKind.AttemptProgressed, "progress:corrupt", attempt), cancellationToken));
        Assert.Equal(2, appendError.LineNumber);
    }

    [Fact]
    public async Task A_complete_unknown_kind_at_the_tail_is_reported_instead_of_treated_as_torn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var log = new FleetEventLog(Live, Rollover, maxLiveBytes: 100_000);
        await File.WriteAllTextAsync(
            Live,
            "{\"id\":1,\"at\":\"2026-09-11T16:00:00Z\",\"kind\":\"futureKind\",\"dedupeKey\":\"future:1\"}",
            cancellationToken);

        var error = await Assert.ThrowsAsync<FleetEventLogReadException>(
            () => log.ReadAfter(0, cancellationToken));
        Assert.Equal(1, error.LineNumber);
        Assert.Contains("Unknown fleet event kind", error.InnerException!.Message, StringComparison.Ordinal);
    }

    private static FleetEventDraft Draft(FleetEventKind kind, string key, FleetAttemptId attempt) =>
        new(kind, key, DateTimeOffset.Parse("2026-09-11T16:00:00Z"), AttemptId: attempt);
}
