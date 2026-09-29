using System.Text.Json;
using Baton.Accounting;
using Baton.Cli;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Status;
using Baton.Tests.Shared;

namespace Baton.Cli.Tests;

public sealed class ReadinessConductorCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "baton-readiness-command-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ReadinessConductorCommandTests() => Directory.CreateDirectory(_root);

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_root);

    [Fact]
    public async Task Prepare_and_decide_require_matching_claim_revision_clean_workspace_and_context()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        await GitAsync(workspace, "init");
        await GitAsync(workspace, "remote", "add", "origin", "https://github.com/test/readiness.git");
        await GitAsync(workspace, "-c", "user.name=Test", "-c", "user.email=test@example.com",
            "commit", "--allow-empty", "-m", "fixture");
        var revision = await GitAsync(workspace, "rev-parse", "HEAD");
        var identity = RepositoryIdentity.From("https://github.com/test/readiness.git", null)!;
        await ConductorClaimStore.ClaimAsync(identity, "conductor-one", _root, cancellationToken: Ct);

        var context = new ReadinessContext(1, "run-one", identity.Value, workspace, revision,
            DateTimeOffset.Parse("2026-09-28T14:00:00Z"),
            [new ReadinessEvidence("checks", "green", "required checks passed")]);
        var contextFile = Path.Combine(_root, "context.json");
        var contextBytes = JsonSerializer.SerializeToUtf8Bytes(context, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await File.WriteAllBytesAsync(contextFile, contextBytes, Ct);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(contextBytes)).ToLowerInvariant();
        var request = new ReadinessRequest(1, context.Key, identity.Value, workspace, revision,
            "conductor-one", context.ObservedAt, digest, "codex-subscription-cli", "gpt-5.6-luna", "low");
        var requestFile = Path.Combine(_root, "request.json");
        async Task WriteRequestAsync(ReadinessRequest value) =>
            await File.WriteAllTextAsync(requestFile, JsonSerializer.Serialize(value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), Ct);

        await WriteRequestAsync(request with { Holder = "another-conductor" });
        await Assert.ThrowsAsync<CliArgumentException>(() =>
            ReadinessConductorCommand.PrepareAsync(requestFile, TextWriter.Null, _root, cancellationToken: Ct));
        await WriteRequestAsync(request with { Repository = "github.com/another/repository" });
        await Assert.ThrowsAsync<CliArgumentException>(() =>
            ReadinessConductorCommand.PrepareAsync(requestFile, TextWriter.Null, _root, cancellationToken: Ct));
        await WriteRequestAsync(request with { Revision = new string('a', 40) });
        await Assert.ThrowsAsync<CliArgumentException>(() =>
            ReadinessConductorCommand.PrepareAsync(requestFile, TextWriter.Null, _root, cancellationToken: Ct));

        await WriteRequestAsync(request);
        await ReadinessConductorCommand.PrepareAsync(requestFile, TextWriter.Null, _root, cancellationToken: Ct);
        await ReadinessConductorCommand.PrepareAsync(requestFile, TextWriter.Null, _root, cancellationToken: Ct);
        await WriteRequestAsync(request with { ContextSha256 = new string('a', 64) });
        await Assert.ThrowsAsync<ConductorObligationConflictException>(() =>
            ReadinessConductorCommand.PrepareAsync(requestFile, TextWriter.Null, _root, cancellationToken: Ct));

        var key = "owned-readiness:" + context.Key;
        var calls = 0;
        Task<RetainedReadinessResponse> Launch(string obligationId, ReadinessRequest input,
            ReadinessContext evidence, string _, CancellationToken __)
        {
            calls++;
            return Task.FromResult(new RetainedReadinessResponse(
                new ReadinessDecision(obligationId, input.Repository, input.Revision,
                    input.ContextSha256, ReadinessChoice.Recommend, "All supplied checks are green."),
                new ReadinessUsage(100, 25, 0), "codex-subscription-cli", "gpt-5.6-luna", "low",
                DateTimeOffset.UtcNow));
        }

        await File.WriteAllTextAsync(contextFile, "{}", Ct);
        await Assert.ThrowsAsync<ConductorObligationConflictException>(() =>
            ReadinessConductorCommand.DecideAsync(key, contextFile, TextWriter.Null, _root,
                launch: Launch, cancellationToken: Ct));
        await File.WriteAllBytesAsync(contextFile, contextBytes, Ct);

        var dirt = Path.Combine(workspace, "untracked.txt");
        await File.WriteAllTextAsync(dirt, "dirty", Ct);
        await Assert.ThrowsAsync<CliArgumentException>(() =>
            ReadinessConductorCommand.DecideAsync(key, contextFile, TextWriter.Null, _root,
                launch: Launch, cancellationToken: Ct));
        FileCleanup.EnsureDeleted(dirt);
        Assert.Equal(0, calls);

        using var output = new StringWriter();
        await ReadinessConductorCommand.DecideAsync(key, contextFile, output, _root,
            launch: Launch, cancellationToken: Ct);
        Assert.Equal(1, calls);
        Assert.Contains("recommend", output.ToString(), StringComparison.Ordinal);
        await ReadinessConductorCommand.DecideAsync(key, contextFile, TextWriter.Null, _root,
            launch: (_, _, _, _, _) => throw new InvalidOperationException("must replay"), cancellationToken: Ct);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Oversized_but_bounded_context_is_refused_before_launch_marker()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        await GitAsync(workspace, "init");
        await GitAsync(workspace, "remote", "add", "origin", "https://github.com/test/readiness.git");
        await GitAsync(workspace, "-c", "user.name=Test", "-c", "user.email=test@example.com",
            "commit", "--allow-empty", "-m", "fixture");
        var revision = await GitAsync(workspace, "rev-parse", "HEAD");
        var identity = RepositoryIdentity.From("https://github.com/test/readiness.git", null)!;
        await ConductorClaimStore.ClaimAsync(identity, "conductor-one", _root, cancellationToken: Ct);
        var context = new ReadinessContext(1, "large-context", identity.Value, workspace, revision,
            DateTimeOffset.Parse("2026-09-28T14:00:00Z"), Enumerable.Range(0, 28)
                .Select(i => new ReadinessEvidence($"check-{i}", "green", new string('x', 1024))).ToArray());
        var contextBytes = JsonSerializer.SerializeToUtf8Bytes(context, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.InRange(contextBytes.Length, 24 * 1024 + 1, 64 * 1024);
        var contextFile = Path.Combine(_root, "large-context.json");
        await File.WriteAllBytesAsync(contextFile, contextBytes, Ct);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(contextBytes)).ToLowerInvariant();
        var request = new ReadinessRequest(1, context.Key, identity.Value, workspace, revision,
            "conductor-one", context.ObservedAt, digest, "codex-subscription-cli", "gpt-5.6-luna", "low");
        var requestFile = Path.Combine(_root, "large-request.json");
        await File.WriteAllTextAsync(requestFile, JsonSerializer.Serialize(request,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), Ct);
        await ReadinessConductorCommand.PrepareAsync(requestFile, TextWriter.Null, _root, cancellationToken: Ct);

        var launches = 0;
        var key = "owned-readiness:" + context.Key;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReadinessConductorCommand.DecideAsync(key, contextFile, TextWriter.Null, _root,
                launch: (_, _, _, _, _) =>
                {
                    launches++;
                    throw new InvalidOperationException("must not launch");
                }, cancellationToken: Ct));
        Assert.Equal(0, launches);
        var store = new ConductorObligationStore(new FleetEventLog(Path.Combine(_root, "fleet", "events.jsonl"),
            Path.Combine(_root, "fleet", "events.1.jsonl"), 16 * 1024 * 1024),
            Path.Combine(_root, "fleet", BatonPaths.ConductorObligationsFileName));
        Assert.Equal(ConductorObligationStatus.Pending, (await store.ReadAsync(key, Ct))!.Status);
        Assert.False(File.Exists(Path.Combine(store.GetReadinessEvidenceDirectory(key), "launch.json")));
    }

    private static async Task<string> GitAsync(string workspace, params string[] args)
    {
        using var child = ChildProcessTree.Start("git", info =>
        {
            info.WorkingDirectory = workspace;
            foreach (var arg in args) info.ArgumentList.Add(arg);
        });
        var stdout = child.StandardOutput.ReadToEndAsync(Ct);
        var stderr = child.StandardError.ReadToEndAsync(Ct);
        await child.Process.WaitForExitAsync(Ct);
        var result = await stdout;
        var error = await stderr;
        Assert.True(child.Process.ExitCode == 0, error);
        return result.Trim();
    }
}
