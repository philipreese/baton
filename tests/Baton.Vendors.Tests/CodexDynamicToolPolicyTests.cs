using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Domain;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Vendors.Tests;

public sealed class CodexDynamicToolPolicyTests
{
    [Fact]
    public void Read_only_role_gets_reads_and_declared_output_but_no_workspace_write_or_command()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);

        var names = ToolNames(fixture.Policy);

        Assert.Contains(CodexDynamicToolPolicy.ReadTextTool, names);
        Assert.Contains(CodexDynamicToolPolicy.ListFilesTool, names);
        Assert.Contains(CodexDynamicToolPolicy.SearchTextTool, names);
        Assert.Contains(CodexDynamicToolPolicy.WriteOutputTool, names);
        Assert.DoesNotContain(CodexDynamicToolPolicy.WriteTextTool, names);
        Assert.DoesNotContain(CodexDynamicToolPolicy.RunCommandTool, names);
        Assert.DoesNotContain(CodexDynamicToolPolicy.ReadCommandOutputTool, names);
        // #1996: the manifest half of the grant. A role without WriteFiles must not be shown the edit
        // tool at all — the polarity partner of Implement_role_gets_workspace_write_and_command_tools.
        Assert.DoesNotContain(CodexDynamicToolPolicy.ApplyPatchTool, names);
    }

    [Fact]
    public void Implement_role_gets_workspace_write_and_command_tools()
    {
        using var fixture = new PolicyFixture(
            new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true), ["changes.md"]);

        var names = ToolNames(fixture.Policy);

        Assert.Contains(CodexDynamicToolPolicy.ApplyPatchTool, names);
        Assert.Contains(CodexDynamicToolPolicy.WriteTextTool, names);
        Assert.Contains(CodexDynamicToolPolicy.RunCommandTool, names);
        Assert.Contains(CodexDynamicToolPolicy.ReadCommandOutputTool, names);
    }

    [Fact]
    public void Command_output_schema_requires_an_opaque_reference_and_channel_and_bounds_ranges()
    {
        using var fixture = new PolicyFixture(
            new PermissionGrant(RunShellCommands: true), ["changes.md"]);
        var tool = fixture.Policy.BuildToolDefinitions()
            .Single(node => node!["name"]!.GetValue<string>() == CodexDynamicToolPolicy.ReadCommandOutputTool)!;
        var schema = (JsonObject)tool["inputSchema"]!;
        var properties = (JsonObject)schema["properties"]!;

        Assert.Equal(
            ["reference", "channel"],
            schema["required"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal(
            ["stdout", "stderr"],
            properties["channel"]!["enum"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal(0, properties["offset"]!["minimum"]!.GetValue<int>());
        Assert.Equal(12_000, properties["length"]!["maximum"]!.GetValue<int>());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void Read_tool_schema_keeps_path_only_compatibility_and_bounds_optional_ranges()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);
        var tool = fixture.Policy.BuildToolDefinitions()
            .Single(node => node!["name"]!.GetValue<string>() == CodexDynamicToolPolicy.ReadTextTool)!;
        var schema = (JsonObject)tool["inputSchema"]!;
        var properties = (JsonObject)schema["properties"]!;

        Assert.Equal(["path"], schema["required"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal("integer", properties["offset"]!["type"]!.GetValue<string>());
        Assert.Equal(0, properties["offset"]!["minimum"]!.GetValue<int>());
        Assert.Equal("integer", properties["length"]!["type"]!.GetValue<string>());
        Assert.Equal(12_000, properties["length"]!["maximum"]!.GetValue<int>());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void Search_tool_schema_exposes_an_optional_bounded_continuation_position()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);
        var tool = fixture.Policy.BuildToolDefinitions()
            .Single(node => node!["name"]!.GetValue<string>() == CodexDynamicToolPolicy.SearchTextTool)!;
        var schema = (JsonObject)tool["inputSchema"]!;
        var properties = (JsonObject)schema["properties"]!;

        Assert.Equal(
            ["path", "query"],
            schema["required"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal("integer", properties["start"]!["type"]!.GetValue<string>());
        Assert.Equal(0, properties["start"]!["minimum"]!.GetValue<int>());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
    }

    /// <summary>
    /// #1996's first arm: the tool codex reaches for natively changes files on disk, and does it
    /// through the same write path <c>baton_write_text</c> takes. All three operations in one envelope
    /// because "the whole patch applies" is part of the claim.
    /// </summary>
    [Fact]
    public async Task Apply_patch_adds_updates_and_deletes_files_on_disk()
    {
        using var fixture = new PolicyFixture(WriteShapedGrant, ["changes.md"]);
        File.WriteAllText(Path.Combine(fixture.Workspace, "edit.txt"), "one\ntwo\nthree\n");
        File.WriteAllText(Path.Combine(fixture.Workspace, "gone.txt"), "obsolete\n");

        // Lf, and an exact "\n" expectation, because this source file is CRLF on disk and a raw string
        // literal keeps the endings it was written with: asserting Environment.NewLine here passed on
        // Windows whatever the added file's endings were, which is the bug the arm below measures.
        var result = await fixture.ApplyPatchAsync(Lf(
            """
            *** Begin Patch
            *** Add File: src/new.txt
            +created
            *** Update File: edit.txt
            @@
             one
            -two
            +TWO
             three
            *** Delete File: gone.txt
            *** End Patch
            """));

        Assert.True(result.Success, result.Text);
        Assert.Equal("created\n",
            File.ReadAllText(Path.Combine(fixture.Workspace, "src", "new.txt")));
        Assert.Equal("one\nTWO\nthree\n", File.ReadAllText(Path.Combine(fixture.Workspace, "edit.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Workspace, "gone.txt")));
    }

    /// <summary>
    /// The line-ending arm, which no LF fixture can fail: nearly every file this engine's own workers
    /// edit on Windows is CRLF, and a patch body's lines arrive from JSON carrying none. If the split
    /// left the carriage return on the line, no context would ever match and every real edit would come
    /// back as a context failure.
    /// </summary>
    [Fact]
    public async Task Apply_patch_matches_context_in_a_crlf_file_and_keeps_its_line_endings()
    {
        using var fixture = new PolicyFixture(WriteShapedGrant, ["changes.md"]);
        var target = Path.Combine(fixture.Workspace, "crlf.txt");
        File.WriteAllText(target, "alpha\r\nbeta\r\ngamma\r\n");

        var result = await fixture.ApplyPatchAsync(
            """
            *** Begin Patch
            *** Update File: crlf.txt
             alpha
            -beta
            +BETA
             gamma
            *** End Patch
            """);

        Assert.True(result.Success, result.Text);
        Assert.Equal("alpha\r\nBETA\r\ngamma\r\n", File.ReadAllText(target));
    }

    /// <summary>
    /// #1996 re-review LOW: an added file takes LF, not the worker machine's ending — a codex lane on
    /// Windows added CRLF files to an all-LF repository, which fails a formatting gate a step later
    /// rather than here. Both arms in one test because the rule is a pair: LF by default, and the
    /// envelope's own CRLF when the patch arrived carrying it.
    /// </summary>
    [Fact]
    public async Task An_added_file_takes_lf_unless_the_patch_envelope_itself_carries_crlf()
    {
        using var fixture = new PolicyFixture(WriteShapedGrant, ["changes.md"]);
        const string patch = "*** Begin Patch\n*** Add File: added.txt\n+first\n+second\n*** End Patch";

        var lf = await fixture.ApplyPatchAsync(patch);
        var crlf = await fixture.ApplyPatchAsync(
            patch.Replace("added.txt", "added-crlf.txt", StringComparison.Ordinal)
                .ReplaceLineEndings("\r\n"));

        Assert.True(lf.Success, lf.Text);
        Assert.True(crlf.Success, crlf.Text);
        Assert.Equal("first\nsecond\n", File.ReadAllText(Path.Combine(fixture.Workspace, "added.txt")));
        Assert.Equal("first\r\nsecond\r\n",
            File.ReadAllText(Path.Combine(fixture.Workspace, "added-crlf.txt")));
    }

    /// <summary>
    /// #1996 re-review HIGH: a context block that fits in two places is refused, never placed at the
    /// first fit — the corrupted-file-that-reports-success case, so the assertion is on the file's
    /// bytes as much as on the result. The two functions differ only where the patch does not look.
    /// </summary>
    [Fact]
    public async Task Apply_patch_with_context_matching_twice_is_refused_and_changes_nothing()
    {
        using var fixture = new PolicyFixture(WriteShapedGrant, ["changes.md"]);
        var target = Path.Combine(fixture.Workspace, "handlers.py");
        const string original =
            "def handle_open():\n    log.debug(msg)\n    return None\n\n"
            + "def handle_retry():\n    log.debug(msg)\n    return None\n";
        File.WriteAllText(target, original);

        var result = await fixture.ApplyPatchAsync(
            "*** Begin Patch\n*** Update File: handlers.py\n"
            + "     log.debug(msg)\n-    return None\n+    return Retry()\n*** End Patch");

        Assert.False(result.Success);
        Assert.DoesNotContain(GrantRefusal.Marker, result.Text);
        Assert.Contains("ambiguous", result.Text, StringComparison.Ordinal);
        Assert.Contains("matches at 2 places, first at lines 2 and 6", result.Text, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(target));
    }

    /// <summary>
    /// The other polarity of the same rule, and the half that makes the refusal recoverable: the '@@'
    /// locator codex's dialect defines moves the search past the first fit, so the identical hunk that
    /// was refused above now names one place and applies — to the SECOND function, which is the
    /// assertion that fails if the locator is read as a bare chunk boundary again.
    /// </summary>
    [Fact]
    public async Task A_section_locator_narrows_two_matches_to_one_and_the_hunk_applies_there()
    {
        using var fixture = new PolicyFixture(WriteShapedGrant, ["changes.md"]);
        var target = Path.Combine(fixture.Workspace, "handlers.py");
        File.WriteAllText(target,
            "def handle_open():\n    log.debug(msg)\n    return None\n\n"
            + "def handle_retry():\n    log.debug(msg)\n    return None\n");

        var result = await fixture.ApplyPatchAsync(
            "*** Begin Patch\n*** Update File: handlers.py\n@@ def handle_retry():\n"
            + "     log.debug(msg)\n-    return None\n+    return Retry()\n*** End Patch");

        Assert.True(result.Success, result.Text);
        Assert.Equal(
            "def handle_open():\n    log.debug(msg)\n    return None\n\n"
            + "def handle_retry():\n    log.debug(msg)\n    return Retry()\n",
            File.ReadAllText(target));
    }

    /// <summary>
    /// #1996 re-review HIGH: the locator names an INDENTED line — every line inside a class or a
    /// function, and the shape the stacked-locator refusal tells the model to keep. The anchor is
    /// trimmed when it is parsed, so an exact comparison against the file's own line matched nothing
    /// here and the hunk was refused as locator-not-found: the remedy the ambiguity refusal above
    /// advertises was inoperative for exactly the files it is needed on. Both spellings, because the
    /// model can reproduce the indentation or drop it and neither used to work.
    /// </summary>
    [Theory]
    [InlineData("@@     def handle_retry(self):")]
    [InlineData("@@ def handle_retry(self):")]
    public async Task A_locator_naming_an_indented_line_anchors_on_its_text_not_its_column(string locator)
    {
        using var fixture = new PolicyFixture(WriteShapedGrant, ["changes.md"]);
        var target = Path.Combine(fixture.Workspace, "handlers.py");
        const string original =
            "class Handlers:\n    def handle_open(self):\n        log.debug(msg)\n"
            + "        return None\n\n    def handle_retry(self):\n        log.debug(msg)\n"
            + "        return None\n";
        File.WriteAllText(target, original);

        var result = await fixture.ApplyPatchAsync(
            $"*** Begin Patch\n*** Update File: handlers.py\n{locator}\n"
            + "         log.debug(msg)\n-        return None\n+        return Retry()\n*** End Patch");

        Assert.True(result.Success, result.Text);
        // The SECOND method, which is the assertion that discriminates: without the locator this same
        // hunk matches both methods and is refused as ambiguous, and an anchor that matched the first
        // method's line would refuse identically.
        Assert.Equal(
            "class Handlers:\n    def handle_open(self):\n        log.debug(msg)\n"
            + "        return None\n\n    def handle_retry(self):\n        log.debug(msg)\n"
            + "        return Retry()\n",
            File.ReadAllText(target));
    }

    /// <summary>
    /// #1996 re-review LOW: two headers spelling ONE file are refused even when only a resolver can
    /// tell they are one — the parser compares path text, so './edit.txt' and (on Windows) 'EDIT.txt'
    /// get past it. Planned from disk and written in order, the second header's write silently
    /// discards the first's hunks and the result still reports success, which is HIGH 1's failure
    /// reached through an alias. The assertion is on the file's bytes: a check that ran inside the
    /// write loop instead would refuse with the first write already on disk.
    /// </summary>
    [Theory]
    [InlineData("./edit.txt", false)]
    [InlineData("EDIT.txt", true)]
    public async Task Two_headers_resolving_to_one_file_are_refused_before_anything_is_written(
        string alias, bool windowsOnly)
    {
        if (windowsOnly && !OperatingSystem.IsWindows())
        {
            Assert.Skip("path case is only an alias where the platform comparer is case-insensitive");
            return;
        }
        using var fixture = new PolicyFixture(WriteShapedGrant, ["changes.md"]);
        var target = Path.Combine(fixture.Workspace, "edit.txt");
        File.WriteAllText(target, "one\ntwo\n");

        var result = await fixture.ApplyPatchAsync(
            $"*** Begin Patch\n*** Update File: edit.txt\n one\n-two\n+TWO\n"
            + $"*** Update File: {alias}\n-one\n+ONE\n two\n*** End Patch");

        Assert.False(result.Success);
        // Not a grant decision — the same polarity the parser's own duplicate-path rows are pinned to.
        Assert.DoesNotContain(GrantRefusal.Marker, result.Text);
        Assert.Contains("same file", result.Text, StringComparison.Ordinal);
        Assert.Equal("one\ntwo\n", File.ReadAllText(target));
    }

    /// <summary>
    /// #1996 re-review HIGH: a path with two headers is refused whole and neither header's hunks are
    /// applied. Cross-kind on purpose — a duplicate check keyed on (kind, path) passes an Update+Update
    /// row while still letting a Delete and an Update on one path run in sequence, which deletes the
    /// file and then re-creates it from disk content that no longer exists.
    /// </summary>
    [Theory]
    [InlineData("*** Delete File: edit.txt\n*** Update File: edit.txt\n one\n-two\n+TWO")]
    [InlineData("*** Update File: edit.txt\n one\n-two\n+TWO\n*** Update File: edit.txt\n-one\n+ONE")]
    // The separator the check unifies, because a Windows backslash path is codex's measured habit
    // (#1920): 'dir/edit.txt' and 'dir\edit.txt' are one file with two headers.
    [InlineData("*** Update File: dir/edit.txt\n one\n-two\n+TWO\n*** Update File: dir\\edit.txt\n-one\n+ONE")]
    public async Task A_path_with_two_patch_headers_is_refused_whole_and_neither_is_applied(string body)
    {
        using var fixture = new PolicyFixture(WriteShapedGrant, ["changes.md"]);
        var target = Path.Combine(fixture.Workspace, "edit.txt");
        File.WriteAllText(target, "one\ntwo\n");

        var result = await fixture.ApplyPatchAsync($"*** Begin Patch\n{body}\n*** End Patch");

        Assert.False(result.Success);
        Assert.DoesNotContain(GrantRefusal.Marker, result.Text);
        Assert.Contains("a path appears twice", result.Text, StringComparison.Ordinal);
        Assert.Contains("edit.txt", result.Text, StringComparison.Ordinal);
        Assert.Equal("one\ntwo\n", File.ReadAllText(target));
        Assert.True(File.Exists(target));
    }

    /// <summary>
    /// #1996 re-review HIGH: every path in the envelope crosses the reparse-point check while the patch
    /// is being PLANNED. The added path is second on purpose — checked first inside the write loop
    /// instead, this refusal arrives with the earlier file already rewritten, which is the guarantee
    /// the tool description states.
    /// </summary>
    [Fact]
    public async Task A_patch_adding_a_file_under_a_junction_is_refused_before_its_earlier_file_is_written()
    {
        using var fixture = new PolicyFixture(WriteShapedGrant, ["changes.md"]);
        var readme = Path.Combine(fixture.Workspace, "README.md");
        File.WriteAllText(readme, "before\n");
        var target = Path.Combine(fixture.Workspace, "real");
        Directory.CreateDirectory(target);
        // A junction rather than a symbolic link: `mklink /J` needs no Developer Mode or elevation.
        // Same shape as VendorMemoryRootTests' reparse-point arm, including its host skip.
        var junction = Path.Combine(fixture.Workspace, "vendor");
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        if (mklink is null)
        {
            Assert.Skip("this host could not start cmd.exe, so no reparse point can be planted here");
            return;
        }

        await mklink.WaitForExitAsync(TestContext.Current.CancellationToken);
        if (mklink.ExitCode != 0 || !Directory.Exists(junction))
        {
            Assert.Skip("this host refused `mklink /J`, so no reparse point can be planted here");
            return;
        }

        var result = await fixture.ApplyPatchAsync(
            "*** Begin Patch\n*** Update File: README.md\n-before\n+after\n"
            + "*** Add File: vendor/new.txt\n+planted\n*** End Patch");

        Assert.False(result.Success);
        Assert.Contains(GrantRefusal.Marker, result.Text);
        Assert.Contains("reparse point", result.Text, StringComparison.Ordinal);
        Assert.Equal("before\n", File.ReadAllText(readme));
        Assert.False(File.Exists(Path.Combine(target, "new.txt")));
    }

    /// <summary>
    /// The grant arm, and the reason the applier resolves every path before it writes any byte: the
    /// FIRST file of this patch is a legal workspace edit. A non-atomic implementation refuses the
    /// second path exactly like this one does and still leaves the first file rewritten, so a
    /// single-path test would pass on the build this test exists to fail.
    /// </summary>
    [Fact]
    public async Task Apply_patch_outside_the_workspace_is_refused_and_no_file_in_the_patch_is_touched()
    {
        using var fixture = new PolicyFixture(WriteShapedGrant, ["changes.md"]);
        var inside = Path.Combine(fixture.Workspace, "inside.txt");
        var outside = Path.Combine(Path.GetDirectoryName(fixture.Workspace)!, "outside.txt");
        File.WriteAllText(inside, "keep\n");
        File.WriteAllText(outside, "original\n");

        var result = await fixture.ApplyPatchAsync(
            $"""
            *** Begin Patch
            *** Update File: inside.txt
            -keep
            +changed
            *** Update File: {outside.Replace('\\', '/')}
            -original
            +escaped
            *** End Patch
            """);

        Assert.False(result.Success);
        Assert.Contains(GrantRefusal.Marker, result.Text);
        Assert.Contains("outside this Baton's workspace root", result.Text, StringComparison.Ordinal);
        Assert.Equal("keep\n", File.ReadAllText(inside));
        Assert.Equal("original\n", File.ReadAllText(outside));
    }

    /// <summary>
    /// A context line that is not in the file must FAIL — not be placed somewhere else that looks
    /// close. The wrong offset is the one failure mode of this tool that reports success and corrupts
    /// the file, so the assertion is on the file's content, not only on the result.
    /// </summary>
    [Fact]
    public async Task Apply_patch_with_context_that_does_not_match_fails_and_changes_nothing()
    {
        using var fixture = new PolicyFixture(WriteShapedGrant, ["changes.md"]);
        var target = Path.Combine(fixture.Workspace, "edit.txt");
        File.WriteAllText(target, "one\ntwo\nthree\n");

        var result = await fixture.ApplyPatchAsync(
            """
            *** Begin Patch
            *** Update File: edit.txt
             one
            -TWO
            +two
            *** End Patch
            """);

        Assert.False(result.Success);
        Assert.DoesNotContain(GrantRefusal.Marker, result.Text);
        Assert.Contains("Patch context not found", result.Text, StringComparison.Ordinal);
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(target));
    }

    /// <summary>
    /// The excluded corners of the subset, each answered by a failure that names itself rather than by
    /// a guess. A malformed or unsupported envelope is not a grant decision, so none of these is marked
    /// or counted as a refusal — the polarity partner is the outside-the-workspace test above.
    /// </summary>
    [Theory]
    [InlineData("*** Update File: edit.txt\n-one\n+ONE", "*** End Patch")]
    [InlineData("*** Begin Patch\n*** Update File: edit.txt\n*** Move to: moved.txt\n-one\n+ONE\n*** End Patch",
        "Move to")]
    [InlineData("*** Begin Patch\n*** Update File: edit.txt\n+added\n*** End Patch", "no context")]
    [InlineData("*** Begin Patch\n*** Add File: edit.txt\n+clobber\n*** End Patch", "already exists")]
    [InlineData("*** Begin Patch\n*** Update File: absent.txt\n one\n-x\n*** End Patch", "does not exist")]
    // The two '@@' corners (#1996 re-review HIGH): a locator naming no line of the file is not
    // silently ignored, and two stacked locators are not silently collapsed into one.
    [InlineData("*** Begin Patch\n*** Update File: edit.txt\n@@ def absent():\n one\n-two\n+TWO\n*** End Patch",
        "Patch locator not found")]
    [InlineData("*** Begin Patch\n*** Update File: edit.txt\n@@ class A:\n@@ def b():\n one\n-two\n+TWO\n*** End Patch",
        "one locator per hunk")]
    public async Task An_unsupported_or_malformed_patch_fails_with_its_own_reason(
        string patch, string expectedReason)
    {
        using var fixture = new PolicyFixture(WriteShapedGrant, ["changes.md"]);
        var target = Path.Combine(fixture.Workspace, "edit.txt");
        File.WriteAllText(target, "one\ntwo\n");

        var result = await fixture.ApplyPatchAsync(patch);

        Assert.False(result.Success);
        Assert.DoesNotContain(GrantRefusal.Marker, result.Text);
        Assert.Contains(expectedReason, result.Text, StringComparison.Ordinal);
        Assert.Equal("one\ntwo\n", File.ReadAllText(target));
    }

    /// <summary>
    /// #1996 moved this population: <c>apply_patch</c> on a role without WriteFiles used to reach the
    /// unknown-tool fallthrough (unmarked, uncounted, and carrying #1920's write-path guidance) and is
    /// now a real grant refusal. The guidance must survive that move, which is the half a rename of the
    /// stand-in name would have silently dropped.
    /// </summary>
    [Fact]
    public async Task Apply_patch_without_the_write_grant_is_a_refusal_that_still_names_the_role_write_path()
    {
        using var fixture = new PolicyFixture(ReviewShapedGrant, ["report.md"]);

        var result = await fixture.ApplyPatchAsync("*** Begin Patch\n*** Delete File: workspace.txt\n*** End Patch");

        Assert.False(result.Success);
        Assert.Contains(GrantRefusal.Marker, result.Text);
        Assert.Equal(1, RefusedStepsCountedFor(result));
        Assert.Contains("cannot edit workspace files", result.Text, StringComparison.Ordinal);
        Assert.Contains(CodexDynamicToolPolicy.WriteOutputTool, result.Text, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(fixture.Workspace, "workspace.txt")));
    }

    /// <summary>
    /// A raw string literal keeps the line endings of THIS source file, which git checks out CRLF. A
    /// patch that must be LF to mean what the test says says so here rather than depending on that.
    /// </summary>
    private static string Lf(string patch) => patch.ReplaceLineEndings("\n");

    /// <summary>The shipped implement role's shape: reads, workspace writes, an unscoped shell.</summary>
    private static PermissionGrant WriteShapedGrant =>
        new(ReadFiles: true, WriteFiles: true, RunShellCommands: true);

    // #1920: one row per refusal shape in the issue's measured table — the four Baton-produced ones
    // (row 1 a path outside the readable roots, rows 2-3 a backslash path, row 9 the standing deny
    // list, rows 11-15 a command matching no allow pattern) plus the audit comment's dominant
    // unknown-tool case, five apply_patch attempts. A shape with no row here is a shape that can
    // regress to teaching nothing, which is what the issue measured.
    [Theory]
    [InlineData("outside-readable-roots", "ask for its content quoted inline")]
    [InlineData("unsupported-backslash", "use forward slashes")]
    [InlineData("standing-deny", "permanently closed for this role")]
    [InlineData("ungranted-pattern",
        "this session's granted shell patterns are: git diff*, git log*, git show*, git status*")]
    [InlineData("write-shaped-unknown-tool", "cannot edit workspace files")]
    public async Task Each_refusal_shape_names_its_granted_alternative(
        string refusalShape, string expectedAlternative)
    {
        using var fixture = new PolicyFixture(ReviewShapedGrant, ["report.md"]);

        var result = await ExecuteRefusalShapeAsync(fixture, refusalShape);

        Assert.False(result.Success);
        Assert.Contains(expectedAlternative, result.Text, StringComparison.Ordinal);
    }

    // The polarity arm for the rows above: a SHELL refusal a respelling could get past also names the
    // granted read path, which is #1920's literal ask (the measured loop was `rg` four times before
    // baton_search_text). #1972 removed the standing-deny row from this theory and gave it the
    // opposite assertion below: that rung is permanently closed, so a read tool is no answer to it.
    [Theory]
    [InlineData("unsupported-backslash")]
    [InlineData("ungranted-pattern")]
    public async Task Every_shell_refusal_a_respelling_could_pass_names_the_granted_read_tools(
        string refusalShape)
    {
        using var fixture = new PolicyFixture(ReviewShapedGrant, ["report.md"]);

        var result = await ExecuteRefusalShapeAsync(fixture, refusalShape);

        Assert.False(result.Success);
        Assert.Contains(CodexDynamicToolPolicy.ReadTextTool, result.Text, StringComparison.Ordinal);
        Assert.Contains(CodexDynamicToolPolicy.SearchTextTool, result.Text, StringComparison.Ordinal);
    }

    // #1972: the third producing site of the same clause, corrected with both PreToolUse hooks. codex's
    // review role reads the SAME WorkerRoles.json deny list, which is mostly write-shaped, so this rung
    // was answering `gh pr comment …` with two read tools — the refusal shape the issue opens with. The
    // theory above is the control: same fixture, same declared tools, refused on a rung a respelling
    // could pass, clause present.
    [Fact]
    public async Task A_standing_deny_names_no_read_tool_because_that_rung_is_permanently_closed()
    {
        using var fixture = new PolicyFixture(ReviewShapedGrant, ["report.md"]);

        var result = await ExecuteRefusalShapeAsync(fixture, "standing-deny");

        Assert.False(result.Success);
        Assert.Contains("permanently closed for this role", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(CodexDynamicToolPolicy.ReadTextTool, result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(CodexDynamicToolPolicy.SearchTextTool, result.Text, StringComparison.Ordinal);
    }

    // The negative arm the rows above need to discriminate: the clause is derived from the tools this
    // role actually declared, so a grant that withholds reads is told nothing rather than pointed at
    // two tools it does not have. Without this, a hardcoded clause passes every row above.
    [Fact]
    public async Task A_shell_refusal_names_no_read_tool_when_the_role_declares_none()
    {
        var grant = ReviewShapedGrant with { ReadFiles = false };
        using var fixture = new PolicyFixture(grant, []);

        var result = await ExecuteRefusalShapeAsync(fixture, "ungranted-pattern");

        Assert.False(result.Success);
        Assert.DoesNotContain(CodexDynamicToolPolicy.ReadTextTool, result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(CodexDynamicToolPolicy.SearchTextTool, result.Text, StringComparison.Ordinal);
    }

    // The write half of #1920's ask 2, in both directions: the same apply_patch attempt is answered
    // with baton_write_text on a write-granted role and with the declared-output write on a review
    // role, never with the read tools.
    [Fact]
    public async Task A_write_shaped_unknown_tool_names_the_write_path_the_role_actually_has()
    {
        using var reviewFixture = new PolicyFixture(ReviewShapedGrant, ["report.md"]);
        using var implementFixture = new PolicyFixture(
            ReviewShapedGrant with { WriteFiles = true }, ["changes.md"]);

        var onReview = await ExecuteRefusalShapeAsync(reviewFixture, "write-shaped-unknown-tool");
        var onImplement = await ExecuteRefusalShapeAsync(implementFixture, "write-shaped-unknown-tool");

        Assert.Contains(CodexDynamicToolPolicy.WriteOutputTool, onReview.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(CodexDynamicToolPolicy.WriteTextTool, onReview.Text, StringComparison.Ordinal);
        Assert.Contains(CodexDynamicToolPolicy.WriteTextTool, onImplement.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot edit workspace files", onImplement.Text, StringComparison.Ordinal);
    }

    // #1972: LooksLikeWriteAttempt's narrowing was enforced by a comment alone -- re-adding "update" to
    // WriteAttemptFragments failed nothing. codex's own update_plan is not a file write, so answering it
    // about the write path is the same non-responsive guidance in the other direction. Both polarities
    // under ONE grant (review-shaped: no WriteFiles, one declared output, so DescribeWritePath reaches
    // the baton_write_output branch and the string under test is the one it emits), which is what makes
    // the negative row about the name rather than about the fixture.
    [Theory]
    [InlineData("update_plan", false)]
    [InlineData("str_replace_editor", true)]
    public async Task Only_a_write_shaped_unknown_tool_name_is_answered_about_the_write_path(
        string toolName, bool expectsWritePath)
    {
        using var fixture = new PolicyFixture(ReviewShapedGrant, ["report.md"]);

        var result = await fixture.ExecuteAsync(toolName, new { path = "src/file.cs", content = "x" });

        Assert.False(result.Success);
        Assert.Equal(
            expectsWritePath,
            result.Text.Contains("cannot edit workspace files", StringComparison.Ordinal));
    }

    /// <summary>The shipped review role's shape: reads, a scoped read-only shell, no workspace write.</summary>
    private static PermissionGrant ReviewShapedGrant => new(
        ReadFiles: true,
        RunShellCommands: true,
        ShellCommandPatterns: ["git diff*", "git log*", "git show*", "git status*"],
        DeniedShellCommandPatterns: ["git remote*"],
        ShellCommandsAreReadOnly: true);

    private static Task<CodexDynamicToolResult> ExecuteRefusalShapeAsync(
        PolicyFixture fixture, string refusalShape) => refusalShape switch
        {
            "outside-readable-roots" => fixture.ExecuteAsync(
                CodexDynamicToolPolicy.ReadTextTool,
                new { path = Path.Combine(Path.GetTempPath(), "baton-another-room", "verdict.json") }),
            "unsupported-backslash" => fixture.ExecuteAsync(
                CodexDynamicToolPolicy.RunCommandTool, new { command = @"git status C:\repo" }),
            "standing-deny" => fixture.ExecuteAsync(
                CodexDynamicToolPolicy.RunCommandTool, new { command = "git remote -v" }),
            "ungranted-pattern" => fixture.ExecuteAsync(
                CodexDynamicToolPolicy.RunCommandTool, new { command = "rg needle" }),
            // #1996 implemented apply_patch, so the write-shaped UNKNOWN population is now every other
            // editor name a model might reach for. str_replace_editor is one that still trips
            // LooksLikeWriteAttempt (on "edit"), which is what this row is testing.
            "write-shaped-unknown-tool" => fixture.ExecuteAsync(
                "str_replace_editor", new { path = "src/file.cs", content = "patch" }),
            _ => throw new InvalidOperationException($"Unknown refusal fixture '{refusalShape}'."),
        };

    [Fact]
    public async Task Withheld_workspace_write_can_still_write_only_an_exact_declared_output()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);

        var allowed = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.WriteOutputTool, new { name = "report.md", content = "review" });
        var denied = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.WriteOutputTool, new { name = "other.md", content = "escape" });

        Assert.True(allowed.Success);
        Assert.Equal("review", File.ReadAllText(Path.Combine(fixture.Output, "report.md")));
        Assert.False(denied.Success);
        Assert.False(File.Exists(Path.Combine(fixture.Output, "other.md")));
    }

    [Fact]
    public async Task Workspace_path_traversal_is_denied()
    {
        using var fixture = new PolicyFixture(
            new PermissionGrant(ReadFiles: true, WriteFiles: true), ["changes.md"]);
        var outside = Path.Combine(Path.GetDirectoryName(fixture.Workspace)!, "outside.txt");

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.WriteTextTool, new { path = outside, content = "escape" });

        Assert.False(result.Success);
        Assert.Contains("outside", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public async Task Workspace_writer_cannot_target_a_declared_output_artifact_path()
    {
        using var fixture = new PolicyFixture(
            new PermissionGrant(ReadFiles: true, WriteFiles: true), ["changes.md"]);
        var artifactPath = Path.Combine(fixture.Output, "changes.md");

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.WriteTextTool, new { path = artifactPath, content = "escape" });

        Assert.False(result.Success);
        Assert.Contains(GrantRefusal.Marker, result.Text);
        Assert.Contains("outside", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(artifactPath));
    }

    [Fact]
    public async Task Contract_input_is_readable_even_when_general_workspace_reads_are_withheld()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(), ["answer.md"], createInput: true);

        var input = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path = fixture.Input });
        var workspace = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path = Path.Combine(fixture.Workspace, "workspace.txt") });

        Assert.True(input.Success);
        Assert.Equal("input", input.Text);
        Assert.False(workspace.Success);
    }

    [Fact]
    public async Task Scoped_command_uses_canonical_allow_deny_and_option_token_checks()
    {
        var grant = new PermissionGrant(
            ReadFiles: true,
            RunShellCommands: true,
            ShellCommandPatterns: ["git *", "git log*"],
            DeniedShellCommandPatterns: ["git status --short*"],
            DeniedShellOptionTokens: ["--output"],
            ShellCommandsAreReadOnly: true);
        using var fixture = new PolicyFixture(grant, ["report.md"]);

        var allowed = await fixture.ExecuteAsync(CodexDynamicToolPolicy.RunCommandTool, new { command = "git --version" });
        var deniedFamily = await fixture.ExecuteAsync(CodexDynamicToolPolicy.RunCommandTool, new { command = "git status --short" });
        var deniedOption = await fixture.ExecuteAsync(CodexDynamicToolPolicy.RunCommandTool, new { command = "git log --output=escape" });
        var deniedChain = await fixture.ExecuteAsync(CodexDynamicToolPolicy.RunCommandTool, new { command = "git --version && echo escape" });

        Assert.True(allowed.Success);
        Assert.False(deniedFamily.Success);
        Assert.False(deniedOption.Success);
        Assert.False(deniedChain.Success);
    }

    /// <summary>
    /// #1921 review HIGH: the marker separates "the grant declined this" from "this ran and failed".
    /// Both arms in one test because the pair is the discrimination — an assertion that a refusal is
    /// marked passes just as well on the build that marked every failure, which is how the over-count
    /// shipped. The count is taken through the same parser a settle runs, over the envelope
    /// <c>CodexAppServerBroker</c> writes, rather than by re-testing for the marker a second way.
    /// </summary>
    [Fact]
    public async Task An_allowed_command_that_exits_non_zero_is_a_failure_and_a_denied_one_is_a_refusal()
    {
        var grant = new PermissionGrant(
            RunShellCommands: true, ShellCommandPatterns: ["exit*"], ShellCommandsAreReadOnly: true);
        using var fixture = new PolicyFixture(grant, ["report.md"]);

        // `exit 1` is a builtin of both shells this policy starts (cmd /d /s /c, /bin/sh -c).
        var failed = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = "exit 1" });
        var refused = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = "curl https://example.com" });

        Assert.False(failed.Success);
        Assert.Contains("Command exited 1", failed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(GrantRefusal.Marker, failed.Text);
        Assert.Equal(0, RefusedStepsCountedFor(failed));

        Assert.False(refused.Success);
        Assert.Contains(GrantRefusal.Marker, refused.Text);
        Assert.Equal(1, RefusedStepsCountedFor(refused));
    }

    /// <summary>
    /// #2206's measured contract through the production command path. The fixture puts diagnostics at
    /// both displayed edges and in the omitted middle of BOTH channels, then exits non-zero. Reading
    /// the middle through the opaque reference and replaying the result both leave the counter at one:
    /// recovery is not a second execution. A second policy is the cross-room polarity partner.
    /// </summary>
    [Fact]
    public async Task Long_failed_command_is_bounded_channelled_and_recoverable_without_reexecution()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(RunShellCommands: true), ["report.md"]);
        var command = LongFailureCommand(fixture.Workspace);

        var failed = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = command.CommandLine });

        Assert.False(failed.Success);
        Assert.True(failed.Text.Length <= 12_000, $"command returned {failed.Text.Length} characters");
        Assert.StartsWith("Command exited 7.", failed.Text, StringComparison.Ordinal);
        Assert.Contains("stdout:", failed.Text, StringComparison.Ordinal);
        Assert.Contains("stderr:", failed.Text, StringComparison.Ordinal);
        Assert.Contains(
            $"stdout: {command.Stdout.Length} characters produced", failed.Text, StringComparison.Ordinal);
        Assert.Contains(
            $"stderr: {command.Stderr.Length} characters produced", failed.Text, StringComparison.Ordinal);
        Assert.Contains("STDOUT-START", failed.Text, StringComparison.Ordinal);
        Assert.Contains("STDOUT-END", failed.Text, StringComparison.Ordinal);
        Assert.Contains("STDERR-START", failed.Text, StringComparison.Ordinal);
        Assert.Contains("STDERR-END", failed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("STDOUT-MIDDLE", failed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("STDERR-MIDDLE", failed.Text, StringComparison.Ordinal);
        Assert.Contains(CodexDynamicToolPolicy.ReadCommandOutputTool, failed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(GrantRefusal.Marker, failed.Text);

        var reference = CommandOutputReference(failed.Text);
        var stdoutMiddle = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadCommandOutputTool,
            new
            {
                reference,
                channel = "stdout",
                offset = command.Stdout.IndexOf("STDOUT-MIDDLE", StringComparison.Ordinal) - 40,
                length = 200,
            });
        var stderrMiddle = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadCommandOutputTool,
            new
            {
                reference,
                channel = "stderr",
                offset = command.Stderr.IndexOf("STDERR-MIDDLE", StringComparison.Ordinal) - 40,
                length = 200,
            });

        Assert.True(stdoutMiddle.Success, stdoutMiddle.Text);
        Assert.True(stderrMiddle.Success, stderrMiddle.Text);
        Assert.Contains("STDOUT-MIDDLE", stdoutMiddle.Text, StringComparison.Ordinal);
        Assert.Contains("STDERR-MIDDLE", stderrMiddle.Text, StringComparison.Ordinal);
        Assert.True(stdoutMiddle.Text.Length <= 12_000);
        Assert.True(stderrMiddle.Text.Length <= 12_000);

        var replay = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = command.CommandLine });
        Assert.False(replay.Success, replay.Text);
        Assert.Contains("replayed: identical command", replay.Text, StringComparison.Ordinal);
        Assert.True(replay.Text.Length <= 12_000, $"replay returned {replay.Text.Length} characters");
        Assert.Single(File.ReadAllLines(command.CounterPath));

        var invalid = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadCommandOutputTool,
            new { reference = "command-not-a-real-reference", channel = "stdout" });
        using var otherExecution = new PolicyFixture(
            new PermissionGrant(RunShellCommands: true), ["report.md"]);
        var foreign = await otherExecution.ExecuteAsync(
            CodexDynamicToolPolicy.ReadCommandOutputTool,
            new { reference, channel = "stdout" });
        using var withheld = new PolicyFixture(new PermissionGrant(), ["report.md"]);
        var ungranted = await withheld.ExecuteAsync(
            CodexDynamicToolPolicy.ReadCommandOutputTool,
            new { reference, channel = "stdout" });

        Assert.False(invalid.Success);
        Assert.False(foreign.Success);
        Assert.Equal(invalid.Text, foreign.Text);
        Assert.DoesNotContain(reference, foreign.Text, StringComparison.Ordinal);
        Assert.False(ungranted.Success);
        Assert.Contains(GrantRefusal.Marker, ungranted.Text);
    }

    [Fact]
    public async Task Command_output_ranges_are_scalar_safe_and_have_bounded_continuations()
    {
        using var fixture = new PolicyFixture(
            new PermissionGrant(
                RunShellCommands: true,
                ShellCommandPatterns: ["git *"],
                ShellCommandsAreReadOnly: true),
            ["report.md"]);
        File.WriteAllText(Path.Combine(fixture.Workspace, "😀.txt"), "unicode");
        var initialized = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = "git init" });
        Assert.True(initialized.Success, initialized.Text);

        var status = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool,
            new { command = "git -c core.quotepath=false status --short" });
        Assert.True(status.Success, status.Text);
        var reference = CommandOutputReference(status.Text);
        var whole = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadCommandOutputTool,
            new { reference, channel = "stdout" });
        var raw = whole.Text[(whole.Text.IndexOf('\n', StringComparison.Ordinal) + 1)..];
        var scalarOffset = raw.IndexOf("😀", StringComparison.Ordinal);
        Assert.True(scalarOffset >= 0, whole.Text);

        var scalar = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadCommandOutputTool,
            new { reference, channel = "stdout", offset = scalarOffset, length = 1 });
        var equivalentScalar = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadCommandOutputTool,
            new { reference, channel = "stdout", offset = scalarOffset, length = 2 });
        var split = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadCommandOutputTool,
            new { reference, channel = "stdout", offset = scalarOffset + 1, length = 1 });

        Assert.True(scalar.Success, scalar.Text);
        Assert.Contains("😀", scalar.Text, StringComparison.Ordinal);
        Assert.True(scalar.Text.Length <= 12_000);
        Assert.Equal(scalar.Text, JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(scalar.Text)));
        Assert.True(equivalentScalar.Success, equivalentScalar.Text);
        Assert.StartsWith("[replayed: identical read", equivalentScalar.Text, StringComparison.Ordinal);
        Assert.False(split.Success);
        Assert.Contains("Unicode scalar boundary", split.Text, StringComparison.Ordinal);
        Assert.Contains("next range:", scalar.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_retention_limit_reports_permanent_loss_explicitly()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(RunShellCommands: true), ["report.md"]);
        var commandLine = OverflowCommand(fixture.Workspace);

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = commandLine });

        Assert.True(result.Success, result.Text);
        Assert.True(result.Text.Length <= 12_000, $"command returned {result.Text.Length} characters");
        Assert.Contains("1000000-character per-channel retention limit", result.Text, StringComparison.Ordinal);
        Assert.Contains("permanently unavailable", result.Text, StringComparison.Ordinal);
        var reference = CommandOutputReference(result.Text);
        var lastRetained = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadCommandOutputTool,
            new { reference, channel = "stdout", offset = 999_900, length = 100 });
        var pastRetention = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadCommandOutputTool,
            new { reference, channel = "stdout", offset = 1_000_001, length = 1 });

        Assert.True(lastRetained.Success, lastRetained.Text);
        Assert.Contains("retention loss:", lastRetained.Text, StringComparison.Ordinal);
        Assert.True(lastRetained.Text.Length <= 12_000);
        Assert.False(pastRetention.Success);
        Assert.Contains("past the retained stdout length", pastRetention.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timed_out_command_is_replayed_as_failure_with_recoverable_output_and_no_second_side_effect()
    {
        using var fixture = new PolicyFixture(
            new PermissionGrant(RunShellCommands: true),
            ["report.md"],
            // wait-ok: injected ceiling reaches the timeout branch quickly; the child is killed there.
            commandCeiling: _ => TimeSpan.FromMilliseconds(500));
        var command = HangingSideEffectCommand(fixture.Workspace, "timeout");

        var timedOut = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = command.CommandLine });
        var reference = CommandOutputReference(timedOut.Text);
        var retained = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadCommandOutputTool,
            new { reference, channel = "stdout", offset = 0, length = 100 });
        var replay = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = command.CommandLine });

        Assert.False(timedOut.Success, timedOut.Text);
        Assert.Contains("default command ceiling", timedOut.Text, StringComparison.Ordinal);
        Assert.True(retained.Success, retained.Text);
        Assert.Contains("BEFORE-HANG", retained.Text, StringComparison.Ordinal);
        Assert.False(replay.Success);
        Assert.Contains("replayed: identical command", replay.Text, StringComparison.Ordinal);
        Assert.Contains(reference, replay.Text, StringComparison.Ordinal);
        Assert.Single(File.ReadAllLines(command.CounterPath));
    }

    [Fact]
    public async Task Timed_out_volatile_diff_gets_one_retry_after_expiry_then_replays_that_failure()
    {
        var clock = new StepClock(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero));
        var expectedAttempt = 0;
        using var fixture = new PolicyFixture(
            new PermissionGrant(RunShellCommands: true),
            ["report.md"],
            // wait-ok: injected ceiling reaches the timeout branch quickly; the child is killed there.
            commandCeiling: _ => TimeSpan.FromMilliseconds(500),
            timeProvider: clock,
            // This rendezvous runs after Process.Start and immediately before CancelAfter. Waiting
            // for attempt N here therefore controls the timeout boundary; it is not a ready signal
            // racing a timeout that was already armed.
            beforeCommandTimeoutStarts: (fixture, cancellationToken) => WaitForCommandAttempt(
                Path.Combine(fixture.Workspace, "volatile-timeout-runs.txt"),
                ++expectedAttempt,
                cancellationToken,
                "external-diff helper"));
        var command = HangingExternalDiffCommand(fixture.Workspace, "volatile-timeout");

        var firstFailure = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = command.CommandLine });
        AssertExternalDiffAttempts(command.CounterPath, 1, "first timeout");
        var immediateReplay = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = command.CommandLine });
        AssertExternalDiffAttempts(command.CounterPath, 1, "immediate replay");
        clock.Advance(RepeatedToolCallLedger.Window + TimeSpan.FromSeconds(1));
        var failureAfterExpiry = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = command.CommandLine });
        AssertExternalDiffAttempts(command.CounterPath, 2, "post-expiry timeout");
        var replayAfterExpiry = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = command.CommandLine });
        AssertExternalDiffAttempts(command.CounterPath, 2, "final replay");

        Assert.False(firstFailure.Success, firstFailure.Text);
        Assert.Contains("default command ceiling", firstFailure.Text, StringComparison.Ordinal);
        Assert.False(immediateReplay.Success, immediateReplay.Text);
        Assert.Contains("replayed: identical command", immediateReplay.Text, StringComparison.Ordinal);
        Assert.False(failureAfterExpiry.Success, failureAfterExpiry.Text);
        Assert.Contains("default command ceiling", failureAfterExpiry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("replayed: identical command", failureAfterExpiry.Text, StringComparison.Ordinal);
        Assert.False(replayAfterExpiry.Success, replayAfterExpiry.Text);
        Assert.Contains("default command ceiling", replayAfterExpiry.Text, StringComparison.Ordinal);
        Assert.Contains("replayed: identical command", replayAfterExpiry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Throwing_pre_timeout_rendezvous_cleans_up_started_command_and_replays_failure()
    {
        List<DisposalTrackingStream> captures = [];
        using var fixture = new PolicyFixture(
            new PermissionGrant(RunShellCommands: true),
            ["report.md"],
            commandCaptureStreamFactory: path =>
            {
                var capture = new DisposalTrackingStream(WritableCaptureStream(path));
                captures.Add(capture);
                return capture;
            },
            beforeCommandTimeoutStarts: (fixture, cancellationToken) =>
            {
                WaitForCommandAttempt(
                    Path.Combine(fixture.Workspace, "throwing-rendezvous-runs.txt"),
                    1,
                    cancellationToken,
                    "hanging child");
                throw new InvalidOperationException("synthetic rendezvous failure");
            });
        var command = HangingSideEffectCommand(fixture.Workspace, "throwing-rendezvous");

        var failed = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = command.CommandLine });
        var replay = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = command.CommandLine });

        Assert.False(failed.Success, failed.Text);
        Assert.Contains("test-only pre-timeout rendezvous failed", failed.Text, StringComparison.Ordinal);
        Assert.Contains("synthetic rendezvous failure", failed.Text, StringComparison.Ordinal);
        Assert.Contains("BEFORE-HANG", failed.Text, StringComparison.Ordinal);
        Assert.False(replay.Success, replay.Text);
        Assert.Contains("replayed: identical command", replay.Text, StringComparison.Ordinal);
        Assert.Contains("synthetic rendezvous failure", replay.Text, StringComparison.Ordinal);
        Assert.Single(File.ReadAllLines(command.CounterPath));
        Assert.False(File.Exists(command.CompletionPath));
        Assert.Equal(2, captures.Count);
        Assert.All(captures, capture => Assert.True(capture.IsDisposed));
    }

    [Fact]
    public async Task Uncancelled_rendezvous_cancellation_is_recorded_as_callback_failure_not_timeout()
    {
        using var fixture = new PolicyFixture(
            new PermissionGrant(RunShellCommands: true),
            ["report.md"],
            beforeCommandTimeoutStarts: (fixture, cancellationToken) =>
            {
                WaitForCommandAttempt(
                    Path.Combine(fixture.Workspace, "uncancelled-rendezvous-runs.txt"),
                    1,
                    cancellationToken,
                    "hanging child");
                throw new OperationCanceledException("synthetic uncancelled rendezvous cancellation");
            });
        var command = HangingSideEffectCommand(fixture.Workspace, "uncancelled-rendezvous");

        var failed = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = command.CommandLine });
        var replay = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = command.CommandLine });

        Assert.False(failed.Success, failed.Text);
        Assert.Contains("test-only pre-timeout rendezvous failed", failed.Text, StringComparison.Ordinal);
        Assert.Contains(
            "OperationCanceledException: synthetic uncancelled rendezvous cancellation",
            failed.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("default command ceiling", failed.Text, StringComparison.Ordinal);
        Assert.Contains("BEFORE-HANG", failed.Text, StringComparison.Ordinal);
        Assert.False(replay.Success, replay.Text);
        Assert.Contains("replayed: identical command", replay.Text, StringComparison.Ordinal);
        Assert.Contains("synthetic uncancelled rendezvous cancellation", replay.Text, StringComparison.Ordinal);
        Assert.Single(File.ReadAllLines(command.CounterPath));
        Assert.False(File.Exists(command.CompletionPath));
    }

    [Fact]
    public async Task Cancelled_pre_timeout_rendezvous_cleans_up_and_records_failure_before_propagating()
    {
        List<DisposalTrackingStream> captures = [];
        CancellationTokenSource? callerCancellation = null;
        using var fixture = new PolicyFixture(
            new PermissionGrant(RunShellCommands: true),
            ["report.md"],
            commandCaptureStreamFactory: path =>
            {
                var capture = new DisposalTrackingStream(WritableCaptureStream(path));
                captures.Add(capture);
                return capture;
            },
            beforeCommandTimeoutStarts: (fixture, cancellationToken) =>
            {
                WaitForCommandAttempt(
                    Path.Combine(fixture.Workspace, "cancelled-rendezvous-runs.txt"),
                    1,
                    cancellationToken,
                    "hanging child");
                callerCancellation!.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            });
        var command = HangingSideEffectCommand(fixture.Workspace, "cancelled-rendezvous");
        using var cancellation = new CancellationTokenSource();
        callerCancellation = cancellation;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ExecuteWithCancellationAsync(
            CodexDynamicToolPolicy.RunCommandTool,
            new { command = command.CommandLine },
            cancellation.Token));
        var replay = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = command.CommandLine });

        Assert.False(replay.Success, replay.Text);
        Assert.Contains("replayed: identical command", replay.Text, StringComparison.Ordinal);
        Assert.Contains("Command was cancelled after it started.", replay.Text, StringComparison.Ordinal);
        Assert.Contains("BEFORE-HANG", replay.Text, StringComparison.Ordinal);
        Assert.Single(File.ReadAllLines(command.CounterPath));
        Assert.False(File.Exists(command.CompletionPath));
        Assert.Equal(2, captures.Count);
        Assert.All(captures, capture => Assert.True(capture.IsDisposed));
    }

    [Fact]
    public void Completed_command_attempt_marker_is_readable_while_its_writer_remains_open()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows enforces the marker file sharing contract this control exercises");
            return;
        }

        using var fixture = new PolicyFixture(
            new PermissionGrant(RunShellCommands: true),
            ["report.md"]);
        var markerPath = Path.Combine(fixture.Workspace, "open-marker-runs.txt");
        using var marker = new FileStream(
            markerPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read);

        marker.Write("ra"u8);
        marker.Flush(flushToDisk: true);
        Assert.Equal(0, ExternalDiffAttemptCount(markerPath));

        marker.Write("n\r\n"u8);
        marker.Flush(flushToDisk: true);
        Assert.Equal(1, ExternalDiffAttemptCount(markerPath));
    }

    [Fact]
    public async Task Failed_volatile_diff_invalidates_cached_command_and_read_but_keeps_its_failure()
    {
        using var fixture = new PolicyFixture(
            new PermissionGrant(ReadFiles: true, RunShellCommands: true),
            ["report.md"],
            // wait-ok: injected ceiling reaches the timeout branch quickly; the child is killed there.
            commandCeiling: _ => TimeSpan.FromMilliseconds(500));
        var volatileCommand = HangingExternalDiffCommand(fixture.Workspace, "volatile-cache");
        var originalStamp = File.GetLastWriteTimeUtc(volatileCommand.MutatedPath);
        var cachedCommand = OperatingSystem.IsWindows()
            ? $"type {Path.GetFileName(volatileCommand.MutatedPath)}"
            : $"cat ./{Path.GetFileName(volatileCommand.MutatedPath)}";

        var commandBefore = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = cachedCommand });
        var readBefore = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path = volatileCommand.MutatedPath });
        var failed = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = volatileCommand.CommandLine });
        // Make the stat pair identical so only explicit invalidation can expose the helper's mutation.
        File.SetLastWriteTimeUtc(volatileCommand.MutatedPath, originalStamp);
        var failureReplay = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = volatileCommand.CommandLine });
        var readAfter = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path = volatileCommand.MutatedPath });
        var commandAfter = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = cachedCommand });

        Assert.Contains("before", commandBefore.Text, StringComparison.Ordinal);
        Assert.Equal("before", readBefore.Text);
        Assert.False(failed.Success, failed.Text);
        Assert.Equal("AFTER!", readAfter.Text);
        Assert.DoesNotContain("replayed: identical read", readAfter.Text, StringComparison.Ordinal);
        Assert.Contains("AFTER!", commandAfter.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("replayed: identical command", commandAfter.Text, StringComparison.Ordinal);
        Assert.False(failureReplay.Success, failureReplay.Text);
        Assert.Contains("replayed: identical command", failureReplay.Text, StringComparison.Ordinal);
        Assert.Single(File.ReadAllLines(volatileCommand.CounterPath));
    }

    /// <summary>
    /// A normal process exit only settles capture; it does not prove either exit success or mutation
    /// safety. Both exit dispositions invoke the same external-diff helper and restore its target's
    /// stat pair, so only broad post-execution invalidation can expose the same-length mutation. The
    /// non-zero arm also proves the recorded disposition remains a failed replay, not a successful
    /// result inferred from normal completion.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(7, false)]
    public async Task Normally_completed_volatile_helper_invalidates_unrelated_caches(
        int helperExitCode, bool expectedSuccess)
    {
        using var fixture = new PolicyFixture(
            new PermissionGrant(ReadFiles: true, RunShellCommands: true), ["report.md"]);
        var volatileCommand = CompletedExternalDiffCommand(
            fixture.Workspace, $"volatile-exit-{helperExitCode}", helperExitCode);
        var originalStamp = File.GetLastWriteTimeUtc(volatileCommand.MutatedPath);
        var cachedCommand = OperatingSystem.IsWindows()
            ? $"type {Path.GetFileName(volatileCommand.MutatedPath)}"
            : $"cat ./{Path.GetFileName(volatileCommand.MutatedPath)}";

        var commandBefore = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = cachedCommand });
        var readBefore = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path = volatileCommand.MutatedPath });
        var completed = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = volatileCommand.CommandLine });
        File.SetLastWriteTimeUtc(volatileCommand.MutatedPath, originalStamp);

        CodexDynamicToolResult? failedReplay = null;
        if (!expectedSuccess)
        {
            failedReplay = await fixture.ExecuteAsync(
                CodexDynamicToolPolicy.RunCommandTool, new { command = volatileCommand.CommandLine });
        }

        var readAfter = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path = volatileCommand.MutatedPath });
        var commandAfter = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = cachedCommand });

        Assert.Contains("before", commandBefore.Text, StringComparison.Ordinal);
        Assert.Equal("before", readBefore.Text);
        Assert.Equal(expectedSuccess, completed.Success);
        Assert.Equal("AFTER!", readAfter.Text);
        Assert.DoesNotContain("replayed: identical read", readAfter.Text, StringComparison.Ordinal);
        Assert.Contains("AFTER!", commandAfter.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("replayed: identical command", commandAfter.Text, StringComparison.Ordinal);
        if (failedReplay is not null)
        {
            Assert.False(failedReplay.Success, failedReplay.Text);
            Assert.Contains("replayed: identical command", failedReplay.Text, StringComparison.Ordinal);
        }
        Assert.Single(File.ReadAllLines(volatileCommand.CounterPath));
    }

    [Fact]
    public async Task Changed_or_oversized_retained_file_is_rejected_without_reading_the_replacement()
    {
        using var fixture = new PolicyFixture(
            new PermissionGrant(RunShellCommands: true), ["report.md"]);
        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = "echo retained-output" });
        Assert.True(result.Success, result.Text);
        var reference = CommandOutputReference(result.Text);
        var retainedPath = Path.Combine(fixture.Output, $".{reference}.stdout.log");
        var original = File.ReadAllBytes(retainedPath);
        original[0] ^= 0x01;
        File.WriteAllBytes(retainedPath, original);

        var changed = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadCommandOutputTool,
            new { reference, channel = "stdout", offset = 0, length = 1 });

        using (var replacement = new FileStream(retainedPath, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            replacement.SetLength(500_000_000);
        }
        var oversized = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadCommandOutputTool,
            new { reference, channel = "stdout", offset = 0, length = 1 });

        Assert.False(changed.Success);
        Assert.Contains("changed after capture", changed.Text, StringComparison.Ordinal);
        Assert.False(oversized.Success);
        Assert.Contains("changed after capture", oversized.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_failures_before_and_after_spawn_never_repeat_a_command_side_effect()
    {
        var opens = 0;
        using var openFailure = new PolicyFixture(
            new PermissionGrant(RunShellCommands: true),
            ["report.md"],
            commandCaptureStreamFactory: _ => ++opens == 2
                ? throw new IOException("synthetic open failure")
                : new MemoryStream());
        var unopened = HangingSideEffectCommand(openFailure.Workspace, "unopened-capture");

        var didNotStart = await openFailure.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = unopened.CommandLine });

        Assert.False(didNotStart.Success);
        Assert.Contains("synthetic open failure", didNotStart.Text, StringComparison.Ordinal);
        Assert.False(File.Exists(unopened.CounterPath));

        var captureOpens = 0;
        using var writeFailure = new PolicyFixture(
            new PermissionGrant(ReadFiles: true, RunShellCommands: true),
            ["report.md"],
            commandCaptureStreamFactory: path => ++captureOpens is 3 or 4
                ? new WriteFailingStream()
                : WritableCaptureStream(path));
        var started = HangingExternalDiffCommand(writeFailure.Workspace, "failed-capture");
        var originalStamp = File.GetLastWriteTimeUtc(started.MutatedPath);
        var cachedCommand = OperatingSystem.IsWindows()
            ? $"type {Path.GetFileName(started.MutatedPath)}"
            : $"cat ./{Path.GetFileName(started.MutatedPath)}";
        await writeFailure.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = cachedCommand });
        await writeFailure.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path = started.MutatedPath });

        var failed = await writeFailure.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = started.CommandLine });
        File.SetLastWriteTimeUtc(started.MutatedPath, originalStamp);
        var replay = await writeFailure.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = started.CommandLine });
        var readAfter = await writeFailure.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path = started.MutatedPath });
        var commandAfter = await writeFailure.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = cachedCommand });

        Assert.False(failed.Success);
        Assert.Contains("retaining its output failed", failed.Text, StringComparison.Ordinal);
        Assert.False(replay.Success);
        Assert.Contains("replayed: identical command", replay.Text, StringComparison.Ordinal);
        Assert.Equal("AFTER!", readAfter.Text);
        Assert.DoesNotContain("replayed: identical read", readAfter.Text, StringComparison.Ordinal);
        Assert.Contains("AFTER!", commandAfter.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("replayed: identical command", commandAfter.Text, StringComparison.Ordinal);
        Assert.Single(File.ReadAllLines(started.CounterPath));
    }

    [Fact]
    public async Task Command_output_repeat_identity_includes_reference_channel_and_actual_window()
    {
        using var fixture = new PolicyFixture(
            new PermissionGrant(RunShellCommands: true), ["report.md"]);
        var command = LongFailureCommand(fixture.Workspace);
        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = command.CommandLine });
        var reference = CommandOutputReference(result.Text);

        var stdout = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadCommandOutputTool,
            new { reference, channel = "stdout", offset = 0, length = 20 });
        var stderr = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadCommandOutputTool,
            new { reference, channel = "stderr", offset = 0, length = 20 });
        var replay = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadCommandOutputTool,
            new { reference, channel = "stdout", offset = 0, length = 20 });
        var refused = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadCommandOutputTool,
            new { reference, channel = "stdout", offset = 0, length = 20 });

        Assert.True(stdout.Success, stdout.Text);
        Assert.True(stderr.Success, stderr.Text);
        Assert.DoesNotContain("replayed:", stderr.Text, StringComparison.Ordinal);
        Assert.True(replay.Success, replay.Text);
        Assert.StartsWith("[replayed: identical read", replay.Text, StringComparison.Ordinal);
        Assert.False(refused.Success);
        Assert.Contains(RepeatedToolCallLedger.ReadRepeatRefusal, refused.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_but_an_identical_retry_cannot_repeat_side_effects()
    {
        using var fixture = new PolicyFixture(
            new PermissionGrant(ReadFiles: true, RunShellCommands: true),
            ["report.md"],
            commandCeiling: _ => TimeSpan.FromSeconds(5));
        var command = HangingExternalDiffCommand(fixture.Workspace, "cancelled");
        var originalStamp = File.GetLastWriteTimeUtc(command.MutatedPath);
        var cachedCommand = OperatingSystem.IsWindows()
            ? $"type {Path.GetFileName(command.MutatedPath)}"
            : $"cat ./{Path.GetFileName(command.MutatedPath)}";
        await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = cachedCommand });
        await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path = command.MutatedPath });
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var readyWatcher = new FileSystemWatcher(
            fixture.Workspace, Path.GetFileName(command.ReadyPath))
        {
            NotifyFilter = NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        readyWatcher.Created += (_, _) => ready.TrySetResult();
        readyWatcher.Renamed += (_, _) => ready.TrySetResult();
        using var cancellation = new CancellationTokenSource();
        var execution = fixture.ExecuteWithCancellationAsync(
            CodexDynamicToolPolicy.RunCommandTool,
            new { command = command.CommandLine },
            cancellation.Token);

        try
        {
            if (File.Exists(command.ReadyPath))
            {
                ready.TrySetResult();
            }
            // wait-ok: bounded hang backstop; the helper atomically publishes ready after both side effects.
            await ready.Task.WaitAsync(
                TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.True(File.Exists(command.ReadyPath), "helper readiness must precede caller cancellation");
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                // wait-ok: cleanup backstop; cancellation kills the synthetic hanging child.
                await execution.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
        File.SetLastWriteTimeUtc(command.MutatedPath, originalStamp);
        var replay = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = command.CommandLine });
        var readAfter = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path = command.MutatedPath });
        var commandAfter = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = cachedCommand });

        Assert.False(replay.Success);
        Assert.Contains("replayed: identical command", replay.Text, StringComparison.Ordinal);
        Assert.Contains("cancelled after it started", replay.Text, StringComparison.Ordinal);
        Assert.Equal("AFTER!", readAfter.Text);
        Assert.DoesNotContain("replayed: identical read", readAfter.Text, StringComparison.Ordinal);
        Assert.Contains("AFTER!", commandAfter.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("replayed: identical command", commandAfter.Text, StringComparison.Ordinal);
        Assert.Single(File.ReadAllLines(command.CounterPath));
    }

    /// <summary>
    /// The third outcome the funnel used to mark: a command the grant ALLOWED that Baton then killed for
    /// exceeding its ceiling. Run against a 150ms ceiling rather than the shipped minutes — the ceiling
    /// is a constructor parameter for exactly this reason.
    /// <para>
    /// #1998: an ordinary command is on the DEFAULT class, and the text says so — the polarity partner of
    /// <see cref="A_shipping_command_is_killed_at_the_shipping_ceiling_and_says_so"/> below.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_command_killed_at_the_tool_limit_is_a_failure_and_carries_no_refusal_marker()
    {
        var sleep = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1" : "sleep 30";
        var grant = new PermissionGrant(
            RunShellCommands: true, ShellCommandPatterns: ["ping*", "sleep*"], ShellCommandsAreReadOnly: true);
        using var fixture = new PolicyFixture(
            grant, ["report.md"], commandCeiling: _ => TimeSpan.FromMilliseconds(150));

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = sleep });

        Assert.False(result.Success);
        Assert.Contains("default command ceiling", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(GrantRefusal.Marker, result.Text);
        Assert.False(ShellCommandCeilings.IsShippingCeilingTimeout(result.Text));
        Assert.Equal(0, RefusedStepsCountedFor(result));
    }

    /// <summary>
    /// #1998: the broker bounds a command by its CLASS, and a shipping command is killed at the shipping
    /// ceiling rather than the default one. The two injected ceilings differ, so a policy that classified
    /// this line as <see cref="ShellCommandClass.Other"/> would print the other figure and fail here —
    /// which is what makes the arm discriminate rather than merely reach the timeout.
    /// <para>
    /// <b>No push can occur, and the fixture's own directory is not what guarantees that.</b> It is a
    /// fresh temp directory with no repository, but <c>git</c> walks UP — on Windows the temp root sits
    /// under the user profile, and an ancestor that happens to be a repository would give a bare
    /// <c>git push</c> a real upstream. What makes this inert is the command itself:
    /// <c>--dry-run</c> against a remote name that exists nowhere. Its leading tokens are unchanged, so
    /// it classifies exactly as a real push does; the push therefore fails on every host, and
    /// <c>||</c> is what runs the hanging segment and carries the line to the ceiling.
    /// </para>
    /// <para>
    /// <b>Seam for #2002, named rather than assumed away.</b> That issue's arm 1 refuses backgrounding
    /// shapes "including a trailing <c>&amp;</c>"; this arm reached the ceiling through a mid-line
    /// <c>&amp;</c> until now, so a refusal pattern matching one would have taken the only end-to-end
    /// proof of the shipping ceiling with it. <c>||</c> is not a backgrounding shape on either shell, so
    /// nothing in this PR now depends on that decision. Arm 2 is the one still open: if a byte-identical
    /// repeat replays the previous output, a replayed timeout re-emits the marker in a fresh
    /// run-command result, and <c>ShippingCeilingStreamReader</c> reads the FINAL such result — so #2002
    /// has to decide whether a replay is a fresh answer, and its note must not lead with the marker.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_shipping_command_is_killed_at_the_shipping_ceiling_and_says_so()
    {
        var hang = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1" : "sleep 30";
        var grant = new PermissionGrant(
            RunShellCommands: true, ShellCommandPatterns: ["git push*", "ping*", "sleep*"]);
        using var fixture = new PolicyFixture(
            grant,
            ["report.md"],
            commandCeiling: commandClass => commandClass == ShellCommandClass.Shipping
                ? TimeSpan.FromMilliseconds(150)
                : TimeSpan.FromMilliseconds(100));

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool,
            new { command = $"git push --dry-run nonexistent-remote-xyzzy || {hang}" });

        Assert.False(result.Success);
        Assert.Contains("shipping command ceiling (0.15 s)", result.Text, StringComparison.Ordinal);
        Assert.True(ShellCommandCeilings.IsShippingCeilingTimeout(result.Text));
        Assert.DoesNotContain(GrantRefusal.Marker, result.Text);
    }

    /// <summary>
    /// #2135: <c>git commit</c> is a publication verb too — a repository's pre-commit hook can run for
    /// minutes exactly as its pre-push hook can (basis #988 splits its hook that way: lint at commit,
    /// tests at push) — so it has to reach the shipping ceiling on this path the same way
    /// <see cref="A_shipping_command_is_killed_at_the_shipping_ceiling_and_says_so"/> shows for
    /// <c>git push</c>. The fixture's temp directory holds no repository, so the commit itself fails
    /// immediately ("not a git repository") and <c>||</c> carries the line to the hang, exactly as the
    /// push arm's own remark explains for that command.
    /// </summary>
    [Fact]
    public async Task A_git_commit_is_killed_at_the_shipping_ceiling_and_says_so()
    {
        var hang = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1" : "sleep 30";
        var grant = new PermissionGrant(
            RunShellCommands: true, ShellCommandPatterns: ["git commit*", "ping*", "sleep*"]);
        using var fixture = new PolicyFixture(
            grant,
            ["report.md"],
            commandCeiling: commandClass => commandClass == ShellCommandClass.Shipping
                ? TimeSpan.FromMilliseconds(150)
                : TimeSpan.FromMilliseconds(100));

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool,
            new { command = $"git commit -m wip || {hang}" });

        Assert.False(result.Success);
        Assert.Contains("shipping command ceiling (0.15 s)", result.Text, StringComparison.Ordinal);
        Assert.True(ShellCommandCeilings.IsShippingCeilingTimeout(result.Text));
        Assert.DoesNotContain(GrantRefusal.Marker, result.Text);
    }

    /// <summary>
    /// A read of a path the grant ALLOWS that simply is not there — unsuccessful, unmarked, uncounted.
    /// The polarity partner is <see cref="Contract_input_is_readable_even_when_general_workspace_reads_are_withheld"/>'s
    /// second arm, where the same tool is refused because the path is outside the readable roots.
    /// </summary>
    [Fact]
    public async Task A_missing_file_inside_the_granted_roots_is_a_failure_and_a_path_outside_them_is_a_refusal()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);

        var missing = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path = Path.Combine(fixture.Workspace, "absent.txt") });
        var outside = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool,
            new { path = Path.Combine(Path.GetDirectoryName(fixture.Workspace)!, "outside.txt") });

        Assert.False(missing.Success);
        Assert.DoesNotContain(GrantRefusal.Marker, missing.Text);
        Assert.False(outside.Success);
        Assert.Contains(GrantRefusal.Marker, outside.Text);
    }

    /// <summary>
    /// The last arm of the funnel the refused/failed split did not re-examine (#1921 re-review): a tool
    /// name Baton implements NOWHERE. Every implemented name has its own case with its own grant check,
    /// so a tool a grant withheld never reaches the fallthrough — what reaches it is a hallucinated or
    /// stale name, a malformed call rather than a decision any grant took. Paired with the withheld
    /// <c>baton_write_text</c> on the same role, which is a real refusal, because an assertion that the
    /// unknown tool is unmarked passes just as well on a build that stopped marking anything.
    /// </summary>
    [Fact]
    public async Task An_unimplemented_tool_name_is_a_failure_and_a_withheld_implemented_one_is_a_refusal()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);

        var unknown = await fixture.ExecuteAsync("str_replace_editor", new { path = "src/a.cs", content = "x" });
        var withheld = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.WriteTextTool, new { path = "src/a.cs", content = "x" });

        Assert.False(unknown.Success);
        Assert.DoesNotContain(GrantRefusal.Marker, unknown.Text);
        Assert.Equal(0, RefusedStepsCountedFor(unknown));

        Assert.False(withheld.Success);
        Assert.Contains(GrantRefusal.Marker, withheld.Text);
        Assert.Equal(1, RefusedStepsCountedFor(withheld));
    }

    /// <summary>
    /// The <c>item.completed</c> envelope <c>CodexAppServerBroker</c> writes for one result, counted by
    /// the parser a settle and <c>baton audit lanes</c> both read through.
    /// </summary>
    private static int RefusedStepsCountedFor(CodexDynamicToolResult result) =>
        new CodexUsageParser().CountRefusedToolSteps(JsonSerializer.Serialize(new
        {
            type = "item.completed",
            item = new
            {
                type = "mcp_tool_call",
                tool = "baton_run_command",
                status = result.Success ? "completed" : "failed",
                aggregated_output = result.Text,
            },
        }));

    /// <summary>
    /// #2016 re-review MEDIUM: the BROKER wiring of <see cref="OwnPullRequestOnlyRule"/> — the third
    /// of its three enforcement points, and the only one whose end-to-end path had no test. What the
    /// rule itself decides is specified once, in <c>OwnPullRequestOnlyRuleTests</c>; nothing here
    /// re-tests that. What is only true on this path is asserted instead: that the rule is
    /// CONSTRUCTED at all for a codex implement lane (the <c>AppliesTo</c> call in the constructor),
    /// that <see cref="CodexDynamicToolPolicy.RunCommandTool"/> consults it, and that the answer
    /// arrives as a Refused rather than a Failed, so it lands in the refusal count.
    /// <para>
    /// Off the REAL catalog grant, because the claim is about the shipped implement role: a role
    /// whose shell patterns grew a <c>gh pr</c> allowance would silently stop being governed here.
    /// No process is spawned — the refusal returns ahead of <c>Process.Start</c>, which is what makes
    /// this testable without a vendor CLI on the machine.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_codex_implement_lane_is_refused_a_pull_request_it_did_not_open()
    {
        using var fixture = new PolicyFixture(WorkerRoleCatalog.For("implement").Grant, ["changes.md"]);

        var refused = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = "gh pr view 1994" });

        Assert.False(refused.Success);
        // The rule's own sentence, not merely "a refusal": the allow/deny and option-token rungs above
        // it refuse too, and a loose assertion passes on a build where this rung was never wired.
        Assert.Contains(OwnPullRequestOnlyRule.Rule, refused.Text, StringComparison.Ordinal);
        Assert.Contains("has not opened a pull request yet", refused.Text, StringComparison.Ordinal);
        // Refused rather than Failed, which is what RunCommandAsync's comment at this rung claims.
        Assert.Contains(GrantRefusal.Marker, refused.Text);
        Assert.Equal(1, RefusedStepsCountedFor(refused));
    }

    /// <summary>
    /// A refused sibling read answers the same way however often it is asked: it is never replayed
    /// and never becomes #2002's repeat refusal, so the lane keeps being told which rule refused it
    /// rather than being told it already asked. That holds because the ledger only ever replays an
    /// entry whose OUTPUT it recorded, and a refused command produced none — see
    /// <c>RepeatedToolCallLedger.ClassifyCommand</c>, which states that case directly.
    /// <para>
    /// Deliberately NOT claiming to pin the rung order. The sibling rung does sit ahead of
    /// <c>_repeats.ClassifyCommand</c> on this path, but reversing the two would not change any
    /// answer here for exactly the reason above, so no test can show it and this one does not.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_refused_sibling_read_is_never_replayed_or_answered_by_the_repeat_rule()
    {
        using var fixture = new PolicyFixture(WorkerRoleCatalog.For("implement").Grant, ["changes.md"]);

        for (var ask = 1; ask <= 3; ask++)
        {
            var refused = await fixture.ExecuteAsync(
                CodexDynamicToolPolicy.RunCommandTool, new { command = "gh pr view 1994" });

            Assert.False(refused.Success);
            Assert.Contains(OwnPullRequestOnlyRule.Rule, refused.Text, StringComparison.Ordinal);
            // The two answers the ledger would give instead, named rather than merely implied by the
            // sentence above: a replay is a SUCCESS carrying a preamble, and the repeat refusal is a
            // different sentence about a different rule.
            Assert.DoesNotContain("replayed:", refused.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(
                RepeatedToolCallLedger.CommandRepeatRefusal, refused.Text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The #2190 production shape end to end through a hermetic fake: a preselected executable
    /// outside the workspace receives exact argv, including a quoted Windows title as one value.
    /// Its attributed, successful, repository-matching output opens exactly one repository-qualified
    /// read. The interpreter prefix is an explicit internal test seam; production accepts only a
    /// native file named gh/gh.exe and supplies no prefix.
    /// </summary>
    [Fact]
    public async Task The_broker_learns_ownership_only_from_direct_verified_gh_create()
    {
        const string ownPullRequestUrl = "https://github.com/aer-works/baton/pull/2005";
        using var fixture = new PolicyFixture(
            WorkerRoleCatalog.For("implement").Grant,
            ["changes.md"],
            directGhOutput: ownPullRequestUrl);
        var gh = ShimGh(fixture.Workspace, ownPullRequestUrl, surroundingCharacters: 3_500);

        var before = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = $"{gh} pr view 2005" });
        var create = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool,
            new { command = "gh pr create --draft --title \"fix(codex): Example [proof]\" --body-file body.md" });
        var after = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = $"{gh} pr view 2005" });

        Assert.False(before.Success);
        Assert.Contains("has not opened a pull request yet", before.Text, StringComparison.Ordinal);
        Assert.True(create.Success, create.Text);
        Assert.Equal(
            ["pr", "create", "--draft", "--title", "fix(codex): Example [proof]", "--body-file",
             "body.md", "--repo", "aer-works/baton", "--head", "2190-verified-pr-ownership"],
            File.ReadAllLines(fixture.DirectGhArguments));
        Assert.True(after.Success, after.Text);
        Assert.DoesNotContain(OwnPullRequestOnlyRule.Rule, after.Text, StringComparison.Ordinal);
        // And the sibling stays refused with the room's own number now named in the refusal, so the
        // create opened the gate for ONE number rather than for every number.
        var sibling = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = $"{gh} pr view 1994" });
        Assert.False(sibling.Success);
        Assert.Contains("This room opened aer-works/baton#2005", sibling.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_workspace_local_gh_create_is_refused_without_spawning_it()
    {
        using var fixture = new PolicyFixture(WorkerRoleCatalog.For("implement").Grant, ["changes.md"]);
        var marker = Path.Combine(fixture.Workspace, "spawned.txt");
        var gh = ShimGh(fixture.Workspace, "https://github.com/aer-works/baton/pull/2005");

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = $"{gh} pr create --fill > {marker}" });

        Assert.False(result.Success);
        Assert.Contains("Direct `gh pr create`", result.Text, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
    }

    [Theory]
    [InlineData("gh pr create --title \"$(whoami)\"")]
    [InlineData("gh pr create --fill | tee pr.txt")]
    [InlineData("gh pr create --fill > pr.txt")]
    [InlineData("gh pr create --fill && echo done")]
    [InlineData("git push && gh pr create --fill")]
    public async Task Ambiguous_create_is_refused_before_the_direct_executable_spawns(string commandLine)
    {
        using var fixture = new PolicyFixture(
            WorkerRoleCatalog.For("implement").Grant,
            ["changes.md"],
            directGhOutput: "https://github.com/aer-works/baton/pull/2005");

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = commandLine });

        Assert.False(result.Success);
        Assert.False(File.Exists(fixture.DirectGhArguments));
    }

    [Theory]
    [InlineData("GH_REPO=other/repo gh pr create --fill")]
    [InlineData("env GH_REPO=other/repo gh pr create --fill")]
    [InlineData("sh -c \"gh pr create --fill\"")]
    [InlineData("cmd /c \"gh pr create --fill\"")]
    [InlineData("pwsh -Command \"gh pr create --fill\"")]
    public async Task Environment_prefixed_and_readable_shell_wrapped_create_is_refused_before_spawn(
        string commandLine)
    {
        using var fixture = new PolicyFixture(
            WorkerRoleCatalog.For("implement").Grant,
            ["changes.md"],
            directGhOutput: "https://github.com/aer-works/baton/pull/2005");

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = commandLine });

        Assert.False(result.Success);
        Assert.Contains("standalone bare `gh pr create`", result.Text, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.DirectGhArguments));
    }

    [Theory]
    [InlineData("cmd /c \"{gh} pr create&echo done\"")]
    [InlineData("cmd /c \"{gh} pr create&&echo done\"")]
    [InlineData("cmd /c \"{gh} pr create|echo done\"")]
    [InlineData("cmd /c \"{gh} pr create||echo done\"")]
    [InlineData("sh -c '{gh} pr create;true'")]
    public async Task Attached_control_in_a_readable_shell_wrapped_create_is_refused_before_spawn(
        string commandTemplate)
    {
        var spawnPathReached = false;
        using var fixture = new PolicyFixture(
            WorkerRoleCatalog.For("implement").Grant,
            ["changes.md"],
            commandCaptureStreamFactory: _ =>
            {
                spawnPathReached = true;
                throw new IOException("fixture stopped before process spawn");
            },
            directGhOutput: "https://github.com/aer-works/baton/pull/2005");
        var gh = commandTemplate.StartsWith("sh", StringComparison.Ordinal) ? "./gh" : ".\\gh";
        var commandLine = commandTemplate.Replace("{gh}", gh, StringComparison.Ordinal);

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = commandLine });

        Assert.False(result.Success);
        Assert.Contains(GrantRefusal.Marker, result.Text, StringComparison.Ordinal);
        Assert.Contains("gh pr create", result.Text, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.DirectGhArguments));
        Assert.False(spawnPathReached);
    }

    [Theory]
    [InlineData("sh -c \"g'h' pr create&&true\"", true)]
    [InlineData("bash -c \"g\\h p\\r cre\\ate;true\"", true)]
    [InlineData("zsh -c \"echo safe|g'h' pr create\"", true)]
    [InlineData("cmd /c \"g^h p^r cre^ate&echo done\"", true)]
    [InlineData("cmd /c \"g\"\"h pr create||echo done\"", true)]
    [InlineData("pwsh -Command \"g`h pr create; Write-Output done\"", true)]
    [InlineData("powershell -Command \"& 'gh' pr create\"", true)]
    [InlineData("sh -c \"printf '%s\\n' 'gh pr create&echo done'\"", false)]
    [InlineData("sh -c \"echo gh pr create\"", false)]
    [InlineData("sh -c \"echo g'h' pr create\\&echo done\"", false)]
    [InlineData("cmd /c \"echo gh pr create^&echo done\"", false)]
    [InlineData("pwsh -Command \"Write-Output 'gh pr create&echo done'\"", false)]
    [InlineData("env MESSAGE='gh pr create&echo done' echo ordinary", false)]
    [InlineData("sh -c \"echo $CLI\"", true)]
    [InlineData("sh -c \"if true; then gh pr create; fi\"", true)]
    [InlineData("env -S 'gh pr create'", true)]
    // Newline is pre-existing coverage, not regression evidence for this repair.
    [InlineData("sh -c \"gh pr create\ntrue\"", true)]
    public async Task Wrapper_lexical_polarity_is_enforced_before_any_process_can_spawn(
        string commandLine, bool refused)
    {
        var spawnPathReached = false;
        using var fixture = new PolicyFixture(
            new PermissionGrant(RunShellCommands: true), ["changes.md"],
            commandCaptureStreamFactory: _ =>
            {
                // The first durable sink is opened before Process.Start. Stop even a broken
                // classifier here: these adversarial commands must never reach a real CLI.
                spawnPathReached = true;
                throw new IOException("fixture stopped before process spawn");
            },
            directGhOutput: "https://github.com/aer-works/baton/pull/2005");

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = commandLine });

        Assert.Equal(!refused, spawnPathReached);
        Assert.Equal(refused, result.Text.Contains(GrantRefusal.Marker, StringComparison.Ordinal));
        Assert.False(File.Exists(fixture.DirectGhArguments));
        if (!refused)
            Assert.Contains("fixture stopped before process spawn", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("@cmd /c \"gh pr create --fill\"", true, false)]
    [InlineData("c^md /c \"gh pr create --fill\"", true, false)]
    [InlineData("@cmd /c \"echo ordinary\"", false, false)]
    [InlineData("c^md /c \"echo ordinary\"", false, false)]
    [InlineData("git log --grep=don't", false, false)]
    [InlineData("s\\h -c \"gh pr create --fill\"", false, true)]
    [InlineData("s\\h -c \"echo ordinary\"", false, false)]
    [InlineData("'cmd' /c \"gh pr create --fill\"", false, true)]
    [InlineData("echo ordinary;cmd /c \"gh pr create --fill\"", false, true)]
    [InlineData("echo ordinary^&cmd /c \"gh pr create --fill\"", false, true)]
    [InlineData("echo ordinary\\&cmd /c \"gh pr create --fill\"", true, false)]
    [InlineData("echo %PATH% $HOME *.cs {literal}", false, false)]
    [InlineData("echo ordinary > out.txt 2>&1", false, false)]
    [InlineData("if ordinary", false, false)]
    public async Task Native_outer_shell_polarity_is_enforced_before_spawn(
        string commandLine, bool windowsRefused, bool posixRefused)
    {
        var refused = OperatingSystem.IsWindows() ? windowsRefused : posixRefused;
        var spawnPathReached = false;
        using var fixture = new PolicyFixture(
            WorkerRoleCatalog.For("implement").Grant, ["changes.md"],
            commandCaptureStreamFactory: _ =>
            {
                spawnPathReached = true;
                throw new IOException("fixture stopped before process spawn");
            },
            directGhOutput: "https://github.com/aer-works/baton/pull/2005");

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = commandLine });

        Assert.Equal(!refused, spawnPathReached);
        Assert.Equal(refused, result.Text.Contains(GrantRefusal.Marker, StringComparison.Ordinal));
        Assert.False(File.Exists(fixture.DirectGhArguments));
        if (!refused)
            Assert.Contains("fixture stopped before process spawn", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_literal_create_mention_in_a_native_wrapper_reaches_real_output()
    {
        using var fixture = new PolicyFixture(WorkerRoleCatalog.For("implement").Grant, ["changes.md"]);
        var command = OperatingSystem.IsWindows()
            ? "cmd /c echo \"gh pr create&echo done\""
            : "sh -c \"printf '%s\\n' 'gh pr create&echo done'\"";

        var result = await fixture.ExecuteAsync(CodexDynamicToolPolicy.RunCommandTool, new { command });

        Assert.True(result.Success, result.Text);
        Assert.Contains("gh pr create&echo done", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_normal_non_create_shell_workflow_still_executes()
    {
        using var fixture = new PolicyFixture(WorkerRoleCatalog.For("implement").Grant, ["changes.md"]);

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = "echo ordinary-workflow" });

        Assert.True(result.Success, result.Text);
        Assert.Contains("ordinary-workflow", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_create_command_with_attached_control_still_executes()
    {
        using var fixture = new PolicyFixture(WorkerRoleCatalog.For("implement").Grant, ["changes.md"]);

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool,
            new { command = "echo ordinary-workflow&&echo done" });

        Assert.True(result.Success, result.Text);
        Assert.Contains("ordinary-workflow", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://github.com/other/repo/pull/2005", 0)]
    [InlineData("https://github.com/aer-works/baton/pull/2005", 7)]
    public async Task Foreign_or_nonzero_direct_create_does_not_mint_ownership(
        string output, int exitCode)
    {
        using var fixture = new PolicyFixture(
            WorkerRoleCatalog.For("implement").Grant,
            ["changes.md"],
            directGhOutput: output,
            directGhExitCode: exitCode);
        var gh = ShimGh(fixture.Workspace, "view fixture");

        _ = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = "gh pr create --fill" });
        var read = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = $"{gh} pr view 2005" });

        Assert.False(read.Success);
        Assert.Contains("has not opened a pull request yet", read.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task General_shell_output_never_mints_ownership()
    {
        using var fixture = new PolicyFixture(WorkerRoleCatalog.For("implement").Grant, ["changes.md"]);
        var gh = ShimGh(fixture.Workspace, "view fixture");
        var outputCommand = OperatingSystem.IsWindows()
            ? "echo https://github.com/aer-works/baton/pull/2005"
            : "printf '%s\\n' https://github.com/aer-works/baton/pull/2005";

        var output = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = outputCommand });
        var read = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.RunCommandTool, new { command = $"{gh} pr view 2005" });

        Assert.True(output.Success, output.Text);
        Assert.False(read.Success);
        Assert.Contains("has not opened a pull request yet", read.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes a fake <c>gh</c> into <paramref name="directory"/> that prints <paramref name="url"/>
    /// and exits zero, and returns the RELATIVE spelling to invoke it by. Relative on purpose: a bare
    /// <c>gh</c> would resolve to whatever real CLI is on this machine's PATH.
    /// </summary>
    private static string ShimGh(string directory, string url, int surroundingCharacters = 0)
    {
        var before = new string('x', surroundingCharacters);
        var after = new string('y', surroundingCharacters);
        if (OperatingSystem.IsWindows())
        {
            // `.\gh` with no extension: cmd applies PATHEXT to a relative path, so this reaches gh.cmd
            // and never a `gh.exe` elsewhere. The rule reads the file name off the path either way.
            File.WriteAllText(
                Path.Combine(directory, "gh.cmd"),
                $"@echo off\r\n@echo {before}\r\n@echo {url}\r\n@echo {after}\r\n");
            return ".\\gh";
        }

        var script = Path.Combine(directory, "gh");
        File.WriteAllText(script, $"#!/bin/sh\nprintf '%s\\n' '{before}' '{url}' '{after}'\n");
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return "./gh";
    }

    [Fact]
    public async Task Reparse_point_escape_is_denied_when_the_platform_can_create_one()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);
        var outside = Path.Combine(fixture.Root, "outside");
        var link = Path.Combine(fixture.Workspace, "linked");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path = Path.Combine(link, "secret.txt") });

        Assert.False(result.Success);
        Assert.Contains("reparse", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Existing_symlink_destination_cannot_redirect_a_granted_write_outside_its_root()
    {
        using var fixture = new PolicyFixture(
            new PermissionGrant(ReadFiles: true, WriteFiles: true), ["report.md"]);
        var outside = Path.Combine(fixture.Root, "outside.txt");
        var link = Path.Combine(fixture.Workspace, "linked.txt");
        File.WriteAllText(outside, "original");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.WriteTextTool, new { path = link, content = "escape" });

        Assert.False(result.Success);
        Assert.Contains("reparse", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("original", File.ReadAllText(outside));
    }

    [Fact]
    public async Task Search_bounds_huge_line_snippets_and_points_to_a_retrievable_file_range()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);
        var path = Path.Combine(fixture.Workspace, "huge.txt");
        File.WriteAllText(path, new string('a', 20_000) + "NEEDLE" + new string('b', 20_000));

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.SearchTextTool, new { path, query = "NEEDLE" });

        Assert.True(result.Success, result.Text);
        Assert.True(result.Text.Length <= 12_000, $"search returned {result.Text.Length} characters");
        Assert.Contains("huge.txt:1:", result.Text, StringComparison.Ordinal);
        Assert.Contains("snippet truncated", result.Text, StringComparison.Ordinal);
        Assert.Contains("read range: offset=", result.Text, StringComparison.Ordinal);
        Assert.Contains("NEEDLE", result.Text, StringComparison.Ordinal);

        var omittedPrefix = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path, offset = 19_500, length = 1_000 });
        Assert.True(omittedPrefix.Success, omittedPrefix.Text);
        Assert.Contains("NEEDLE", omittedPrefix.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Truncated_search_snippets_preserve_non_bmp_scalars_when_serialized()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);
        var path = Path.Combine(fixture.Workspace, "unicode-search.txt");
        File.WriteAllText(path, "😀" + new string('a', 124) + "NEEDLE" + new string('b', 500));

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.SearchTextTool, new { path, query = "NEEDLE" });

        Assert.True(result.Success, result.Text);
        Assert.Contains("NEEDLE", result.Text, StringComparison.Ordinal);
        var serialized = JsonSerializer.Serialize(result.Text);
        Assert.Equal(result.Text, JsonSerializer.Deserialize<string>(serialized));
    }

    [Fact]
    public async Task Search_caps_the_aggregate_response_when_many_lines_match()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);
        var path = Path.Combine(fixture.Workspace, "many.txt");
        File.WriteAllLines(path, Enumerable.Range(0, 500)
            .Select(index => $"NEEDLE {index:D3} {new string('x', 500)}"));

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.SearchTextTool, new { path, query = "NEEDLE" });

        Assert.True(result.Success, result.Text);
        Assert.True(result.Text.Length <= 12_000, $"search returned {result.Text.Length} characters");
        Assert.Contains("[incomplete:", result.Text, StringComparison.Ordinal);
        Assert.Contains("response capped at 12000 characters", result.Text, StringComparison.Ordinal);
        var nextStart = NextSearchStart(result.Text);

        var continuation = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.SearchTextTool, new { path, query = "NEEDLE", start = nextStart });

        Assert.True(continuation.Success, continuation.Text);
        Assert.Contains($"NEEDLE {nextStart:D3}", continuation.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("NEEDLE 000", continuation.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_continues_past_the_match_count_limit_in_one_specific_file()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);
        var path = Path.Combine(fixture.Workspace, "a");
        File.WriteAllLines(path, Enumerable.Repeat("x", 600));

        var first = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.SearchTextTool, new { path, query = "x" });
        var continuation = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.SearchTextTool,
            new { path, query = "x", start = NextSearchStart(first.Text) });

        Assert.True(first.Success, first.Text);
        Assert.True(continuation.Success, continuation.Text);
        Assert.Contains("match limit of 500 reached", first.Text, StringComparison.Ordinal);
        Assert.Contains("next search: start=500", first.Text, StringComparison.Ordinal);
        Assert.StartsWith("a:501:x", continuation.Text, StringComparison.Ordinal);
        Assert.Contains("a:600:x", continuation.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("a:1:x", continuation.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recursive_discovery_skips_build_directories_and_binary_files_but_direct_reads_remain_available()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);
        var bin = Path.Combine(fixture.Workspace, "bin");
        var obj = Path.Combine(fixture.Workspace, "obj");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(obj);
        File.WriteAllText(Path.Combine(bin, "generated.txt"), "NEEDLE generated bin");
        File.WriteAllText(Path.Combine(obj, "generated.txt"), "NEEDLE generated obj");
        var binary = Path.Combine(fixture.Workspace, "binary.dat");
        File.WriteAllBytes(binary, [0xff, 0xfe, 0x00, 0x4e, 0x45, 0x45, 0x44, 0x4c, 0x45]);
        File.WriteAllText(Path.Combine(fixture.Workspace, "source.cs"), "// NEEDLE source");

        var listing = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ListFilesTool, new { path = fixture.Workspace });
        var search = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.SearchTextTool, new { path = fixture.Workspace, query = "NEEDLE" });
        var generatedRead = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path = Path.Combine(bin, "generated.txt") });
        var binaryRead = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path = binary });

        Assert.True(listing.Success, listing.Text);
        Assert.Contains("source.cs", listing.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("bin/", listing.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("obj/", listing.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("binary.dat", listing.Text, StringComparison.Ordinal);
        Assert.True(search.Success, search.Text);
        Assert.Contains("source.cs:1:", search.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("generated", search.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("binary.dat", search.Text, StringComparison.Ordinal);
        Assert.True(generatedRead.Success, generatedRead.Text);
        Assert.Contains("NEEDLE generated bin", generatedRead.Text, StringComparison.Ordinal);
        Assert.True(binaryRead.Success, binaryRead.Text);
    }

    [Fact]
    public async Task Read_ranges_are_recoverable_range_aware_repeats_with_clear_validation_and_denials()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);
        var path = Path.Combine(fixture.Workspace, "long.txt");
        File.WriteAllText(path, new string('a', 12_000) + new string('b', 12_000) + new string('c', 1_000));

        var first = await fixture.ExecuteAsync(CodexDynamicToolPolicy.ReadTextTool, new { path });
        Assert.True(first.Success, first.Text);
        Assert.True(first.Text.Length <= 12_000, $"read returned {first.Text.Length} characters");
        Assert.Contains("[incomplete:", first.Text, StringComparison.Ordinal);
        Assert.Contains("next range: offset=", first.Text, StringComparison.Ordinal);

        var secondRange = new { path, offset = 12_000, length = 12_000 };
        var second = await fixture.ExecuteAsync(CodexDynamicToolPolicy.ReadTextTool, secondRange);
        var replay = await fixture.ExecuteAsync(CodexDynamicToolPolicy.ReadTextTool, secondRange);
        var refused = await fixture.ExecuteAsync(CodexDynamicToolPolicy.ReadTextTool, secondRange);

        Assert.True(second.Success, second.Text);
        Assert.DoesNotContain("replayed:", second.Text, StringComparison.Ordinal);
        Assert.Contains('b', second.Text);
        Assert.Contains("replayed: identical read", replay.Text, StringComparison.Ordinal);
        Assert.False(refused.Success);
        Assert.Contains("previous read is still the answer", refused.Text, StringComparison.Ordinal);

        foreach (var invalid in new object[]
                 {
                     new { path, offset = -1, length = 10 },
                     new { path, offset = 0, length = 0 },
                     new { path, offset = 0, length = 12_001 },
                     new { path, offset = 25_001, length = 1 },
                 })
        {
            var failed = await fixture.ExecuteAsync(CodexDynamicToolPolicy.ReadTextTool, invalid);
            Assert.False(failed.Success);
            Assert.Contains("range", failed.Text, StringComparison.OrdinalIgnoreCase);
        }

        var denied = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool,
            new
            {
                path = Path.Combine(Path.GetDirectoryName(fixture.Workspace)!, "outside.txt"),
                offset = 1,
                length = 10,
            });
        Assert.False(denied.Success);
        Assert.Contains(GrantRefusal.Marker, denied.Text);
    }

    [Fact]
    public async Task Read_ranges_preserve_non_bmp_scalars_and_serialize_without_replacement()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);
        var path = Path.Combine(fixture.Workspace, "unicode.txt");
        File.WriteAllText(path, "😀X");

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path, offset = 0, length = 1 });

        Assert.True(result.Success, result.Text);
        Assert.StartsWith("😀", result.Text, StringComparison.Ordinal);
        Assert.Contains("returned file characters 0..1 of 3", result.Text, StringComparison.Ordinal);
        Assert.Contains("next range: offset=2, length=1", result.Text, StringComparison.Ordinal);
        var serialized = JsonSerializer.Serialize(result.Text);
        Assert.Equal(result.Text, JsonSerializer.Deserialize<string>(serialized));

        var equivalentRange = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path, offset = 0, length = 2 });
        Assert.Contains("replayed: identical read", equivalentRange.Text, StringComparison.Ordinal);

        var splitStart = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path, offset = 1, length = 1 });
        Assert.False(splitStart.Success);
        Assert.Contains("Unicode scalar boundary", splitStart.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_repeat_identity_uses_the_equivalent_post_budget_window()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);
        var path = Path.Combine(fixture.Workspace, "budgeted.txt");
        File.WriteAllText(path, new string('a', 25_000));

        var fresh = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path, offset = 0, length = 12_000 });
        var replay = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path, offset = 0, length = 11_999 });
        var refused = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ReadTextTool, new { path, offset = 0, length = 12_000 });

        Assert.True(fresh.Success, fresh.Text);
        Assert.DoesNotContain("replayed:", fresh.Text, StringComparison.Ordinal);
        Assert.True(fresh.Text.Length <= 12_000, $"fresh read returned {fresh.Text.Length} characters");
        Assert.True(replay.Success, replay.Text);
        Assert.StartsWith("[replayed: identical read", replay.Text, StringComparison.Ordinal);
        Assert.True(replay.Text.Length <= 12_000, $"replay returned {replay.Text.Length} characters");
        Assert.Equal(fresh.Text, replay.Text[(replay.Text.IndexOf('\n', StringComparison.Ordinal) + 1)..]);
        Assert.False(refused.Success);
        Assert.Contains("previous read is still the answer", refused.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recursive_listing_excludes_git_object_database_content()
    {
        using var fixture = new PolicyFixture(new PermissionGrant(ReadFiles: true), ["report.md"]);
        var git = Path.Combine(fixture.Workspace, ".git", "objects");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "large-object"), "irrelevant");

        var result = await fixture.ExecuteAsync(
            CodexDynamicToolPolicy.ListFilesTool, new { path = fixture.Workspace });

        Assert.True(result.Success);
        Assert.Contains("workspace.txt", result.Text);
        Assert.DoesNotContain("large-object", result.Text);
    }

    private static (
        string CommandLine,
        string CounterPath,
        string Stdout,
        string Stderr) LongFailureCommand(string directory)
    {
        static string Line(string channel, int index, char fill)
        {
            var evidence = index switch
            {
                0 => $"{channel}-START",
                20 => $"{channel}-MIDDLE",
                40 => $"{channel}-END",
                _ => $"{channel}-{index:D2}",
            };
            return evidence + " " + new string(fill, 700);
        }

        var stdoutLines = Enumerable.Range(0, 41).Select(index => Line("STDOUT", index, 'o')).ToArray();
        var stderrLines = Enumerable.Range(0, 41).Select(index => Line("STDERR", index, 'e')).ToArray();
        var counterPath = Path.Combine(directory, "command-runs.txt");
        string commandLine;
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(directory, "long-failure.cmd");
            var lines = new List<string> { "@echo off", "@echo ran>>command-runs.txt" };
            lines.AddRange(stdoutLines.Select(line => "@echo " + line));
            lines.AddRange(stderrLines.Select(line => "@echo " + line + ">&2"));
            lines.Add("@exit /b 7");
            File.WriteAllLines(script, lines);
            commandLine = "long-failure.cmd";
        }
        else
        {
            var script = Path.Combine(directory, "long-failure");
            var lines = new List<string> { "#!/bin/sh", "printf 'ran\\n' >> command-runs.txt" };
            lines.AddRange(stdoutLines.Select(line => $"printf '%s\\n' '{line}'"));
            lines.AddRange(stderrLines.Select(line => $"printf '%s\\n' '{line}' >&2"));
            lines.Add("exit 7");
            File.WriteAllLines(script, lines);
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            commandLine = "./long-failure";
        }

        return (
            commandLine,
            counterPath,
            string.Join(Environment.NewLine, stdoutLines) + Environment.NewLine,
            string.Join(Environment.NewLine, stderrLines) + Environment.NewLine);
    }

    private static string OverflowCommand(string directory)
    {
        var payload = new string('z', 1_000);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(
                Path.Combine(directory, "overflow.cmd"),
                $"@echo off\r\n@set \"payload={payload}\"\r\n"
                + "@for /L %%i in (1,1,1100) do @echo %payload%\r\n");
            return "overflow.cmd";
        }

        var script = Path.Combine(directory, "overflow");
        File.WriteAllText(
            script,
            "#!/bin/sh\ni=0\nwhile [ \"$i\" -lt 1100 ]; do\n"
            + $"  printf '%s\\n' '{payload}'\n"
            + "  i=$((i + 1))\ndone\n");
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return "./overflow";
    }

    private static (string CommandLine, string CounterPath, string CompletionPath) HangingSideEffectCommand(
        string directory, string name)
    {
        var counterPath = Path.Combine(directory, $"{name}-runs.txt");
        var completionPath = Path.Combine(directory, $"{name}-completed.txt");
        var capturePayload = new string('x', 5_000);
        if (OperatingSystem.IsWindows())
        {
            var scriptName = $"{name}.cmd";
            File.WriteAllText(
                Path.Combine(directory, scriptName),
                $"@echo off\r\n@echo BEFORE-HANG\r\n@echo ran>>{name}-runs.txt\r\n"
                + $"@echo {capturePayload}\r\n"
                + $"@ping -n 30 127.0.0.1 >nul\r\n@echo completed>{name}-completed.txt\r\n");
            return (scriptName, counterPath, completionPath);
        }

        var scriptPath = Path.Combine(directory, name);
        File.WriteAllText(
            scriptPath,
            $"#!/bin/sh\nprintf 'BEFORE-HANG\\n'\nprintf 'ran\\n' >> {name}-runs.txt\n"
            + $"printf '%s\\n' '{capturePayload}'\nsleep 30\nprintf 'completed\\n' > {name}-completed.txt\n");
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return ($"./{name}", counterPath, completionPath);
    }

    private static (
        string CommandLine,
        string CounterPath,
        string MutatedPath,
        string ReadyPath) HangingExternalDiffCommand(
        string directory, string name)
    {
        var terminalCommand = OperatingSystem.IsWindows()
            ? "exec ping -n 30 127.0.0.1 >/dev/null"
            : "exec sleep 30";
        return ExternalDiffCommand(directory, name, terminalCommand);
    }

    private static (
        string CommandLine,
        string CounterPath,
        string MutatedPath,
        string ReadyPath) CompletedExternalDiffCommand(
        string directory, string name, int exitCode) =>
        ExternalDiffCommand(directory, name, $"exit {exitCode}");

    private static (
        string CommandLine,
        string CounterPath,
        string MutatedPath,
        string ReadyPath) ExternalDiffCommand(
        string directory, string name, string terminalCommand)
    {
        var counterPath = Path.Combine(directory, $"{name}-runs.txt");
        var mutatedName = $"{name}-mutated.txt";
        var mutatedPath = Path.Combine(directory, mutatedName);
        var readyName = $"{name}-ready.txt";
        var readyPath = Path.Combine(directory, readyName);
        File.WriteAllText(mutatedPath, "before");
        var helperName = $"{name}-diff.sh";
        var helperPath = Path.Combine(directory, helperName);
        File.WriteAllText(
            helperPath,
            $"#!/bin/sh\nprintf 'ran\\n' >> {name}-runs.txt\nprintf 'AFTER!' > {mutatedName}\n"
            + $"printf 'ready\\n' > {readyName}.tmp\nmv {readyName}.tmp {readyName}\n"
            + $"printf 'HELPER-RAN\\n'\n{terminalCommand}\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                helperPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        var helperCommand = OperatingSystem.IsWindows()
            ? $"sh ./{helperName}"
            : $"./{helperName}";

        // No commit is needed: the index supplies the old side and the worktree supplies the new.
        // An empty template and hooks path keep this synthetic repository isolated from operator git
        // setup; PolicyFixture removes the repository and helper during teardown.
        RunGitSetup(directory, "init", "--quiet", "--template=");
        RunGitSetup(directory, "config", "core.hooksPath", "");
        RunGitSetup(directory, "config", "diff.counter.command", helperCommand);
        File.WriteAllText(Path.Combine(directory, ".gitattributes"), "tracked.txt diff=counter\n");
        var trackedPath = Path.Combine(directory, "tracked.txt");
        File.WriteAllText(trackedPath, "before\n");
        RunGitSetup(directory, "add", ".gitattributes", "tracked.txt");
        File.WriteAllText(trackedPath, "after changed\n");

        return ("git diff --ext-diff", counterPath, mutatedPath, readyPath);
    }

    private static void WaitForCommandAttempt(
        string counterPath,
        int expectedAttempts,
        CancellationToken cancellationToken,
        string commandDescription)
    {
        var entered = SpinWait.SpinUntil(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ExternalDiffAttemptCount(counterPath) >= expectedAttempts;
            },
            TimeSpan.FromSeconds(10));
        Assert.True(
            entered,
            $"timeout setup: {commandDescription} attempt {expectedAttempts} did not enter before the timer gate");
    }

    private static void AssertExternalDiffAttempts(string counterPath, int expected, string phase)
    {
        var actual = ExternalDiffAttemptCount(counterPath);
        Assert.True(
            actual == expected,
            $"{phase}: expected {expected} external-diff helper attempt(s), actual {actual}");
    }

    private static int ExternalDiffAttemptCount(string counterPath)
    {
        if (!File.Exists(counterPath))
        {
            return 0;
        }

        // cmd.exe can still hold its append handle when the rendezvous first sees this file. Share
        // with that writer, and count only newline-terminated markers so a partial append is not
        // mistaken for readiness.
        using var stream = new FileStream(
            counterPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Count(character => character == '\n');
    }

    private static Stream WritableCaptureStream(string path) =>
        new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static void RunGitSetup(string directory, params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new IOException("Could not start git while preparing the synthetic diff fixture.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new IOException(
                $"Git fixture setup failed with exit {process.ExitCode}: {stdout}{stderr}");
        }
    }

    private static string CommandOutputReference(string text)
    {
        const string marker = "Command output reference: ";
        var start = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = text.IndexOfAny(['\r', '\n'], start);
        return text[start..end];
    }

    private static IReadOnlyList<string> ToolNames(CodexDynamicToolPolicy policy) =>
        policy.BuildToolDefinitions().Select(node => node!["name"]!.GetValue<string>()).ToArray();

    private static int NextSearchStart(string text)
    {
        const string marker = "next search: start=";
        var start = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = text.IndexOfAny([';', '.', ']'], start);
        return int.Parse(text[start..end], System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class WriteFailingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("synthetic capture write failure");

        public override Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("synthetic capture write failure"));

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("synthetic capture write failure"));
    }

    private sealed class DisposalTrackingStream(Stream inner) : Stream
    {
        public bool IsDisposed { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);

        public override Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed)
            {
                IsDisposed = true;
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!IsDisposed)
            {
                IsDisposed = true;
                await inner.DisposeAsync();
            }
            GC.SuppressFinalize(this);
        }
    }

    private sealed class StepClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class PolicyFixture : IDisposable
    {
        public PolicyFixture(
            PermissionGrant grant,
            IReadOnlyList<string> outputs,
            bool createInput = false,
            Func<ShellCommandClass, TimeSpan>? commandCeiling = null,
            Func<string, Stream>? commandCaptureStreamFactory = null,
            TimeProvider? timeProvider = null,
            Action<PolicyFixture, CancellationToken>? beforeCommandTimeoutStarts = null,
            string? directGhOutput = null,
            int directGhExitCode = 0)
        {
            Root = Path.Combine(Path.GetTempPath(), $"baton-codex-policy-{Guid.NewGuid():N}");
            Workspace = Path.Combine(Root, "workspace");
            Output = Path.Combine(Root, "output");
            Input = Path.Combine(Root, "input.txt");
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(Output);
            File.WriteAllText(Path.Combine(Workspace, "workspace.txt"), "workspace");
            if (createInput)
            {
                File.WriteAllText(Input, "input");
            }
            GhPullRequestCreateProvenance? provenance = null;
            IReadOnlyList<string>? directPrefix = null;
            if (directGhOutput is not null)
            {
                DirectGhArguments = Path.Combine(Root, "fake-gh-argv.txt");
                if (OperatingSystem.IsWindows())
                {
                    var script = Path.Combine(Root, "fake-gh.ps1");
                    var escapedArguments = DirectGhArguments.Replace("'", "''", StringComparison.Ordinal);
                    var escapedOutput = directGhOutput.Replace("'", "''", StringComparison.Ordinal);
                    File.WriteAllText(script,
                        $"$args | Set-Content -LiteralPath '{escapedArguments}' -Encoding utf8\r\n"
                        + $"Write-Output '{escapedOutput}'\r\nexit {directGhExitCode}\r\n");
                    var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    var interpreter = Path.Combine(
                        systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
                    provenance = new GhPullRequestCreateProvenance(
                        interpreter, "aer-works/baton", "2190-verified-pr-ownership");
                    directPrefix = ["-NoProfile", "-NonInteractive", "-File", script];
                }
                else
                {
                    var script = Path.Combine(Root, "fake-gh.sh");
                    File.WriteAllText(script,
                        $"printf '%s\\n' \"$@\" > '{DirectGhArguments}'\n"
                        + $"printf '%s\\n' '{directGhOutput}'\nexit {directGhExitCode}\n");
                    provenance = new GhPullRequestCreateProvenance(
                        "/bin/sh", "aer-works/baton", "2190-verified-pr-ownership");
                    directPrefix = [script];
                }
            }

            Policy = commandCaptureStreamFactory is null && beforeCommandTimeoutStarts is null
                && provenance is null
                ? new CodexDynamicToolPolicy(
                    grant, Workspace, Output, createInput ? [Input] : [], outputs, commandCeiling,
                    timeProvider)
                : new CodexDynamicToolPolicy(
                    grant,
                    Workspace,
                    Output,
                    createInput ? [Input] : [],
                    outputs,
                    commandCeiling,
                    timeProvider,
                    commandCaptureStreamFactory,
                    beforeCommandTimeoutStarts is null
                        ? null
                        : cancellationToken => beforeCommandTimeoutStarts(this, cancellationToken),
                    provenance,
                    directPrefix);
        }

        public string Root { get; }
        public string Workspace { get; }
        public string Output { get; }
        public string Input { get; }
        public string DirectGhArguments { get; } = string.Empty;
        public CodexDynamicToolPolicy Policy { get; }

        public Task<CodexDynamicToolResult> ApplyPatchAsync(string patch) =>
            ExecuteAsync(CodexDynamicToolPolicy.ApplyPatchTool, new { input = patch });

        public Task<CodexDynamicToolResult> ExecuteAsync(string toolName, object arguments) =>
            ExecuteWithCancellationAsync(toolName, arguments, TestContext.Current.CancellationToken);

        public async Task<CodexDynamicToolResult> ExecuteWithCancellationAsync(
            string toolName, object arguments, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
            return await Policy.ExecuteAsync(toolName, doc.RootElement, cancellationToken);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                // Git object files are read-only on Windows. The external-diff regression creates
                // them with `git add`, so normalize fixture-owned files before recursive cleanup.
                var gitObjects = Path.Combine(Workspace, ".git", "objects");
                if (Directory.Exists(gitObjects))
                {
                    foreach (var path in Directory.EnumerateFiles(
                                 gitObjects, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(path, FileAttributes.Normal);
                    }
                }
                DirectoryCleanup.DeleteRecursively(Root);
            }
        }
    }
}
