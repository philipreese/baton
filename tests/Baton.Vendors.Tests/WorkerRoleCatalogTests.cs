using System.Linq;
using System.Text.Json;
using Baton.Vendors;
using Baton.Domain;
using Baton.Status;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// #888: the shared worker-role catalog. Proves a role resolves its vendor/model/effort from its
/// tier (so a role never hardcodes a model), that a tier edit reaches every role on it with no
/// rebuild (the env override stands in for the runtime <c>worker-tiers.json</c> the operator drops),
/// and that a malformed catalog fails loudly rather than dispatching something nobody chose.
/// </summary>
/// <remarks>
/// #1524: every override below is an isolated <see cref="BatonEnvironmentSnapshot.BeginScope"/>, not
/// a process mutation, so this class needs no <c>SerializedEnvironmentCollection</c> enrollment and
/// runs parallel-safe.
/// </remarks>
public class WorkerRoleCatalogTests
{
    private sealed class TempCatalog : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"wrc-{Guid.NewGuid():N}");

        public TempCatalog() => Directory.CreateDirectory(Dir);

        public string Write(string name, string content)
        {
            var path = Path.Combine(Dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            DirectoryCleanup.DeleteRecursively(Dir);
        }
    }

    private const string DefaultOutputs = """[{"name":"out.md","schema":"none","instruction":"Write to out.md."}]""";

    private static string Role(string id, string tier, bool write = false, bool shell = false, bool net = false,
        int timeout = 10, bool verdict = false, string outputs = DefaultOutputs) =>
        $$"""
          {"id":"{{id}}","tier":"{{tier}}","read_files":true,"write_files":{{(write ? "true" : "false")}},
           "run_shell_commands":{{(shell ? "true" : "false")}},"network_access":{{(net ? "true" : "false")}},
           "timeout_minutes":{{timeout}},"verdict_schema":{{(verdict ? "true" : "false")}},"purpose":"p","outputs":{{outputs}}}
          """;

    private static IDisposable PointAt(TempCatalog cat, string tiersJson, string rolesJson) =>
        BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with
        {
            WorkerTiersPathOverride = cat.Write("tiers.json", tiersJson),
            WorkerRolesPathOverride = cat.Write("roles.json", rolesJson),
        });

    // A test that reads the SHIPPED default must be hermetic against the runtime overrides: with no
    // override set, ResolvePath falls through {BATON_HOME|~/.baton}/worker-*.json, so on a machine
    // where an operator has used that documented override the test would silently read their file
    // instead of the shipped one. Point the catalog's OWN snapshot fields straight at the shipped
    // files under AppContext.BaseDirectory (copied there by the csproj's CopyToOutputDirectory).
    // Deliberately NOT via HomeOverride: that field is what BatonPaths.Root reads, so setting it here
    // would race a parallel BatonProfileStore.DefaultPath read the way mutating BATON_HOME once red an
    // unrelated test (#893). The two Worker*PathOverride fields are read only by WorkerRoleCatalog, so
    // nothing else can see them -- and BeginScope's own isolation means nothing else does anyway.
    private static IDisposable ShippedDefault() =>
        BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with
        {
            WorkerTiersPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"),
            WorkerRolesPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerRoles.json"),
        });

    [Fact]
    public void The_shipped_catalog_resolves_each_role_against_its_tier()
    {
        using var env = ShippedDefault();

        var review = WorkerRoleCatalog.For("review");
        Assert.Equal("claude", review.Adapter);
        // #1861: frontier is opus/high -- docs/dispatch.md's tier paragraph has the measurements.
        Assert.Equal("opus", review.Model);
        Assert.Equal("high", review.Effort);
        Assert.False(review.Grant.WriteFiles);
        // #1456 (spec/baton.md §9): review reverses #1355's flat shell refusal on claude specifically
        // -- read-only git/gh, scoped by pattern and enforced by claude's measured --allowedTools/
        // --disallowedTools ceiling, asserted read-only so it does not have to widen WriteFiles/
        // NetworkAccess to satisfy PermissionGrant.CategoriesDefeatedByTheShell. NetworkAccess stays
        // false -- gh's own reach to github.com is not the same thing as the categorical WebFetch/
        // WebSearch grant, and ShellCommandsAreReadOnly is what lets the two coexist.
        Assert.False(review.Grant.NetworkAccess);
        Assert.True(review.Grant.RunShellCommands);
        Assert.True(review.Grant.ShellCommandsAreReadOnly);
        Assert.NotNull(review.Grant.ShellCommandPatterns);
        Assert.Contains("git diff*", review.Grant.ShellCommandPatterns);
        Assert.Contains("git log*", review.Grant.ShellCommandPatterns);
        Assert.Contains("gh pr view*", review.Grant.ShellCommandPatterns);
        Assert.DoesNotContain("gh api*", review.Grant.ShellCommandPatterns);
        Assert.NotNull(review.Grant.DeniedShellCommandPatterns);
        Assert.Contains("git commit*", review.Grant.DeniedShellCommandPatterns);
        Assert.Contains("git push*", review.Grant.DeniedShellCommandPatterns);
        Assert.True(review.ProducesVerdict);

        var factCheck = WorkerRoleCatalog.For("fact-check");
        Assert.Equal("claude", factCheck.Adapter);
        Assert.False(factCheck.Grant.WriteFiles);
        // F4 (#1355 PR #1385 review): the issue names review/fact-check/advise as the read lanes, but
        // only review got a tested guarantee here -- fact-check appeared nowhere under tests/. Mirrors
        // review's own NetworkAccess/RunShellCommands assertions above.
        Assert.False(factCheck.Grant.NetworkAccess);
        Assert.False(factCheck.Grant.RunShellCommands);

        // #1386: advise is the third read lane the issue named, narrowed to write_files: false once
        // #1765 retired dispatch.py's grant_refusal and #901's audited-write widening was confirmed to
        // un-refuse a withheld-write role once a worktree is provisioned.
        var advise = WorkerRoleCatalog.For("advise");
        Assert.False(advise.Grant.WriteFiles);
        Assert.False(advise.Grant.NetworkAccess);
        Assert.False(advise.Grant.RunShellCommands);

        var implement = WorkerRoleCatalog.For("implement");
        // #1863 (operator ruling, 2026-09-06): standard is codex gpt-6-astra/medium, ending #1861's
        // interim claude opus/medium. docs/dispatch.md's tier paragraph carries the measurement; the
        // grant shape below is vendor-independent and unchanged by the tier move.
        Assert.Equal("codex", implement.Adapter);
        Assert.Equal("gpt-6-astra", implement.Model);
        Assert.Equal("medium", implement.Effort);
        Assert.True(implement.Grant.RunShellCommands);
        // #1355: network stays granted here -- a CATEGORICAL RunShellCommands grant without
        // NetworkAccess is refused for every grant-consuming adapter (PermissionGrant.
        // CategoriesDefeatedByTheShell); the pattern-scoped, read-only shell review carries above is
        // the documented exception (#1456), and implement's shell is categorical, so defaulting network
        // off would make every unmodified dispatch of this role throw whichever vendor the tier names.
        // See the role's own purpose field in WorkerRoles.json for the full reasoning.
        Assert.True(implement.Grant.NetworkAccess);
        Assert.False(implement.ProducesVerdict);
        Assert.Equal(TimeSpan.FromMinutes(40), implement.Timeout);
    }

    // #1686 review F12: nothing tested the catalog's MaxToolSteps values before this -- a build that
    // dropped maxToolStepsOverride in RoleDispatch.ToBinding passed the whole suite, and so did the
    // `advise: 20` / spec's "advise unset" contradiction F1 named. Pins the shipped values directly.
    [Fact]
    public void The_shipped_catalog_s_MaxToolSteps_match_the_measured_caps()
    {
        using var env = ShippedDefault();

        Assert.Equal(610, WorkerRoleCatalog.For("implement").MaxToolSteps);
        Assert.Equal(100, WorkerRoleCatalog.For("review").MaxToolSteps);
        // #1686 review F1: advise has no measured floor (spec/baton.md §3 has the reason), so it stays
        // unset rather than a guess -- matching the spec's own claim about it.
        Assert.Null(WorkerRoleCatalog.For("advise").MaxToolSteps);
    }

    // #1691: the catalog's third arrest axis, pinned the same way and for a stronger reason -- here
    // "unset" is the WHOLE finding, and spec/baton.md §3 is where the measurement behind it lives.
    // Deliberately iterates the whole catalog rather than naming roles, so the drift #1686 review F1
    // found -- a role whose caps the records and the JSON disagreed about -- has nowhere to hide.
    [Fact]
    public void The_shipped_catalog_arms_no_billed_rate_trigger_on_any_role()
    {
        using var env = ShippedDefault();

        foreach (var role in WorkerRoleCatalog.All)
        {
            Assert.True(
                role.BilledRateLimit is null,
                $"role '{role.Id}' declares billed_rate_limit {role.BilledRateLimit}, which spec/baton.md §3 says no role does. Re-run tools/room-rate-sweep/sweep.py: if a defensible value now exists, §3's calibration changes with it.");
        }
    }

    [Fact]
    public void One_tier_edit_reaches_every_role_on_that_tier_with_no_rebuild()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"shared":{"adapter":"gemini","model":"a-future-model","effort":null}}""",
            $"[{Role("a", "shared")},{Role("b", "shared", write: true)}]");

        Assert.Equal("a-future-model", WorkerRoleCatalog.For("a").Model);
        Assert.Equal("a-future-model", WorkerRoleCatalog.For("b").Model);
        Assert.False(WorkerRoleCatalog.For("a").Grant.WriteFiles);
        Assert.True(WorkerRoleCatalog.For("b").Grant.WriteFiles);
    }

    [Fact]
    public void A_role_naming_an_undefined_tier_fails_loudly()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"known":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("x", "missing")}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkerRoleCatalog.All);
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void A_duplicate_role_id_fails_loudly()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("dup", "t")},{Role("dup", "t")}]");

        Assert.Throws<InvalidOperationException>(() => _ = WorkerRoleCatalog.All);
    }

    [Fact]
    public void An_unknown_role_id_throws_naming_the_known_ones()
    {
        using var env = ShippedDefault();

        var ex = Assert.Throws<KeyNotFoundException>(() => WorkerRoleCatalog.For("does-not-exist"));
        Assert.Contains("review", ex.Message);
    }

    [Fact]
    public void A_role_missing_a_required_field_fails_loudly()
    {
        using var cat = new TempCatalog();
        // `purpose` omitted. Without [JsonRequired] this would deserialize to a null Purpose and ship a
        // role nobody authored; the catalog's contract is to fail at load, not at dispatch.
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            """[{"id":"x","tier":"t","read_files":true,"write_files":false,"run_shell_commands":false,"network_access":false,"timeout_minutes":10,"verdict_schema":false}]""");

        Assert.Throws<JsonException>(() => _ = WorkerRoleCatalog.All);
    }

    [Fact]
    public void A_catalog_file_with_comments_fails_loudly_so_both_readers_agree()
    {
        using var cat = new TempCatalog();
        // tools/audit-completeness/completeness.py reads WorkerTiers.json through stdlib json.load,
        // which rejects comments (tools/baton-agy-loop/dispatch.py did too, before #1759 retired it).
        // The C# reader must reject them too, or an operator's inline // WHY loads in the engine and
        // breaks every dispatch.
        using var env = PointAt(
            cat,
            "{\n  // #742 operator directive\n  \"t\":{\"adapter\":\"gemini\",\"model\":\"m\",\"effort\":null}\n}",
            $"[{Role("x", "t")}]");

        Assert.Throws<JsonException>(() => _ = WorkerRoleCatalog.All);
    }

    [Fact]
    public void The_shipped_review_role_declares_a_prose_report_and_a_schema_checked_verdict()
    {
        using var env = ShippedDefault();

        var outputs = WorkerRoleCatalog.For("review").Outputs;

        var verdict = outputs.Single(o => o.Name == "verdict.json");
        Assert.Equal(OutputSchema.ReviewVerdict, verdict.Schema);
        Assert.Contains("verdict.json", verdict.Instruction, StringComparison.Ordinal);

        var prose = outputs.Single(o => o.Name == "report.md");
        Assert.Equal(OutputSchema.None, prose.Schema);
    }

    [Fact]
    public void The_shipped_mutation_roles_declare_their_handoff_outputs()
    {
        using var env = ShippedDefault();

        // implement's summary is a floor + handoff, existence-only -- its correctness is a review's job.
        var changes = Assert.Single(WorkerRoleCatalog.For("implement").Outputs);
        Assert.Equal("changes.md", changes.Name);
        Assert.Equal(OutputSchema.None, changes.Schema);

        // janitor declares its report AND branch.diff -- the diff is the ground truth a following review
        // reads (#789). Both named, so dropping either from the catalog fails here (the #741 failure was
        // a wrong filename on this exact role).
        var janitor = WorkerRoleCatalog.For("janitor").Outputs;
        Assert.Contains(janitor, o => o.Name == "janitor.md");
        Assert.Contains(janitor, o => o.Name == "branch.diff");
        Assert.All(janitor, o => Assert.Equal(OutputSchema.None, o.Schema));
    }

    [Fact]
    public void An_output_maps_its_schema_from_the_catalog_string()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("r", "t", outputs: """[{"name":"verdict.json","schema":"review_verdict","instruction":"i"}]""")}]");

        var output = Assert.Single(WorkerRoleCatalog.For("r").Outputs);
        Assert.Equal(OutputSchema.ReviewVerdict, output.Schema);
    }

    [Fact]
    public void An_output_with_an_unknown_schema_fails_loudly()
    {
        using var cat = new TempCatalog();
        // A typo'd schema must throw at load, not default to None and silently drop the verdict check.
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("r", "t", outputs: """[{"name":"x","schema":"verdikt","instruction":"i"}]""")}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkerRoleCatalog.All);
        Assert.Contains("verdikt", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_role_missing_the_outputs_field_fails_loudly()
    {
        using var cat = new TempCatalog();
        // outputs is [JsonRequired] like every other field: an omitted array would deserialize to null
        // and ship a role that declares nothing, dispatching a worker told to write no artifact.
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            """[{"id":"r","tier":"t","read_files":true,"write_files":false,"run_shell_commands":false,"network_access":false,"timeout_minutes":10,"verdict_schema":false,"purpose":"p"}]""");

        Assert.Throws<JsonException>(() => _ = WorkerRoleCatalog.All);
    }

    [Fact]
    public void An_output_missing_a_required_field_fails_loudly()
    {
        using var cat = new TempCatalog();
        // instruction omitted -- without [JsonRequired] it would bind to null and dispatch a worker
        // never told to produce the file the contract then fails it for not producing.
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("r", "t", outputs: """[{"name":"x","schema":"none"}]""")}]");

        Assert.Throws<JsonException>(() => _ = WorkerRoleCatalog.All);
    }

    [Fact]
    public void A_role_declaring_an_empty_outputs_list_fails_loudly()
    {
        using var cat = new TempCatalog();
        // Present but empty: [JsonRequired] is satisfied, so only an explicit count guard catches this.
        // A role that declares nothing has no floor -- a silent no-op worker would pass.
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("r", "t", outputs: "[]")}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkerRoleCatalog.All);
        Assert.Contains("r", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_role_declaring_a_null_outputs_value_fails_loudly_by_name()
    {
        using var cat = new TempCatalog();
        // outputs present but null passes [JsonRequired]; without the guard it throws an unnamed
        // ArgumentNullException out of Select, unlike every other failure here which names the role.
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("r", "t", outputs: "null")}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkerRoleCatalog.All);
        Assert.Contains("r", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_output_named_with_a_leading_dot_fails_loudly_at_load()
    {
        using var cat = new TempCatalog();
        // '.'-prefixed names are reserved for engine stream logs; ProducedOutput refuses them at
        // dispatch, so the catalog must refuse them at load rather than defer the failure.
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("r", "t", outputs: """[{"name":".notes.md","schema":"none","instruction":"i"}]""")}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkerRoleCatalog.All);
        Assert.Contains(".notes.md", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_patch_role_declares_a_patch_diff_output_with_diff_schema()
    {
        using var env = ShippedDefault();

        var patchRole = WorkerRoleCatalog.For("patch");
        Assert.False(patchRole.Grant.WriteFiles);
        Assert.False(patchRole.ProducesVerdict);

        var output = Assert.Single(patchRole.Outputs);
        Assert.Equal("patch.diff", output.Name);
        Assert.Equal(OutputSchema.Diff, output.Schema);
        Assert.Contains("patch.diff", output.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void An_output_maps_diff_schema_from_the_catalog_string()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("p", "t", outputs: """[{"name":"patch.diff","schema":"diff","instruction":"i"}]""")}]");

        var output = Assert.Single(WorkerRoleCatalog.For("p").Outputs);
        Assert.Equal(OutputSchema.Diff, output.Schema);
    }

    [Fact]
    public void The_dispatch_doc_role_table_matches_the_catalog_exactly()
    {
        // #1091: docs/dispatch.md lists the roles and what each writes. An operator doc that drifts from
        // the catalog is a documentation defect, so pin the table to WorkerRoleCatalog bidirectionally:
        // every role appears with its exact outputs, and the table names no role the catalog does not.
        var docPath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "dispatch.md");
        var doc = File.ReadAllText(docPath);

        // Scope to the "## Roles" section so the flags table above it is not parsed as roles.
        var start = doc.IndexOf("## Roles", StringComparison.Ordinal);
        Assert.True(start >= 0, "dispatch.md has no '## Roles' section");
        var end = doc.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        var section = end >= 0 ? doc[start..end] : doc[start..];

        // A role row is `| `<id>` | <tier> | `out`, `out` | ... |` — id is the first cell's sole
        // backticked token, tier is the second cell's bare word, outputs are the backticked file names.
        var rowRegex = new System.Text.RegularExpressions.Regex(@"^\|\s*`([a-z-]+)`\s*\|\s*([a-z]+)\s*\|.*$",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        var fileRegex = new System.Text.RegularExpressions.Regex(@"`([\w.-]+\.[a-z]+)`");

        var documented = new Dictionary<string, (string Tier, HashSet<string> Outputs)>();
        foreach (System.Text.RegularExpressions.Match row in rowRegex.Matches(section))
        {
            var id = row.Groups[1].Value;
            var tier = row.Groups[2].Value;
            var outs = fileRegex.Matches(row.Value).Select(m => m.Groups[1].Value).ToHashSet();
            documented[id] = (tier, outs);
        }

        var catalog = WorkerRoleCatalog.All.ToDictionary(
            r => r.Id, r => (r.Tier, Outputs: r.Outputs.Select(o => o.Name).ToHashSet()));

        Assert.Equal(catalog.Keys.OrderBy(k => k), documented.Keys.OrderBy(k => k));
        foreach (var (id, expected) in catalog)
        {
            Assert.True(documented[id].Outputs.SetEquals(expected.Outputs),
                $"dispatch.md role '{id}' writes {string.Join(",", documented[id].Outputs)}; catalog says {string.Join(",", expected.Outputs)}");
            Assert.True(string.Equals(documented[id].Tier, expected.Tier, StringComparison.Ordinal),
                $"dispatch.md role '{id}' tier is '{documented[id].Tier}'; catalog says '{expected.Tier}'");
        }
    }

    [Fact]
    public void The_review_verdict_instruction_embeds_a_schema_valid_example_and_names_the_enum_sets()
    {
        // #1092: the instruction named "ReviewVerdict JSON" but showed no shape, so a strong model
        // guessed findings[].claim and the closed severity/status enums wrong and was rejected on
        // repeat (the schema traps are pinned in ReviewVerdictSchemaTests). It must now carry a
        // concrete example the schema accepts -- a wrong example would be worse than none -- and name
        // the status values a single example cannot show.
        var instruction = WorkerRoleCatalog.For("review").Outputs.Single(o => o.Name == "verdict.json").Instruction;

        var open = instruction.IndexOf('{');
        var close = instruction.LastIndexOf('}');
        Assert.True(open >= 0 && close > open, "the instruction embeds no JSON example object");
        var example = instruction.Substring(open, close - open + 1);
        Assert.True(
            ReviewVerdictSchema.TryParse(System.Text.Encoding.UTF8.GetBytes(example), out _, out var error),
            $"the instruction's example must parse as a ReviewVerdict: {error}");

        // status is the subtler closed set (confirmed/refuted/unverified); the example shows only one,
        // so the other two must be named or a model still guesses them.
        Assert.Contains("refuted", instruction);
        Assert.Contains("unverified", instruction);
    }

    // #1745: token_budget accepts either a bare number (Fixed, today's shape) or an object mapping
    // adapter name to number (PerAdapter) -- both parsed by WorkerRoleCatalog, never left to a bare
    // long that could not represent the map shape at all.

    [Fact]
    public void A_role_with_a_single_number_token_budget_parses_as_Fixed()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $$"""[{{Role("r", "t")[..^1]}}, "token_budget": 42}]""");

        var budget = Assert.IsType<TokenBudgetSpec.Fixed>(WorkerRoleCatalog.For("r").TokenBudget);
        Assert.Equal(42, budget.Value);
    }

    [Fact]
    public void A_role_with_a_per_adapter_map_parses_as_PerAdapter()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            "[" + Role("r", "t")[..^1] + ", \"token_budget\": {\"claude\": 10, \"agy\": 20}}]");

        var budget = Assert.IsType<TokenBudgetSpec.PerAdapter>(WorkerRoleCatalog.For("r").TokenBudget);
        Assert.Equal(10, budget.ByAdapter["claude"]);
        Assert.Equal(20, budget.ByAdapter["agy"]);
    }

    [Fact]
    public void A_role_with_no_token_budget_key_parses_as_null()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("r", "t")}]");

        Assert.Null(WorkerRoleCatalog.For("r").TokenBudget);
    }

    [Fact]
    public void A_token_budget_map_naming_an_unknown_adapter_fails_loudly_by_role_and_key()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            "[" + Role("r", "t")[..^1] + ", \"token_budget\": {\"gemini\": 10}}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkerRoleCatalog.All);
        Assert.Contains("r", ex.Message, StringComparison.Ordinal);
        Assert.Contains("gemini", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_token_budget_map_value_that_is_not_a_whole_number_fails_loudly_by_role_and_key()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            "[" + Role("r", "t")[..^1] + ", \"token_budget\": {\"claude\": \"a lot\"}}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkerRoleCatalog.All);
        Assert.Contains("r", ex.Message, StringComparison.Ordinal);
        Assert.Contains("claude", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_token_budget_that_is_neither_a_number_nor_an_object_fails_loudly()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $$"""[{{Role("r", "t")[..^1]}}, "token_budget": "a lot"}]""");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkerRoleCatalog.All);
        Assert.Contains("r", ex.Message, StringComparison.Ordinal);
    }

    // #1788: delivers_branch -- see WorkerRole.DeliversBranch's own remarks for what it gates.

    [Fact]
    public void The_shipped_implement_role_delivers_a_branch_and_no_other_role_does()
    {
        using var env = ShippedDefault();

        Assert.True(WorkerRoleCatalog.For("implement").DeliversBranch);

        foreach (var role in WorkerRoleCatalog.All.Where(r => r.Id != "implement"))
        {
            Assert.False(role.DeliversBranch, $"role '{role.Id}' unexpectedly declares delivers_branch: true.");
        }
    }

    /// <summary>
    /// The lockstep half of #1788's own Build section: a role whose brief ends in a push must actually
    /// be able to write to the tree in the first place -- a read-shaped role (write_files: false)
    /// declaring delivers_branch would be incoherent, and nothing else in the catalog loader catches it.
    /// One-directional on purpose (a write-capable role need not deliver a branch, e.g. janitor -- see
    /// WorkerRole.DeliversBranch's own remarks for why that one stays false).
    /// </summary>
    [Fact]
    public void Every_role_that_delivers_a_branch_can_write_files()
    {
        using var env = ShippedDefault();

        foreach (var role in WorkerRoleCatalog.All.Where(r => r.DeliversBranch))
        {
            Assert.True(role.Grant.WriteFiles, $"role '{role.Id}' declares delivers_branch: true but write_files: false.");
        }
    }

    [Fact]
    public void A_role_with_no_delivers_branch_key_parses_as_false()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("r", "t")}]");

        Assert.False(WorkerRoleCatalog.For("r").DeliversBranch);
    }

    [Fact]
    public void A_role_declaring_delivers_branch_true_parses_as_true()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            "[" + Role("r", "t")[..^1] + ", \"delivers_branch\": true}]");

        Assert.True(WorkerRoleCatalog.For("r").DeliversBranch);
    }

    // #1802: allows_subagents -- see WorkerRole.AllowsSubagents's own remarks for what it gates.

    /// <summary>
    /// Catalog lockstep: pins the four roles' values the #1802 brief names explicitly. advise is the
    /// only shipped role whose whole purpose is weighing options via fan-out; every other role
    /// (including implement and review, the two the duplicate-review waste was measured on) withholds
    /// the vendor's subagent tool by leaving the key omitted (default false).
    /// </summary>
    [Fact]
    public void The_shipped_advise_role_allows_subagents_and_implement_and_review_do_not()
    {
        using var env = ShippedDefault();

        Assert.True(WorkerRoleCatalog.For("advise").AllowsSubagents);
        Assert.False(WorkerRoleCatalog.For("implement").AllowsSubagents);
        Assert.False(WorkerRoleCatalog.For("review").AllowsSubagents);
    }

    [Fact]
    public void A_role_with_no_allows_subagents_key_parses_as_false()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("r", "t")}]");

        Assert.False(WorkerRoleCatalog.For("r").AllowsSubagents);
    }

    [Fact]
    public void A_role_declaring_allows_subagents_true_parses_as_true()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            "[" + Role("r", "t")[..^1] + ", \"allows_subagents\": true}]");

        Assert.True(WorkerRoleCatalog.For("r").AllowsSubagents);
    }

    /// <summary>#1745: spec/baton.md §3 has why `review` and why its two values are equal.</summary>
    [Fact]
    public void The_shipped_review_role_carries_a_per_adapter_map_whose_values_equal_the_prior_single_figure()
    {
        using var env = ShippedDefault();

        var budget = Assert.IsType<TokenBudgetSpec.PerAdapter>(WorkerRoleCatalog.For("review").TokenBudget);
        Assert.Equal(250_000, budget.ByAdapter["claude"]);
        Assert.Equal(250_000, budget.ByAdapter["agy"]);
        Assert.Equal(250_000, budget.ByAdapter["codex"]);
    }

    /// <summary>#1745: every other shipped role keeps today's single-number shape unchanged.</summary>
    [Fact]
    public void Every_other_shipped_role_keeps_a_single_number_token_budget()
    {
        using var env = ShippedDefault();

        Assert.Equal(1_200_000L, ((TokenBudgetSpec.Fixed)WorkerRoleCatalog.For("implement").TokenBudget!).Value);
        Assert.Equal(150_000L, ((TokenBudgetSpec.Fixed)WorkerRoleCatalog.For("advise").TokenBudget!).Value);
    }
}
