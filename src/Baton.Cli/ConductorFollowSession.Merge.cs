using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Conductor;
using Baton.Queue;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ExactMergeRequest(
    [property: JsonRequired] string Repository,
    [property: JsonRequired] string Holder,
    [property: JsonRequired] string ClaimGeneration,
    [property: JsonRequired] string AttachmentId,
    [property: JsonRequired] string RequestId,
    [property: JsonRequired] string TaskId,
    [property: JsonRequired] int PullRequest,
    [property: JsonRequired] string PullRequestUrl,
    [property: JsonRequired] string HeadSha,
    [property: JsonRequired] string Method,
    [property: JsonRequired] DateTimeOffset ExpiresAt,
    [property: JsonRequired] string? ReadyReceiptId,
    [property: JsonRequired] string? ReadyReceiptSha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ExactMergeRevokeRequest(
    [property: JsonRequired] string Repository,
    [property: JsonRequired] string GrantId,
    [property: JsonRequired] string RequestId);

internal sealed record ExactMergeControl(string Issuer, string RequestId, string InputSha256,
    string Operation, string GrantId, DateTimeOffset AcceptedAt);

internal sealed record ExactMergeJudgment(string Id, TaskReadyReceipt Ready, string ReadySha256,
    DateTimeOffset RetainedAt, string State = "pending", string? Decision = null,
    string Reason = "reason unavailable", string? ResponseSha256 = null, string? Receipt = null);

internal sealed record ExactMergeIssuedAttempt(string Id, string GrantId, string JudgmentId,
    string ReadySha256, string ResponseSha256, string QualificationSha256, DateTimeOffset IssuedAt,
    string Executor = "unknown", int? ExitCode = null, int? HttpStatus = null,
    string? CommitSha = null, string? ResponseDigest = null, ExactMergeObservation? Observation = null,
    ExactMergeQualification? Qualification = null, IReadOnlyList<string>? Arguments = null, string? Response = null);

internal sealed record ExactMergeObservation(string State, string? HeadSha, string? MergeCommitSha,
    DateTimeOffset ObservedAt, string Owner, string NextTrigger);

internal sealed record ExactMergeGrant(string Id, string Issuer, string InputSha256,
    ExactMergeRequest Request, DateTimeOffset AcceptedAt, string RequestSha256,
    string ConfigurationSha256, string State = "accepted", DateTimeOffset? RevokedAt = null,
    ExactMergeJudgment? Judgment = null, ExactMergeIssuedAttempt? Attempt = null);

internal sealed record ExactMergeLedger(int SchemaVersion, string Repository,
    IReadOnlyList<ExactMergeGrant> Grants, IReadOnlyList<ExactMergeControl> Controls);

internal sealed record GlassMergeCandidate(string TaskId, int PullRequest, string PullRequestUrl,
    string HeadSha, string? ReadyReceiptId, string? ReadyReceiptSha256);

internal sealed record GlassMergeStatus(string GrantId, string TaskId, string PullRequestUrl, string HeadSha,
    string Issuer, string RequestId, DateTimeOffset AcceptedAt, DateTimeOffset ExpiresAt,
    string Permission, string Delivery, string? Decision, string Reason, string Operation,
    bool Revoked, ExactMergeObservation? Observation);

internal sealed record GlassMergeView(IReadOnlyList<GlassMergeCandidate> Candidates,
    IReadOnlyList<GlassMergeStatus> Grants, string? Diagnostic = null);

internal sealed record GlassMergeControlResult(string GrantId, string RequestId, string Operation,
    DateTimeOffset AcceptedAt, bool Replayed, ExactMergeRequest? Grant = null);

internal sealed record ExactMergeSource(
    [property: JsonRequired] ExactMergeGrant Grant,
    [property: JsonRequired] ExactMergeJudgment Judgment,
    [property: JsonRequired] ConductorFollowRequest Request,
    [property: JsonRequired] ConductorFollowState Configuration);

internal sealed partial class ConductorFollowSession
{
    private const string MergeLockPrefix = "baton-exact-merge";
    internal static Action<string>? AfterMergePersistence { get; set; }
    internal static Func<DateTimeOffset> MergeClock { get; set; } = () => DateTimeOffset.UtcNow;
    internal static string ReadyDigest(TaskReadyReceipt ready) => Digest(JsonSerializer.Serialize(ready, Json));
    internal static string MergeDigest<T>(T value) => Digest(JsonSerializer.Serialize(value, Json));
    internal static string MergePrUrl(string repository, int number) => $"https://{repository}/pull/{number}";

    private static RepositoryIdentity MergeIdentity(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository) || repository.Length > 256 || repository.Any(char.IsControl))
            throw new CliArgumentException("Exact merge repository is invalid.");
        var identity = RepositoryIdentity.From("https://" + repository, null);
        if (identity?.Value != repository || repository.Split('/') is not ["github.com", _, _])
            throw new CliArgumentException("Exact merge supports one canonical GitHub repository.");
        return identity;
    }

    private static string MergeLedgerPath(string root, RepositoryIdentity identity) =>
        Path.Combine(Path.GetFullPath(root), "conductor-merge", identity.FileSlug, "grants.json");

    private static ExactMergeLedger ReadMergeLedger(string root, RepositoryIdentity identity)
    {
        var path = MergeLedgerPath(root, identity);
        RejectLinks(path);
        var ledger = File.Exists(path) ? Read<ExactMergeLedger>(path, MaxResponseBytes)
            : new ExactMergeLedger(SchemaVersion, identity.Value, [], []);
        if (ledger.SchemaVersion != SchemaVersion || ledger.Repository != identity.Value
            || ledger.Grants is null || ledger.Controls is null || ledger.Grants.Count > 100 || ledger.Controls.Count > 200
            || ledger.Grants.Any(g => g is null || g.Request is null)
            || ledger.Controls.Any(c => c is null)
            || ledger.Grants.Select(g => g.Id).Distinct().Count() != ledger.Grants.Count
            || ledger.Controls.Select(c => c.Issuer + "\n" + c.RequestId).Distinct().Count() != ledger.Controls.Count)
            throw new IOException("Exact merge register is incomplete or exceeds its bound.");
        foreach (var grant in ledger.Grants)
        {
            ValidateMergeRequest(grant.Request);
            if (grant.Request.Repository != identity.Value || !IsDigest(grant.Id)
                || grant.Id != CorrectionReceiptId(grant.Issuer, grant.Request.RequestId)
                || grant.InputSha256 != MergeDigest(grant.Request)
                || !IsDigest(grant.RequestSha256) || !IsDigest(grant.ConfigurationSha256)
                || grant.AcceptedAt == default || grant.State is not ("accepted" or "superseded" or "authority-invalidated")
                || !ledger.Controls.Any(c => c.Operation == "grant" && c.GrantId == grant.Id
                    && c.InputSha256 == grant.InputSha256 && c.Issuer == grant.Issuer && c.RequestId == grant.Request.RequestId))
                throw new IOException("Exact merge grant identity drifted.");
            if (grant.Judgment is { } judgment && (judgment.Ready is null || judgment.ReadySha256 != ReadyDigest(judgment.Ready)
                || judgment.Id != MergeJudgmentId(grant, judgment.Ready)
                || !ReadyMatches(grant.Request, judgment.Ready)
                || judgment.State is not ("pending" or "issued" or "complete" or "uncertain" or "refused")))
                throw new IOException("Exact ready judgment identity drifted.");
            if (grant.Attempt is { } attempt && (grant.Judgment is not { State: "complete", Decision: "Merge" } j
                || attempt.GrantId != grant.Id || attempt.JudgmentId != j.Id || attempt.ReadySha256 != j.ReadySha256
                || attempt.ResponseSha256 != j.ResponseSha256 || attempt.Id != Digest("merge-attempt\n" + j.Id)
                || attempt.IssuedAt == default || attempt.Executor is not ("unknown" or "confirmed" or "refused")
                || attempt.Qualification is not { } qualification || attempt.QualificationSha256 != MergeDigest(qualification)
                || qualification.ServerPolicy is null
                || qualification.ServerPolicySha256 != MergeDigest(qualification.ServerPolicy)
                || attempt.Arguments is null || !attempt.Arguments.SequenceEqual(WorkItemAdvancer.ExactMergeArguments(grant.Request))
                || attempt.Response is not null && (System.Text.Encoding.UTF8.GetByteCount(attempt.Response) > 64 * 1024
                    || Digest(attempt.Response) != attempt.ResponseDigest)))
                throw new IOException("Exact issued attempt identity drifted.");
        }
        return ledger;
    }

    private static void ValidateMergeRequest(ExactMergeRequest request)
    {
        _ = MergeIdentity(request.Repository);
        if (new[] { request.Holder, request.ClaimGeneration, request.RequestId, request.TaskId }
                .Any(v => !ConductorClaimStore.IsSafeHostedHolderLabel(v))
            || request.AttachmentId is not { Length: 32 } || !request.AttachmentId.All(Uri.IsHexDigit)
            || request.HeadSha is not { Length: 40 } || !request.HeadSha.All(char.IsAsciiHexDigit)
            || request.PullRequest <= 0 || request.PullRequestUrl != MergePrUrl(request.Repository, request.PullRequest)
            || request.Method != "squash" || request.ExpiresAt == default
            || (request.ReadyReceiptId is null) != (request.ReadyReceiptSha256 is null)
            || request.ReadyReceiptId is not null && (!IsDigest(request.ReadyReceiptId) || !IsDigest(request.ReadyReceiptSha256)))
            throw new CliArgumentException("Exact merge requires the displayed task/PR/full head, owner, acquisition, attachment, squash and expiry.");
    }

    private static bool ReadyMatches(ExactMergeRequest request, TaskReadyReceipt ready) =>
        ready.TaskId == request.TaskId && ready.Repository == request.Repository && ready.PullRequest == request.PullRequest
        && ready.HeadSha == request.HeadSha && (request.ReadyReceiptId is null
            || request.ReadyReceiptId == ready.Id && request.ReadyReceiptSha256 == ReadyDigest(ready));

    private static string MergeJudgmentId(ExactMergeGrant grant, TaskReadyReceipt ready) =>
        Digest($"merge-judgment\n{grant.Id}\n{ready.Id}\n{ReadyDigest(ready)}\n{grant.Request.ClaimGeneration}");

    private static QueueItem MergeRow(QueueSnapshot queue, ExactMergeRequest request, bool ready = false)
    {
        var rows = queue.Items.Where(i => i.OwnedTask?.Id == request.TaskId).Take(2).ToArray();
        if (rows.Length != 1 || rows[0] is not { Retirement: null, CancelledAt: null, Halted: false } row
            || row.Repository != request.Repository || row.OwnedTask!.Repository != request.Repository
            || row.OwnedTask.ConductorHolder != request.Holder || row.PullRequest != request.PullRequest
            || row.Issue != row.OwnedTask.Issue || row.ChecksHeadSha != request.HeadSha
            || ready && (row.Stage != WorkStage.Ready || row.State != QueueItemState.Queued
                || row.OwnedTask.Ready is not { } receipt || !ReadyMatches(request, receipt)))
            throw new CliArgumentException("Exact merge task, PR, head or ready identity changed.");
        return row;
    }

    private static string MergePermission(ExactMergeGrant grant, ConductorClaimRecord? claim,
        ConductorFollowAttachment? attachment, DateTimeOffset now)
    {
        if (grant.Attempt is not null) return "spent";
        if (grant.RevokedAt is not null) return "revoked";
        if (grant.State != "accepted") return grant.State;
        if (grant.Request.ExpiresAt <= now) return "expired";
        if (!ConductorClaimStore.IsCurrentHostedAcquisition(claim, grant.Request.Holder, grant.Request.ClaimGeneration)
            || attachment is null || !attachment.Attached || attachment.Id != grant.Request.AttachmentId
            || attachment.Holder != grant.Request.Holder || attachment.ClaimGeneration != grant.Request.ClaimGeneration)
            return "authority-invalidated";
        return "accepted/unspent";
    }

    private static bool UnresolvedTarget(ExactMergeGrant g, ExactMergeRequest request) =>
        g.Request.PullRequest == request.PullRequest && g.Request.HeadSha == request.HeadSha
        && g.Attempt is { } attempt && attempt.Executor != "refused";

    private static async Task MutateMergeAsync(string root, RepositoryIdentity identity,
        Func<QueueSnapshot, ConductorClaimRecord?, ExactMergeLedger, ExactMergeLedger> mutate, CancellationToken token)
    {
        await QueueStore.ReadWithCurrentClaimAsync(Path.Combine(root, "queue", "queue.json"), identity, root,
            (queue, claim) => MutexGuardedFileLock.RunUnderLock(MergeLedgerPath(root, identity), MergeLockPrefix,
                GlassLockTimeout, () =>
                {
                    token.ThrowIfCancellationRequested();
                    var old = ReadMergeLedger(root, identity);
                    var updated = mutate(queue, claim, old);
                    if (updated != old) Write(MergeLedgerPath(root, identity), updated);
                    return true;
                }), token, GlassLockTimeout).ConfigureAwait(false);
    }

    internal static async Task<GlassMergeControlResult> MergeControlFromGlassAsync(string body,
        string root, string issuer, bool revoke, CancellationToken token)
    {
        if (body.Length > 4096 || string.IsNullOrWhiteSpace(issuer) || issuer.Length > 256 || issuer.Any(char.IsControl))
            throw new CliArgumentException("Exact merge request is not bounded or authenticated.");
        var request = revoke ? null : Deserialize<ExactMergeRequest>(body);
        var revocation = revoke ? Deserialize<ExactMergeRevokeRequest>(body) : null;
        if (request is not null) ValidateMergeRequest(request);
        var repository = request?.Repository ?? revocation!.Repository;
        var requestId = request?.RequestId ?? revocation!.RequestId;
        if (!ConductorClaimStore.IsSafeHostedHolderLabel(requestId) || revoke && !IsDigest(revocation!.GrantId))
            throw new CliArgumentException("Exact merge request ID or grant ID is invalid.");
        var identity = MergeIdentity(repository);
        var digest = request is null ? MergeDigest(revocation) : MergeDigest(request);
        GlassMergeControlResult? result = null;
        await MutateMergeAsync(root, identity, (queue, claim, ledger) =>
        {
            var operation = revoke ? "revoke" : "grant";
            var previous = ledger.Controls.SingleOrDefault(c => c.Issuer == issuer && c.RequestId == requestId);
            if (previous is not null)
            {
                if (previous.InputSha256 != digest || previous.Operation != operation)
                    throw new CliArgumentException("Exact merge request ID conflicts with retained input.");
                result = new(previous.GrantId, requestId, operation, previous.AcceptedAt, true,
                    ledger.Grants.Single(g => g.Id == previous.GrantId).Request);
                return ledger;
            }
            if (ledger.Controls.Count >= 200 || !revoke && ledger.Grants.Count >= 100)
                throw new CliArgumentException("Exact merge retention capacity reached; owner must inspect.");
            var now = MergeClock();
            ExactMergeGrant grant;
            var grants = ledger.Grants.ToList();
            if (revoke)
            {
                grant = grants.SingleOrDefault(g => g.Id == revocation!.GrantId)
                    ?? throw new CliArgumentException("Exact retained merge grant was not found.");
                // Late revocation is durable audit, never a cancellation promise or an allowance reset.
                grants[grants.IndexOf(grant)] = grant with { RevokedAt = grant.RevokedAt ?? now };
            }
            else
            {
                var registration = ValidateCurrentHostedAuthorityForGrant(claim, request!, root);
                var row = MergeRow(queue, request!);
                if (request!.ExpiresAt <= now || request.ExpiresAt > now.AddHours(24))
                    throw new CliArgumentException("Exact merge expiry must be in the next 24 hours.");
                var ready = row.Stage == WorkStage.Ready ? row.OwnedTask!.Ready : null;
                if (row.Stage == WorkStage.Ready && ready is null)
                    throw new CliArgumentException("Already-ready task lacks an exact receipt.");
                if (ready is not null ? request.ReadyReceiptId != ready.Id || request.ReadyReceiptSha256 != ReadyDigest(ready)
                    : request.ReadyReceiptId is not null)
                    throw new CliArgumentException("Already-ready grants must bind the exact displayed receipt.");
                if (ledger.Grants.Any(g => UnresolvedTarget(g, request)))
                    throw new CliArgumentException("An issued merge attempt fences this exact target across acquisitions.");
                var followRequest = Read<ConductorFollowRequest>(Path.Combine(registration.SessionDirectory, "request.json"));
                var state = Read<ConductorFollowState>(Path.Combine(registration.SessionDirectory, "session.json"));
                ValidateRequest(followRequest);
                if (registration.RequestSha256 != MergeDigest(followRequest) || state.Holder != request.Holder
                    || state.ClaimGeneration != request.ClaimGeneration || state.Repository != request.Repository
                    || ReadCeiling(root, followRequest.Workspace) != state.ProjectCeiling
                    || !state.ProjectCeiling.IsUnrestricted || followRequest.PermissionGrant != SupportedGrant)
                    throw new CliArgumentException("Exact merge project policy or retained configuration is unavailable.");
                grant = new(CorrectionReceiptId(issuer, requestId), issuer, digest, request, now,
                    registration.RequestSha256, ConfigurationDigest(state));
                if (ready is not null) grant = grant with { Judgment = new(MergeJudgmentId(grant, ready), ready, ReadyDigest(ready), now) };
                for (var index = 0; index < grants.Count; index++)
                    if (grants[index].Attempt is null && grants[index].Request.TaskId == request.TaskId
                        && grants[index].Request.PullRequest == request.PullRequest && grants[index].Request.HeadSha == request.HeadSha
                        && grants[index].State == "accepted")
                        grants[index] = grants[index] with { State = "superseded" };
                grants.Add(grant);
            }
            var control = new ExactMergeControl(issuer, requestId, digest, operation, grant.Id, now);
            result = new(grant.Id, requestId, operation, now, false, grant.Request);
            return ledger with { Grants = grants, Controls = ledger.Controls.Append(control).ToArray() };
        }, token).ConfigureAwait(false);
        AfterMergePersistence?.Invoke(revoke ? "revocation" : "acceptance");
        return result!;
    }

    private static ConductorFollowAttachment ValidateCurrentHostedAuthorityForGrant(ConductorClaimRecord? claim,
        ExactMergeRequest request, string root)
    {
        if (!ConductorClaimStore.IsCurrentHostedAcquisition(claim, request.Holder, request.ClaimGeneration))
            throw new CliArgumentException("Exact merge acquisition is stopped or displaced.");
        var identity = MergeIdentity(request.Repository);
        var registration = Read<ConductorFollowAttachment>(Path.Combine(root, "conductor-follow", identity.FileSlug, "registration.json"));
        ValidateControlRegistration(identity, root, registration, request.Holder, request.ClaimGeneration, request.AttachmentId);
        if (!registration.Attached) throw new CliArgumentException("Exact merge attachment is detached.");
        return registration;
    }

    internal static GlassMergeControlResult? LookupMergeControl(string root, string repository, string issuer, string requestId)
    {
        if (!ConductorClaimStore.IsSafeHostedHolderLabel(requestId)) throw new CliArgumentException("Invalid merge request ID.");
        var ledger = ReadMergeLedger(root, MergeIdentity(repository));
        var control = ledger.Controls.SingleOrDefault(c => c.Issuer == issuer && c.RequestId == requestId);
        return control is null ? null : new(control.GrantId, control.RequestId, control.Operation, control.AcceptedAt, true,
            ledger.Grants.Single(g => g.Id == control.GrantId).Request);
    }

    internal static GlassMergeView ReadGlassMerge(RepositoryIdentity identity, string root,
        IReadOnlyList<QueueItem> rows, ConductorClaimRecord? claim)
    {
        try
        {
            var ledger = ReadMergeLedger(root, identity);
            ConductorFollowAttachment? attachment = null;
            try { attachment = Read<ConductorFollowAttachment>(Path.Combine(root, "conductor-follow", identity.FileSlug, "registration.json")); }
            catch (Exception ex) when (IsRefusal(ex)) { }
            var candidates = rows.Where(i => i.Repository == identity.Value && i.OwnedTask is not null
                && i.OwnedTask.ConductorHolder == claim?.Holder && i.PullRequest > 0 && i.ChecksHeadSha is { Length: 40 }
                && i.Retirement is null && i.CancelledAt is null && !i.Halted).Take(20)
                .Select(i => new GlassMergeCandidate(i.OwnedTask!.Id, i.PullRequest!.Value,
                    MergePrUrl(identity.Value, i.PullRequest.Value), i.ChecksHeadSha!,
                    i.Stage == WorkStage.Ready ? i.OwnedTask.Ready?.Id : null,
                    i.Stage == WorkStage.Ready && i.OwnedTask.Ready is { } ready ? ReadyDigest(ready) : null)).ToArray();
            var grants = ledger.Grants.TakeLast(20).Select(g => new GlassMergeStatus(g.Id, g.Request.TaskId,
                g.Request.PullRequestUrl, g.Request.HeadSha, g.Issuer, g.Request.RequestId, g.AcceptedAt, g.Request.ExpiresAt,
                MergePermission(g, claim, attachment, MergeClock()), g.Judgment?.State ?? "awaiting-ready",
                g.Judgment?.Decision, g.Judgment?.Reason ?? "reason unavailable", g.Attempt?.Executor ?? "not issued",
                g.RevokedAt is not null, g.Attempt?.Observation)).ToArray();
            return new(candidates, grants);
        }
        catch (Exception ex) when (IsRefusal(ex)) { return new([], [], "Exact merge register unavailable; permission and outcomes are unknown."); }
    }
}
