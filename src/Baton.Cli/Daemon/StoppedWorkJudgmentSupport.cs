using System.Globalization;
using System.Text.Json;
using Baton.Accounting;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Daemon;

internal static class StoppedWorkJudgmentKey
{
    internal const string Prefix = "stopped-judgment:";
    internal const string Action = "stopped-work-judgment";
    internal const string Capability = "stopped-work-advice";
    internal const string Adapter = "codex-subscription-cli";
    internal const string Owner = "repository-conductor";

    internal static string For(string repository, string tag, FleetAttemptId attempt, WorkStage stage) =>
        Prefix + repository + ":" + tag + ":" + attempt.Value + ":" + WorkStages.Token(stage);

    internal static bool TryParse(string key, out string repository, out string tag,
        out FleetAttemptId attempt, out WorkStage stage)
    {
        repository = string.Empty;
        tag = string.Empty;
        attempt = default;
        stage = default;
        if (!key.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var parts = key[Prefix.Length..].Split(':');
        if (parts.Length != 4 || string.IsNullOrWhiteSpace(parts[0])
            || string.IsNullOrWhiteSpace(parts[1]) || string.IsNullOrWhiteSpace(parts[2])
            || string.IsNullOrWhiteSpace(parts[3])) return false;
        repository = parts[0];
        tag = parts[1];
        attempt = new FleetAttemptId(parts[2]);
        return WorkStages.TryParseToken(parts[3], out stage);
    }
}

internal static class StoppedWorkAdviceSettings
{
    internal static bool IsEnabled(string repository)
    {
        if (!File.Exists(BatonPaths.SettingsFile)) return false;
        try
        {
            var settings = JsonSerializer.Deserialize<DaemonSettings>(
                File.ReadAllText(BatonPaths.SettingsFile));
            return settings?.Queue.IsStoppedWorkAdviceEnabled(repository) == true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Stopped-work advice settings are unreadable; automatic advice remains off.");
            return false;
        }
    }
}

public sealed partial class WorkItemAdvancer
{
    internal static bool IsStoppedWorkAdviceEnabledNow(string? repository) =>
        repository is { Length: > 0 } && StoppedWorkAdviceSettings.IsEnabled(repository);

    internal async Task ValidateStoppedWorkHeadAsync(QueueItem source, CancellationToken cancellationToken)
    {
        var intent = source.StoppedWorkJudgment
            ?? throw new ConductorObligationStoreException("Stopped-work source is missing.");
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(15));
        var observed = await ReadPullRequestAsync(source, bound.Token).ConfigureAwait(false);
        if (!observed.Succeeded || observed.Number != intent.PullRequest
            || !string.Equals(observed.HeadSha, intent.PullRequestHead, StringComparison.Ordinal))
            throw new ConductorObligationStoreException("Stopped-work pull-request evidence changed or is unavailable.");
    }
}
