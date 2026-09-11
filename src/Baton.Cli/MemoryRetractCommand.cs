using System.Globalization;
using Baton.Accounting;
using Baton.Memory;
using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// <c>baton memory retract &lt;entry-id&gt; --reason &lt;text&gt; [--repository &lt;id&gt;]</c> (#2113):
/// append ONE retraction naming an entry in the canonical per-repository store, refusing an id that
/// names no entry there and one that is already retracted.
/// </summary>
/// <remarks>
/// <para>
/// <b>It appends; it never deletes.</b> The rule is spec/baton.md §12's and <see cref="MemoryRetraction"/>
/// carries the derivation; what this verb adds is the two refusals and the report. The entry's row is
/// untouched, the retraction is a row of its own in <c>retractions.jsonl</c>, and
/// <see cref="MemoryStore.ReadResolvedAsync"/> is where the two are put together for every reader.
/// </para>
/// <para>
/// <b>The same store <c>add</c> and the importer write, through the same type</b> — the reasoning
/// <see cref="MemoryAddCommand"/> gives for not owning a second append applies unchanged.
/// </para>
/// <para>
/// <b>No manifest, and no <c>--undo</c>.</b> An add can be reversed because it should sometimes never
/// have happened; a retraction is a statement about history and reversing it would be deleting
/// history. <see cref="MemoryRetraction"/>'s remarks say what an operator does instead.
/// </para>
/// <para>A successful retraction immediately runs #2138's shared projection path. A projection
/// failure leaves the retraction committed and a durable retry obligation.</para>
/// </remarks>
public static class MemoryRetractCommand
{
    /// <param name="retractedByOverride">
    /// Test seam, for the reason <see cref="MemoryAddCommand.ExecuteAsync"/>'s <c>assertedByOverride</c>
    /// exists. Production callers pass nothing and get <see cref="MemoryLaneAssertion.Resolve()"/>.
    /// </param>
    /// <param name="repositoryProbe">
    /// Test seam for the git probe behind the default subject — the same seam, with the same
    /// asymmetry, <see cref="MemoryAddCommand.ExecuteAsync"/> documents.
    /// </param>
    public static async Task<int> ExecuteAsync(
        MemoryRetractOptions options,
        TextWriter output,
        string? retractedByOverride = null,
        Func<CancellationToken, Task<string?>>? repositoryProbe = null,
        CancellationToken cancellationToken = default,
        string? claudeHomeOverride = null,
        string? userHomeOverride = null,
        Action<string, byte[]>? projectionWriterOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        if (options.Help)
        {
            output.WriteLine(MemoryRetractOptionsParser.Usage);
            foreach (var line in MemoryRetractOptionsParser.HelpLines)
            {
                output.WriteLine(line);
            }

            return 0;
        }

        var repository = options.Repository
            ?? await ProbeRepositoryAsync(repositoryProbe, cancellationToken).ConfigureAwait(false)
            ?? throw new BatonMemoryException(
                "No repository to look for this entry in: git answers for nothing in " +
                $"'{Environment.CurrentDirectory}' and no '--repository <id>' was passed. This verb " +
                "will not guess a store — run it inside the checkout the memory is about, or name " +
                "the repository. " + MemoryRetractOptionsParser.Usage);

        var slug = FleetMemory.SlugFor(repository);
        var entriesFile = BatonPaths.MemoryEntriesFile(slug);
        var retractionsFile = BatonPaths.MemoryRetractionsFile(slug);

        // Raw read, deliberately: a resolved read would already have dropped a retracted entry, and
        // "already retracted" and "no such entry" are different refusals with different remedies.
        var stored = await MemoryStore.ReadAllAsync(entriesFile, cancellationToken).ConfigureAwait(false);
        var entry = stored.FirstOrDefault(e => string.Equals(e.Id, options.EntryId, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            output.WriteLine(
                $"REFUSED  no entry {options.EntryId} in {entriesFile}" +
                (File.Exists(entriesFile)
                    ? $" ({stored.Count} entries there)."
                    : " -- that store file does not exist."));
            output.WriteLine(
                "         Nothing was written. Check the id against 'baton memory sync --repository " +
                $"{repository}' (every projected and omitted entry is named with its id), or the " +
                "store file itself.");
            return 1;
        }

        var retractions = await MemoryStore.ReadRetractionsAsync(retractionsFile, cancellationToken).ConfigureAwait(false);
        if (retractions.FirstOrDefault(r => string.Equals(r.EntryId, entry.Id, StringComparison.Ordinal)) is { } earlier)
        {
            output.WriteLine(
                $"REFUSED  entry {entry.Id} is already retracted -- by {earlier.RetractedBy} at " +
                $"{earlier.RetractedAtUtc.ToString("O", CultureInfo.InvariantCulture)}, reason: {earlier.Reason}");
            output.WriteLine(
                "         Nothing was written: an entry is retracted at most once, and there is no " +
                "un-retract. If the fact holds again, 'baton memory add' it as it now stands.");
            return 1;
        }

        var retraction = MemoryRetraction.Create(
            entry.Id, entry.Repository, options.Reason, retractedByOverride ?? MemoryLaneAssertion.Resolve(), DateTime.UtcNow);

        var appended = await MemoryStore.AppendRetractionsAndGetAppendedAsync(
            [retraction], retractionsFile, cancellationToken).ConfigureAwait(false);
        if (appended.Count == 0)
        {
            var concurrent = (await MemoryStore.ReadRetractionsAsync(retractionsFile, cancellationToken).ConfigureAwait(false))
                .First(r => string.Equals(r.EntryId, entry.Id, StringComparison.Ordinal));
            output.WriteLine(
                $"REFUSED  entry {entry.Id} was concurrently retracted by {concurrent.RetractedBy} at " +
                $"{concurrent.RetractedAtUtc.ToString("O", CultureInfo.InvariantCulture)}, reason: {concurrent.Reason}");
            output.WriteLine("         Nothing was appended by this call, so it created no projection obligation.");
            return 1;
        }

        output.WriteLine($"RETRACTED {entry.Id} ({MemoryJsonNames.Of(entry.Kind)}) in {entriesFile}");
        output.WriteLine($"         reason      {retraction.Reason}");
        output.WriteLine($"         retractedBy {retraction.RetractedBy}");
        output.WriteLine($"         row         {retractionsFile}");
        output.WriteLine(
            "         The entry's own row is untouched: history is never deleted. 'baton memory audit' " +
            "lists this retraction with its reason.");
        await MemoryProjectionTrigger.ProjectAfterCanonicalWriteAsync(
            repository,
            output,
            claudeHomeOverride,
            userHomeOverride,
            projectionWriterOverride,
            cancellationToken).ConfigureAwait(false);

        return 0;
    }

    private static async Task<string?> ProbeRepositoryAsync(
        Func<CancellationToken, Task<string?>>? probe, CancellationToken cancellationToken)
    {
        if (probe is not null)
        {
            return await probe(cancellationToken).ConfigureAwait(false);
        }

        var identity = await RepositoryIdentityResolver
            .TryResolveAsync(Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false);

        return identity?.Value;
    }
}
