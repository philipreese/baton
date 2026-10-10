using System.Text;
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
internal sealed record ConductorCorrectionRequest(
    [property: JsonRequired] string Repository,
    [property: JsonRequired] string Holder,
    [property: JsonRequired] string ClaimGeneration,
    [property: JsonRequired] string AttachmentId,
    [property: JsonRequired] string RequestId,
    [property: JsonRequired] string Text);

internal sealed record ConductorCorrectionInput(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string EventId,
    [property: JsonRequired] string ReceiptId,
    [property: JsonRequired] string Issuer,
    [property: JsonRequired] ConductorCorrectionRequest Request,
    [property: JsonRequired] string InputSha256,
    [property: JsonRequired] string TextSha256,
    [property: JsonRequired] string RequestSha256,
    [property: JsonRequired] string ConfigurationSha256);

internal sealed record ConductorCorrectionReceipt(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string ReceiptId,
    [property: JsonRequired] string EventId,
    [property: JsonRequired] string Issuer,
    [property: JsonRequired] string RequestId,
    [property: JsonRequired] string Repository,
    [property: JsonRequired] string Holder,
    [property: JsonRequired] string ClaimGeneration,
    [property: JsonRequired] string AttachmentId,
    [property: JsonRequired] string InputSha256,
    [property: JsonRequired] string SourceSha256,
    [property: JsonRequired] DateTimeOffset AcceptedAt);

internal sealed record ConductorCorrectionPredecessor(
    [property: JsonRequired] string Key,
    [property: JsonRequired] string ContextSha256,
    [property: JsonRequired] string Disposition,
    string? EventId = null);

internal sealed record ConductorCorrectionSlot(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string ReceiptId,
    [property: JsonRequired] string EventId,
    [property: JsonRequired] string SourceSha256,
    [property: JsonRequired] string State,
    [property: JsonRequired] string Reason,
    [property: JsonRequired] ConductorCorrectionPredecessor? Predecessor);

internal sealed record GlassCorrectionStatus(string ReceiptId, string RequestId,
    string Repository, string Holder, string ClaimGeneration, string AttachmentId,
    DateTimeOffset AcceptedAt, string State, string Reason, string Application = "not verified");

internal sealed record GlassCorrectionResult(string Outcome, GlassCorrectionStatus? Receipt,
    string Diagnostic, bool Replayed = false);

internal sealed record HostedFollowTarget(string Repository, string Holder, string ClaimGeneration,
    string AttachmentId);

internal sealed record ConductorCorrectionRevocation(
    [property: JsonRequired] string ReceiptId,
    [property: JsonRequired] string EventId,
    [property: JsonRequired] string SourceSha256,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] string Reason);

internal sealed class CorrectionCapacityException(GlassCorrectionStatus existing) : BatonFlowException(
    "One correction is unresolved for this acquisition.")
{
    internal GlassCorrectionStatus Existing { get; } = existing;
}

internal sealed partial class ConductorFollowSession
{
    private const string CorrectionLockPrefix = "baton-conductor-correction";
    private string CorrectionSlotPath => Path.Combine(_directory, "correction.json");
    internal static Action<string>? AfterCorrectionPersistence { get; set; }

    private static bool IsCorrection(ConductorFollowEventIdentity identity) => identity.Kind switch
    {
        "correction" => true,
        "halted-task" => false,
        _ => throw new IOException("Unknown or null follow event kind."),
    };

    private static void RefuseCorrectionDecision(string directory)
    {
        if (File.Exists(Path.Combine(directory, "decision.json")))
            throw new IOException("Correction decision evidence is corruption.");
    }

    private static string CorrectionReceiptId(string issuer, string requestId) =>
        Digest(JsonSerializer.Serialize(new[] { issuer, requestId }, Json));

    private static string CorrectionReceiptPath(string root, string receiptId) =>
        Path.Combine(root, "conductor-follow", "correction-receipts", receiptId + ".json");

    private static string CorrectionDirectory(string root, string repository, string generation)
    {
        var identity = RepositoryIdentity.From("https://" + repository, null)
            ?? throw new CliArgumentException("Invalid correction repository.");
        if (identity.Value != repository || repository.StartsWith("gitdir:", StringComparison.Ordinal))
            throw new CliArgumentException("Invalid correction repository.");
        return Path.Combine(Path.GetFullPath(root), "conductor-follow", identity.FileSlug,
            Digest(repository + "\n" + generation));
    }

    private static bool IsDigest(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static ConductorCorrectionRequest ParseCorrection(string body, string issuer, bool checkBodyBound = true)
    {
        if (checkBodyBound && body.Length > 4096 || string.IsNullOrWhiteSpace(issuer) || issuer.Length > 256 || issuer.Any(char.IsControl))
            throw new CliArgumentException("Correction request exceeds its bound or has no authenticated issuer.");
        using var document = JsonDocument.Parse(body);
        RejectDuplicateProperties(document.RootElement);
        var names = document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.EnumerateObject().Select(p => p.Name).ToArray() : [];
        if (!names.Order(StringComparer.Ordinal).SequenceEqual(
            new[] { "repository", "holder", "claimGeneration", "attachmentId", "requestId", "text" }.Order(StringComparer.Ordinal)))
            throw new CliArgumentException("Correction requires exactly the displayed target, requestId and text.");
        var request = Deserialize<ConductorCorrectionRequest>(body);
        if (new[] { request.Repository, request.Holder, request.ClaimGeneration, request.AttachmentId, request.RequestId }
            .Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 256 || v.Any(char.IsControl))
            || !ConductorClaimStore.IsSafeHostedHolderLabel(request.Holder)
            || !ConductorClaimStore.IsSafeHostedHolderLabel(request.ClaimGeneration)
            || !ConductorClaimStore.IsSafeHostedHolderLabel(request.RequestId)
            || request.AttachmentId.Length != 32 || !request.AttachmentId.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 4096)
            throw new CliArgumentException("Correction requires a bounded exact target and nonblank text.");
        _ = CorrectionDirectory(".", request.Repository, request.ClaimGeneration);
        return request;
    }

    private static void WriteImmutable<T>(string path, T value)
    {
        RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                4096, FileOptions.WriteThrough))
            {
                stream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json)));
                stream.Flush(true);
            }
            RejectLinks(path);
            File.Move(temporary, path); // Atomic publication without replacement.
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task<GlassCorrectionResult> CorrectFromGlassAsync(string body, string root,
        string issuer, CancellationToken token)
    {
        root = Path.GetFullPath(root);
        RejectLinks(root);
        var request = ParseCorrection(body, issuer);
        var receiptId = CorrectionReceiptId(issuer, request.RequestId);
        var receiptPath = CorrectionReceiptPath(root, receiptId);
        var inputDigest = Digest(JsonSerializer.Serialize(request, Json));
        // Exact historical lookup precedes current target, trust, attachment and capacity checks.
        if (File.Exists(receiptPath))
            return ReplayCorrection(root, receiptPath, issuer, request.RequestId, inputDigest);
        var identity = RepositoryIdentity.From("https://" + request.Repository, null)!;
        var directory = CorrectionDirectory(root, request.Repository, request.ClaimGeneration);
        var registrationPath = Path.Combine(root, "conductor-follow", identity.FileSlug, "registration.json");
        var registration = Read<ConductorFollowAttachment>(registrationPath);
        ValidateControlRegistration(identity, root, registration, request.Holder, request.ClaimGeneration, request.AttachmentId);
        var followRequest = Read<ConductorFollowRequest>(Path.Combine(directory, "request.json"));
        var state = Read<ConductorFollowState>(Path.Combine(directory, "session.json"));
        var eventId = Digest("correction\n" + receiptId + "\n" + Guid.NewGuid().ToString("N"));
        var input = new ConductorCorrectionInput(SchemaVersion, eventId, receiptId, issuer, request,
            inputDigest, Digest(request.Text), registration.RequestSha256, ConfigurationDigest(state));
        var sourcePath = Path.Combine(directory, "correction-inputs", eventId + ".json");
        WriteImmutable(sourcePath, input);
        AfterCorrectionPersistence?.Invoke("source");
        GlassCorrectionResult? result = null;
        var possibleCommit = false;
        try
        {
            await QueueStore.MutateWithCurrentClaimAsync(Path.Combine(root, "queue", "queue.json"), identity, root,
                (queue, claim) => MutexGuardedFileLock.RunUnderLock(Path.Combine(directory, "correction.json"),
                    CorrectionLockPrefix, GlassLockTimeout, () =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (File.Exists(receiptPath))
                        {
                            result = ReplayCorrection(root, receiptPath, issuer, request.RequestId, inputDigest);
                            return queue;
                        }
                        var current = Read<ConductorFollowAttachment>(registrationPath);
                        ValidateControlRegistration(identity, root, current, request.Holder, request.ClaimGeneration, request.AttachmentId);
                        var currentState = Read<ConductorFollowState>(Path.Combine(directory, "session.json"));
                        if (!ConductorClaimStore.IsCurrentHostedAcquisition(claim, request.Holder, request.ClaimGeneration)
                            || !current.Attached
                            || current.RequestSha256 != input.RequestSha256
                            || ConfigurationDigest(currentState) != input.ConfigurationSha256
                            || ReadCeiling(root, followRequest.Workspace) != currentState.ProjectCeiling
                            || currentState.ProjectCeiling.Cap(followRequest.PermissionGrant) != SupportedGrant
                            || followRequest.PermissionGrant != SupportedGrant)
                            throw new CliArgumentException("Correction target, attachment, trust or configuration changed.");
                        var slotPath = Path.Combine(directory, "correction.json");
                        if (File.Exists(slotPath))
                        {
                            var previous = ReadCorrectionSlot(slotPath);
                            var oldPath = CorrectionReceiptPath(root, previous.ReceiptId);
                            if (File.Exists(oldPath))
                            {
                                var oldReceipt = Read<ConductorCorrectionReceipt>(oldPath);
                                if (oldReceipt.EventId != previous.EventId || oldReceipt.SourceSha256 != previous.SourceSha256)
                                    throw new IOException("Committed correction slot differs from its receipt.");
                                var old = ReadCorrectionStatus(root, oldReceipt);
                                if (old.State is not ("delivered" or "not-delivered"))
                                    throw new CorrectionCapacityException(old);
                            }
                            else
                            {
                                // Only an exact, marker-free preparation with no committed receipt is discardable.
                                ValidateUncommittedPreparation(root, directory, previous);
                            }
                        }
                        if (currentState.Frozen) throw new CliArgumentException("Known-frozen session refuses new correction input.");
                        var predecessor = SelectCorrectionPredecessor(queue.Items, request, followRequest.Workspace);
                        var slot = new ConductorCorrectionSlot(SchemaVersion, receiptId, eventId, FileDigest(sourcePath),
                            "queued", "Received; waiting for a permitted read-only turn.", predecessor);
                        Write(slotPath, slot);
                        AfterCorrectionPersistence?.Invoke("slot");
                        var receipt = new ConductorCorrectionReceipt(SchemaVersion, receiptId, eventId, issuer,
                            request.RequestId, request.Repository, request.Holder, request.ClaimGeneration, request.AttachmentId,
                            inputDigest, slot.SourceSha256, DateTimeOffset.UtcNow);
                        possibleCommit = true;
                        WriteImmutable(receiptPath, receipt);
                        AfterCorrectionPersistence?.Invoke("acceptance");
                        result = new("accepted", ProjectCorrection(receipt, "queued",
                            claim!.Held ? "Hosted acquisition is Held; Unhold permits reconciliation." : slot.Reason),
                            "Received. Current work may finish; application is not verified.");
                        return queue;
                    }), token, GlassLockTimeout).ConfigureAwait(false);
            return result!;
        }
        catch (CorrectionCapacityException ex)
        {
            return new("capacity", ex.Existing, "One correction remains unresolved; no new input accepted.");
        }
        catch (Exception ex) when (possibleCommit && (IsRefusal(ex) || ex is OperationCanceledException))
        {
            return new("unknown", null, "Acceptance outcome unknown. Look up this exact requestId; do not repeat POST automatically.");
        }
    }

    private static GlassCorrectionResult ReplayCorrection(string root, string path, string issuer, string requestId, string digest)
    {
        var receipt = Read<ConductorCorrectionReceipt>(path);
        if (receipt.Issuer != issuer || receipt.RequestId != requestId || receipt.InputSha256 != digest)
            throw new CliArgumentException("Correction requestId conflicts with previously accepted input.");
        return new("accepted", ReadCorrectionStatus(root, receipt), "Original acceptance receipt; no delivery wake.", Replayed: true);
    }

    private static ConductorCorrectionPredecessor? SelectCorrectionPredecessor(
        IEnumerable<QueueItem> candidates, ConductorCorrectionRequest request, string workspace)
    {
        var row = OrderHostedCandidates(candidates).FirstOrDefault(item =>
            item.Repository == request.Repository && item.OwnedTask?.ConductorHolder == request.Holder
            && item.Halted && item.State == QueueItemState.Failed && item.Workspace == workspace
            && item.Retirement is null && item.CancelledAt is null && item.ReplacementReviewAction is null
            && item.StoppedWorkJudgment is { Key: not null, ContextSha256: not null, FollowContinuationPending: true } intent
            && intent.FollowAttachmentId == request.AttachmentId && intent.Holder == request.Holder
            && item.OwnedTask!.Blocked?.ObligationKey == intent.Key && intent.State == StoppedWorkJudgmentState.Pending
            && StoppedWorkAdviceEvidence.Hash(StoppedWorkAdviceEvidence.Context(intent)) == intent.ContextSha256);
        return row?.StoppedWorkJudgment is { } selected
            ? new(selected.Key!, selected.ContextSha256!, "pending") : null;
    }

    internal static IOrderedEnumerable<QueueItem> OrderHostedCandidates(IEnumerable<QueueItem> candidates) =>
        candidates.OrderBy(item => item.StoppedWorkJudgment?.ObservedAt)
            .ThenBy(item => item.StoppedWorkJudgment?.Key, StringComparer.Ordinal);

    private static ConductorCorrectionSlot ReadCorrectionSlot(string path)
    {
        var slot = Read<ConductorCorrectionSlot>(path, 8192);
        if (slot.SchemaVersion != SchemaVersion || !IsDigest(slot.ReceiptId) || !IsDigest(slot.EventId)
            || !IsDigest(slot.SourceSha256) || slot.State is not ("queued" or "waiting" or "issued" or "uncertain" or "delivered" or "not-delivered")
            || !IsSafeCorrectionReason(slot.Reason)
            || slot.Predecessor is { } p && (p.Disposition is not ("pending" or "opportunity" or "resolved" or "uncertain")
                || !IsDigest(p.ContextSha256) || !StoppedWorkJudgmentKey.TryParse(p.Key, out _, out _, out _, out _)
                || p.EventId is not null && !IsDigest(p.EventId)))
            throw new IOException("Invalid correction slot.");
        return slot;
    }

    private static bool IsSafeCorrectionReason(string reason) => reason is
        "Received; waiting for a permitted read-only turn."
        or "Hosted acquisition is Held; Unhold permits reconciliation."
        or "Read-only turn completed; application is not verified."
        or "Turn issued; outcome pending."
        or "Unissued input revoked by ownership or attachment change."
        or "Issued or retained evidence is uncertain; owner must inspect. No retry authority."
        or "Global queue Hold prevents correction launch."
        or "Retained ordinary predecessor awaits its admission opportunity."
        or "Required daemon runway admission refused; no launch admitted."
        or "Recorded trust or correction configuration changed."
        or "Correction workspace identity changed."
        or "Current authority or admission cannot be verified; scheduler reconciliation rechecks.";

    private static void ValidateUncommittedPreparation(string root, string directory, ConductorCorrectionSlot slot)
    {
        var input = Read<ConductorCorrectionInput>(Path.Combine(directory, "correction-inputs", slot.EventId + ".json"));
        ValidateCorrectionInput(root, input);
        if (input.ReceiptId != slot.ReceiptId || FileDigest(Path.Combine(directory, "correction-inputs", slot.EventId + ".json")) != slot.SourceSha256
            || Directory.Exists(Path.Combine(directory, "events", Digest(slot.EventId))))
            throw new IOException("Uncommitted preparation is not demonstrably unissued.");
    }

    private static void ValidateCorrectionInput(string root, ConductorCorrectionInput input)
    {
        if (input.SchemaVersion != SchemaVersion || !IsDigest(input.EventId) || !IsDigest(input.ReceiptId)
            || input.ReceiptId != CorrectionReceiptId(input.Issuer, input.Request.RequestId)
            || input.InputSha256 != Digest(JsonSerializer.Serialize(input.Request, Json))
            || input.TextSha256 != Digest(input.Request.Text) || !IsDigest(input.RequestSha256)
            || !IsDigest(input.ConfigurationSha256))
            throw new IOException("Correction input binding is corrupt.");
        _ = ParseCorrection(JsonSerializer.Serialize(input.Request, Json), input.Issuer, checkBodyBound: false);
        _ = CorrectionDirectory(root, input.Request.Repository, input.Request.ClaimGeneration);
    }

    private static ConductorCorrectionInput ValidateCorrectionReceipt(string root, ConductorCorrectionReceipt receipt)
    {
        if (receipt.SchemaVersion != SchemaVersion || receipt.AcceptedAt == default || !IsDigest(receipt.EventId)
            || receipt.ReceiptId != CorrectionReceiptId(receipt.Issuer, receipt.RequestId)
            || !IsDigest(receipt.SourceSha256) || !IsDigest(receipt.InputSha256))
            throw new IOException("Correction acceptance receipt is corrupt.");
        var directory = CorrectionDirectory(root, receipt.Repository, receipt.ClaimGeneration);
        var source = Path.Combine(directory, "correction-inputs", receipt.EventId + ".json");
        var input = Read<ConductorCorrectionInput>(source, 32 * 1024);
        ValidateCorrectionInput(root, input);
        if (input.ReceiptId != receipt.ReceiptId || input.EventId != receipt.EventId || input.Issuer != receipt.Issuer
            || input.Request.RequestId != receipt.RequestId || input.Request.Repository != receipt.Repository
            || input.Request.Holder != receipt.Holder || input.Request.ClaimGeneration != receipt.ClaimGeneration
            || input.Request.AttachmentId != receipt.AttachmentId || input.InputSha256 != receipt.InputSha256
            || FileDigest(source) != receipt.SourceSha256)
            throw new IOException("Correction receipt source drifted.");
        return input;
    }

    private static GlassCorrectionStatus ProjectCorrection(ConductorCorrectionReceipt receipt, string state, string reason) =>
        new(receipt.ReceiptId, receipt.RequestId, receipt.Repository, receipt.Holder, receipt.ClaimGeneration,
            receipt.AttachmentId, receipt.AcceptedAt, state, reason);

    internal static Task<GlassCorrectionResult> LookupCorrectionReceiptAsync(string root, string issuer, string requestId,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(issuer) || issuer.Length > 256 || issuer.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(requestId) || requestId.Length > 256 || requestId.Any(char.IsControl))
            throw new CliArgumentException("A bounded exact requestId is required.");
        try
        {
            var path = CorrectionReceiptPath(root, CorrectionReceiptId(issuer, requestId));
            RejectLinks(path);
            if (!File.Exists(path)) return Task.FromResult(new GlassCorrectionResult("absent", null, "No committed acceptance receipt for this requestId."));
            var receipt = Read<ConductorCorrectionReceipt>(path, 8192);
            if (receipt.Issuer != issuer || receipt.RequestId != requestId) throw new IOException("Receipt identity differs.");
            return Task.FromResult(new GlassCorrectionResult("accepted", ReadCorrectionStatus(root, receipt), "Exact retained acceptance lookup."));
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            return Task.FromResult(new GlassCorrectionResult("unknown", null, "Exact acceptance or delivery evidence is unavailable; no retry authority."));
        }
    }

    private static GlassCorrectionStatus ReadCorrectionStatus(string root, ConductorCorrectionReceipt receipt)
    {
        var input = ValidateCorrectionReceipt(root, receipt);
        var directory = CorrectionDirectory(root, receipt.Repository, receipt.ClaimGeneration);
        var state = Read<ConductorFollowState>(Path.Combine(directory, "session.json"));
        var request = Read<ConductorFollowRequest>(Path.Combine(directory, "request.json"));
        if (state.Repository != receipt.Repository || state.Holder != receipt.Holder || state.ClaimGeneration != receipt.ClaimGeneration
            || ConfigurationDigest(state) != input.ConfigurationSha256
            || Digest(JsonSerializer.Serialize(request, Json)) != input.RequestSha256)
            throw new IOException("Correction configuration proof is unavailable.");
        var evidence = Path.Combine(directory, "events", Digest(receipt.EventId));
        RejectLinks(evidence);
        RefuseCorrectionDecision(evidence);
        var revocationPath = Path.Combine(directory, "correction-inputs", receipt.EventId + ".revoked.json");
        if (File.Exists(revocationPath))
        {
            var revocation = Read<ConductorCorrectionRevocation>(revocationPath, 8192);
            if (revocation.ReceiptId != receipt.ReceiptId || revocation.EventId != receipt.EventId
                || revocation.SourceSha256 != receipt.SourceSha256 || revocation.ObservedAt == default
                || revocation.Reason != "Unissued input revoked by ownership or attachment change."
                || File.Exists(Path.Combine(evidence, "launch.json")) || File.Exists(Path.Combine(evidence, "response.json")))
                throw new IOException("Unissued revocation proof is inconsistent.");
            return ProjectCorrection(receipt, "not-delivered", revocation.Reason);
        }
        if (Directory.Exists(evidence))
        {
            var identity = Read<ConductorFollowEventIdentity>(Path.Combine(evidence, "identity.json"));
            ValidateCorrectionEventSource(evidence, identity, state);
            if (identity.ObligationId != receipt.EventId || identity.ContextSha256 != receipt.SourceSha256)
                throw new IOException("Correction event differs from acceptance.");
            if (File.Exists(Path.Combine(evidence, "launch.json")))
            {
                ValidateTurnLaunch(evidence, identity, state);
                if (File.Exists(Path.Combine(evidence, "response.json")))
                {
                    _ = ReadResponse(Path.Combine(evidence, "response.json"), identity, state);
                    if (File.Exists(Path.Combine(evidence, "receipt.txt")))
                    {
                        _ = EnsureReceipt(evidence, repair: false);
                        return ProjectCorrection(receipt, "delivered", "Read-only turn completed; application is not verified.");
                    }
                    return ProjectCorrection(receipt, "issued", "Complete response retained; delivery receipt repair pending.");
                }
                var uncertain = state.Frozen || File.Exists(Path.Combine(evidence, "partial-transcript.jsonl"));
                return ProjectCorrection(receipt, uncertain ? "uncertain" : "issued",
                    uncertain ? "Issued turn outcome is uncertain; owner must inspect retained evidence." : "Turn issued; outcome pending.");
            }
            if (File.Exists(Path.Combine(evidence, "response.json")) || File.Exists(Path.Combine(evidence, "receipt.txt")))
                throw new IOException("Correction output has no launch proof.");
        }
        var slotPath = Path.Combine(directory, "correction.json");
        if (!File.Exists(slotPath)) throw new IOException("Unissued acceptance has no slot.");
        var slot = ReadCorrectionSlot(slotPath);
        if (slot.EventId != receipt.EventId || slot.ReceiptId != receipt.ReceiptId || slot.SourceSha256 != receipt.SourceSha256)
            throw new IOException("Unissued acceptance slot differs.");
        if (slot.State is "issued" or "delivered" or "not-delivered") throw new IOException("Slot claims an unproven terminal or issued disposition.");
        return ProjectCorrection(receipt, slot.State, slot.Reason);
    }

    private static void ValidateTurnLaunch(string evidence, ConductorFollowEventIdentity identity, ConductorFollowState state)
    {
        var launch = Read<ConductorFollowLaunch>(Path.Combine(evidence, "launch.json"));
        if (launch.SchemaVersion != SchemaVersion || launch.ObligationId != identity.ObligationId
            || launch.ObligationKey != identity.ObligationKey
            || launch.ExpectedSessionId is not null && launch.ExpectedSessionId != state.SessionId)
            throw new IOException("Incomplete launch identity.");
    }

    private static void ValidateCorrectionEventSource(string directory, ConductorFollowEventIdentity identity, ConductorFollowState state)
    {
        if (!IsCorrection(identity)) throw new IOException("Correction kind is missing.");
        RefuseCorrectionDecision(directory);
        var input = Read<ConductorCorrectionInput>(Path.Combine(directory, "source.json"), 32 * 1024);
        // The root is derived from the event directory, never an input-selected location.
        var root = Path.GetFullPath(Path.Combine(directory, "..", "..", "..", "..", ".."));
        ValidateCorrectionInput(root, input);
        var receipt = Read<ConductorCorrectionReceipt>(CorrectionReceiptPath(root, input.ReceiptId), 8192);
        _ = ValidateCorrectionReceipt(root, receipt);
        if (identity.SchemaVersion != SchemaVersion || identity.SourceAdapter != "operator"
            || identity.SourceCapability != "correction" || identity.ObligationId != input.EventId
            || identity.ObligationKey != "correction:" + input.ReceiptId
            || identity.Repository != state.Repository || input.Request.Repository != state.Repository
            || identity.ClaimGeneration != state.ClaimGeneration || input.Request.ClaimGeneration != state.ClaimGeneration
            || input.Request.Holder != state.Holder || identity.ConfigurationSha256 != ConfigurationDigest(state)
            || identity.ConfigurationSha256 != input.ConfigurationSha256
            || identity.ContextSha256 != receipt.SourceSha256 || receipt.EventId != input.EventId
            || FileDigest(Path.Combine(directory, "source.json")) != receipt.SourceSha256)
            throw new IOException("Correction retained source identity drifted.");
    }

    private void ValidateFinalHostedAuthority(QueueSnapshot queue, ConductorClaimRecord? claim,
        string? attachmentId, bool daemon)
    {
        if (!ConductorClaimStore.IsCurrentHostedAuthority(claim, _request.Holder, _generation))
        {
            if (ConductorClaimStore.IsCurrentHostedAcquisition(claim, _request.Holder, _generation) && claim!.Held)
                throw new HostedConductorHeldException();
            throw new CliArgumentException("Hosted authority revoked before launch.");
        }
        if (daemon && queue.Held)
            throw new HostedConductorAdmissionPendingException("Global queue Hold prevents correction launch.");
        if (ReadCeiling(_root, _request.Workspace) != _ceiling
            || File.Exists(Path.Combine(_directory, "request.json"))
                && Read<ConductorFollowRequest>(Path.Combine(_directory, "request.json")) != _request)
            throw new CliArgumentException("Follow queue, request or grant changed before launch.");
        if (File.Exists(AttachmentPath))
        {
            var current = Read<ConductorFollowAttachment>(AttachmentPath);
            ValidateAttachment(current);
            if (!current.Attached || current.Id != attachmentId)
                throw new CliArgumentException("Follow attachment revoked before launch.");
        }
        else if (attachmentId is not null) throw new CliArgumentException("Follow attachment missing before launch.");
    }

    private void GuardOrdinaryCorrectionPriority(string key, string eventId, string contextSha256)
    {
        if (!File.Exists(CorrectionSlotPath)) return;
        var slot = ReadCorrectionSlot(CorrectionSlotPath);
        if (!File.Exists(CorrectionReceiptPath(_root, slot.ReceiptId))) return;
        _ = ValidateCorrectionReceipt(_root, Read<ConductorCorrectionReceipt>(CorrectionReceiptPath(_root, slot.ReceiptId)));
        if (slot.State is "delivered" or "not-delivered") return;
        if (slot.Predecessor is not { Disposition: "pending" } predecessor || predecessor.Key != key || predecessor.ContextSha256 != contextSha256)
            throw new CliArgumentException("Accepted correction has priority over new ordinary input.");
        Write(CorrectionSlotPath, slot with { Predecessor = predecessor with { Disposition = "opportunity", EventId = eventId } });
    }

    internal static async Task<HostedFollowTarget?> CorrectionTargetAsync(string root, string repository, CancellationToken token)
    {
        var identity = RepositoryIdentity.From("https://" + repository, null);
        if (identity is null) return null;
        var registrationPath = Path.Combine(root, "conductor-follow", identity.FileSlug, "registration.json");
        try
        {
            var registration = Read<ConductorFollowAttachment>(registrationPath);
            ValidateControlRegistration(identity, root, registration, registration.Holder, registration.ClaimGeneration, registration.Id);
            var directory = CorrectionDirectory(root, repository, registration.ClaimGeneration);
            if (!File.Exists(Path.Combine(directory, "correction.json"))) return null;
            var slot = ReadCorrectionSlot(Path.Combine(directory, "correction.json"));
            if (!File.Exists(CorrectionReceiptPath(root, slot.ReceiptId)) || slot.State is "delivered" or "not-delivered") return null;
            _ = ValidateCorrectionReceipt(root, Read<ConductorCorrectionReceipt>(CorrectionReceiptPath(root, slot.ReceiptId)));
            token.ThrowIfCancellationRequested();
            return new(repository, registration.Holder, registration.ClaimGeneration, registration.Id);
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            Console.Error.WriteLine("Correction target evidence unavailable; retained input remains unresolved.");
            return null;
        }
    }

    internal static HostedFollowTarget? HostedTargetFor(QueueItem source, string root)
    {
        if (source.StoppedWorkJudgment?.FollowAttachmentId is not { } attachment || source.Repository is null) return null;
        try
        {
            var identity = RepositoryIdentity.From("https://" + source.Repository, null);
            if (identity is null) return null;
            var registration = Read<ConductorFollowAttachment>(Path.Combine(root, "conductor-follow", identity.FileSlug, "registration.json"));
            ValidateControlRegistration(identity, root, registration, registration.Holder, registration.ClaimGeneration, attachment);
            return new(source.Repository, registration.Holder, registration.ClaimGeneration, attachment);
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            Console.Error.WriteLine("Hosted continuation identity unavailable; source remains retained.");
            return null;
        }
    }

    internal static async Task<ConductorFollowResult?> NotifyCorrectionAsync(HostedFollowTarget target, string root,
        CancellationToken token, ConductorFollowBroker? broker = null,
        Func<string, CancellationToken, Task<RepositoryIdentity?>>? resolver = null)
    {
        var directory = CorrectionDirectory(root, target.Repository, target.ClaimGeneration);
        // Historical issued delivery recovery must work after authority changes.
        var request = Read<ConductorFollowRequest>(Path.Combine(directory, "request.json"));
        var state = Read<ConductorFollowState>(Path.Combine(directory, "session.json"));
        var identity = RepositoryIdentity.From("https://" + target.Repository, null)!;
        var session = new ConductorFollowSession(root, identity, request, state.ProjectCeiling, target.ClaimGeneration,
            broker ?? ((configuration, prompt, outputDirectory, inputs, output, error, ct, started) =>
                CodexAppServerBroker.RunAsync(configuration, prompt, outputDirectory, inputs, output, error, ct, started)));
        // No remote work or session wait in acceptance. Delivery retains existing resolver checks.
        return await session.DeliverCorrectionAsync(token, resolver ?? RepositoryIdentityResolver.TryResolveAsync).ConfigureAwait(false);
    }

    private async Task UpdateCorrectionSlotAsync(string eventId, Func<ConductorCorrectionSlot, ConductorCorrectionSlot> update,
        CancellationToken token)
    {
        await QueueStore.MutateWithCurrentClaimAsync(Path.Combine(_root, "queue", "queue.json"), _identity, _root, (queue, _) =>
        {
            var current = ReadCorrectionSlot(CorrectionSlotPath);
            if (current.EventId == eventId) Write(CorrectionSlotPath, update(current));
            return queue;
        }, token, GlassLockTimeout).ConfigureAwait(false);
    }

    private async Task<ConductorFollowResult> DeliverCorrectionAsync(CancellationToken token,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> resolver)
    {
        try
        {
            return await Task.Run(() => MutexGuardedFileLock.RunUnderLock(StatePath, LockPrefix, TimeSpan.FromMilliseconds(100),
                () => DeliverCorrectionUnderLockAsync(token, resolver).GetAwaiter().GetResult()), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            return new("refused", null, null, _directory, "Session busy or retained correction evidence unavailable; accepted input remains pending.");
        }
    }

    private async Task<ConductorFollowResult> DeliverCorrectionUnderLockAsync(CancellationToken token,
        Func<string, CancellationToken, Task<RepositoryIdentity?>> resolver)
    {
        var slot = ReadCorrectionSlot(CorrectionSlotPath);
        if (!File.Exists(CorrectionReceiptPath(_root, slot.ReceiptId))) return new("refused", null, null, _directory, "Uncommitted preparation cannot launch.");
        var receipt = Read<ConductorCorrectionReceipt>(CorrectionReceiptPath(_root, slot.ReceiptId));
        var input = ValidateCorrectionReceipt(_root, receipt);
        var evidence = Path.Combine(_directory, "events", Digest(slot.EventId));
        var state = LoadState();
        ValidateState(state);
        var launched = File.Exists(Path.Combine(evidence, "launch.json"));
        try
        {
            RecoverSession(state);
            if (File.Exists(Path.Combine(evidence, "response.json")))
            {
                var identity = Read<ConductorFollowEventIdentity>(Path.Combine(evidence, "identity.json"));
                ValidateCorrectionEventSource(evidence, identity, state);
                var response = ReadResponse(Path.Combine(evidence, "response.json"), identity, state);
                var proof = EnsureReceipt(evidence);
                await UpdateCorrectionSlotAsync(slot.EventId, s => s with { State = "delivered", Reason = "Read-only turn completed; application is not verified." }, token).ConfigureAwait(false);
                return new("replayed", identity.ObligationKey, slot.EventId, evidence, "Completed correction recovered without another call.", proof, response);
            }
            if (state.Frozen || launched) throw new IOException("Correction session is frozen or issued outcome is unresolved.");
            if (slot.State is "delivered" or "not-delivered") return new("replayed", null, slot.EventId, evidence, slot.Reason);
            var claim = await ConductorClaimStore.GetClaimAsync(_identity, _root, token, GlassLockTimeout).ConfigureAwait(false);
            var registration = Read<ConductorFollowAttachment>(AttachmentPath);
            ValidateAttachment(registration);
            if (!ConductorClaimStore.IsCurrentHostedAcquisition(claim, _request.Holder, _generation)
                || !registration.Attached || registration.Id != receipt.AttachmentId)
            {
                await UpdateCorrectionSlotAsync(slot.EventId, s =>
                {
                    var path = Path.Combine(_directory, "correction-inputs", slot.EventId + ".revoked.json");
                    if (File.Exists(Path.Combine(evidence, "launch.json"))) throw new IOException("Issued input cannot be revoked.");
                    if (!File.Exists(path)) WriteImmutable(path, new ConductorCorrectionRevocation(slot.ReceiptId, slot.EventId,
                        slot.SourceSha256, DateTimeOffset.UtcNow, "Unissued input revoked by ownership or attachment change."));
                    return s with { State = "not-delivered", Reason = "Unissued input revoked by ownership or attachment change." };
                }, token).ConfigureAwait(false);
                return new("refused", null, slot.EventId, evidence, "Unissued correction revoked; not delivered.");
            }
            if (claim!.Held) throw new HostedConductorHeldException();
            var queue = await QueueStore.LoadAsync(Path.Combine(_root, "queue", "queue.json"), token, GlassLockTimeout).ConfigureAwait(false);
            if (queue.Held) throw new CliArgumentException("Global queue Hold prevents correction launch.");
            if (await resolver(_request.Workspace, token).WaitAsync(token).ConfigureAwait(false) != _identity)
                throw new CliArgumentException("Correction workspace identity changed.");
            if (ReadCeiling(_root, _request.Workspace) != _ceiling || input.ConfigurationSha256 != ConfigurationDigest(state)
                || input.RequestSha256 != Digest(JsonSerializer.Serialize(_request, Json)))
                throw new CliArgumentException("Recorded trust or correction configuration changed.");
            if (slot.Predecessor is { Disposition: "pending" } predecessor)
            {
                var rows = queue.Items.Where(item => item.StoppedWorkJudgment?.Key == predecessor.Key).ToArray();
                if (rows.Length == 1 && rows[0].Halted && rows[0].Retirement is null && rows[0].CancelledAt is null
                    && rows[0].ReplacementReviewAction is null && rows[0].StoppedWorkJudgment!.ContextSha256 == predecessor.ContextSha256
                    && rows[0].StoppedWorkJudgment!.FollowContinuationPending
                    && rows[0].StoppedWorkJudgment!.FollowAttachmentId == receipt.AttachmentId)
                    throw new CliArgumentException("Retained ordinary predecessor awaits its admission opportunity.");
                await UpdateCorrectionSlotAsync(slot.EventId, s => s with { Predecessor = predecessor with { Disposition = "resolved" } }, token).ConfigureAwait(false);
            }
            if (slot.Predecessor is { Disposition: "uncertain" })
                throw new IOException("Ordinary predecessor outcome is uncertain.");
            if (slot.Predecessor is { Disposition: "opportunity" } opportunity)
                await UpdateCorrectionSlotAsync(slot.EventId, s => s with { Predecessor = opportunity with { Disposition = "resolved" } }, token).ConfigureAwait(false);
            await AdmitDaemonTurnAsync(slot.EventId, token).ConfigureAwait(false);
            Directory.CreateDirectory(evidence);
            var identityEvidence = new ConductorFollowEventIdentity(SchemaVersion, "correction:" + slot.ReceiptId,
                slot.EventId, _identity.Value, _generation, state.SessionId, ConfigurationDigest(state), "operator", "correction", slot.SourceSha256, "correction");
            Write(Path.Combine(evidence, "identity.json"), identityEvidence);
            var source = Path.Combine(evidence, "source.json");
            if (!File.Exists(source)) WriteImmutable(source, input);
            if (FileDigest(source) != slot.SourceSha256) throw new IOException("Correction source changed.");
            AfterCorrectionPersistence?.Invoke("prelaunch");
            await QueueStore.MutateWithCurrentClaimAsync(Path.Combine(_root, "queue", "queue.json"), _identity, _root,
                (currentQueue, currentClaim) =>
                {
                    ValidateFinalHostedAuthority(currentQueue, currentClaim, receipt.AttachmentId, daemon: true);
                    var currentSlot = ReadCorrectionSlot(CorrectionSlotPath);
                    if (currentSlot.EventId != slot.EventId || currentSlot.ReceiptId != slot.ReceiptId
                        || currentSlot.SourceSha256 != slot.SourceSha256 || currentSlot.State is "delivered" or "not-delivered"
                        || currentSlot.Predecessor is { Disposition: "pending" or "uncertain" })
                        throw new CliArgumentException("Correction slot or predecessor changed before launch.");
                    _ = ValidateCorrectionReceipt(_root, Read<ConductorCorrectionReceipt>(CorrectionReceiptPath(_root, slot.ReceiptId)));
                    Write(Path.Combine(evidence, "launch.json"), new ConductorFollowLaunch(SchemaVersion,
                        identityEvidence.ObligationKey, slot.EventId, state.SessionId));
                    return currentQueue;
                }, token, GlassLockTimeout).ConfigureAwait(false);
            launched = true;
            AfterCorrectionPersistence?.Invoke("marker");
            await UpdateCorrectionSlotAsync(slot.EventId, s => s with { State = "issued", Reason = "Turn issued; outcome pending." }, token).ConfigureAwait(false);
            var delivered = await ExecuteFollowTurnAsync(evidence, source, identityEvidence, state,
                "Read the explicit source.json operator correction. Its text is the accepted operator input for this separate read-only turn. "
                + "Use the existing conversation and file-read-only grant. Delivery grants no action authority and does not prove application.", token).ConfigureAwait(false);
            AfterCorrectionPersistence?.Invoke("response");
            var proofReceipt = EnsureReceipt(evidence);
            AfterCorrectionPersistence?.Invoke("delivery");
            RefuseCorrectionDecision(evidence);
            AppendJournal(new(SchemaVersion, identityEvidence.ObligationKey, slot.EventId, delivered.SessionId,
                FileDigest(Path.Combine(evidence, "response.json")), proofReceipt));
            await UpdateCorrectionSlotAsync(slot.EventId, s => s with { State = "delivered", Reason = "Read-only turn completed; application is not verified." }, token).ConfigureAwait(false);
            return new("delivered", identityEvidence.ObligationKey, slot.EventId, evidence,
                "Correction turn completed; application is not verified.", proofReceipt, delivered);
        }
        catch (HostedConductorHeldException)
        {
            await UpdateCorrectionSlotAsync(slot.EventId, s => s with { State = "waiting", Reason = "Hosted acquisition is Held; Unhold permits reconciliation." }, token).ConfigureAwait(false);
            return new("held-pending", null, slot.EventId, evidence, "Hosted acquisition is Held; correction remains pending.");
        }
        catch (Exception ex) when (IsRefusal(ex) || ex is OperationCanceledException)
        {
            // Recovery corruption, including planted decisions, freezes even without a new marker.
            var uncertain = launched || ex is IOException;
            if (uncertain && !File.Exists(Path.Combine(evidence, "response.json"))) WriteState(Read<ConductorFollowState>(StatePath) with { Frozen = true });
            await UpdateCorrectionSlotAsync(slot.EventId, s => s with
            {
                State = uncertain ? "uncertain" : "waiting",
                Reason = uncertain ? "Issued or retained evidence is uncertain; owner must inspect. No retry authority."
                    : ex is CliArgumentException or HostedConductorAdmissionPendingException ? SafeCorrectionWait(ex.Message) : "Current authority or admission cannot be verified; scheduler reconciliation rechecks.",
            }, CancellationToken.None).ConfigureAwait(false);
            return new(uncertain ? "uncertain" : "refused", null, slot.EventId, evidence,
                uncertain ? "Correction delivery uncertain; input remains unresolved." : "Correction remains waiting for admission.");
        }
    }

    private static string SafeCorrectionWait(string reason) => reason switch
    {
        "Global queue Hold prevents correction launch." => reason,
        "Retained ordinary predecessor awaits its admission opportunity." => reason,
        "Required daemon runway admission refused; no launch admitted." => reason,
        "Recorded trust or correction configuration changed." => reason,
        "Correction workspace identity changed." => reason,
        _ => "Current authority or admission cannot be verified; scheduler reconciliation rechecks.",
    };

    internal static async Task ResolveCorrectionPredecessorAsync(HostedFollowTarget target, string root,
        string key, ConductorFollowResult? result, CancellationToken token)
    {
        var directory = CorrectionDirectory(root, target.Repository, target.ClaimGeneration);
        var path = Path.Combine(directory, "correction.json");
        if (!File.Exists(path)) return;
        var identity = RepositoryIdentity.From("https://" + target.Repository, null)!;
        await QueueStore.MutateWithCurrentClaimAsync(Path.Combine(root, "queue", "queue.json"), identity, root, (queue, claim) =>
        {
            var slot = ReadCorrectionSlot(path);
            if (!File.Exists(CorrectionReceiptPath(root, slot.ReceiptId)) || slot.Predecessor is not { Disposition: "pending" or "opportunity" } p || p.Key != key)
                return queue;
            // Reversible acquisition restrictions preserve the opportunity. Mutable-head refusals
            // still retain ordinary scheduler eligibility independently of this bounded fairness slot.
            var request = Read<ConductorFollowRequest>(Path.Combine(directory, "request.json"));
            var state = Read<ConductorFollowState>(Path.Combine(directory, "session.json"));
            if (queue.Held || !ConductorClaimStore.IsCurrentHostedAuthority(claim, target.Holder, target.ClaimGeneration)
                || ReadCeiling(root, request.Workspace) != state.ProjectCeiling
                || result?.Status is "held-pending" or "admission-pending") return queue;
            var disposition = result?.Status == "uncertain" ? "uncertain" : "resolved";
            Write(path, slot with { Predecessor = p with { Disposition = disposition } });
            return queue;
        }, token, GlassLockTimeout).ConfigureAwait(false);
    }

    private async Task<ConductorFollowResponse> ExecuteFollowTurnAsync(string evidence, string sourcePath,
        ConductorFollowEventIdentity identity, ConductorFollowState state, string instructions, CancellationToken token)
    {
        using var transcript = new BoundedWriter(MaxResponseBytes / 2);
        using var error = new BoundedWriter(8192);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(state.TimeoutSeconds));
        var configuration = new CodexBrokerConfiguration(_request.Workspace, state.Model, state.Effort,
            state.SessionId, state.SessionId is not null, state.EffectiveGrant, ["response.txt"], false);
        var output = Path.Combine(evidence, "output");
        Directory.CreateDirectory(output);
        try
        {
            var exit = await _broker(configuration, (state.SessionId is null ? _request.InitialInstructions + "\n" : "") + instructions,
                output, [sourcePath], transcript, error, timeout.Token, (threadId, _) =>
                {
                    if (string.IsNullOrWhiteSpace(threadId) || threadId.Length > 256 || threadId.Any(char.IsControl)
                        || state.SessionId is not null && state.SessionId != threadId)
                        throw new InvalidOperationException("Native thread identity changed or missing.");
                    state = state with { SessionId = threadId };
                    WriteState(state);
                    identity = identity with { SessionId = threadId };
                    Write(Path.Combine(evidence, "identity.json"), identity);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            var lines = transcript.ToString().Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            if (exit != 0 || !Complete(lines, state.SessionId)) throw new IOException("Native turn did not complete.");
            var retained = new ConductorFollowResponse(SchemaVersion, identity.ObligationKey, identity.ObligationId, state.SessionId!, exit, lines);
            Write(Path.Combine(evidence, "response.json"), retained);
            _ = ReadResponse(Path.Combine(evidence, "response.json"), identity, state);
            return retained;
        }
        catch (Exception ex) when (IsRefusal(ex) || ex is OperationCanceledException)
        {
            WriteAtomic(Path.Combine(evidence, "partial-transcript.jsonl"), transcript.ToString());
            WriteAtomic(Path.Combine(evidence, "broker-error.txt"), error.ToString());
            throw;
        }
    }
}
