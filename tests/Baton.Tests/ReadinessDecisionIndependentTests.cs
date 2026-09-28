using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Baton.Conductor;

namespace Baton.Tests;

public sealed class ReadinessDecisionIndependentTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private static readonly ReadinessRequest Request = new(
        1,
        "trip-2026-09-28",
        "philipreese/baton",
        @"C:\worktrees\baton",
        "0123456789abcdef0123456789abcdef01234567",
        "conductor",
        new DateTimeOffset(2026, 9, 28, 14, 30, 0, TimeSpan.Zero),
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        "codex-subscription-cli",
        "gpt-5.6-luna",
        "low");

    private static readonly ReadinessContext Context = new(
        1,
        Request.Key,
        Request.Repository,
        Request.Workspace,
        Request.Revision,
        Request.ObservedAt,
        [new ReadinessEvidence("required-checks", "green", "all required checks passed")]);

    private static readonly ReadinessDecision Decision = new(
        "obligation-1",
        Request.Repository,
        Request.Revision,
        Request.ContextSha256,
        ReadinessChoice.Recommend,
        "The supplied evidence supports readiness advice.");

    [Fact]
    public void Typed_contracts_round_trip_without_wire_drift()
    {
        AssertRoundTrips(Request);
        AssertRoundTrips(new ReadinessEvidence("required-checks", "green", "all required checks passed"));
        AssertRoundTrips(Context);
        AssertRoundTrips(Decision);
        AssertRoundTrips(new ReadinessUsage(120, 40, 10));
        AssertRoundTrips(new RetainedReadinessResponse(
            Decision,
            new ReadinessUsage(120, 40, 10),
            "codex-subscription-cli",
            "gpt-5.6-luna",
            "low",
            Request.ObservedAt));
    }

    [Fact]
    public void Readiness_request_rejects_unknown_properties()
    {
        AssertRejectsUnknownProperty(Request);
    }

    [Fact]
    public void Readiness_context_rejects_unknown_properties()
    {
        AssertRejectsUnknownProperty(Context);
    }

    [Fact]
    public void Readiness_decision_rejects_unknown_properties()
    {
        AssertRejectsUnknownProperty(Decision);
    }

    [Fact]
    public void Retained_response_rejects_unknown_properties()
    {
        AssertRejectsUnknownProperty(new RetainedReadinessResponse(
            Decision,
            new ReadinessUsage(120, 40, 10),
            "codex-subscription-cli",
            "gpt-5.6-luna",
            "low",
            Request.ObservedAt));
    }

    [Fact]
    public void Readiness_usage_rejects_unknown_properties()
    {
        AssertRejectsUnknownProperty(new ReadinessUsage(120, 40, 10));
    }

    [Theory]
    [InlineData(ReadinessChoice.Hold, "hold")]
    [InlineData(ReadinessChoice.Recommend, "recommend")]
    public void Readiness_choices_are_explicit_camel_case_strings(ReadinessChoice choice, string expected)
    {
        var json = JsonSerializer.Serialize(Decision with { Decision = choice }, Json);
        var document = JsonNode.Parse(json)!.AsObject();

        Assert.Equal(expected, document["decision"]?.GetValue<string>());
        Assert.DoesNotContain("\"decision\":0", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"decision\":1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Readiness_decision_does_not_accept_an_executable_action_field()
    {
        var document = JsonNode.Parse(JsonSerializer.Serialize(Decision, Json))!.AsObject();
        document["action"] = "merge";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ReadinessDecision>(document.ToJsonString(), Json));
    }

    [Fact]
    public void Readiness_decision_rejects_a_missing_decision()
    {
        var document = JsonNode.Parse(JsonSerializer.Serialize(Decision, Json))!.AsObject();
        Assert.True(document.Remove("decision"));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ReadinessDecision>(document.ToJsonString(), Json));
    }

    [Fact]
    public void Readiness_decision_rejects_an_undefined_numeric_choice()
    {
        var document = JsonNode.Parse(JsonSerializer.Serialize(Decision, Json))!.AsObject();
        document["decision"] = 99;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ReadinessDecision>(document.ToJsonString(), Json));
    }

    private static void AssertRoundTrips<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, Json);
        var roundTripped = JsonSerializer.Deserialize<T>(json, Json);

        Assert.NotNull(roundTripped);
        Assert.Equal(json, JsonSerializer.Serialize(roundTripped, Json));
    }

    private static void AssertRejectsUnknownProperty<T>(T value)
    {
        var document = JsonSerializer.SerializeToNode(value, Json)!.AsObject();
        document["unexpected"] = true;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<T>(document.ToJsonString(), Json));
    }
}
