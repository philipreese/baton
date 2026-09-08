using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// <c>baton trust</c> (#1166): decision 0004's project ceiling has no interactive first-use trust
/// prompt in a headless dispatch, so this is the explicit operator verb the scope ruling calls for —
/// list/register/revoke against <see cref="ProjectCeilingStore"/>. Not a
/// <see cref="CommandResult"/>/<see cref="FlowStateReporter"/> command (no workflow pump, no projected
/// state to report): joins <c>watch</c>/<c>keep</c>/<c>unkeep</c> in <c>Program.cs</c>'s own carve-out
/// for exactly that shape.
/// </summary>
public static class TrustCommand
{
    public static Task<int> ExecuteAsync(TrustOptions options, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();

        return options.Mode switch
        {
            TrustMode.List => Task.FromResult(List(output)),
            TrustMode.Register => Task.FromResult(Register(options, output)),
            TrustMode.Revoke => Task.FromResult(Revoke(options, output)),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }

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
            output.WriteLine($"{path}  {ceiling.Describe()}{provenance}");
        }

        return 0;
    }

    private static int Register(TrustOptions options, TextWriter output)
    {
        ProjectCeilingStore.Set(options.ProjectPath!, options.Ceiling!, ProjectCeilingStore.DefaultPath);
        output.WriteLine($"Trusted '{options.ProjectPath}' with ceiling {options.Ceiling!.Describe()}.");
        return 0;
    }

    private static int Revoke(TrustOptions options, TextWriter output)
    {
        var revocation = ProjectCeilingStore.Revoke(options.ProjectPath!, ProjectCeilingStore.DefaultPath);
        if (!revocation.Revoked)
        {
            output.WriteLine($"No ceiling was recorded for '{options.ProjectPath}' — nothing to revoke.");
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

}
