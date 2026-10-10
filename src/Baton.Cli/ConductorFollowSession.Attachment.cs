using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Queue;
using Baton.Runway;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli;

internal sealed record ConductorFollowAttachment(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Repository,
    [property: JsonRequired] string ClaimGeneration,
    [property: JsonRequired] string Holder,
    [property: JsonRequired] string SessionDirectory,
    [property: JsonRequired] string RequestSha256,
    [property: JsonRequired] DateTimeOffset CutoverAt,
    [property: JsonRequired] bool Attached);

internal sealed partial class ConductorFollowSession
{
    private string AttachmentPath => Path.Combine(Path.GetDirectoryName(_directory)!, "registration.json");

    internal static async Task<int> SetAttachmentAsync(string requestFile, bool attached, TextWriter output,
        string root, Func<string, CancellationToken, Task<RepositoryIdentity?>> resolver,
        CancellationToken token, ConductorFollowBroker? broker = null)
    {
        var session = await CreateAsync(requestFile, root, resolver, token, broker).ConfigureAwait(false);
        await Task.Run(() => MutexGuardedFileLock.RunUnderLock(session.StatePath, LockPrefix,
            TimeSpan.FromMinutes(6), () =>
            {
                var state = session.LoadState();
                session.ValidateState(state);
                var claim = ConductorClaimStore.GetClaimAsync(session._identity, root, token).GetAwaiter().GetResult();
                if (claim?.Holder != session._request.Holder
                    || ConductorClaimStore.GetClaimGeneration(claim) != session._generation
                    || ReadCeiling(root, session._request.Workspace) != session._ceiling)
                    throw new CliArgumentException("Follow attachment authority changed.");
                // The cutover and every halt's attachment stamp share the queue commit lock.
                QueueStore.MutateAsync(Path.Combine(root, "queue", "queue.json"), snapshot =>
                {
                    var old = File.Exists(session.AttachmentPath)
                        ? Read<ConductorFollowAttachment>(session.AttachmentPath) : null;
                    if (old is not null) session.ValidateAttachment(old);
                    if (old?.Attached == attached) return snapshot;
                    var registration = attached
                        ? session.NewAttachment()
                        : old is not null ? old with { Attached = false }
                        : throw new CliArgumentException("No explicit follow attachment exists.");
                    Write(Path.Combine(session._directory, "request.json"), session._request);
                    Write(session.AttachmentPath, registration);
                    return snapshot;
                }, token).GetAwaiter().GetResult();
                return true;
            }), token).ConfigureAwait(false);
        await output.WriteLineAsync(attached ? "Conductor follow attached." : "Conductor follow detached.").ConfigureAwait(false);
        return 0;
    }

    private void ValidateAttachment(ConductorFollowAttachment registration)
    {
        if (registration.SchemaVersion != SchemaVersion || registration.Id is not { Length: 32 }
            || !registration.Id.All(Uri.IsHexDigit) || registration.Repository != _identity.Value
            || registration.ClaimGeneration != _generation || registration.Holder != _request.Holder
            || registration.SessionDirectory != _directory || registration.CutoverAt == default
            || registration.RequestSha256 != Digest(JsonSerializer.Serialize(_request, Json)))
            throw new CliArgumentException("Retained follow attachment drifted.");
    }

    private ConductorFollowAttachment NewAttachment() =>
        new(SchemaVersion, Guid.NewGuid().ToString("N"), _identity.Value, _generation, _request.Holder,
            _directory, Digest(JsonSerializer.Serialize(_request, Json)), DateTimeOffset.UtcNow, true);

    // Called only inside the successful halt CAS, after the attachment cutover has committed.
    internal static string? AttachmentAtHalt(QueueItem source)
    {
        var identity = RepositoryIdentity.From("https://" + source.Repository, null);
        if (identity is null || source.OwnedTask is null) return null;
        var path = Path.Combine(BatonPaths.Root, "conductor-follow", identity.FileSlug, "registration.json");
        if (!File.Exists(path)) return null;
        try
        {
            var registration = Read<ConductorFollowAttachment>(path);
            return registration.SchemaVersion == SchemaVersion && registration.Attached
                && registration.Id is { Length: 32 } && registration.Id.All(Uri.IsHexDigit)
                && registration.Repository == source.Repository && registration.Holder == source.OwnedTask.ConductorHolder
                ? registration.Id : null;
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            Console.Error.WriteLine("Follow attachment unavailable; owned halt retained without delivery admission.");
            return null;
        }
    }

    internal static bool HasRegistrationFor(QueueItem source)
    {
        var identity = RepositoryIdentity.From("https://" + source.Repository, null);
        return identity is not null && File.Exists(Path.Combine(BatonPaths.Root,
            "conductor-follow", identity.FileSlug, "registration.json"));
    }

    internal static async Task<ConductorFollowResult?> NotifyAttachedAsync(QueueItem source,
        CancellationToken token, ConductorFollowBroker? broker = null,
        Func<string, CancellationToken, Task<RepositoryIdentity?>>? resolver = null)
    {
        var identity = RepositoryIdentity.From("https://" + source.Repository, null);
        if (identity is null || source.StoppedWorkJudgment is not { Key: { } key, FollowAttachmentId: { } id }) return null;
        var path = Path.Combine(BatonPaths.Root, "conductor-follow", identity.FileSlug, "registration.json");
        if (!File.Exists(path)) return null;
        var registration = Read<ConductorFollowAttachment>(path);
        if (!registration.Attached || registration.Id != id) return null;
        var session = await CreateAsync(Path.Combine(registration.SessionDirectory, "request.json"),
            BatonPaths.Root, resolver ?? RepositoryIdentityResolver.TryResolveAsync, token, broker).ConfigureAwait(false);
        session.ValidateAttachment(registration);
        var result = await session.DeliverAsync(key, token, async (obligation, ct) =>
        {
            var current = Read<ConductorFollowAttachment>(path);
            session.ValidateAttachment(current);
            if (!current.Attached || current.Id != id)
                throw new CliArgumentException("Follow attachment revoked before launch.");
            var queue = await QueueStore.LoadAsync(BatonPaths.QueueFile, ct).ConfigureAwait(false);
            var row = queue.Items.SingleOrDefault(item => item.StoppedWorkJudgment?.Key == key);
            if (queue.Held || row?.StoppedWorkJudgment?.FollowAttachmentId != id)
                throw new CliArgumentException("Follow source admission changed.");
            await session.AdmitDaemonTurnAsync(obligation, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (result.Status is "delivered" or "replayed")
        {
            var current = Read<ConductorFollowAttachment>(path);
            session.ValidateAttachment(current);
            if (!current.Attached || current.Id != id)
                throw new CliArgumentException("Follow attachment revoked after delivery.");
            var queue = await QueueStore.LoadAsync(BatonPaths.QueueFile, token).ConfigureAwait(false);
            var row = queue.Items.SingleOrDefault(item => item.StoppedWorkJudgment?.Key == key);
            if (queue.Held || row?.StoppedWorkJudgment?.FollowAttachmentId != id)
                throw new CliArgumentException("Follow source admission changed after delivery.");
        }
        return result;
    }

    private async Task AdmitDaemonTurnAsync(ConductorObligation obligation, CancellationToken token)
    {
        var settings = await DaemonSettingsStore.LoadAsync(Path.Combine(_root, BatonPaths.SettingsFileName), token).ConfigureAwait(false);
        var thresholds = settings.RunwayHold.For(_request.Adapter);
        // Persisted counters only: notification and idle reconciliation never poll a vendor.
        var snapshot = RunwaySnapshotReader.ReadFrom(Path.Combine(_root, BatonPaths.SecretPatternsDirectoryName,
            $"usage.{_request.Adapter}.json"));
        var decision = RunwayGate.Evaluate(_request.Adapter, snapshot, thresholds, DateTimeOffset.UtcNow);
        var policy = RunwayReservationPolicies.Resolve(settings.RunwayHold.ReservationPolicy);
        var location = CostLedgerLocation.ResolveForRead(_identity.FileSlug);
        var costs = policy.UsesCostLedger ? await CostLedgerStore.ReadAllAsync(location, token).ConfigureAwait(false) : [];
        var requests = new[] { new RunwayAdmissionRequest(_request.Adapter, decision.IsHold,
            decision.Reason == RunwayGate.UnmeasuredReason, decision.Reason, decision.Counters,
            thresholds.WeekHoldPct, thresholds.SessionHoldPct, thresholds.EffectiveMaxSnapshotAge.TotalHours,
            decision.SnapshotHarvestedAt, decision.HeadroomPoints,
            policy.Estimate(new(_request.Adapter, "conductor", costs)),
            obligation.ObligationId, "conductor", null, DateTimeOffset.UtcNow) };
        var entries = await RunwayAdmissionLedgerStore.ReserveAndRecordAsync(requests,
            Path.Combine(_root, BatonPaths.FleetDirectoryName, BatonPaths.RunwayAdmissionLedgerFileName), token).ConfigureAwait(false);
        if (entries.Count != 1 || entries[0].Decision != RunwayAdmissionDecisions.Admitted)
            throw new CliArgumentException("Required daemon runway admission refused; no launch admitted.");
    }
}
