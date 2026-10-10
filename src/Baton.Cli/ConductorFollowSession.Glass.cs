using System.Text.Json.Serialization;
using Baton.Accounting;
using Baton.Conductor;
using Baton.Queue;
using Baton.Status;

namespace Baton.Cli;

internal sealed record GlassConductorStatus(string Repository, string? Holder, string? ClaimGeneration,
    string? AttachmentId, string State, string? Adapter = null, string? Model = null,
    string? Effort = null, string? Permissions = null, string? Diagnostic = null);

internal sealed record GlassConductorsSnapshot(DateTimeOffset ObservedAt, IReadOnlyList<GlassConductorStatus> Conductors);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GlassConductorDetachRequest(
    [property: JsonRequired] string Repository,
    [property: JsonRequired] string Holder,
    [property: JsonRequired] string ClaimGeneration,
    [property: JsonRequired] string AttachmentId);

internal sealed partial class ConductorFollowSession
{
    internal static async Task<GlassConductorsSnapshot> ReadGlassStatusAsync(string root,
        CancellationToken token, Func<string, CancellationToken, Task<RepositoryIdentity?>>? resolver = null)
    {
        var rows = new List<GlassConductorStatus>();
        try
        {
            RejectLinks(root);
            var claims = await ConductorClaimStore.ListHeldClaimsAsync(root, token).ConfigureAwait(false);
            foreach (var summary in claims.Take(100))
            {
                // Local common-directory identities contain absolute paths; never send those to Glass.
                var identity = summary.Repository.StartsWith("gitdir:", StringComparison.Ordinal)
                    ? null : RepositoryIdentity.From("https://" + summary.Repository, null);
                if (identity is null || identity.Value != summary.Repository || identity.FileSlug != summary.RepositorySlug)
                {
                    rows.Add(new("Local or invalid repository", null, null, null, "unavailable",
                        Diagnostic: "This claim cannot be displayed safely."));
                    continue;
                }
                GlassConductorStatus status = new(identity.Value, SafeLabel(summary.Holder), null, null, "unavailable",
                    Diagnostic: "Claim or attachment is unreadable, stale, or mismatched.");
                try
                {
                    var claim = await ConductorClaimStore.GetClaimAsync(identity, root, token).ConfigureAwait(false);
                    if (claim?.Holder != summary.Holder) throw new CliArgumentException("Claim changed.");
                    var generation = ConductorClaimStore.GetClaimGeneration(claim);
                    if (SafeLabel(claim.Holder) != claim.Holder || SafeLabel(generation) != generation)
                        throw new CliArgumentException("Identity cannot be safely displayed.");
                    status = status with { ClaimGeneration = SafeLabel(generation) };
                    var registrationPath = Path.Combine(root, "conductor-follow", identity.FileSlug, "registration.json");
                    ConductorFollowAttachment registration;
                    try { registration = Read<ConductorFollowAttachment>(registrationPath); }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        rows.Add(status with { State = "unattached", Diagnostic = "No automatic delivery registration." });
                        continue;
                    }
                    var session = await ReadGlassSessionAsync(identity, generation, root, registration,
                        resolver, token).ConfigureAwait(false);
                    var state = Read<ConductorFollowState>(session.StatePath);
                    session.ValidateState(state);
                    var current = await ConductorClaimStore.GetClaimAsync(identity, root, token).ConfigureAwait(false);
                    if (current?.Holder != claim.Holder || ConductorClaimStore.GetClaimGeneration(current) != generation
                        || Read<ConductorFollowAttachment>(registrationPath) != registration)
                        throw new CliArgumentException("Conductor changed during status read.");
                    status = status with
                    {
                        AttachmentId = registration.Id,
                        State = !registration.Attached ? "detached" : state.Frozen ? "frozen" : "attached",
                        Adapter = state.Adapter,
                        Model = state.Model,
                        Effort = state.Effort,
                        Permissions = "File reads only; no writes, shell, network, escalation, or merge authority.",
                        Diagnostic = state.Frozen ? "Session frozen; automatic launches refused." :
                            "Registration snapshot; not evidence of a running or healthy turn.",
                    };
                }
                catch (Exception ex) when (IsRefusal(ex))
                {
                    status = status with { State = "unavailable", Diagnostic = "Claim or attachment is unreadable, stale, or mismatched." };
                }
                rows.Add(status);
            }
            if (claims.Count > 100)
                rows.Add(new("Additional claims", null, null, null, "unavailable", Diagnostic: "Display limit reached."));
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            rows.Clear();
            rows.Add(new("Conductor registry", null, null, null, "unavailable", Diagnostic: "Registry could not be verified."));
        }
        return new(DateTimeOffset.UtcNow, rows);
    }

    private static string SafeLabel(string value) => value.Length <= 256 && !value.Any(char.IsControl)
        && !value.Contains('\\') && !value.Contains(":/") && !value.StartsWith('/') ? value : "<redacted>";

    private static async Task<ConductorFollowSession> ReadGlassSessionAsync(RepositoryIdentity identity,
        string generation, string root, ConductorFollowAttachment registration,
        Func<string, CancellationToken, Task<RepositoryIdentity?>>? resolver, CancellationToken token)
    {
        // Derive the path from authoritative identity, never follow a registration-supplied path.
        var directory = Path.Combine(Path.GetFullPath(root), "conductor-follow", identity.FileSlug,
            Digest(identity.Value + "\n" + generation));
        if (registration.SessionDirectory != directory || registration.Repository != identity.Value
            || registration.ClaimGeneration != generation)
            throw new CliArgumentException("Attachment identity drifted.");
        var session = await CreateAsync(Path.Combine(directory, "request.json"), root,
            resolver ?? RepositoryIdentityResolver.TryResolveAsync, token).ConfigureAwait(false);
        session.ValidateAttachment(registration);
        return session;
    }

    internal static async Task DetachFromGlassAsync(string body, string root, CancellationToken token,
        Func<string, CancellationToken, Task<RepositoryIdentity?>>? resolver = null)
    {
        var request = Deserialize<GlassConductorDetachRequest>(body);
        if (new[] { request.Repository, request.Holder, request.ClaimGeneration, request.AttachmentId }
            .Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)))
            throw new CliArgumentException("Exact conductor identity is required.");
        var identity = RepositoryIdentity.From("https://" + request.Repository, null);
        if (identity is null || identity.Value != request.Repository || request.Repository.StartsWith("gitdir:", StringComparison.Ordinal))
            throw new CliArgumentException("Invalid repository identity.");
        var path = Path.Combine(root, "conductor-follow", identity.FileSlug, "registration.json");
        var registration = Read<ConductorFollowAttachment>(path);
        var session = await ReadGlassSessionAsync(identity, request.ClaimGeneration, root, registration,
            resolver, token).ConfigureAwait(false);
        session.ValidateState(Read<ConductorFollowState>(session.StatePath));
        // Queue admission and detach share this cutover. No session lock: an in-flight turn may finish.
        await Task.Run(() => MutexGuardedFileLock.RunUnderLock(Path.Combine(root, "queue", "queue.json"),
            QueueStore.LockNamePrefix, TimeSpan.FromSeconds(5), () =>
                ConductorClaimStore.WithCurrentClaim(identity, root, claim =>
                {
                    var current = Read<ConductorFollowAttachment>(path);
                    session.ValidateAttachment(current);
                    if (claim?.Holder != request.Holder || ConductorClaimStore.GetClaimGeneration(claim) != request.ClaimGeneration
                        || current.Holder != request.Holder || current.Id != request.AttachmentId)
                        throw new CliArgumentException("Conductor identity changed; refresh before detaching.");
                    // Preserve request, native session identity, and every piece of delivery/action evidence.
                    if (current.Attached) Write(path, current with { Attached = false });
                    return true;
                })), token).ConfigureAwait(false);
    }
}
