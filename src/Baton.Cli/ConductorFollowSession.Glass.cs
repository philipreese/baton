using System.Text.Json.Serialization;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Queue;
using Baton.Status;

namespace Baton.Cli;

internal sealed record GlassConductorStatus(string Repository, string? Holder, string? ClaimGeneration,
    string? AttachmentId, string State, string? Adapter = null, string? Model = null,
    string? Effort = null, string? Permissions = null, string? Diagnostic = null, bool ResumeEligible = false,
    bool StopEligible = false, bool TakeoverEligible = false, string? DestinationAddress = null,
    string? RetainedHolder = null, string? RetainedGeneration = null, bool HistoricalProvider = false,
    IReadOnlyList<GlassHostedControlResult>? Controls = null, IReadOnlyList<GlassIssuedActionStatus>? Actions = null,
    bool Held = false, long ControlRevision = 0, bool HoldEligible = false, bool UnholdEligible = false,
    string? AdmissionWait = null, bool QueueHeld = false, ConductorHostedControlReceipt? HoldControl = null);

internal sealed record GlassHostedControlResult(ConductorHostedControlReceipt Receipt, string Cleanup);
internal sealed record GlassIssuedActionStatus(string Tag, string State, string? Reason, string? NextTrigger, string Holder);

internal sealed record GlassConductorsSnapshot(DateTimeOffset ObservedAt, IReadOnlyList<GlassConductorStatus> Conductors);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GlassConductorDetachRequest(
    [property: JsonRequired] string Repository,
    [property: JsonRequired] string Holder,
    [property: JsonRequired] string ClaimGeneration,
    [property: JsonRequired] string AttachmentId);

internal sealed partial class ConductorFollowSession
{
    private static readonly TimeSpan GlassLockTimeout = TimeSpan.FromSeconds(1);

    internal static async Task<GlassConductorsSnapshot> ReadGlassStatusAsync(string root,
        CancellationToken token, Func<string, CancellationToken, Task<RepositoryIdentity?>>? resolver = null)
    {
        var rows = new List<GlassConductorStatus>();
        var callerToken = token;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        token = budget.Token;
        try
        {
            RejectLinks(root);
            var claims = await ConductorClaimStore.ListRetainedClaimsAsync(root, token, GlassLockTimeout).ConfigureAwait(false);
            foreach (var summary in claims.Take(100))
            {
                callerToken.ThrowIfCancellationRequested();
                // Local common-directory identities contain absolute paths; never send those to Glass.
                var identity = summary.Repository.StartsWith("gitdir:", StringComparison.Ordinal)
                    ? null : RepositoryIdentity.From("https://" + summary.Repository, null);
                if (identity is null || identity.Value != summary.Repository || identity.FileSlug != summary.RepositorySlug)
                {
                    rows.Add(new("Local or invalid repository", null, null, null, "unavailable",
                        Diagnostic: "This claim cannot be displayed safely."));
                    continue;
                }
                GlassConductorStatus status = new(identity.Value, summary.Holder is null ? null : SafeLabel(summary.Holder), null, null, "unavailable",
                    Diagnostic: "Claim or attachment is unreadable, stale, or mismatched.");
                if (token.IsCancellationRequested)
                {
                    rows.Add(status with { Diagnostic = "Status inspection budget exhausted; refresh to inspect this conductor." });
                    continue;
                }
                try
                {
                    var claim = await ConductorClaimStore.GetClaimAsync(identity, root, token, GlassLockTimeout).ConfigureAwait(false);
                    if (claim is null || claim.Holder != summary.Holder) throw new CliArgumentException("Claim changed.");
                    var generation = ConductorClaimStore.GetClaimGeneration(claim);
                    if (claim.Holder is not null && SafeLabel(claim.Holder) != claim.Holder || SafeLabel(generation) != generation)
                        throw new CliArgumentException("Identity cannot be safely displayed.");
                    status = status with { ClaimGeneration = SafeLabel(generation) };
                    var controls = (claim.Transitions ?? []).Where(t => t.Control is not null)
                        .Select(t => t.Control!).TakeLast(20).ToArray();
                    var activity = await ReadGlassActionsAsync(identity.Value, root, token).ConfigureAwait(false);
                    status = status with
                    {
                        DestinationAddress = claim.DestinationAddress,
                        Held = claim.Held,
                        ControlRevision = claim.ControlRevision,
                        HoldControl = claim.Held ? controls.LastOrDefault(c => c.Operation == "hold" && c.ResultGeneration == generation) : null,
                        Controls = controls.Select(c => new GlassHostedControlResult(c, ControlCleanupState(identity, root, c))).ToArray(),
                        Actions = activity.Actions,
                        QueueHeld = activity.QueueHeld,
                    };
                    if (claim.Holder is null)
                    {
                        rows.Add(status with { State = "released", Diagnostic = "Claim released; retained control history only." });
                        continue;
                    }
                    var registrationPath = Path.Combine(root, "conductor-follow", identity.FileSlug, "registration.json");
                    ConductorFollowAttachment registration;
                    try { registration = Read<ConductorFollowAttachment>(registrationPath); }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        rows.Add(status with { State = "unattached", Diagnostic = "No automatic delivery registration." });
                        continue;
                    }
                    var displaced = registration.Holder != claim.Holder || registration.ClaimGeneration != generation;
                    var transferred = displaced && controls.Any(c => c.Operation == "takeover" && MatchesControlTarget(registration, c));
                    if (claim.Stopped || displaced && controls.Any(c => c.Operation is "stop" or "takeover" && MatchesControlTarget(registration, c)))
                    {
                        ValidateControlRegistration(identity, root, registration, registration.Holder,
                            registration.ClaimGeneration, registration.Id);
                        var historical = Read<ConductorFollowState>(Path.Combine(registration.SessionDirectory, "session.json"));
                        status = status with
                        {
                            State = claim.Stopped ? "stopped" : transferred ? "taken-over" : "prior-acquisition",
                            AttachmentId = registration.Id,
                            RetainedHolder = registration.Holder,
                            RetainedGeneration = registration.ClaimGeneration,
                            HistoricalProvider = true,
                            Adapter = historical.Adapter,
                            Model = historical.Model,
                            Effort = historical.Effort,
                            TakeoverEligible = claim.Stopped,
                            Diagnostic = claim.Stopped ? "Hosted acquisition stopped; no Resume path."
                                : transferred ? "Ownership transferred; no replacement hosted session started."
                                : "Retained provider from a prior acquisition; no hosted session attached to the current acquisition.",
                        };
                        var finalClaim = await ConductorClaimStore.GetClaimAsync(identity, root, token, GlassLockTimeout).ConfigureAwait(false);
                        if (finalClaim?.Holder != claim.Holder || finalClaim.Stopped != claim.Stopped
                            || ConductorClaimStore.GetClaimGeneration(finalClaim) != generation
                            || Read<ConductorFollowAttachment>(registrationPath) != registration)
                        {
                            status = status with { StopEligible = false, TakeoverEligible = false };
                            throw new CliArgumentException("Conductor changed during status read.");
                        }
                        rows.Add(status);
                        continue;
                    }
                    ValidateControlRegistration(identity, root, registration, claim.Holder!, generation, registration.Id);
                    // Revocation eligibility depends on exact authority identity, not delivery recovery or trust.
                    status = status with
                    {
                        AttachmentId = registration.Id,
                        StopEligible = true,
                        TakeoverEligible = true,
                        HoldEligible = !claim.Held,
                        UnholdEligible = claim.Held,
                        AdmissionWait = claim.Held ? "Unhold this acquisition; valid pending work continues on the next scheduler reconciliation."
                            : "Pending work waits for current source, head, grants, opt-in and runway admission."
                    };
                    var session = await ReadGlassSessionAsync(identity, generation, root, registration,
                        resolver, token).ConfigureAwait(false);
                    var state = Read<ConductorFollowState>(session.StatePath);
                    session.ValidateState(state);
                    var resumeEligible = false;
                    string? resumeDiagnostic = null;
                    if (!registration.Attached && !state.Frozen)
                    {
                        try
                        {
                            resumeEligible = await Task.Run(() => MutexGuardedFileLock.RunUnderLock(
                                session.StatePath, LockPrefix, GlassLockTimeout, () =>
                                {
                                    session.ValidateResumeEvidence();
                                    return true;
                                }), token).ConfigureAwait(false);
                        }
                        catch (RetainedInspectionBoundException ex) { resumeDiagnostic = ex.Message; }
                        catch (Exception ex) when (IsRefusal(ex)) { resumeEligible = false; }
                    }
                    var current = await ConductorClaimStore.GetClaimAsync(identity, root, token, GlassLockTimeout).ConfigureAwait(false);
                    if (current?.Holder != claim.Holder || current.Stopped != claim.Stopped || current.ControlRevision != claim.ControlRevision || ConductorClaimStore.GetClaimGeneration(current) != generation
                        || Read<ConductorFollowAttachment>(registrationPath) != registration)
                    {
                        status = status with { StopEligible = false, TakeoverEligible = false, HoldEligible = false, UnholdEligible = false };
                        throw new CliArgumentException("Conductor changed during status read.");
                    }
                    status = status with
                    {
                        AttachmentId = registration.Id,
                        State = !registration.Attached ? "detached" : state.Frozen ? "frozen" : claim.Held ? "held" : "attached",
                        Adapter = state.Adapter,
                        Model = state.Model,
                        Effort = state.Effort,
                        ResumeEligible = resumeEligible,
                        Permissions = "File reads only; no writes, shell, network, escalation, or merge authority.",
                        Diagnostic = state.Frozen ? "Session frozen; automatic launches refused." :
                            !registration.Attached && !resumeEligible ? resumeDiagnostic ?? "Resume unavailable: session busy or retained delivery evidence could not be verified." :
                            "Registration snapshot; not evidence of a running or healthy turn.",
                    };
                }
                catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
                {
                    status = status with { State = "unavailable", Diagnostic = "Status inspection budget exhausted; refresh to inspect this conductor." };
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
        catch (Exception ex) when (IsRefusal(ex) || ex is OperationCanceledException && !callerToken.IsCancellationRequested)
        {
            rows.Add(new("Conductor registry", null, null, null, "unavailable", Diagnostic: "Registry could not be verified."));
        }
        return new(DateTimeOffset.UtcNow, rows);
    }

    private static string SafeLabel(string value) => ConductorClaimStore.IsSafeHostedHolderLabel(value) ? value : "<redacted>";

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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        ConductorFollowSession session;
        try
        {
            session = await CreateAsync(Path.Combine(directory, "request.json"), root,
                resolver ?? RepositoryIdentityResolver.TryResolveAsync, timeout.Token,
                claimLockTimeout: GlassLockTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new CliArgumentException("Conductor verification is busy; refresh before retrying.");
        }
        session.ValidateAttachment(registration);
        return session;
    }

    private void ValidateResumeEvidence()
    {
        var state = Read<ConductorFollowState>(StatePath);
        ValidateState(state);
        if (state.Frozen) throw new CliArgumentException("Frozen session cannot resume automatic delivery.");
        InspectRetainedSession(state, repair: false);
    }

    internal static async Task<GlassConductorStatus> ResumeFromGlassAsync(string body, string root,
        CancellationToken token, Func<string, CancellationToken, Task<RepositoryIdentity?>>? resolver = null)
    {
        var (request, identity) = ParseGlassIdentity(body);
        var path = Path.Combine(root, "conductor-follow", identity.FileSlug, "registration.json");
        var registration = Read<ConductorFollowAttachment>(path);
        var session = await ReadGlassSessionAsync(identity, request.ClaimGeneration, root, registration,
            resolver, token).ConfigureAwait(false);
        return await Task.Run(() => MutexGuardedFileLock.RunUnderLock(session.StatePath, LockPrefix,
            GlassLockTimeout, () =>
            {
                // Never wait for a vendor turn while holding queue admission. Keep the session
                // exclusive through validation and the queue/claim cutover.
                return MutexGuardedFileLock.RunUnderLock(Path.Combine(root, "queue", "queue.json"),
                    QueueStore.LockNamePrefix, GlassLockTimeout, () =>
                        ConductorClaimStore.WithCurrentClaim(identity, root, claim =>
                        {
                            token.ThrowIfCancellationRequested();
                            RejectLinks(Path.Combine(root, identity.FileSlug, BatonPaths.ConductorClaimFileName));
                            var current = Read<ConductorFollowAttachment>(path);
                            session.ValidateAttachment(current);
                            if (!ConductorClaimStore.IsCurrentHostedAuthority(claim, request.Holder, request.ClaimGeneration)
                                || current.Holder != request.Holder || current.Id != request.AttachmentId || current.Attached)
                                throw new CliArgumentException("Conductor identity changed; refresh before resuming.");
                            if (Read<ConductorFollowRequest>(Path.Combine(session._directory, "request.json")) != session._request
                                || ReadCeiling(root, session._request.Workspace) != session._ceiling)
                                throw new CliArgumentException("Follow attachment authority changed.");
                            session.ValidateResumeEvidence();
                            var next = session.NewAttachment();
                            Write(path, next);
                            return new GlassConductorStatus(identity.Value, next.Holder, next.ClaimGeneration, next.Id, "attached");
                        }, GlassLockTimeout));
            }), token).ConfigureAwait(false);
    }

    private static (GlassConductorDetachRequest Request, RepositoryIdentity Identity) ParseGlassIdentity(string body)
    {
        var request = Deserialize<GlassConductorDetachRequest>(body);
        if (new[] { request.Repository, request.Holder, request.ClaimGeneration, request.AttachmentId }
            .Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)))
            throw new CliArgumentException("Exact conductor identity is required.");
        var identity = RepositoryIdentity.From("https://" + request.Repository, null);
        if (identity is null || identity.Value != request.Repository || request.Repository.StartsWith("gitdir:", StringComparison.Ordinal))
            throw new CliArgumentException("Invalid repository identity.");
        return (request, identity);
    }

    private static void ValidateControlRegistration(RepositoryIdentity identity, string root,
        ConductorFollowAttachment registration, string holder, string generation, string? attachmentId)
    {
        var directory = Path.Combine(Path.GetFullPath(root), "conductor-follow", identity.FileSlug,
            Digest(identity.Value + "\n" + generation));
        if (registration.SchemaVersion != SchemaVersion || registration.Id is not { Length: 32 }
            || !registration.Id.All(Uri.IsHexDigit) || registration.Id != attachmentId
            || registration.Repository != identity.Value || registration.Holder != holder
            || registration.ClaimGeneration != generation || registration.SessionDirectory != directory
            || registration.CutoverAt == default || registration.RequestSha256 is not { Length: 64 })
            throw new CliArgumentException("Exact hosted registration could not be verified.");
        var request = Read<ConductorFollowRequest>(Path.Combine(directory, "request.json"), 64 * 1024);
        var state = Read<ConductorFollowState>(Path.Combine(directory, "session.json"));
        if (request.Repository != identity.Value || request.Holder != holder
            || Digest(System.Text.Json.JsonSerializer.Serialize(request, Json)) != registration.RequestSha256
            || state.SchemaVersion != SchemaVersion || state.Repository != identity.Value
            || state.Holder != holder || state.ClaimGeneration != generation)
            throw new CliArgumentException("Exact hosted session identity could not be verified.");
    }

    internal static Action? AfterHostedControlFence { get; set; }

    internal static async Task<GlassHostedControlResult> ControlFromGlassAsync(string body, string root,
        string issuer, bool takeover, CancellationToken token) =>
        await ControlFromGlassAsync(body, root, issuer, takeover ? "takeover" : "stop", token).ConfigureAwait(false);

    internal static async Task<GlassHostedControlResult> ControlFromGlassAsync(string body, string root,
        string issuer, string operation, CancellationToken token)
    {
        var request = Deserialize<ConductorHostedControlRequest>(body);
        var identity = RepositoryIdentity.From("https://" + request.Repository, null);
        if (identity is null || identity.Value != request.Repository || request.Repository.StartsWith("gitdir:", StringComparison.Ordinal))
            throw new CliArgumentException("Invalid repository identity.");
        RejectLinks(root);
        RejectLinks(Path.Combine(root, identity.FileSlug, BatonPaths.ConductorClaimFileName));
        return await Task.Run(() => MutexGuardedFileLock.RunUnderLock(Path.Combine(root, "queue", "queue.json"),
            QueueStore.LockNamePrefix, GlassLockTimeout, () =>
            {
                var receipt = ConductorClaimStore.ApplyHostedControl(identity, root, request, issuer, operation, _ =>
                {
                    token.ThrowIfCancellationRequested();
                    var registration = Read<ConductorFollowAttachment>(Path.Combine(root, "conductor-follow", identity.FileSlug, "registration.json"));
                    ValidateControlRegistration(identity, root, registration, request.Holder, request.ClaimGeneration, request.AttachmentId);
                }, GlassLockTimeout);
                try
                {
                    CleanupHostedControl(identity, root, receipt);
                }
                catch (Exception ex) when (IsRefusal(ex))
                {
                    return new GlassHostedControlResult(receipt, "pending: revocation is durable; exact registration cleanup requires reconciliation");
                }
                return new GlassHostedControlResult(receipt, "complete");
            }), token).ConfigureAwait(false);
    }

    private static bool MatchesControlTarget(ConductorFollowAttachment registration, ConductorHostedControlReceipt receipt) =>
        registration.Repository == receipt.Request.Repository && registration.Holder == receipt.Request.Holder
        && registration.ClaimGeneration == receipt.Request.ClaimGeneration && registration.Id == receipt.Request.AttachmentId;

    private static void CleanupHostedControl(RepositoryIdentity identity, string root, ConductorHostedControlReceipt receipt)
    {
        if (receipt.Operation is "hold" or "unhold") return;
        var path = Path.Combine(root, "conductor-follow", identity.FileSlug, "registration.json");
        ConductorFollowAttachment registration;
        try { registration = Read<ConductorFollowAttachment>(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }
        if (!MatchesControlTarget(registration, receipt)) return;
        if (registration.Attached)
        {
            AfterHostedControlFence?.Invoke();
            Write(path, registration with { Attached = false });
        }
    }

    private static string ControlCleanupState(RepositoryIdentity identity, string root, ConductorHostedControlReceipt receipt)
    {
        if (receipt.Operation is "hold" or "unhold") return "complete";
        try
        {
            var registration = Read<ConductorFollowAttachment>(Path.Combine(root, "conductor-follow", identity.FileSlug, "registration.json"));
            return MatchesControlTarget(registration, receipt) && registration.Attached ? "pending" : "complete";
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return "complete"; }
        catch (Exception ex) when (IsRefusal(ex)) { return "uncertain"; }
    }

    internal static async Task ReconcileHostedControlCleanupAsync(string root, CancellationToken token)
    {
        IReadOnlyList<ConductorClaimRecord> claims;
        try { claims = await ConductorClaimStore.ListRetainedClaimsAsync(root, token, GlassLockTimeout).ConfigureAwait(false); }
        catch (Exception ex) when (IsRefusal(ex))
        {
            Console.Error.WriteLine("Hosted control cleanup registry unavailable; retained revocation receipts require reconciliation.");
            return;
        }
        foreach (var summary in claims.Where(c => c.Transitions!.Any(t => t.Control is not null)))
        {
            var identity = RepositoryIdentity.From("https://" + summary.Repository, null);
            if (identity is null) continue;
            try
            {
                await Task.Run(() => MutexGuardedFileLock.RunUnderLock(Path.Combine(root, "queue", "queue.json"),
                QueueStore.LockNamePrefix, GlassLockTimeout, () => ConductorClaimStore.WithCurrentClaim(identity, root, claim =>
                {
                    foreach (var receipt in (claim?.Transitions ?? []).Select(t => t.Control).Where(c => c is not null))
                        CleanupHostedControl(identity, root, receipt!);
                    return true;
                }, GlassLockTimeout)), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRefusal(ex))
            {
                Console.Error.WriteLine("Hosted control registration cleanup pending; authority revocation remains durable.");
            }
        }
    }

    private static async Task<(IReadOnlyList<GlassIssuedActionStatus> Actions, bool QueueHeld)> ReadGlassActionsAsync(string repository, string root, CancellationToken token)
    {
        var snapshot = await QueueStore.LoadAsync(Path.Combine(root, "queue", "queue.json"), token, GlassLockTimeout).ConfigureAwait(false);
        var identity = RepositoryIdentity.From("https://" + repository, null)!;
        var claim = await ConductorClaimStore.GetClaimAsync(identity, root, token, GlassLockTimeout).ConfigureAwait(false);
        bool Revoked(string holder) => claim is null || claim.Stopped || claim.Holder != holder;
        var actions = snapshot.Items.Where(i => i.Repository == repository && i.ReplacementReviewAction is not null).Take(100)
            .Select(i => new GlassIssuedActionStatus(SafeLabel(i.Tag),
                i.ReplacementReviewAction!.ActionObservedAt is not null ? "observed" :
                i.ReplacementReviewAction.ReplacementAttemptId is null && i.ReplacementReviewAction.BlockedReason is not null ? "refused" :
                i.ReplacementReviewAction.ReplacementAttemptId is null && Revoked(i.ReplacementReviewAction.Holder) ? "revoked-pending" :
                i.ReplacementReviewAction.HeldPending ? "held-pending-marker" :
                i.ReplacementReviewAction.BlockedReason is not null
                    ? i.ReplacementReviewAction.ReplacementAttemptId is null ? "refused"
                        : i.ReplacementReviewAction.TerminalObservation is null ? "uncertain" : "terminal-unresolved" :
                i.ReplacementReviewAction.CompletionProof is not null ? "completion-proof-retained" :
                i.ReplacementReviewAction.ReplacementAttemptId is not null
                    ? i.AttemptStartedFactDurable ? "in-flight" : "issued" : "pending",
                i.ReplacementReviewAction.BlockedReason ?? i.ReplacementReviewAction.PausedReason,
                i.ReplacementReviewAction.ReplacementAttemptId is null && i.ReplacementReviewAction.BlockedReason is not null
                    ? i.ReplacementReviewAction.NextTrigger
                    : i.ReplacementReviewAction.ReplacementAttemptId is null && Revoked(i.ReplacementReviewAction.Holder)
                    ? "Owner must inspect retained evidence; Stop or displaced ownership cannot be undone by Unhold."
                    : i.ReplacementReviewAction.HeldPending
                        ? "Unhold this acquisition; the scheduler rechecks source, head, grants, opt-in and runway before launch."
                    : i.ReplacementReviewAction.NextTrigger, SafeLabel(i.ReplacementReviewAction.Holder))).ToList();
        foreach (var source in snapshot.Items.Where(i => i.Repository == repository && i.ReplacementReviewAction is null
            && i.StoppedWorkJudgment is { FollowAttachmentId: not null, FollowContinuationPending: true }).Take(100 - actions.Count))
        {
            var pending = source.StoppedWorkJudgment!;
            var registration = Read<ConductorFollowAttachment>(Path.Combine(root, "conductor-follow", identity.FileSlug, "registration.json"));
            ValidateControlRegistration(identity, root, registration, registration.Holder, registration.ClaimGeneration, registration.Id);
            if (registration.Id != pending.FollowAttachmentId || registration.Holder != pending.Holder)
            {
                actions.Add(new(SafeLabel(source.Tag), "unresolved-prior-attachment", "Retained event belongs to a prior attachment.",
                    "Owner must inspect its retained evidence; Unhold cannot restamp or replay it.", SafeLabel(pending.Holder!)));
                continue;
            }
            if (Revoked(pending.Holder!) || claim is not null && ConductorClaimStore.GetClaimGeneration(claim) != registration.ClaimGeneration)
            {
                actions.Add(new(SafeLabel(source.Tag), "revoked-pending", "Hosted acquisition was stopped or displaced.",
                    "Owner must inspect retained evidence; Unhold cannot restore this acquisition or replay its event.", SafeLabel(pending.Holder!)));
                continue;
            }
            var fleet = Path.Combine(root, BatonPaths.FleetDirectoryName);
            var obligations = new ConductorObligationStore(new FleetEventLog(Path.Combine(fleet, BatonPaths.FleetEventsFileName),
                Path.Combine(fleet, BatonPaths.FleetEventsRolloverFileName), 16 * 1024 * 1024), Path.Combine(fleet, BatonPaths.ConductorObligationsFileName));
            var obligation = await obligations.ReadAsync(pending.Key!, token).ConfigureAwait(false);
            var eventDirectory = obligation is null ? null : Path.Combine(registration.SessionDirectory, "events", Digest(obligation.ObligationId));
            var completed = eventDirectory is not null && File.Exists(Path.Combine(eventDirectory, "response.json"));
            var uncertain = !completed && eventDirectory is not null && File.Exists(Path.Combine(eventDirectory, "launch.json"));
            actions.Add(new(SafeLabel(source.Tag), completed ? "received-response-pending-decision" : uncertain ? "uncertain-turn" : "event-pending-turn",
                pending.FollowContinuationWait ?? "Real event retained; delivery admission pending.",
                pending.FollowContinuationTrigger ?? "Next scheduler reconciliation checks current eligibility.", SafeLabel(pending.Holder!)));
        }
        return (actions, snapshot.Held);
    }

    internal static async Task DetachFromGlassAsync(string body, string root, CancellationToken token,
        Func<string, CancellationToken, Task<RepositoryIdentity?>>? resolver = null)
    {
        var (request, identity) = ParseGlassIdentity(body);
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
