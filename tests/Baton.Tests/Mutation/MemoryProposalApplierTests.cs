using System.Diagnostics;
using System.Text.Json;
using Baton.Domain;
using Baton.Mutation;

namespace Baton.Tests.Mutation;

public class MemoryProposalApplierTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _memoryRoot;

    public MemoryProposalApplierTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "baton_memory_applier_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _memoryRoot = Path.Combine(_tempDirectory, "memory");
    }

    private string WriteCapture(string json, string fileName = "proposal-1.json")
    {
        var path = Path.Combine(_tempDirectory, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task Add_writes_content_to_target_path_under_memory()
    {
        var capture = WriteCapture("""{"Operation":"add","TargetPath":"fact.md","Content":"the fact","Rationale":"learned it"}""");

        await MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken);

        Assert.Equal("the fact", await File.ReadAllTextAsync(Path.Combine(_memoryRoot, "fact.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Edit_overwrites_an_existing_fact_file()
    {
        Directory.CreateDirectory(_memoryRoot);
        await File.WriteAllTextAsync(Path.Combine(_memoryRoot, "fact.md"), "stale", TestContext.Current.CancellationToken);
        var capture = WriteCapture("""{"Operation":"edit","TargetPath":"fact.md","Content":"fresh","Rationale":"corrected"}""");

        await MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken);

        Assert.Equal("fresh", await File.ReadAllTextAsync(Path.Combine(_memoryRoot, "fact.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_removes_an_existing_fact_file()
    {
        Directory.CreateDirectory(_memoryRoot);
        await File.WriteAllTextAsync(Path.Combine(_memoryRoot, "fact.md"), "gone soon", TestContext.Current.CancellationToken);
        var capture = WriteCapture("""{"Operation":"delete","TargetPath":"fact.md","Content":null,"Rationale":"superseded"}""");

        await MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(_memoryRoot, "fact.md")));
    }

    /// <summary>#672's explicit requirement: a delete against a target that is not there is a LOUD failure, never a silent success.</summary>
    [Fact]
    public async Task Delete_of_a_nonexistent_target_throws_loudly()
    {
        var capture = WriteCapture("""{"Operation":"delete","TargetPath":"never-existed.md","Content":null,"Rationale":"superseded"}""");

        await Assert.ThrowsAsync<InvalidRoomMutationException>(
            () => MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 0044 review finding: 'add' and 'edit' must not be synonymous, or an approved 'add' can
    /// silently overwrite a fact nobody decided to overwrite. Both-polarity pair with the test
    /// below.
    /// </summary>
    [Fact]
    public async Task Add_against_an_existing_target_throws_loudly_and_does_not_overwrite()
    {
        Directory.CreateDirectory(_memoryRoot);
        await File.WriteAllTextAsync(Path.Combine(_memoryRoot, "fact.md"), "original", TestContext.Current.CancellationToken);
        var capture = WriteCapture("""{"Operation":"add","TargetPath":"fact.md","Content":"clobber","Rationale":"r"}""");

        await Assert.ThrowsAsync<InvalidRoomMutationException>(
            () => MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken));

        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(_memoryRoot, "fact.md"), TestContext.Current.CancellationToken));
    }

    /// <summary>Other polarity: 'edit' against a target that does not exist must not silently create it.</summary>
    [Fact]
    public async Task Edit_against_a_nonexistent_target_throws_loudly_and_creates_nothing()
    {
        var capture = WriteCapture("""{"Operation":"edit","TargetPath":"never-existed.md","Content":"new","Rationale":"r"}""");

        await Assert.ThrowsAsync<InvalidRoomMutationException>(
            () => MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(Path.Combine(_memoryRoot, "never-existed.md")));
    }

    /// <summary>
    /// The discriminating red: a naive apply that just does <c>Path.Combine(memoryRoot, targetPath)</c>
    /// with no containment check would happily write outside memory/ here. This is the guard's
    /// non-negotiable case (#672) -- proven with a real filesystem write attempt, not a string check.
    /// </summary>
    [Fact]
    public async Task A_traversal_targetPath_is_refused_and_writes_nothing_outside_memory()
    {
        var escapeTarget = Path.Combine(_tempDirectory, "escaped.md");
        var capture = WriteCapture(
            """{"Operation":"add","TargetPath":"../escaped.md","Content":"pwned","Rationale":"malicious"}""");

        await Assert.ThrowsAsync<InvalidRoomMutationException>(
            () => MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(escapeTarget));
    }

    [Fact]
    public async Task A_rooted_targetPath_is_refused()
    {
        var rooted = "C:\\evil.md";
        var capture = Path.Combine(_tempDirectory, "proposal-rooted.json");
        File.WriteAllText(capture, JsonSerializer.Serialize(
            new MemoryProposalCapture("add", rooted, "pwned", "malicious")));

        await Assert.ThrowsAsync<InvalidRoomMutationException>(
            () => MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken));
    }

    /// <summary>Positive polarity paired with the traversal-refusal tests above: an ordinary nested targetPath inside memory/ is allowed.</summary>
    [Fact]
    public async Task A_nested_targetPath_inside_memory_is_allowed()
    {
        var capture = WriteCapture(
            """{"Operation":"add","TargetPath":"topics/fact.md","Content":"nested fact","Rationale":"learned it"}""");

        await MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken);

        Assert.Equal(
            "nested fact",
            await File.ReadAllTextAsync(Path.Combine(_memoryRoot, "topics", "fact.md"), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// #856: the lexical guard is `Path.GetFullPath` + prefix check, which never asks the
    /// filesystem whether a path component is a reparse point. A junction placed under memory/
    /// pointing outside it passes that string check -- proven red here against unmodified code
    /// (before the fix, this test fails: the write lands in <c>outsideDirectory</c>). On
    /// Windows, directory junctions are creatable with plain filesystem write access (no admin,
    /// no Developer Mode) via <c>mklink /J</c>; that's the mechanism actually measured here.
    /// </summary>
    [Fact]
    public async Task A_reparse_point_under_memory_that_resolves_outside_it_is_refused()
    {
        Directory.CreateDirectory(_memoryRoot);
        var outsideDirectory = Path.Combine(_tempDirectory, "outside");
        Directory.CreateDirectory(outsideDirectory);

        var linkPath = Path.Combine(_memoryRoot, "escape");
        if (!TryCreateDirectoryReparsePoint(linkPath, outsideDirectory, out var skipReason))
        {
            Assert.Skip(skipReason);
            return;
        }

        try
        {
            var capture = WriteCapture(
                """{"Operation":"add","TargetPath":"escape/pwned.md","Content":"pwned","Rationale":"malicious"}""");

            await Assert.ThrowsAsync<InvalidRoomMutationException>(
                () => MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken));

            Assert.False(File.Exists(Path.Combine(outsideDirectory, "pwned.md")));
        }
        finally
        {
            // Recursive delete of a directory containing a reparse point is flaky on Windows
            // (Access to the path denied) -- unlink it explicitly first so Dispose()'s recursive
            // delete of _tempDirectory only ever walks real directories.
            RemoveDirectoryLink(linkPath);
        }
    }

    /// <summary>
    /// Other polarity (#856 item 2): a reparse point under memory/ that resolves back inside
    /// memory/ -- e.g. someone symlinking one fact file to another for their own reasons -- must
    /// keep working. Refusing every reparse point would be a different, broader behaviour change
    /// than closing the outside-escape above.
    /// </summary>
    [Fact]
    public async Task A_reparse_point_under_memory_that_resolves_back_inside_it_is_allowed()
    {
        Directory.CreateDirectory(_memoryRoot);
        var realDirectory = Path.Combine(_memoryRoot, "real");
        Directory.CreateDirectory(realDirectory);

        var linkPath = Path.Combine(_memoryRoot, "alias");
        if (!TryCreateDirectoryReparsePoint(linkPath, realDirectory, out var skipReason))
        {
            Assert.Skip(skipReason);
            return;
        }

        try
        {
            var capture = WriteCapture(
                """{"Operation":"add","TargetPath":"alias/fact.md","Content":"via alias","Rationale":"learned it"}""");

            await MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken);

            Assert.Equal(
                "via alias",
                await File.ReadAllTextAsync(Path.Combine(realDirectory, "fact.md"), TestContext.Current.CancellationToken));

            // The other polarity of #875's index skip, which its own test cannot supply: that test
            // proves an outside file is not listed, using an ordinary in-tree file as its positive.
            // This proves the skip does not ALSO drop a fact that a deliberately-allowed alias
            // points at -- the fact is listed once, at its real path, because the walk reaches it
            // directly rather than through the link.
            var index = await File.ReadAllTextAsync(
                Path.Combine(_memoryRoot, RoomMemoryDocument.IndexFileName), TestContext.Current.CancellationToken);

            Assert.Contains("- real/fact.md", index, StringComparison.Ordinal);
            Assert.DoesNotContain("alias/fact.md", index, StringComparison.Ordinal);
        }
        finally
        {
            RemoveDirectoryLink(linkPath);
        }
    }

    /// <summary>
    /// #856 regression, symmetric-resolution requirement documented on
    /// <see cref="MemoryProposalApplier.ResolveReparsePointsIgnoringMissingTail"/>: the room
    /// directory itself is reached through a junction here, not just a path under memory/.
    /// <para>
    /// This is also the arm that proved the fixed-point resolution necessary, and it is worth
    /// saying where: it passes on Windows either way, and went red on the then-extant CI Linux leg
    /// of #856's PR because the two platforms disagree about whether <c>ResolveLinkTarget</c>
    /// normalises its own result. That control run was CI's, red before and green after — a
    /// historical record, not a claim about today: CI has been Windows-only since #1405/#1424, so
    /// nothing now discriminates that half, and the assertion below runs as a Windows regression pin.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_room_directory_reached_through_a_junction_still_allows_an_in_tree_alias()
    {
        var realRoomDirectory = Path.Combine(Path.GetTempPath(), "baton_memory_applier_room_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(realRoomDirectory);
        var roomAlias = Path.Combine(Path.GetTempPath(), "baton_memory_applier_room_alias_" + Guid.NewGuid().ToString("N"));

        try
        {
            if (!TryCreateDirectoryReparsePoint(roomAlias, realRoomDirectory, out var skipReason))
            {
                Assert.Skip(skipReason);
                return;
            }

            try
            {
                var memoryRoot = Path.Combine(roomAlias, RoomMemoryDocument.MemoryDirectoryName);
                Directory.CreateDirectory(memoryRoot);
                var realDirectory = Path.Combine(memoryRoot, "real");
                Directory.CreateDirectory(realDirectory);

                var aliasPath = Path.Combine(memoryRoot, "alias");
                if (!TryCreateDirectoryReparsePoint(aliasPath, realDirectory, out var innerSkipReason))
                {
                    Assert.Skip(innerSkipReason);
                    return;
                }

                try
                {
                    var capturePath = Path.Combine(_tempDirectory, "proposal-through-room-alias.json");
                    await File.WriteAllTextAsync(
                        capturePath,
                        """{"Operation":"add","TargetPath":"alias/fact.md","Content":"via room alias","Rationale":"learned it"}""",
                        TestContext.Current.CancellationToken);

                    await MemoryProposalApplier.ApplyAsync(roomAlias, capturePath, TestContext.Current.CancellationToken);

                    Assert.Equal(
                        "via room alias",
                        await File.ReadAllTextAsync(Path.Combine(realDirectory, "fact.md"), TestContext.Current.CancellationToken));
                }
                finally
                {
                    RemoveDirectoryLink(aliasPath);
                }
            }
            finally
            {
                RemoveDirectoryLink(roomAlias);
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(realRoomDirectory);
        }
    }

    /// <summary>
    /// #856's chain-following claim, which its own doc comment on
    /// <see cref="MemoryProposalApplier.ResolveReparsePointsIgnoringMissingTail"/> makes
    /// (<c>returnFinalTarget: true</c>) but nothing exercised until here. The shape is chosen so it
    /// can only pass by following the whole chain: the first hop lands <b>inside</b> memory/, so a
    /// single-hop resolution would find an in-tree path and allow the write, while the write itself
    /// would still land outside. A test using a first hop that already points outside would pass
    /// either way and prove nothing.
    /// </summary>
    [Fact]
    public async Task A_chained_reparse_point_whose_first_hop_stays_inside_memory_is_still_refused()
    {
        Directory.CreateDirectory(_memoryRoot);
        var outsideDirectory = Path.Combine(_tempDirectory, "outside");
        Directory.CreateDirectory(outsideDirectory);

        // hop2 sits inside memory/ and points outside; hop1 sits inside memory/ and points at hop2.
        var secondHop = Path.Combine(_memoryRoot, "hop2");
        if (!TryCreateDirectoryReparsePoint(secondHop, outsideDirectory, out var secondHopSkipReason))
        {
            Assert.Skip(secondHopSkipReason);
            return;
        }

        try
        {
            var firstHop = Path.Combine(_memoryRoot, "hop1");
            if (!TryCreateDirectoryReparsePoint(firstHop, secondHop, out var firstHopSkipReason))
            {
                Assert.Skip(firstHopSkipReason);
                return;
            }

            try
            {
                var capture = WriteCapture(
                    """{"Operation":"add","TargetPath":"hop1/pwned.md","Content":"pwned","Rationale":"malicious"}""");

                await Assert.ThrowsAsync<InvalidRoomMutationException>(
                    () => MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken));

                Assert.False(File.Exists(Path.Combine(outsideDirectory, "pwned.md")));
            }
            finally
            {
                RemoveDirectoryLink(firstHop);
            }
        }
        finally
        {
            RemoveDirectoryLink(secondHop);
        }
    }

    /// <summary>
    /// #874: a cyclic reparse point makes <c>ResolveLinkTarget(returnFinalTarget: true)</c> throw
    /// <see cref="IOException"/>, which before the fix escaped <c>ApplyAsync</c> raw instead of
    /// arriving as the <see cref="InvalidRoomMutationException"/> every other refusal there raises.
    /// <para>
    /// Windows-only on purpose, and the scope is the measurement's rather than a convenience: on
    /// Windows a cyclic junction reports as an <b>existing</b> directory, which is what carries
    /// execution past the <c>Directory.Exists</c> guard and into the throwing call. Whether a cyclic
    /// symlink on POSIX reaches the same code path was not measured, so this test does not claim it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_cyclic_reparse_point_is_refused_as_an_invalid_mutation_not_a_raw_io_error()
    {
        Directory.CreateDirectory(_memoryRoot);
        var loopA = Path.Combine(_memoryRoot, "loopA");
        var loopB = Path.Combine(_memoryRoot, "loopB");

        // Created A -> B first, while B does not exist yet: 'mklink /J' does not require its target
        // to be present, which is what makes the cycle constructible at all.
        if (!TryCreateDirectoryReparsePoint(loopA, loopB, out var firstSkipReason))
        {
            Assert.Skip(firstSkipReason);
            return;
        }

        try
        {
            if (!TryCreateDirectoryReparsePoint(loopB, loopA, out var secondSkipReason))
            {
                Assert.Skip(secondSkipReason);
                return;
            }

            try
            {
                var capture = WriteCapture(
                    """{"Operation":"add","TargetPath":"loopA/fact.md","Content":"never lands","Rationale":"cyclic"}""");

                var exception = await Assert.ThrowsAsync<InvalidRoomMutationException>(
                    () => MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken));

                Assert.Contains("reparse point", exception.Message, StringComparison.Ordinal);
            }
            finally
            {
                RemoveDirectoryLink(loopB);
            }
        }
        finally
        {
            RemoveDirectoryLink(loopA);
        }
    }

    /// <summary>
    /// Covers the file half of <c>ResolveIfReparsePoint</c>'s <c>isDirectory ? Directory... :
    /// File...</c> branch, which the directory tests above leave unexercised. A file symlink
    /// needs admin or Developer Mode on Windows, so this skips rather than fakes where it cannot be
    /// created. That is a host-capability guard, not a platform one: it runs wherever symlink
    /// creation is permitted and asserts nothing about which OS is underneath.
    /// </summary>
    [Fact]
    public async Task A_file_reparse_point_under_memory_that_resolves_outside_it_is_refused()
    {
        Directory.CreateDirectory(_memoryRoot);
        var outsideDirectory = Path.Combine(_tempDirectory, "outside");
        Directory.CreateDirectory(outsideDirectory);
        var outsideFile = Path.Combine(outsideDirectory, "real.md");
        await File.WriteAllTextAsync(outsideFile, "outside content", TestContext.Current.CancellationToken);

        var linkPath = Path.Combine(_memoryRoot, "fact-link.md");
        try
        {
            File.CreateSymbolicLink(linkPath, outsideFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Could not create a file symlink in this environment: {ex.Message}");
            return;
        }

        try
        {
            var capture = WriteCapture(
                """{"Operation":"edit","TargetPath":"fact-link.md","Content":"pwned","Rationale":"malicious"}""");

            await Assert.ThrowsAsync<InvalidRoomMutationException>(
                () => MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken));

            Assert.Equal("outside content", await File.ReadAllTextAsync(outsideFile, TestContext.Current.CancellationToken));
        }
        finally
        {
            FileCleanup.Delete(linkPath);
        }
    }

    /// <summary>
    /// #875, whose defect and reasoning live on <c>RegenerateIndex</c> itself rather than being
    /// restated here. What belongs to this test specifically: the applied proposal targets an
    /// ordinary in-tree path, because the index is wrong even when the mutation that triggered the
    /// rebuild was entirely legitimate.
    /// </summary>
    [Fact]
    public async Task The_regenerated_index_does_not_list_files_reached_through_a_reparse_point()
    {
        Directory.CreateDirectory(_memoryRoot);
        var outsideDirectory = Path.Combine(_tempDirectory, "outside");
        Directory.CreateDirectory(outsideDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outsideDirectory, "outside-fact.md"), "not this room's", TestContext.Current.CancellationToken);

        var linkPath = Path.Combine(_memoryRoot, "linked");
        if (!TryCreateDirectoryReparsePoint(linkPath, outsideDirectory, out var skipReason))
        {
            Assert.Skip(skipReason);
            return;
        }

        try
        {
            var capture = WriteCapture(
                """{"Operation":"add","TargetPath":"fact.md","Content":"a real fact","Rationale":"learned it"}""");

            await MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken);

            var index = await File.ReadAllTextAsync(
                Path.Combine(_memoryRoot, RoomMemoryDocument.IndexFileName), TestContext.Current.CancellationToken);

            Assert.Contains("- fact.md", index, StringComparison.Ordinal);
            Assert.DoesNotContain("outside-fact.md", index, StringComparison.Ordinal);
        }
        finally
        {
            RemoveDirectoryLink(linkPath);
        }
    }

    /// <summary>
    /// #874's second trigger, and the one that caught an earlier draft of the fix out: a reparse
    /// point whose own ACL denies this process read access. <c>ResolveLinkTarget</c> throws
    /// <see cref="UnauthorizedAccessException"/>, which does <b>not</b> derive from
    /// <see cref="IOException"/> -- so a catch naming only <c>IOException</c> lets it escape raw,
    /// exactly the failure #874 exists to close. Every guard upstream still passes: measured,
    /// <c>Directory.Exists</c> returns true and <c>File.GetAttributes</c> succeeds on the denied
    /// link.
    /// <para>
    /// Windows-only, because the mechanism is a Windows ACL. The room lives in its own temp
    /// directory rather than under <c>_tempDirectory</c> so that a failure to restore the ACL can
    /// never leave <c>Dispose()</c> unable to clean up the shared tree.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_reparse_point_this_process_cannot_read_is_refused_as_an_invalid_mutation()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), "baton_memory_applier_denied_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(roomDirectory);

        try
        {
            var memoryRoot = Path.Combine(roomDirectory, RoomMemoryDocument.MemoryDirectoryName);
            Directory.CreateDirectory(memoryRoot);
            var outsideDirectory = Path.Combine(roomDirectory, "outside");
            Directory.CreateDirectory(outsideDirectory);

            var linkPath = Path.Combine(memoryRoot, "escape");
            if (!TryCreateDirectoryReparsePoint(linkPath, outsideDirectory, out var skipReason))
            {
                Assert.Skip(skipReason);
                return;
            }

            try
            {
                if (!TryDenyReadAccess(linkPath, out var denySkipReason))
                {
                    Assert.Skip(denySkipReason);
                    return;
                }

                try
                {
                    var capturePath = Path.Combine(_tempDirectory, "proposal-denied-link.json");
                    await File.WriteAllTextAsync(
                        capturePath,
                        """{"Operation":"add","TargetPath":"escape/pwned.md","Content":"pwned","Rationale":"malicious"}""",
                        TestContext.Current.CancellationToken);

                    var exception = await Assert.ThrowsAsync<InvalidRoomMutationException>(
                        () => MemoryProposalApplier.ApplyAsync(roomDirectory, capturePath, TestContext.Current.CancellationToken));

                    Assert.Contains("reparse point", exception.Message, StringComparison.Ordinal);
                }
                finally
                {
                    RestoreReadAccess(linkPath);
                }
            }
            finally
            {
                RemoveDirectoryLink(linkPath);
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #875 for a <b>file</b> symlink rather than a directory junction. Its own test, not another
    /// assertion bolted onto the junction one, so that an environment which cannot create file
    /// symlinks reports this as skipped instead of quietly dropping the check -- a check that cannot
    /// run must not look like a check that ran and found nothing.
    /// </summary>
    [Fact]
    public async Task The_regenerated_index_does_not_list_a_file_symlink_pointing_outside_memory()
    {
        Directory.CreateDirectory(_memoryRoot);
        var outsideDirectory = Path.Combine(_tempDirectory, "outside");
        Directory.CreateDirectory(outsideDirectory);
        var outsideFile = Path.Combine(outsideDirectory, "outside-fact.md");
        await File.WriteAllTextAsync(outsideFile, "not this room's", TestContext.Current.CancellationToken);

        var linkPath = Path.Combine(_memoryRoot, "linked-fact.md");
        try
        {
            File.CreateSymbolicLink(linkPath, outsideFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Could not create a file symlink in this environment: {ex.Message}");
            return;
        }

        try
        {
            var capture = WriteCapture(
                """{"Operation":"add","TargetPath":"fact.md","Content":"a real fact","Rationale":"learned it"}""");

            await MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken);

            var index = await File.ReadAllTextAsync(
                Path.Combine(_memoryRoot, RoomMemoryDocument.IndexFileName), TestContext.Current.CancellationToken);

            Assert.Contains("- fact.md", index, StringComparison.Ordinal);
            Assert.DoesNotContain("linked-fact.md", index, StringComparison.Ordinal);
        }
        finally
        {
            FileCleanup.Delete(linkPath);
        }
    }

    /// <summary>
    /// Denies the current user read/traverse on <paramref name="path"/> via <c>icacls</c>, spawned
    /// directly rather than through a shell for the same quoting reason as
    /// <see cref="TryCreateDirectoryReparsePoint"/>. Inheritance is broken first, otherwise the
    /// inherited grant from the temp tree wins and the deny never bites. Returns false with a reason
    /// rather than faking the arm if the environment will not apply it.
    /// </summary>
    private static bool TryDenyReadAccess(string path, out string skipReason)
    {
        var user = Environment.UserName;
        if (RunIcacls(path, "/inheritance:r", "/grant:r", $"{user}:(F)") != 0)
        {
            skipReason = "Could not break ACL inheritance in this environment; the deny arm would not bite.";
            return false;
        }

        if (RunIcacls(path, "/deny", $"{user}:(RX)") != 0)
        {
            skipReason = "Could not apply a deny ACE in this environment.";
            return false;
        }

        skipReason = "";
        return true;
    }

    /// <summary>
    /// Undoes <see cref="TryDenyReadAccess"/> so the link can be unlinked and the temp tree removed.
    /// Best-effort by design: the room directory is this test's own, so a failure here cannot strand
    /// the shared fixture.
    /// </summary>
    private static void RestoreReadAccess(string path)
    {
        RunIcacls(path, "/remove:d", Environment.UserName);
        RunIcacls(path, "/grant", $"{Environment.UserName}:(F)");
    }

    private static int RunIcacls(string path, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("icacls")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(path);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = global::Baton.Core.ProcessLaunch.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>
    /// Unlinks a reparse point created by <see cref="TryCreateDirectoryReparsePoint"/> without
    /// touching its target -- <c>Directory.Delete(path, recursive: false)</c> on a junction or
    /// directory symlink removes the link itself, never the linked-to contents.
    /// </summary>
    private static void RemoveDirectoryLink(string linkPath)
    {
        if (Directory.Exists(linkPath))
        {
            Directory.Delete(linkPath, recursive: false);
        }
    }

    /// <summary>
    /// Creates a directory reparse point at <paramref name="linkPath"/> pointing at <paramref
    /// name="targetPath"/>: a junction on Windows (via <c>mklink /J</c>, spawned directly rather
    /// than through a shell so path quoting cannot mangle it -- unprivileged, unlike a Windows
    /// directory symlink which needs admin or Developer Mode). Returns false with a reason if the
    /// environment cannot host one -- the caller must skip rather than fake the arm.
    /// </summary>
    private static bool TryCreateDirectoryReparsePoint(string linkPath, string targetPath, out string skipReason)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = global::Baton.Core.ProcessLaunch.Start(startInfo)!;
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            skipReason = $"'mklink /J' exited {process.ExitCode}: {process.StandardError.ReadToEnd()}";
            return false;
        }

        skipReason = "";
        return true;
    }

    [Fact]
    public async Task Applying_regenerates_the_index_with_one_line_per_fact_file()
    {
        var first = WriteCapture(
            """{"Operation":"add","TargetPath":"a.md","Content":"a","Rationale":"r"}""", "proposal-a.json");
        await MemoryProposalApplier.ApplyAsync(_tempDirectory, first, TestContext.Current.CancellationToken);

        var second = WriteCapture(
            """{"Operation":"add","TargetPath":"b.md","Content":"b","Rationale":"r"}""", "proposal-b.json");
        await MemoryProposalApplier.ApplyAsync(_tempDirectory, second, TestContext.Current.CancellationToken);

        var index = await File.ReadAllTextAsync(
            Path.Combine(_memoryRoot, RoomMemoryDocument.IndexFileName), TestContext.Current.CancellationToken);

        Assert.Contains("- a.md", index);
        Assert.Contains("- b.md", index);
    }

    [Fact]
    public async Task Deleting_the_only_fact_file_regenerates_an_empty_index()
    {
        var add = WriteCapture(
            """{"Operation":"add","TargetPath":"a.md","Content":"a","Rationale":"r"}""", "proposal-a.json");
        await MemoryProposalApplier.ApplyAsync(_tempDirectory, add, TestContext.Current.CancellationToken);

        var delete = WriteCapture(
            """{"Operation":"delete","TargetPath":"a.md","Content":null,"Rationale":"r"}""", "proposal-b.json");
        await MemoryProposalApplier.ApplyAsync(_tempDirectory, delete, TestContext.Current.CancellationToken);

        var index = await File.ReadAllTextAsync(
            Path.Combine(_memoryRoot, RoomMemoryDocument.IndexFileName), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("- a.md", index);
    }

    [Fact]
    public async Task A_missing_capture_file_throws_loudly()
    {
        var missing = Path.Combine(_tempDirectory, "does-not-exist.json");

        await Assert.ThrowsAsync<InvalidRoomMutationException>(
            () => MemoryProposalApplier.ApplyAsync(_tempDirectory, missing, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        DirectoryCleanup.DeleteRecursively(_tempDirectory);
    }
}
