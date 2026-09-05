using System.Diagnostics;
using System.Text.Json;
using Baton.Memory;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

/// <summary>
/// #1852 phase A's verb, driven end to end over a fixture Claude home and real git checkouts.
/// <c>MemoryAuditReportTests</c> owns which findings fire; this file owns what an operator and a
/// machine consumer actually receive, and the one claim only an end-to-end run can make — that the
/// whole chain (scan, session-<c>cwd</c> read, git probe, report) agrees on a root's repository.
/// </summary>
/// <remarks>
/// The checkouts are real <c>git init</c> trees with real <c>origin</c> remotes, for the same reason
/// <see cref="RepositoryIdentityResolverTests"/> uses them: the identity claim rests on what git
/// answers, and a stubbed probe would assert this file's own expectation back at itself.
/// </remarks>
public sealed class MemoryAuditCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"baton-1852-cli-{Guid.NewGuid():N}");

    private string ClaudeHome => Path.Combine(_root, "claude");

    private string Checkout(string name) => Path.Combine(_root, "checkouts", name);

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_root);

    /// <summary>
    /// The fixture, mirroring the four shapes the #1852 survey found on the real machine: a root whose
    /// origin and filenames disagree, a drained/live pair sharing one file, a root whose checkout is
    /// gone, and a root whose cwd is not a repository at all.
    /// </summary>
    private async Task BuildFixtureAsync()
    {
        await InitGitRepoAsync(Checkout("alpaca"), "https://github.com/philipreese/basis.git");
        await InitGitRepoAsync(Checkout("baton"), "https://github.com/philipreese/baton.git");
        Directory.CreateDirectory(Checkout("plain"));

        WriteRoot("C--alpaca", Checkout("alpaca"), ("MEMORY.md", "index"), ("project_baton_direction.md", "direction"));
        WriteRoot("C--baton", Checkout("baton"), ("MEMORY.md", "index"));
        WriteRoot("C--gone", Checkout("never-created"), ("user_who.md", "who"));
        WriteRoot("C--plain", Checkout("plain"), ("feedback_style.md", "style"));
    }

    /// <summary>
    /// One live root plus the session transcript that names its cwd — ground truth, so the fixture does
    /// not depend on this machine's temp path surviving the directory-name encoding.
    /// </summary>
    private void WriteRoot(string projectDirectoryName, string cwd, params (string Name, string Content)[] files)
    {
        var project = Path.Combine(ClaudeHome, "projects", projectDirectoryName);
        var memory = Path.Combine(project, "memory");
        Directory.CreateDirectory(memory);

        File.WriteAllText(
            Path.Combine(project, "session.jsonl"),
            JsonSerializer.Serialize(new { type = "summary", cwd }) + "\n");

        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(memory, name), content);
        }
    }

    private static async Task<string> RunAsync(MemoryAuditOptions options, string claudeHome)
    {
        var writer = new StringWriter();
        var exitCode = await MemoryAuditCommand.ExecuteAsync(
            options, writer, claudeHome, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        return writer.ToString();
    }

    [Fact]
    public async Task The_text_view_names_every_root_its_repository_and_every_finding()
    {
        await BuildFixtureAsync();

        var text = await RunAsync(new MemoryAuditOptions(), ClaudeHome);

        Assert.Contains("READ-ONLY", text, StringComparison.Ordinal);
        Assert.Contains($"Claude home: {ClaudeHome}", text, StringComparison.Ordinal);
        Assert.Contains("Roots: 4", text, StringComparison.Ordinal);

        // The end-to-end identity claim: the scan found the root, the transcript gave the cwd, and git
        // answered for it. No stub anywhere in that chain.
        Assert.Contains("repository=github.com/philipreese/baton", text, StringComparison.Ordinal);
        Assert.Contains("repository=github.com/philipreese/basis", text, StringComparison.Ordinal);

        Assert.Contains("[ambiguous]", text, StringComparison.Ordinal);
        Assert.Contains("[duplicate]", text, StringComparison.Ordinal);
        Assert.Contains("[orphan]", text, StringComparison.Ordinal);
        Assert.Contains("[no-provenance]", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_json_view_is_one_object_with_roots_findings_and_counts()
    {
        await BuildFixtureAsync();

        var json = await RunAsync(new MemoryAuditOptions(MemoryAuditOutputFormat.Json), ClaudeHome);

        using var document = JsonDocument.Parse(json);
        var view = document.RootElement;

        Assert.Equal(ClaudeHome, view.GetProperty("claudeHome").GetString());
        Assert.Equal(4, view.GetProperty("roots").GetArrayLength());
        Assert.Equal(4, view.GetProperty("counts").GetProperty("roots").GetInt32());
        Assert.Equal(5, view.GetProperty("counts").GetProperty("files").GetInt32());

        var kinds = view.GetProperty("findings").EnumerateArray()
            .Select(f => f.GetProperty("kind").GetString())
            .ToList();
        Assert.Contains("ambiguous", kinds);
        Assert.Contains("duplicate", kinds);
        Assert.Contains("orphan", kinds);
        Assert.Contains("no-provenance", kinds);

        var alpaca = view.GetProperty("roots").EnumerateArray()
            .Single(r => r.GetProperty("root").GetString()!.Contains("C--alpaca", StringComparison.Ordinal));
        Assert.Equal("github.com/philipreese/basis", alpaca.GetProperty("repository").GetString());
        Assert.Equal("session-cwd", alpaca.GetProperty("pathSource").GetString());
        Assert.Equal(Checkout("alpaca"), alpaca.GetProperty("checkoutPath").GetString());
        Assert.True(alpaca.GetProperty("checkoutExists").GetBoolean());

        // WhenWritingNull, asserted where it is load-bearing: a live root has no archiveLabel, and an
        // absent field must be ABSENT rather than null -- "not applicable" and "unknown" are the same
        // reading otherwise.
        Assert.False(alpaca.TryGetProperty("archiveLabel", out _));

        // The ambiguous finding carries BOTH candidate identities and selects neither.
        var ambiguous = view.GetProperty("findings").EnumerateArray()
            .Single(f => f.GetProperty("kind").GetString() == "ambiguous");
        var candidates = ambiguous.GetProperty("candidates").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Equal(new[] { "github.com/philipreese/basis", "github.com/philipreese/baton" }, candidates);
    }

    [Fact]
    public async Task An_empty_claude_home_reports_no_roots_rather_than_failing()
    {
        var text = await RunAsync(new MemoryAuditOptions(), Path.Combine(_root, "absent"));

        Assert.Contains("Roots: 0", text, StringComparison.Ordinal);
        Assert.Contains("Findings: none.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The read-only claim is prose in <c>--help</c>, so it is pinned by a test rather than by review:
    /// a later change that gives this verb a write must break this to ship.
    /// </summary>
    [Fact]
    public async Task Help_states_the_verb_is_read_only_and_lists_every_finding_kind()
    {
        var text = await RunAsync(new MemoryAuditOptions(Help: true), ClaudeHome);

        Assert.Contains("READ-ONLY BY CONSTRUCTION", text, StringComparison.Ordinal);
        Assert.Contains("NO --dry-run", text, StringComparison.Ordinal);

        foreach (var kind in Enum.GetValues<MemoryFindingKind>())
        {
            Assert.Contains(MemoryJsonNames.Of(kind), text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_parser_rejects_dry_run_by_name_rather_than_as_an_unknown_option()
    {
        var refused = Assert.Throws<CliArgumentException>(() => MemoryAuditOptionsParser.Parse(["--dry-run"]));

        Assert.Contains("read-only by construction", refused.Message, StringComparison.Ordinal);

        // Control: an actually-unknown option gets the ordinary message, so the arm above is keyed on
        // --dry-run and is not the generic branch wearing a different sentence.
        var unknown = Assert.Throws<CliArgumentException>(() => MemoryAuditOptionsParser.Parse(["--frobnicate"]));
        Assert.Contains("Unknown option", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_parser_takes_only_format_and_help()
    {
        Assert.Equal(MemoryAuditOutputFormat.Text, MemoryAuditOptionsParser.Parse([]).Format);
        Assert.Equal(MemoryAuditOutputFormat.Json, MemoryAuditOptionsParser.Parse(["--format", "json"]).Format);
        Assert.True(MemoryAuditOptionsParser.Parse(["--help"]).Help);

        Assert.Throws<CliArgumentException>(() => MemoryAuditOptionsParser.Parse(["--format"]));
        Assert.Throws<CliArgumentException>(() => MemoryAuditOptionsParser.Parse(["--format", "csv"]));
        Assert.Throws<CliArgumentException>(() => MemoryAuditOptionsParser.Parse(["audit"]));
    }

    /// <summary>
    /// <see cref="MemorySubjectVocabulary.Default"/> names a repository by its canonical identity, and
    /// that identity is derived from a REMOTE URL — which a rename on the forge changes without
    /// touching a line of this repo. When it drifts, the subject-ambiguity detector does not fail
    /// loudly: it silently stops matching, and the <c>alpaca-agent-bot</c> finding the audit exists to
    /// surface just disappears.
    /// </summary>
    /// <remarks>
    /// So the constant is pinned against the live probe rather than against another copy of itself.
    /// <c>MemoryAuditReportTests</c> spells the same string on both sides of its assertions, which is
    /// the same instrument twice and cannot catch this; this test asks git. A fork whose origin is a
    /// different URL fails here on purpose — the table names THIS repository, and a fork that means to
    /// keep the detector working has to say so.
    /// </remarks>
    [Fact]
    public async Task The_pinned_subject_vocabulary_still_matches_this_repositorys_own_identity()
    {
        var identity = await RepositoryIdentityResolver.TryResolveAsync(
            FindRepoRoot(), TestContext.Current.CancellationToken);

        Assert.NotNull(identity);
        Assert.Contains(
            MemorySubjectVocabulary.Default.IdentityByToken,
            entry => string.Equals(entry.Value, identity.Value, StringComparison.OrdinalIgnoreCase));

        // The control: the assertion above passes on an empty-string identity matched against an
        // empty-string table entry, or on a table that happens to hold whatever git returned. Assert
        // the token side too, so the entry that matched is the one the detector actually reads.
        Assert.True(MemorySubjectVocabulary.Default.IdentityByToken.ContainsKey("baton"));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Baton.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repo root (Baton.slnx) from {AppContext.BaseDirectory}.");
    }

    private static async Task InitGitRepoAsync(string directory, string originUrl)
    {
        Directory.CreateDirectory(directory);
        await RunGitAsync(directory, "init", "-q");
        await RunGitAsync(directory, "remote", "add", "origin", originUrl);
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        // Bounded, per #1804: an unbounded wait here would hold the machine-wide build lock if git
        // ever hung on a credential or filesystem prompt.
        await BoundedProcessWait.RunToExitAsync(
            process, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
    }
}
