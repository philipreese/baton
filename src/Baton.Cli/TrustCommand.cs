using Baton.Accounting;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// <c>baton trust</c> (#1166): decision 0004's project ceiling has no interactive first-use trust
/// prompt in a headless dispatch, so this is the explicit operator verb the scope ruling calls for —
/// list/register/revoke/forget against <see cref="ProjectCeilingStore"/>. Not a
/// <see cref="CommandResult"/>/<see cref="FlowStateReporter"/> command (no workflow pump, no projected
/// state to report): joins <c>watch</c>/<c>keep</c>/<c>unkeep</c> in <c>Program.cs</c>'s own carve-out
/// for exactly that shape.
/// </summary>
public static class TrustCommand
{
    /// <param name="options">The parsed verb.</param>
    /// <param name="output">Where the verb's lines go.</param>
    /// <param name="probe">
    /// Test seam for the register path's repository-identity lookup (#2121) — the same injected-probe
    /// shape <c>InheritedProjectCeiling</c> takes. Null uses git. Only consulted when the store holds a
    /// tombstone, so a register into a store with none spawns nothing.
    /// </param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    public static Task<int> ExecuteAsync(
        TrustOptions options,
        TextWriter output,
        Func<string, CancellationToken, Task<RepositoryIdentity?>>? probe = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();

        return options.Mode switch
        {
            TrustMode.List => Task.FromResult(List(output)),
            TrustMode.Register => RegisterAsync(options, output, probe ?? RepositoryIdentityResolver.TryResolveAsync, cancellationToken),
            TrustMode.Revoke => Task.FromResult(Revoke(options, output)),
            TrustMode.Forget => Task.FromResult(Forget(options, output)),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }

    /// <summary>The <see cref="ExecuteAsync(TrustOptions, TextWriter, Func{string, CancellationToken, Task{RepositoryIdentity?}}?, CancellationToken)"/> shape with git as the probe — <c>Program.cs</c>'s call.</summary>
    public static Task<int> ExecuteAsync(TrustOptions options, TextWriter output, CancellationToken cancellationToken) =>
        ExecuteAsync(options, output, probe: null, cancellationToken);

    private static int List(TextWriter output)
    {
        var ceilings = ProjectCeilingStore.Load(ProjectCeilingStore.DefaultPath);
        if (ceilings.Count == 0)
        {
            output.WriteLine("No project ceilings recorded.");
            return 0;
        }

        foreach (var (path, ceiling) in ceilings.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            // #2076: an inherited entry says where it came from. An operator reading this list is
            // deciding what to revoke, and "Baton copied this from the repository root" and "I typed
            // this" are different facts about the same line — the second half is omitted, rather than
            // printed empty, for the entries an operator did type.
            var provenance = ceiling.InheritedFrom is { Length: > 0 } source ? $"  (inherited from {source})" : string.Empty;
            // #2121: a tombstone is listed as what it is, not as `none`. The operator reading this is
            // deciding whether to re-trust, and "revoked" is the state `baton trust <path>` would clear.
            var state = ceiling.RevokedAt is { } revokedAt ? $"revoked {revokedAt:u}" : ceiling.Describe();
            output.WriteLine($"{path}  {state}{provenance}");
        }

        return 0;
    }

    private static async Task<int> RegisterAsync(
        TrustOptions options,
        TextWriter output,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> probe,
        CancellationToken cancellationToken)
    {
        ProjectCeilingStore.Set(options.ProjectPath!, options.Ceiling!, ProjectCeilingStore.DefaultPath);
        output.WriteLine($"Trusted '{options.ProjectPath}' with ceiling {options.Ceiling!.Describe()}.");

        // #2121: trusting any path of a revoked repository ends the revocation for the whole repository
        // — the tombstones on its other paths would otherwise linger in `--list` reading as a refusal
        // that InheritedProjectCeiling no longer applies (a live source now matches). The probe runs only
        // when there is a tombstone to clear, and each surviving tombstone whose directory still exists
        // is probed once; one whose directory is gone cannot be matched and stays listed.
        var tombstones = ProjectCeilingStore.Load(ProjectCeilingStore.DefaultPath)
            .Where(pair => pair.Value.IsRevoked && Directory.Exists(pair.Key))
            .Select(pair => pair.Key)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tombstones.Count == 0
            || await probe(options.ProjectPath!, cancellationToken).ConfigureAwait(false) is not { } identity)
        {
            return 0;
        }

        var sameRepository = new List<string>();
        foreach (var tombstone in tombstones)
        {
            var candidate = await probe(tombstone, cancellationToken).ConfigureAwait(false);
            if (candidate is not null && string.Equals(candidate.Value, identity.Value, StringComparison.Ordinal))
            {
                sameRepository.Add(tombstone);
            }
        }

        foreach (var cleared in ProjectCeilingStore.ClearRevocations(sameRepository, ProjectCeilingStore.DefaultPath))
        {
            output.WriteLine($"Cleared the revocation of '{cleared}' (same repository); it inherits again on its next dispatch or queue add.");
        }

        return 0;
    }

    private static int Revoke(TrustOptions options, TextWriter output)
    {
        var revocation = ProjectCeilingStore.Revoke(options.ProjectPath!, ProjectCeilingStore.DefaultPath);
        if (!revocation.Revoked)
        {
            // #2121: "nothing to revoke" has two causes, and `--list` shows one of them as `revoked
            // <timestamp>` — saying "no ceiling is recorded" for that one would contradict the list the
            // operator reads next. The tombstone's timestamp is the one the first revoke wrote; the
            // store left it alone.
            output.WriteLine(
                ProjectCeilingStore.TryGetRecord(options.ProjectPath!, ProjectCeilingStore.DefaultPath) is { RevokedAt: { } revokedAt }
                    ? $"'{options.ProjectPath}' is already revoked ({revokedAt:u}) - nothing to revoke."
                    : $"No ceiling is recorded for '{options.ProjectPath}' — nothing to revoke.");
            return 0;
        }

        output.WriteLine($"Revoked the ceiling for '{options.ProjectPath}'.");
        // #2076: the entries that were copied from this one go with it (ProjectCeilingStore.Revoke says
        // why), and each is named so the operator learns which worktrees just lost their ceiling.
        foreach (var derived in revocation.CascadedPaths)
        {
            output.WriteLine($"Also revoked '{derived}', which had inherited it.");
        }

        return 0;
    }

    /// <summary>
    /// <c>--forget</c> (#2121): the only verb whose purpose is removal (<c>--ceiling</c> re-trust also
    /// deletes same-repository tombstones, but only by replacing them with a live record — spec/baton.md
    /// §9). The line names the canonical path
    /// and what the record was — a tombstone with its timestamp, or a live ceiling — so the operator
    /// learns which state just left the store; a forgotten tombstone means the repository reads as
    /// never trusted again, which is the fallback <c>queue add --issue</c> takes (spec/baton.md §9).
    /// </summary>
    private static int Forget(TrustOptions options, TextWriter output)
    {
        var key = ProjectCeilingStore.CanonicalKey(options.ProjectPath!);
        var forgotten = ProjectCeilingStore.Forget(options.ProjectPath!, ProjectCeilingStore.DefaultPath);
        if (forgotten is null)
        {
            output.WriteLine($"No ceiling is recorded for '{key}' — nothing to forget.");
            return 0;
        }

        var was = forgotten.RevokedAt is { } revokedAt ? $"revoked {revokedAt:u}" : $"ceiling {forgotten.Describe()}";
        var provenance = forgotten.InheritedFrom is { Length: > 0 } source ? $", inherited from {source}" : string.Empty;
        output.WriteLine($"Forgot '{key}' (was {was}{provenance}); it now reads as never trusted.");
        return 0;
    }
}
