using Baton.Vendors.Tests.TestSupport;
using Baton.Domain;
using Baton.Runway;

namespace Baton.Vendors.Tests;

/// <summary>
/// The bindings write seam's round-trip bar (M16 Phase 4, issue #153): a saved file must
/// round-trip through the exact <see cref="WorkerBindingConfigParser.Parse"/> every other consumer
/// uses — provable at this layer precisely because the writer lives beside its parser (the phase's
/// placement decision of record).
/// </summary>
public class WorkerBindingConfigWriterTests
{
    private static Dictionary<string, WorkerBindingConfigEntry> TwoWorkerConfig() => new()
    {
        ["architect"] = new WorkerBindingConfigEntry(
            "claude",
            new WorkerContract(
                "architect",
                RequiredInputs: [],
                ProducedOutputs:
                [
                    // Exercises every JsonScalar variant through OutputCondition — the one spot the
                    // opaque produced-outputs round trip (Baton.Ui's WorkerBindingEntryViewModel) could
                    // silently lose fidelity if it were tested with a bare { "Name": ... } only.
                    new ProducedOutput("plan", new OutputCondition("/status", new JsonScalar.String("done"))),
                ],
                OptionalMetadata: ["priority"]),
            "Draft a plan and write it to your output file.",
            TimeSpan.FromMinutes(5),
            Model: "claude-opus-4",
            PermissionScope: "write-only",
            WorkingDirectory: "/home/user/my-project",
            ToolSha: "deadbeef"),
        ["critic"] = new WorkerBindingConfigEntry(
            "gemini",
            new WorkerContract(
                "critic",
                RequiredInputs: ["plan"],
                ProducedOutputs:
                [
                    new ProducedOutput("review", new OutputCondition("/score", new JsonScalar.Number(1))),
                    new ProducedOutput("flag", new OutputCondition("/approved", new JsonScalar.Boolean(true))),
                    new ProducedOutput("note", new OutputCondition("/reason", JsonScalar.Null.Instance)),
                ],
                OptionalMetadata: []),
            "Review the plan.",
            TimeSpan.FromMinutes(1)),
    };

    [Fact]
    public async Task A_saved_config_round_trips_through_the_engines_own_parser()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bindings-writer-{Guid.NewGuid():N}.json");
        try
        {
            var config = TwoWorkerConfig();

            await WorkerBindingConfigWriter.SaveToFileAsync(config, path, TestContext.Current.CancellationToken);
            var parsed = await WorkerBindingConfigParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(config.Keys.OrderBy(k => k), parsed.Keys.OrderBy(k => k));
            foreach (var (workerName, entry) in config)
            {
                var parsedEntry = parsed[workerName];
                Assert.Equal(entry.Adapter, parsedEntry.Adapter);
                Assert.Equal(entry.PromptTemplate, parsedEntry.PromptTemplate);
                Assert.Equal(entry.Timeout, parsedEntry.Timeout);
                Assert.Equal(entry.Model, parsedEntry.Model);
                Assert.Equal(entry.PermissionScope, parsedEntry.PermissionScope);
                Assert.Equal(entry.WorkingDirectory, parsedEntry.WorkingDirectory);
                Assert.Equal(entry.Contract.WorkerName, parsedEntry.Contract.WorkerName);
                Assert.Equal(entry.Contract.RequiredInputs, parsedEntry.Contract.RequiredInputs);
                Assert.Equal(entry.Contract.OptionalMetadata, parsedEntry.Contract.OptionalMetadata);
                Assert.Equal(entry.Contract.ProducedOutputs.Count, parsedEntry.Contract.ProducedOutputs.Count);
                Assert.Equal(entry.ToolSha, parsedEntry.ToolSha);
                for (var i = 0; i < entry.Contract.ProducedOutputs.Count; i++)
                {
                    Assert.Equal(entry.Contract.ProducedOutputs[i].Name, parsedEntry.Contract.ProducedOutputs[i].Name);
                    Assert.Equal(entry.Contract.ProducedOutputs[i].Condition, parsedEntry.Contract.ProducedOutputs[i].Condition);
                }
            }
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task An_empty_config_is_valid_and_round_trips()
    {
        // The editor's New action mints exactly this shape (M16 Phase 4) — an empty config passes
        // the parser's checks (nothing to iterate), so a just-created bindings file is already a
        // parseable file.
        var path = Path.Combine(Path.GetTempPath(), $"bindings-writer-empty-{Guid.NewGuid():N}.json");
        try
        {
            await WorkerBindingConfigWriter.SaveToFileAsync(new Dictionary<string, WorkerBindingConfigEntry>(), path, TestContext.Current.CancellationToken);
            var parsed = await WorkerBindingConfigParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);

            Assert.Empty(parsed);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task An_entry_with_a_blank_adapter_is_rejected_at_write_time_and_nothing_is_written()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bindings-writer-invalid-{Guid.NewGuid():N}.json");
        var invalid = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                string.Empty,
                new WorkerContract("architect", [], [], []),
                "Draft a plan.",
                TimeSpan.FromMinutes(5)),
        };

        var exception = await Assert.ThrowsAsync<WorkerBindingConfigException>(
            () => WorkerBindingConfigWriter.SaveToFileAsync(invalid, path, TestContext.Current.CancellationToken));

        Assert.Contains("Adapter", exception.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task SaveToFileAsync_creates_missing_parent_directories()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bindings-writer-dirs-{Guid.NewGuid():N}", "nested");
        var path = Path.Combine(directory, "bindings.json");
        try
        {
            await WorkerBindingConfigWriter.SaveToFileAsync(TwoWorkerConfig(), path, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(Path.GetDirectoryName(directory)!);
        }
    }

    /// <summary>
    /// #1848 review: the tripwire under the documented key names. `spec/baton.md` §7 and
    /// `docs/dispatch.md` quote the runway override's on-disk shape, and this writer serializes with no
    /// naming policy — so the file carries <c>"RunwayOverride"</c>, not <c>"runwayOverride"</c>, down to
    /// the nested counters. Asserted as exact strings so the docs cannot drift away from the bytes
    /// without a test going red; changing the writer's naming policy would be a migration, not a fix.
    /// </summary>
    [Fact]
    public void Serialize_writes_the_runway_override_under_the_documented_pascal_case_keys()
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["advisor"] = new WorkerBindingConfigEntry(
                "claude",
                new WorkerContract("advise", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []),
                "Weigh the options.",
                TimeSpan.FromMinutes(5),
                RunwayOverride: new RunwayOverride(
                    "claude",
                    "conductor lane, week resets in 2h",
                    Used: true,
                    [new RunwayCounter("week (all models)", 87)],
                    "'week (all models)' is at 87% (holds at 85%)")),
        };

        var json = WorkerBindingConfigWriter.Serialize(config);

        Assert.Contains("\"RunwayOverride\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Vendor\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Reason\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Used\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Counters\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Window\"", json, StringComparison.Ordinal);
        Assert.Contains("\"PercentUsed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"HoldReason\"", json, StringComparison.Ordinal);

        // The polarity arm: the camelCase spelling the docs used to claim is genuinely absent, so this
        // test discriminates rather than passing on a file that happens to contain both.
        Assert.DoesNotContain("\"runwayOverride\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"percentUsed\"", json, StringComparison.Ordinal);

        // And it survives the parser every consumer reads the file with, not just the writer.
        var parsed = WorkerBindingConfigParser.Parse(json);
        Assert.Equal(87, parsed["advisor"].RunwayOverride!.Counters.Single().PercentUsed);
    }

    [Fact]
    public void Serialize_emits_indented_human_editable_json()
    {
        var json = WorkerBindingConfigWriter.Serialize(TwoWorkerConfig());

        Assert.Contains("\n", json);
        Assert.Equal(2, WorkerBindingConfigParser.Parse(json).Count);
    }

    /// <summary>
    /// #1266 / #1267: the replace loses to <b>any</b> open handle on the target, whatever share mode
    /// it was opened with. This is the measurement 0057's "Rests on" row cites, committed rather than
    /// left as an ad-hoc run — a decision record's evidence has to be re-runnable by whoever doubts it.
    /// </summary>
    /// <remarks>
    /// The `Delete`-sharing arm is the one that matters, and 0057's "Rests on" row holds why the
    /// intuition about it is wrong. Believing otherwise is what #1267 records being shipped as fact,
    /// and it made "open every reader delete-tolerant" look like a fix for rename contention.
    /// <para>
    /// The no-holder arm is the control, and it is not decoration: without it, a harness that could
    /// never move a file would report both share modes failing and read as a confirmation.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_replace_loses_to_an_open_handle_whatever_share_mode_it_used()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bindings-share-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            Assert.False(TryReplaceWhileHeld(directory, FileShare.ReadWrite | FileShare.Delete));
            Assert.False(TryReplaceWhileHeld(directory, FileShare.Read));
            Assert.True(
                TryReplaceWhileHeld(directory, share: null),
                "the replace failed with no holder at all, so this measures the harness rather than sharing");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>Stages a file and replaces <paramref name="share"/>-held target; true when the move landed.</summary>
    private static bool TryReplaceWhileHeld(string directory, FileShare? share)
    {
        var target = Path.Combine(directory, $"bindings-{Guid.NewGuid():N}.json");
        File.WriteAllText(target, "{}");
        var staging = target + ".tmp";
        File.WriteAllText(staging, "{}");

        var holder = share is { } s ? new FileStream(target, FileMode.Open, FileAccess.Read, s) : null;
        try
        {
            File.Move(staging, target, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            holder?.Dispose();
        }
    }

    /// <summary>
    /// #1266: the wall-clock budget survives a holder that the attempt-count budget it replaced could
    /// not. The mirror of <c>SnapshotBinderTests</c>'s arm for the same switch — without it this
    /// writer's fix rests on a sibling's measurement rather than its own, which is the analogy the
    /// second reader declined to accept.
    /// </summary>
    [Fact]
    public async Task A_transient_holder_outlasting_the_old_attempt_count_budget_no_longer_fails_the_write()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bindings-hold-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "bindings.json");
        await WorkerBindingConfigWriter.SaveToFileAsync(TwoWorkerConfig(), path, TestContext.Current.CancellationToken);

        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            // 30s injected rather than the production default: the claim is that the retry is still
            // running when the holder releases, and the wide margin keeps the test deterministic even
            // if its own 400ms pause is starved under load. The old budget was ~200ms of backoff.
            var save = WorkerBindingConfigWriter.SaveToFileAsync(
                TwoWorkerConfig(), path, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            // wait-ok: holding past the retired budget, not waiting for a result.
            await Task.Delay(400, TestContext.Current.CancellationToken);
            holder.Dispose();

            await save; // must NOT throw — the attempt-count budget would have given up by now.

            Assert.Equal(2, WorkerBindingConfigParser.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Count);
        }
        finally
        {
            holder.Dispose();
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// #1266: a replace that can never succeed surfaces rather than retrying forever, and takes its
    /// staging file with it. The budget is injected tiny so the exhaustion path runs immediately
    /// instead of burning the production five seconds on a failure that will never clear.
    /// </summary>
    /// <remarks>
    /// A directory at the destination is a permanent failure that needs no second process to
    /// manufacture. It surfaces as <see cref="IOException"/> on some platforms and
    /// <see cref="UnauthorizedAccessException"/> on others — the same pair the retry filter catches,
    /// which is exactly why this arm exists: those two types must not become unfailable just because
    /// the writer retries them.
    /// </remarks>
    [Fact]
    public async Task A_replace_that_can_never_succeed_surfaces_and_leaves_no_staging_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bindings-exhaust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "bindings.json");
        Directory.CreateDirectory(path);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => WorkerBindingConfigWriter.SaveToFileAsync(
                    TwoWorkerConfig(), path, TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken));

            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }
}
