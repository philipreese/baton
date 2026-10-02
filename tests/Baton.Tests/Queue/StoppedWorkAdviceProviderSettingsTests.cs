using System.Text.Json;
using Baton.Conductor;
using Baton.Queue;

namespace Baton.Tests.Queue;

public sealed class StoppedWorkAdviceProviderSettingsTests
{
    private const string Repository = "github.com/example/project";

    [Theory]
    [InlineData("{}", "codex")]
    [InlineData("{\"StoppedWorkAdviceProvider\":{}}", "codex")]
    [InlineData("{\"StoppedWorkAdviceProvider\":{\"github.com/example/project\":\"codex\"}}", "codex")]
    [InlineData("{\"StoppedWorkAdviceProvider\":{\"github.com/example/project\":\"claude\"}}", "claude")]
    public void Selection_defaults_to_codex_and_never_enables_advice(string json, string expected)
    {
        var settings = JsonSerializer.Deserialize<QueueSettings>(json)!;
        Assert.True(settings.TrySelectStoppedWorkAdviceProvider(Repository, out var provider));
        Assert.Equal(expected == "codex" ? StoppedWorkAdviceProviderDescriptor.Codex : StoppedWorkAdviceProviderDescriptor.Claude, provider);
        Assert.False(settings.IsStoppedWorkAdviceEnabled(Repository));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("[]")]
    [InlineData("{\"github.com/example/project\":true}")]
    [InlineData("{\"github.com/example/project\":null}")]
    [InlineData("{\"github.com/example/project\":\"Claude\"}")]
    [InlineData("{\"github.com/example/project\":\"agy\"}")]
    [InlineData("{\"https://github.com/example/project\":\"claude\"}")]
    [InlineData("{\"github.com/example/project\":\"claude\",\"github.com/example/project\":\"codex\"}")]
    public void Malformed_selection_refuses_only_provider_admission_and_preserves_unrelated_settings(string map)
    {
        var settings = JsonSerializer.Deserialize<QueueSettings>("{\"MaxLiveWeight\":9,\"StoppedWorkAdvice\":{\""
            + Repository + "\":true},\"StoppedWorkAdviceProvider\":" + map + "}")!;
        Assert.True(settings.IsStoppedWorkAdviceEnabled(Repository));
        Assert.Equal(9, settings.EffectiveMaxLiveWeight);
        Assert.False(settings.TrySelectStoppedWorkAdviceProvider(Repository, out _));
    }
}
