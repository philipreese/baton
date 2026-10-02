using System.Text.Json;
using Baton.Cli.Daemon;

namespace Baton.Cli.Tests.Daemon;

/// <summary>Legacy journey fixtures explicitly declare one required ci/App policy, never infer it from results.</summary>
internal static class RequiredCheckFixture
{
    internal static GhCliResult Read(IReadOnlyList<string> args, string repository, string head,
        GhCliResult results)
    {
        Assert.Equal(4, args.Count);
        Assert.Equal("api", args[0]);
        Assert.Equal("--hostname", args[1]);
        var parts = repository.Split('/');
        Assert.Equal(parts[0], args[2]);
        var prefix = $"repos/{parts[1]}/{parts[2]}";
        Assert.StartsWith(prefix + "/", args[3], StringComparison.Ordinal);
        if (args[3] == prefix + "/branches/main/protection")
            return new(true, 0, """{"required_status_checks":{"contexts":["ci"],"checks":[{"context":"ci","app_id":15368}]}}""", "");
        if (args[3] == prefix + "/rules/branches/main?per_page=100&page=1"
            || args[3] == prefix + $"/commits/{head}/statuses?per_page=100&page=1")
            return new(true, 0, "[]", "");
        Assert.Equal(prefix + $"/commits/{head}/check-runs?filter=latest&per_page=100&page=1", args[3]);
        if (!results.Started || results.ExitCode != 0) return results;
        try
        {
            using var document = JsonDocument.Parse(results.Stdout);
            var entries = document.RootElement.EnumerateArray().Select((entry, index) =>
            {
                var bucket = entry.GetProperty("bucket").GetString();
                return new
                {
                    id = index + 1,
                    name = entry.TryGetProperty("name", out var name) ? name.GetString() : "ci",
                    head_sha = head,
                    status = bucket == "pending" ? "queued" : "completed",
                    conclusion = bucket switch { "pending" => null, "pass" => "success", "skipping" => "skipped", _ => "failure" },
                    started_at = "2026-09-28T00:00:00Z",
                    app = new { id = 15368 },
                };
            }).ToArray();
            return new(true, 0, JsonSerializer.Serialize(new { total_count = entries.Length, check_runs = entries }), "");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new(true, 0, "not-json", "");
        }
    }
}
