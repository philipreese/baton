using System.Text.Json;
using Baton.Accounting;
using Baton.Conductor;
using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// Execution handlers for <c>baton conductor</c> verbs (#2296): claim, list, release, and takeover.
/// </summary>
public static class ConductorCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static Task<int> ExecuteAsync(
        ConductorOptions options,
        TextWriter stdout,
        string? batonRoot = null,
        Func<string, CancellationToken, Task<RepositoryIdentity?>>? repositoryResolver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stdout);

        var resolver = repositoryResolver ?? RepositoryIdentityResolver.TryResolveAsync;
        var root = batonRoot ?? BatonPaths.Root;

        return options.Verb switch
        {
            ConductorVerb.Claim => ClaimAsync(options, stdout, root, resolver, cancellationToken),
            ConductorVerb.List => ListAsync(options, stdout, root, cancellationToken),
            ConductorVerb.Release => ReleaseAsync(options, stdout, root, resolver, cancellationToken),
            ConductorVerb.Takeover => TakeoverAsync(options, stdout, root, resolver, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }

    private static async Task<int> ClaimAsync(
        ConductorOptions options,
        TextWriter stdout,
        string batonRoot,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> repositoryResolver,
        CancellationToken cancellationToken)
    {
        var workspace = ResolveWorkspacePath(options.Workspace);
        var identity = await ResolveRepositoryIdentityAsync(workspace, repositoryResolver, cancellationToken).ConfigureAwait(false);

        var existing = await ConductorClaimStore.GetClaimAsync(identity, batonRoot, cancellationToken).ConfigureAwait(false);
        var wasAlreadyHeldBySame = existing is not null && string.Equals(existing.Holder, options.Holder, StringComparison.Ordinal);

        var record = await ConductorClaimStore.ClaimAsync(identity, options.Holder!, batonRoot, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (wasAlreadyHeldBySame)
        {
            stdout.WriteLine($"Repository '{identity.Value}' is already claimed by conductor '{record.Holder}'.");
        }
        else
        {
            stdout.WriteLine($"Claimed repository '{identity.Value}' for conductor '{record.Holder}'.");
        }

        return 0;
    }

    private static async Task<int> TakeoverAsync(
        ConductorOptions options,
        TextWriter stdout,
        string batonRoot,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> repositoryResolver,
        CancellationToken cancellationToken)
    {
        var workspace = ResolveWorkspacePath(options.Workspace);
        var identity = await ResolveRepositoryIdentityAsync(workspace, repositoryResolver, cancellationToken).ConfigureAwait(false);

        var (record, displacedHolder) = await ConductorClaimStore.TakeoverAsync(
            identity, options.Holder!, options.Reason!, batonRoot, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        stdout.WriteLine($"Conductor '{record.Holder}' took over repository '{identity.Value}' from '{displacedHolder}'. Reason: {options.Reason}");
        return 0;
    }

    private static async Task<int> ReleaseAsync(
        ConductorOptions options,
        TextWriter stdout,
        string batonRoot,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> repositoryResolver,
        CancellationToken cancellationToken)
    {
        var workspace = ResolveWorkspacePath(options.Workspace);
        var identity = await ResolveRepositoryIdentityAsync(workspace, repositoryResolver, cancellationToken).ConfigureAwait(false);

        await ConductorClaimStore.ReleaseAsync(
            identity, options.Holder!, options.Reason!, batonRoot, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        stdout.WriteLine($"Released repository '{identity.Value}' from conductor '{options.Holder}'. Reason: {options.Reason}");
        return 0;
    }

    private static async Task<int> ListAsync(
        ConductorOptions options,
        TextWriter stdout,
        string batonRoot,
        CancellationToken cancellationToken)
    {
        var claims = await ConductorClaimStore.ListHeldClaimsAsync(batonRoot, cancellationToken).ConfigureAwait(false);

        if (options.Json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(claims, JsonOptions));
            return 0;
        }

        if (claims.Count == 0)
        {
            stdout.WriteLine("No active conductor claims.");
            return 0;
        }

        foreach (var claim in claims)
        {
            stdout.WriteLine(claim.Repository);
            stdout.WriteLine($"  holder: {claim.Holder}");
            stdout.WriteLine($"  acquired: {claim.AcquiredAt:yyyy-MM-ddTHH:mm:ssZ}");
            if (claim.Takeover is not null)
            {
                stdout.WriteLine($"  takeover: from {claim.Takeover.DisplacedHolder} at {claim.Takeover.TakenOverAt:yyyy-MM-ddTHH:mm:ssZ}; reason: {claim.Takeover.Reason}");
            }
        }

        return 0;
    }

    private static string ResolveWorkspacePath(string? rawWorkspace)
    {
        var path = string.IsNullOrWhiteSpace(rawWorkspace) ? Environment.CurrentDirectory : rawWorkspace;
        return Path.GetFullPath(path);
    }

    private static async Task<RepositoryIdentity> ResolveRepositoryIdentityAsync(
        string workspace,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> repositoryResolver,
        CancellationToken cancellationToken)
    {
        var identity = await repositoryResolver(workspace, cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            throw new CliArgumentException(
                $"Workspace '{workspace}' does not resolve to a canonical repository identity. "
                + "A path that cannot produce a canonical repository identity is refused.");
        }

        return identity;
    }
}
