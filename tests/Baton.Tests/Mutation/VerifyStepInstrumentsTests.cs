using System.Text;
using System.Text.Json;
using Baton.Domain;
using Baton.Mutation;
using Baton.Tests.Shared;

namespace Baton.Tests.Mutation;

/// <summary>
/// The engine's stamp on <c>verdict.json</c> (#1882). The load-bearing arm is the overwrite — see
/// <see cref="ReviewVerdict.Instruments"/> for what the field is for and why a model-authored value
/// must lose to the engine's. The other arm is preservation: the verdict schema tolerates unknown
/// fields at every level by design, so the stamp must not round-trip through the record type and
/// quietly delete a worker's own annotations.
/// </summary>
public class VerifyStepInstrumentsTests
{
    private static readonly IReadOnlyList<VerifyInstrument> Instruments =
    [
        new("dotnet build -warnaserror", 0, 34_300),
        new("dotnet test", 1, 91_002),
    ];

    private static string TempVerdict(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"verdict-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    [Fact]
    public async Task The_engine_overwrites_instruments_the_model_wrote_for_itself()
    {
        var path = TempVerdict(
            """
            {"reviewedRef": "1882-lane", "findings": [],
             "instruments": [{"command": "dotnet test", "exitCode": 0, "wallClockMs": 1}]}
            """);
        try
        {
            Assert.True(await VerifyStep.InjectInstrumentsAsync(path, Instruments, CancellationToken.None));

            var parsed = JsonDocument.Parse(File.ReadAllBytes(path));
            var written = parsed.RootElement.GetProperty("instruments");
            Assert.Equal(2, written.GetArrayLength());
            Assert.Equal("dotnet build -warnaserror", written[0].GetProperty("command").GetString());
            // The model's fabricated "dotnet test exited 0" is gone, replaced by the engine's real 1 --
            // not merged with it, and not appended after it.
            Assert.Equal("dotnet test", written[1].GetProperty("command").GetString());
            Assert.Equal(1, written[1].GetProperty("exitCode").GetInt32());
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task Every_other_field_the_worker_wrote_survives_the_stamp()
    {
        var path = TempVerdict(
            """
            {"reviewedRef": "1882-lane", "summary": "one defect", "completion": "complete",
             "findings": [{"severity": "high", "claim": "x", "status": "confirmed", "confidence": 0.9}],
             "model": "opus"}
            """);
        try
        {
            Assert.True(await VerifyStep.InjectInstrumentsAsync(path, Instruments, CancellationToken.None));

            var bytes = File.ReadAllBytes(path);
            var root = JsonDocument.Parse(bytes).RootElement;
            Assert.Equal("one defect", root.GetProperty("summary").GetString());
            Assert.Equal("complete", root.GetProperty("completion").GetString());
            // The two unknown extras the schema promises to tolerate -- one top level, one per finding.
            Assert.Equal("opus", root.GetProperty("model").GetString());
            Assert.Equal(0.9, root.GetProperty("findings")[0].GetProperty("confidence").GetDouble(), 3);

            // And it still parses as a verdict, with the instruments readable off the record.
            Assert.True(ReviewVerdictSchema.TryParse(bytes, out var verdict, out var error), error);
            Assert.Equal(2, verdict!.Instruments!.Count);
            Assert.Equal(91_002, verdict.Instruments[1].WallClockMs);
            Assert.Equal(ReviewCompletion.Complete, verdict.Completion);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task A_timed_out_instrument_reaches_the_verdict_with_a_null_exit_code()
    {
        var path = TempVerdict("""{"reviewedRef": "main", "findings": []}""");
        try
        {
            Assert.True(await VerifyStep.InjectInstrumentsAsync(
                path, [new VerifyInstrument("dotnet build", null, 600_000)], CancellationToken.None));

            Assert.True(ReviewVerdictSchema.TryParse(File.ReadAllBytes(path), out var verdict, out _));
            Assert.Null(Assert.Single(verdict!.Instruments!).ExitCode);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task A_verdict_that_is_absent_or_not_a_json_object_is_reported_not_thrown()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"verdict-{Guid.NewGuid():N}.json");
        Assert.False(await VerifyStep.InjectInstrumentsAsync(missing, Instruments, CancellationToken.None));

        var notAnObject = TempVerdict("[1, 2, 3]");
        var unparseable = TempVerdict("{not json");
        try
        {
            Assert.False(await VerifyStep.InjectInstrumentsAsync(notAnObject, Instruments, CancellationToken.None));
            Assert.False(await VerifyStep.InjectInstrumentsAsync(unparseable, Instruments, CancellationToken.None));

            // Untouched -- a failed stamp never damages what the worker wrote.
            Assert.Equal("[1, 2, 3]", File.ReadAllText(notAnObject));
        }
        finally
        {
            FileCleanup.Delete(notAnObject);
            FileCleanup.Delete(unparseable);
        }
    }

    /// <summary>
    /// Why the stamp renames instead of overwriting in place is stated on
    /// <see cref="VerifyStep.InjectInstrumentsAsync"/>. What is checkable without a race is only the
    /// observable residue, and that is all these two arms claim: after a SUCCESSFUL stamp the
    /// directory holds exactly the verdict and no temporary sibling. Neither arm asserts the mid-write
    /// window was closed — that is not observable from a test; this pins that the rename happened at
    /// all rather than a truncate-in-place having quietly replaced it.
    /// </summary>
    [Fact]
    public async Task The_stamp_leaves_no_temporary_file_behind()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"verdict-atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "verdict.json");
        try
        {
            await File.WriteAllTextAsync(
                path, """{"reviewedRef": "main", "findings": []}""", TestContext.Current.CancellationToken);

            Assert.True(await VerifyStep.InjectInstrumentsAsync(path, Instruments, CancellationToken.None));

            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
            Assert.True(ReviewVerdictSchema.TryParse(
                await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken), out var verdict, out _));
            Assert.Equal(Instruments.Count, verdict!.Instruments!.Count);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// The other polarity, and the arm that discriminates the cleanup: the destination is held open so
    /// the read succeeds and the RENAME fails, which is the one path that can strand a
    /// fully-written temporary in the execution's own output directory. Without the best-effort delete
    /// this arm finds two files instead of one. The worker's verdict is asserted intact besides.
    /// </summary>
    [Fact]
    public async Task A_failed_rewrite_leaves_no_stranded_temporary()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"verdict-atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "verdict.json");
        try
        {
            await File.WriteAllTextAsync(
                path, """{"reviewedRef": "main", "findings": []}""", TestContext.Current.CancellationToken);

            // Held open for exclusive write: the read succeeds, the rename does not.
            using (var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.False(await VerifyStep.InjectInstrumentsAsync(path, Instruments, CancellationToken.None));
            }

            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
            Assert.Equal(
                """{"reviewedRef": "main", "findings": []}""",
                await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void A_verdict_with_no_instruments_still_parses_and_reports_none()
    {
        // The schema bump is additive and optional: every verdict written before #1882, and every
        // review dispatched without --verify-cmd, has no instruments at all.
        Assert.True(ReviewVerdictSchema.TryParse(
            Encoding.UTF8.GetBytes("""{"reviewedRef": "main", "findings": []}"""), out var verdict, out _));

        Assert.Null(verdict!.Instruments);
    }

    [Fact]
    public async Task No_verify_step_removes_the_instruments_the_model_wrote_rather_than_leaving_them()
    {
        // Null instruments means "no step ran", and the field then has to be ABSENT -- not the array
        // the worker invented. This is the arm the overwrite test above cannot cover: there, an engine
        // value replaces the model's, so the field is right either way; here there is no engine value,
        // and doing nothing is precisely what leaves a fabricated test run on disk.
        var path = TempVerdict(
            """
            {"reviewedRef": "1882-lane", "summary": "looks fine", "findings": [],
             "instruments": [{"command": "dotnet test", "exitCode": 0, "wallClockMs": 91002}]}
            """);
        try
        {
            Assert.True(await VerifyStep.InjectInstrumentsAsync(path, instruments: null, CancellationToken.None));

            var root = JsonDocument.Parse(File.ReadAllBytes(path)).RootElement;
            Assert.False(root.TryGetProperty("instruments", out _));
            // Removal, never a rewrite of the whole verdict: the rest of what the worker wrote stands.
            Assert.Equal("looks fine", root.GetProperty("summary").GetString());
            Assert.Equal("1882-lane", root.GetProperty("reviewedRef").GetString());
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task No_verify_step_and_no_model_written_field_rewrites_nothing()
    {
        // The common case by far, and the control for the arm above: there is nothing to remove, so
        // the file is reported clean and left byte-for-byte alone rather than reserialized. A pass
        // here plus a pass there is what tells "removes the key" apart from "always rewrites".
        const string Original = """{"reviewedRef": "main", "findings": [], "model": "opus"}""";
        var path = TempVerdict(Original);
        try
        {
            Assert.True(await VerifyStep.InjectInstrumentsAsync(path, instruments: null, CancellationToken.None));

            Assert.Equal(Original, File.ReadAllText(path));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }
}
