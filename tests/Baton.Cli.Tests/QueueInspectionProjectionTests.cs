using Baton.Cli;
using Baton.Domain;
using Baton.Queue;

namespace Baton.Cli.Tests;

public sealed class QueueInspectionProjectionTests
{
    [Fact]
    public async Task Projection_preserves_unknown_evidence_and_lifecycle_reason_fallback()
    {
        var item = Item("inspection", stage: WorkStage.Review) with
        {
            Repository = null,
            PullRequest = 42,
            LifecycleReason = "fallback rationale",
            Requirements = null,
        };

        var row = Assert.Single(await QueueInspectionProjection.ProjectAsync(
            [item],
            observations: null,
            decisions: [],
            TestContext.Current.CancellationToken));

        Assert.Null(row.PullRequest!.State);
        Assert.Null(row.Requirements.Declared);
        Assert.Equal("fallback rationale", row.Routing.LifecycleReason);
        Assert.Equal("review", row.Lifecycle.Stage);
    }

    [Fact]
    public void Paging_is_stable_and_continuation_is_tied_to_the_snapshot()
    {
        var items = new[] { Item("one"), Item("two"), Item("three") };
        var snapshot = new QueueSnapshot(items);
        var fingerprint = QueueInspectionProjection.Fingerprint(snapshot);

        var first = QueueInspectionProjection.PageItems(items, fingerprint, true, false, 2, null);
        var second = QueueInspectionProjection.PageItems(items, fingerprint, true, false, 2, first.NextCursor);

        Assert.Equal(["one", "two"], first.Items.Select(item => item.Tag));
        Assert.Equal(["three"], second.Items.Select(item => item.Tag));
        Assert.Null(second.NextCursor);

        var changed = new QueueSnapshot([Item("one"), Item("changed"), Item("three")]);
        var refusal = Assert.Throws<CliArgumentException>(() => QueueInspectionProjection.PageItems(
            changed.Items,
            QueueInspectionProjection.Fingerprint(changed),
            true,
            false,
            2,
            first.NextCursor));
        Assert.Equal(QueueInspectionProjection.ChangedSnapshotMessage, refusal.Message);
    }

    [Fact]
    public void Malformed_cursor_has_a_restart_required_operator_message()
    {
        var refusal = Assert.Throws<CliArgumentException>(() => QueueInspectionProjection.PageItems(
            [Item("one")], "fingerprint", true, false, 1, "not-a-cursor"));

        Assert.Equal(QueueInspectionProjection.MalformedCursorMessage, refusal.Message);
    }

    [Fact]
    public void Page_size_contract_is_bounded()
    {
        Assert.Equal(50, QueueInspectionProjection.DefaultPageSize);
        Assert.Equal(200, QueueInspectionProjection.MaxPageSize);
    }

    private static QueueItem Item(
        string tag,
        QueueItemState state = QueueItemState.Queued,
        WorkStage? stage = null) => new()
        {
            Tag = tag,
            Role = "implement",
            Workspace = $"C:\\workspace\\{tag}",
            SpecFile = $"C:\\specs\\{tag}.md",
            State = state,
            Stage = stage,
            Requirements = [],
            DeclaredTaskSize = TaskSizeDeclaration.Parse("medium", "one bounded seam"),
        };
}
