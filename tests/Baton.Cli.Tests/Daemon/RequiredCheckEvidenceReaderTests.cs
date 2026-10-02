using System.Text.Json;
using Baton.Cli.Daemon;
using Baton.Queue;

namespace Baton.Cli.Tests.Daemon;

public sealed class RequiredCheckEvidenceReaderTests
{
    private const string Repository = "github.com/philipreese/baton";
    private const string Host = "github.com";
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const long ExpectedApp = 15368;
    private const string Prefix = "repos/philipreese/baton";
    private const string ClassicEndpoint = Prefix + "/branches/main/protection";
    private const string RulesPage = Prefix + "/rules/branches/main?per_page=100&page=1";
    private const string ChecksPage = Prefix + "/commits/" + Head + "/check-runs?filter=latest&per_page=100&page=1";
    private const string StatusesPage = Prefix + "/commits/" + Head + "/statuses?per_page=100&page=1";

    [Fact]
    public async Task MissingThirdRequiredCheckIsPendingAndPresentThirdCheckIsPassingWithStablePolicy()
    {
        var requirements = new (string Context, long? AppId)[]
        {
            ("ci", ExpectedApp), ("lint", ExpectedApp), ("shape", ExpectedApp)
        };
        var missing = NewFixture(
            Classic(requirements),
            Rules(),
            Runs(
                Run(1, "ci", ExpectedApp, "completed", "success"),
                Run(2, "lint", ExpectedApp, "completed", "success")));
        var present = NewFixture(
            Classic(requirements),
            Rules(),
            Runs(
                Run(1, "ci", ExpectedApp, "completed", "success"),
                Run(2, "lint", ExpectedApp, "completed", "success"),
                Run(3, "shape", ExpectedApp, "completed", "success")));

        var pending = await Reader(missing).ReadAsync(Repository, Head, CancellationToken.None);
        var passing = await Reader(present).ReadAsync(Repository, Head, CancellationToken.None);

        Assert.Equal(PullRequestChecks.Pending, pending.State);
        Assert.Equal(PullRequestChecks.Passing, passing.State);
        Assert.NotNull(pending.Evidence);
        Assert.NotNull(passing.Evidence);
        Assert.False(pending.NoObservedRequiredEvidence);
        Assert.False(passing.NoObservedRequiredEvidence);
        Assert.Equal(pending.Evidence!.PolicySha256, passing.Evidence!.PolicySha256);
        Assert.NotEqual(pending.Evidence.WitnessesSha256, passing.Evidence.WitnessesSha256);
        Assert.Equal(Repository, passing.Evidence.Repository);
        Assert.Equal("main", passing.Evidence.BaseBranch);
        Assert.Equal(Head, passing.Evidence.HeadSha);
        AssertCanonicalCalls(present);
    }

    [Fact]
    public async Task RequiredFailureWinsOverPendingButOptionalFailureDoesNot()
    {
        var requiredFailure = NewFixture(
            Classic(("ci", ExpectedApp), ("lint", ExpectedApp)),
            Rules(),
            Runs(
                Run(1, "ci", ExpectedApp, "completed", "failure"),
                Run(2, "lint", ExpectedApp, "pending", null)));
        var optionalFailure = NewFixture(
            Classic(("ci", null)),
            Rules(),
            Runs(Run(2, "optional", 999, "completed", "failure")),
            statuses: RunStatus(new Status(10, "ci", "pending", "2026-10-01T00:00:00Z")));

        var failing = await Reader(requiredFailure).ReadAsync(Repository, Head, CancellationToken.None);
        var pending = await Reader(optionalFailure).ReadAsync(Repository, Head, CancellationToken.None);

        Assert.Equal(PullRequestChecks.Failing, failing.State);
        Assert.Equal(PullRequestChecks.Pending, pending.State);
        Assert.NotNull(pending.Evidence);
    }

    [Fact]
    public async Task WrongAppIsNotAllowedToSatisfyAnExactAppRequirement()
    {
        var fixture = NewFixture(
            Classic(("ci", ExpectedApp)),
            Rules(),
            Runs(Run(1, "ci", 999, "completed", "success")));

        var reading = await Reader(fixture).ReadAsync(Repository, Head, CancellationToken.None);

        Assert.Equal(PullRequestChecks.Pending, reading.State);
        Assert.NotNull(reading.Evidence);
        Assert.True(reading.NoObservedRequiredEvidence);
    }

    [Fact]
    public async Task OptionalOnlyWitnessDoesNotCountAsObservedRequiredEvidence()
    {
        var fixture = NewFixture(
            Classic(("ci", null)),
            Rules(),
            Runs(Run(1, "optional", ExpectedApp, "completed", "success")));

        var reading = await Reader(fixture).ReadAsync(Repository, Head, CancellationToken.None);

        Assert.Equal(PullRequestChecks.Pending, reading.State);
        Assert.NotNull(reading.Evidence);
        Assert.True(reading.NoObservedRequiredEvidence);
    }

    [Fact]
    public async Task MalformedAndDuplicateResponsesAreUnknown()
    {
        var malformed = NewFixture(
            "{\"required_status_checks\":{\"contexts\":[],\"checks\":[{\"context\":\"ci\"}]}}",
            Rules(),
            Runs());
        var duplicate = NewFixture(
            "{\"required_status_checks\":null,\"required_status_checks\":null}",
            Rules(),
            Runs());

        var malformedReading = await Reader(malformed).ReadAsync(Repository, Head, CancellationToken.None);
        var duplicateReading = await Reader(duplicate).ReadAsync(Repository, Head, CancellationToken.None);

        AssertUnknown(malformedReading);
        AssertUnknown(duplicateReading);
    }

    [Fact]
    public async Task Classic404IsUnknownAndNeverInferredAsUnprotected()
    {
        var fixture = new Fixture();
        fixture.Add(ClassicEndpoint, new GhCliResult(true, 404, "{}", "not found"));

        var reading = await Reader(fixture).ReadAsync(Repository, Head, CancellationToken.None);

        AssertUnknown(reading);
        Assert.Contains("unavailable", reading.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("unprotected", reading.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("none", reading.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplicitClassicNullStillUsesPassingRulesetRequirement()
    {
        var fixture = NewFixture(
            ClassicNull(),
            Rules(("rules", ExpectedApp)),
            Runs(Run(1, "rules", ExpectedApp, "completed", "success")));

        var reading = await Reader(fixture).ReadAsync(Repository, Head, CancellationToken.None);

        Assert.Equal(PullRequestChecks.Passing, reading.State);
        Assert.NotNull(reading.Evidence);
    }

    [Fact]
    public async Task FiveFullRulePagesWithoutAShortPageAreUnknown()
    {
        var fixture = new Fixture();
        fixture.Add(ClassicEndpoint, Ok(ClassicNull()), Ok(ClassicNull()));
        for (var i = 0; i < 10; i++) fixture.Add(RulesPageFor(i % 5 + 1), Ok(FullPullRequestRulePage(i % 5 + 1)));

        var reading = await Reader(fixture).ReadAsync(Repository, Head, CancellationToken.None);

        AssertUnknown(reading);
        Assert.Contains("page bound", reading.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PolicyChangingBetweenBeforeAndAfterReadsIsUnknown()
    {
        var fixture = NewFixture(
            Classic(("ci", ExpectedApp)),
            Rules(),
            Runs(Run(1, "ci", ExpectedApp, "completed", "success")),
            classicAfter: Classic(("lint", ExpectedApp)));

        var reading = await Reader(fixture).ReadAsync(Repository, Head, CancellationToken.None);

        AssertUnknown(reading);
        Assert.Contains("changed during observation", reading.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public async Task CheckRunTotalProvesAFullFifthPageOnlyWhenComplete(int total, bool complete)
    {
        var fixture = new Fixture();
        fixture.Add(ClassicEndpoint, Ok(Classic(("ci", ExpectedApp))), Ok(Classic(("ci", ExpectedApp))));
        fixture.Add(RulesPage, Ok(Rules()), Ok(Rules()));
        fixture.Add(StatusesPage, Ok("[]"));
        for (var page = 1; page <= 5; page++)
        {
            var runs = Enumerable.Range((page - 1) * 100 + 1, 100)
                .Select(id => Run(id, id == 1 ? "ci" : $"optional-{id}", ExpectedApp, "completed", "success"))
                .ToArray();
            fixture.Add(ChecksPage.Replace("&page=1", $"&page={page}", StringComparison.Ordinal), Ok(RunsPage(total, runs)));
        }

        var reading = await Reader(fixture).ReadAsync(Repository, Head, CancellationToken.None);

        if (complete) Assert.Equal(PullRequestChecks.Passing, reading.State);
        else AssertUnknown(reading);
        Assert.Equal(5, fixture.Calls.Count(call => call[3].Contains("/check-runs?", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WitnessNamingAStaleHeadIsUnknown()
    {
        var fixture = NewFixture(
            Classic(("ci", ExpectedApp)),
            Rules(),
            Runs(Run(1, "ci", ExpectedApp, "completed", "success", head: new string('b', 40))));

        var reading = await Reader(fixture).ReadAsync(Repository, Head, CancellationToken.None);

        AssertUnknown(reading);
        Assert.Contains("different head", reading.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedResponseIsUnknownBeforeJsonInterpretation()
    {
        var fixture = new Fixture();
        fixture.Add(ClassicEndpoint, new GhCliResult(true, 0,
            new string('x', 1024 * 1024 + 1), string.Empty));

        var reading = await Reader(fixture).ReadAsync(Repository, Head, CancellationToken.None);

        AssertUnknown(reading);
        Assert.Contains("1 MiB", reading.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameContextAppAndTimestampIsAmbiguousAndUnknown()
    {
        var fixture = NewFixture(
            Classic(("ci", ExpectedApp)),
            Rules(),
            Runs(
                Run(1, "ci", ExpectedApp, "completed", "success", started: "2026-10-01T00:00:00Z"),
                Run(2, "ci", ExpectedApp, "completed", "success", started: "2026-10-01T00:00:00Z")));

        var reading = await Reader(fixture).ReadAsync(Repository, Head, CancellationToken.None);

        AssertUnknown(reading);
        Assert.Contains("ambiguous", reading.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueuedOptionalCheckWithNullStartedAtDoesNotBlockPassingRequiredCheck()
    {
        var fixture = NewFixture(
            Classic(("ci", ExpectedApp)),
            Rules(),
            Runs(
                Run(1, "ci", ExpectedApp, "completed", "success"),
                Run(2, "optional", 999, "queued", null, nullStarted: true)));

        var reading = await Reader(fixture).ReadAsync(Repository, Head, CancellationToken.None);

        Assert.Equal(PullRequestChecks.Passing, reading.State);
        Assert.NotNull(reading.Evidence);
    }

    [Fact]
    public async Task QueuedRequiredCheckWithNullStartedAtCannotBeReplacedByOldGreenRun()
    {
        var fixture = NewFixture(
            Classic(("ci", ExpectedApp)),
            Rules(),
            Runs(
                Run(1, "ci", ExpectedApp, "completed", "success", started: "2026-09-30T23:59:00Z"),
                Run(2, "ci", ExpectedApp, "queued", null, nullStarted: true)));

        var reading = await Reader(fixture).ReadAsync(Repository, Head, CancellationToken.None);

        Assert.Equal(PullRequestChecks.Pending, reading.State);
        Assert.NotNull(reading.Evidence);
    }

    [Theory]
    [InlineData("missing-source-type")]
    [InlineData("missing-source")]
    [InlineData("invalid-ruleset-id")]
    [InlineData("unknown-source-type")]
    [InlineData("mismatched-source")]
    public async Task InvalidRulesetMetadataIsUnknown(string invalidCase)
    {
        var fixture = NewFixture(ClassicNull(), RulesetMetadataRule(invalidCase), Runs());

        var reading = await Reader(fixture).ReadAsync(Repository, Head, CancellationToken.None);

        AssertUnknown(reading);
    }

    [Theory]
    [InlineData("neutral")]
    [InlineData("skipped")]
    [InlineData("unknown")]
    public async Task ClassicStatusesOutsideClosedVocabularyNeverSupplyGreen(string state)
    {
        var fixture = NewFixture(
            Classic(("ci", null)),
            Rules(),
            Runs(),
            statuses: RunStatus(new Status(10, "ci", state, "2026-10-01T00:00:00Z")));

        var reading = await Reader(fixture).ReadAsync(Repository, Head, CancellationToken.None);

        AssertUnknown(reading);
    }

    [Fact]
    public async Task ClassicAndActiveRepositoryOrganizationRulesAreUnioned()
    {
        var fixture = NewFixture(
            Classic(("classic-check", ExpectedApp)),
            RulesWithOrigins(
                ("repository-check", ExpectedApp, "Repository", "philipreese/baton", 101L),
                ("organization-check", ExpectedApp, "Organization", "philipreese", 202L)),
            Runs(Run(1, "repository-check", ExpectedApp, "completed", "success"),
                Run(2, "classic-check", ExpectedApp, "completed", "success")));

        var reading = await Reader(fixture).ReadAsync(Repository, Head, CancellationToken.None);

        Assert.Equal(PullRequestChecks.Pending, reading.State);
        Assert.NotNull(reading.Evidence);
    }

    private static RequiredCheckEvidenceReader Reader(Fixture fixture) => new(fixture.RunAsync);

    private static Fixture NewFixture(
        string classic,
        string rules,
        string runs,
        string statuses = "[]",
        string? classicAfter = null,
        string? rulesAfter = null)
    {
        var fixture = new Fixture();
        fixture.Add(ClassicEndpoint, Ok(classic), Ok(classicAfter ?? classic));
        fixture.Add(RulesPage, Ok(rules), Ok(rulesAfter ?? rules));
        fixture.Add(ChecksPage, Ok(runs));
        fixture.Add(StatusesPage, Ok(statuses));
        return fixture;
    }

    private static string Classic(params (string Context, long? AppId)[] requirements) =>
        JsonSerializer.Serialize(new
        {
            required_status_checks = new
            {
                contexts = requirements.Select(requirement => requirement.Context).ToArray(),
                checks = requirements.Select(requirement => new { context = requirement.Context, app_id = requirement.AppId }).ToArray()
            }
        });

    private static string ClassicNull() => "{\"required_status_checks\":null}";

    private static string Rules(params (string Context, long? AppId)[] requirements) =>
        requirements.Length == 0
            ? "[]"
            : JsonSerializer.Serialize(new[]
            {
                new
                {
                    ruleset_id = 1L,
                    type = "required_status_checks",
                    ruleset_source = "philipreese/baton",
                    ruleset_source_type = "Repository",
                    parameters = new
                    {
                        required_status_checks = requirements.Select(requirement =>
                            new { context = requirement.Context, integration_id = requirement.AppId }).ToArray()
                    }
                }
            });

    private static string RulesWithOrigins(
        params (string Context, long? AppId, string SourceType, string Source, long Id)[] rules) =>
        JsonSerializer.Serialize(rules.Select(rule => new
        {
            ruleset_id = rule.Id,
            type = "required_status_checks",
            ruleset_source = rule.Source,
            ruleset_source_type = rule.SourceType,
            parameters = new
            {
                required_status_checks = new[]
                {
                    new { context = rule.Context, integration_id = rule.AppId }
                }
            }
        }).ToArray());

    private static string RulesetMetadataRule(string invalidCase)
    {
        var rule = new Dictionary<string, object?>
        {
            ["ruleset_id"] = invalidCase == "invalid-ruleset-id" ? 0 : 1,
            ["type"] = "required_status_checks",
            ["ruleset_source"] = "philipreese/baton",
            ["ruleset_source_type"] = "Repository",
            ["parameters"] = new
            {
                required_status_checks = new[]
                {
                    new { context = "rules", integration_id = ExpectedApp }
                }
            }
        };
        if (invalidCase == "missing-source-type") rule.Remove("ruleset_source_type");
        if (invalidCase == "missing-source") rule.Remove("ruleset_source");
        if (invalidCase == "unknown-source-type") rule["ruleset_source_type"] = "Unknown";
        if (invalidCase == "mismatched-source") rule["ruleset_source"] = "other/project";
        return JsonSerializer.Serialize(new[] { rule });
    }

    private static string Runs(params CheckRun[] runs) => RunsPage(runs.Length, runs);

    private static string RunsPage(int total, params CheckRun[] runs) => JsonSerializer.Serialize(new
    {
        total_count = total,
        check_runs = runs.Select(run => new
        {
            id = run.Id,
            head_sha = run.Head,
            name = run.Name,
            status = run.Status,
            conclusion = run.Conclusion,
            started_at = run.Started,
            app = new { id = run.AppId }
        }).ToArray()
    });

    private static string RunStatus(params Status[] statuses) => JsonSerializer.Serialize(statuses.Select(status => new
    {
        id = status.Id,
        context = status.Context,
        state = status.State,
        created_at = status.Created
    }).ToArray());

    private static CheckRun Run(
        long id,
        string name,
        long appId,
        string status,
        string? conclusion,
        string? head = null,
        string? started = null,
        bool nullStarted = false) => new(id, name, appId, status, conclusion, head ?? Head,
        nullStarted ? null : started ?? "2026-10-01T00:00:00Z");

    private static string RulesPageFor(int page) => Prefix + $"/rules/branches/main?per_page=100&page={page}";

    private static string FullPullRequestRulePage(int page) => JsonSerializer.Serialize(
        Enumerable.Range(0, 100).Select(index => new
        {
            ruleset_id = page * 1000L + index + 1,
            type = "pull_request",
            ruleset_source = "philipreese/baton",
            ruleset_source_type = "Repository"
        }).ToArray());

    private static GhCliResult Ok(string json) => new(true, 0, json, string.Empty);

    private static void AssertUnknown(RequiredCheckEvidenceReader.Reading reading)
    {
        Assert.Null(reading.State);
        Assert.Null(reading.Evidence);
        Assert.False(string.IsNullOrWhiteSpace(reading.Error));
    }

    private static void AssertCanonicalCalls(Fixture fixture)
    {
        Assert.NotEmpty(fixture.Calls);
        foreach (var call in fixture.Calls)
        {
            Assert.Equal(4, call.Count);
            Assert.Equal("api", call[0]);
            Assert.Equal("--hostname", call[1]);
            Assert.Equal(Host, call[2]);
            Assert.Contains(call[3], new[] { ClassicEndpoint, RulesPage, ChecksPage, StatusesPage });
        }
    }

    private sealed record CheckRun(long Id, string Name, long AppId, string Status, string? Conclusion,
        string Head, string? Started);

    private sealed record Status(long Id, string Context, string State, string Created);

    private sealed class Fixture
    {
        private readonly Dictionary<string, Queue<GhCliResult>> _responses = new(StringComparer.Ordinal);

        internal List<IReadOnlyList<string>> Calls { get; } = [];

        internal void Add(string endpoint, params GhCliResult[] results)
        {
            if (!_responses.TryGetValue(endpoint, out var queue))
                _responses[endpoint] = queue = new Queue<GhCliResult>();
            foreach (var result in results) queue.Enqueue(result);
        }

        internal Task<GhCliResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            Calls.Add(args.ToArray());
            Assert.Equal(4, args.Count);
            Assert.Equal("api", args[0]);
            Assert.Equal("--hostname", args[1]);
            Assert.Equal(Host, args[2]);
            if (!_responses.TryGetValue(args[3], out var queue) || queue.Count == 0)
                throw new InvalidOperationException($"no fixture response for {args[3]}");
            return Task.FromResult(queue.Dequeue());
        }
    }
}
