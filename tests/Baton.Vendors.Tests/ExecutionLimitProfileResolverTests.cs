using Baton.Domain;
using Baton.Vendors;

namespace Baton.Vendors.Tests;

public class ExecutionLimitProfileResolverTests
{
    private static ExecutionLimitProfile Profile(string model, int timeoutMinutes, long tokens, int steps) => new()
    {
        Adapter = "claude",
        Model = model,
        Role = "review",
        DeclaredTaskSize = "medium",
        Timeout = TimeSpan.FromMinutes(timeoutMinutes),
        TokenBudget = tokens,
        MaxToolSteps = steps,
    };

    [Fact]
    public void Exact_model_keys_select_different_limits()
    {
        var result = ExecutionLimitProfileResolver.Resolve(
            [Profile("opus", 10, 1000, 10), Profile("sonnet", 20, 2000, 20)],
            "CLAUDE", "sonnet", "review", DeclaredTaskSize.Medium,
            TimeSpan.FromMinutes(5), 500, 5);

        Assert.Equal(TimeSpan.FromMinutes(20), result.Timeout);
        Assert.Equal(2000, result.TokenBudget);
        Assert.Equal(20, result.MaxToolSteps);
        Assert.Equal("profile", result.TimeoutSource);
        Assert.Equal("claude/sonnet/review/medium", result.ChosenKey);
        Assert.Equal("claude/sonnet/review/medium", result.OriginatingSelectionKey);
    }

    [Fact]
    public void Explicit_overrides_win_independently_for_each_brake()
    {
        var result = ExecutionLimitProfileResolver.Resolve(
            [Profile("sonnet", 20, 2000, 20)],
            "claude", "sonnet", "review", DeclaredTaskSize.Medium,
            TimeSpan.FromMinutes(5), 500, 5,
            timeoutOverride: TimeSpan.FromMinutes(7), maxToolStepsOverride: 3);

        Assert.Equal(TimeSpan.FromMinutes(7), result.Timeout);
        Assert.Equal(2000, result.TokenBudget);
        Assert.Equal(3, result.MaxToolSteps);
        Assert.Equal("dispatch-override", result.TimeoutSource);
        Assert.Equal("profile", result.TokenBudgetSource);
        Assert.Equal("dispatch-override", result.MaxToolStepsSource);
        Assert.Equal("claude/sonnet/review/medium", result.OriginatingSelectionKey);
    }

    [Fact]
    public void Repeated_call_cap_has_only_profile_or_explicit_override_sources_and_no_role_default()
    {
        var profile = Profile("sonnet", 20, 2000, 20) with { MaxRepeatedToolSteps = 9 };
        var selected = ExecutionLimitProfileResolver.Resolve(
            [profile], "claude", "sonnet", "review", DeclaredTaskSize.Medium,
            TimeSpan.FromMinutes(5), null, null);
        var overridden = ExecutionLimitProfileResolver.Resolve(
            [profile], "claude", "sonnet", "review", DeclaredTaskSize.Medium,
            TimeSpan.FromMinutes(5), null, null, maxRepeatedToolStepsOverride: 4);
        var noProfile = ExecutionLimitProfileResolver.Resolve(
            null, "claude", "sonnet", "review", DeclaredTaskSize.Medium,
            TimeSpan.FromMinutes(5), null, null);

        Assert.Equal(9, selected.MaxRepeatedToolSteps);
        Assert.Equal(ExecutionLimitSource.Profile, selected.MaxRepeatedToolStepsSource);
        Assert.Equal(4, overridden.MaxRepeatedToolSteps);
        Assert.Equal(ExecutionLimitSource.DispatchOverride, overridden.MaxRepeatedToolStepsSource);
        Assert.Null(noProfile.MaxRepeatedToolSteps);
        Assert.Null(noProfile.MaxRepeatedToolStepsSource);
    }

    [Fact]
    public void Same_role_profiles_can_set_different_repeated_call_caps_for_different_models()
    {
        var profiles = new[]
        {
            Profile("opus", 20, 2000, 20) with { MaxRepeatedToolSteps = 7 },
            Profile("sonnet", 20, 2000, 20) with { MaxRepeatedToolSteps = 3 },
        };

        var opus = ExecutionLimitProfileResolver.Resolve(
            profiles, "claude", "opus", "review", DeclaredTaskSize.Medium,
            TimeSpan.FromMinutes(5), null, null);
        var sonnet = ExecutionLimitProfileResolver.Resolve(
            profiles, "claude", "sonnet", "review", DeclaredTaskSize.Medium,
            TimeSpan.FromMinutes(5), null, null);

        Assert.Equal(7, opus.MaxRepeatedToolSteps);
        Assert.Equal(ExecutionLimitSource.Profile, opus.MaxRepeatedToolStepsSource);
        Assert.Equal("claude/opus/review/medium", opus.ChosenKey);
        Assert.Equal(3, sonnet.MaxRepeatedToolSteps);
        Assert.Equal(ExecutionLimitSource.Profile, sonnet.MaxRepeatedToolStepsSource);
        Assert.Equal("claude/sonnet/review/medium", sonnet.ChosenKey);
    }

    [Fact]
    public void A_nonpositive_profile_repeated_call_cap_is_rejected()
    {
        var profile = Profile("sonnet", 20, 2000, 20) with { MaxRepeatedToolSteps = 0 };

        var exception = Assert.Throws<ExecutionLimitProfileConfigurationException>(
            () => ExecutionLimitProfileResolver.Validate([profile]));

        Assert.Contains("repeated tool steps must be positive", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_profile_preserves_role_defaults_with_truthful_sources()
    {
        var result = ExecutionLimitProfileResolver.Resolve(
            [Profile("opus", 10, 1000, 10)],
            "claude", "sonnet", "review", DeclaredTaskSize.Medium,
            TimeSpan.FromMinutes(5), null, null);

        Assert.Equal(TimeSpan.FromMinutes(5), result.Timeout);
        Assert.Null(result.TokenBudget);
        Assert.Null(result.MaxToolSteps);
        Assert.Null(result.ChosenKey);
        Assert.Null(result.OriginatingSelectionKey);
        Assert.Equal("role-default", result.TimeoutSource);
        Assert.Equal("role-default", result.TokenBudgetSource);
        Assert.Equal("role-default", result.MaxToolStepsSource);
    }

    [Fact]
    public void Duplicate_normalized_keys_are_rejected()
    {
        var profiles = new[] { Profile("Sonnet", 10, 1000, 10), Profile(" sonnet ", 20, 2000, 20) };

        var exception = Assert.Throws<ExecutionLimitProfileConfigurationException>(
            () => ExecutionLimitProfileResolver.Validate(profiles));

        Assert.Contains("duplicate normalized key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Binding_limit_resolution_round_trips_with_its_sources()
    {
        var resolution = new ExecutionLimitResolution(
            "claude/sonnet/review/medium", "profile", "dispatch-override", "role-default",
            TimeSpan.FromMinutes(20), 2000, 5, "claude/sonnet/review/medium");
        var entry = new WorkerBindingConfigEntry(
            "claude",
            new WorkerContract("review", [], [new ProducedOutput("report")], []),
            "Review the change.",
            resolution.Timeout,
            Model: "sonnet",
            TokenBudget: resolution.TokenBudget,
            MaxToolSteps: resolution.MaxToolSteps,
            ExecutionLimitResolution: resolution);

        var parsed = WorkerBindingConfigParser.Parse(
            WorkerBindingConfigWriter.Serialize(new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["review"] = entry,
            }));

        Assert.Equal(resolution, parsed["review"].ExecutionLimitResolution);
    }
}
