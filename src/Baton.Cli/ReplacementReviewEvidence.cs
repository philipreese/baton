using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Conductor;
using Baton.Cli.Daemon;
using Baton.Domain;
using Baton.Queue;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli;

internal static class ReplacementReviewEvidenceProvenance
{
    internal const string LegacyAdvice = "legacy-advice";
    internal const string CompletedFollow = "completed-follow";

    internal static string For(QueueReplacementReviewAction action) => action.EvidenceProvenance switch
    {
        null or LegacyAdvice when action.EvidenceDigest is null && action.EvidenceDirectory is null => LegacyAdvice,
        CompletedFollow when action.AdviceDigest == string.Empty
            && action.EvidenceDigest is { Length: > 0 } && action.EvidenceDirectory is { Length: > 0 } => CompletedFollow,
        _ => throw new ConductorObligationStoreException("Replacement review evidence provenance is missing or conflicting."),
    };
}

internal sealed record ReplacementReviewEvidenceInput(
    string Provenance,
    string Digest,
    string EvidenceDirectory,
    ConductorFollowDecisionEvidence Decision);

internal static class ReplacementReviewEvidenceValidator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static void ValidateCompletedFollow(
        ConductorObligation obligation, QueueItem source, ReplacementReviewEvidenceInput evidence,
        string holder, string expectedHead, ConductorClaimRecord? claim)
    {
        ValidateCompletedFollowCore(obligation, source, evidence, holder, expectedHead,
            claim is null ? null : ConductorClaimStore.GetClaimGeneration(claim), null);
        ValidateLiveAdmission(source, evidence.Decision, evidence.EvidenceDirectory, holder, claim);
    }

    private static void ValidateCompletedFollowCore(
        ConductorObligation obligation, QueueItem source, ReplacementReviewEvidenceInput evidence,
        string holder, string expectedHead, string? claimGeneration, string? completionProof)
    {
        if (evidence.Provenance != ReplacementReviewEvidenceProvenance.CompletedFollow
            || evidence.Digest.Length == 0 || evidence.Decision.Decision != "ReplaceReview"
            || evidence.Decision.ObligationKey != obligation.IdempotencyKey
            || evidence.Decision.ObligationId != obligation.ObligationId
            || evidence.Decision.SourceHeadSha != expectedHead
            || evidence.Decision.SourceRepository != obligation.TargetProject
            || evidence.Decision.SourceRepository != source.Repository
            || evidence.Decision.SourceContextSha256 != obligation.ContextSha256
            || evidence.Decision.SourceContextSha256 != source.StoppedWorkJudgment?.ContextSha256
            || evidence.Decision.RequestSha256 is not { Length: 64 }
            || evidence.Decision.ClaimGeneration != claimGeneration && claimGeneration is not null
            || string.IsNullOrEmpty(evidence.Decision.SessionId)
            || evidence.Decision.ConfigurationSha256 is not { Length: 64 }
            || evidence.Digest != evidence.Decision.ResponseSha256
            || obligation.Status is not (ConductorObligationStatus.Pending or ConductorObligationStatus.Submitted)
                && (obligation.Status != ConductorObligationStatus.ActionObserved
                    || string.IsNullOrEmpty(completionProof) || obligation.ActionProof != completionProof)
            || obligation.TransportReceipt is not null)
            throw new ConductorObligationStoreException("Completed follow evidence is not an authentic replacement decision.");

        var directory = evidence.EvidenceDirectory;
        var retained = ConductorFollowSession.ReadDecisionEvidence(directory);
        if (retained != evidence.Decision)
            throw new ConductorObligationStoreException("Completed follow decision evidence changed.");
        ValidateFiles(directory, evidence.Decision, obligation, source, holder);
    }

    internal static void ValidateCompletedFollowActionAdmission(
        ConductorObligation obligation, QueueItem source, QueueReplacementReviewAction action,
        ConductorClaimRecord? claim)
    {
        if (ReplacementReviewEvidenceProvenance.For(action) != ReplacementReviewEvidenceProvenance.CompletedFollow
            || action.EvidenceDigest is null || action.EvidenceDirectory is null)
            throw new ConductorObligationStoreException("Replacement review follow provenance is incomplete.");
        var decision = ConductorFollowSession.ReadDecisionEvidence(action.EvidenceDirectory)
            ?? throw new ConductorObligationStoreException("Completed follow decision evidence is missing.");
        ValidateCompletedFollowCore(obligation, source,
            new ReplacementReviewEvidenceInput(ReplacementReviewEvidenceProvenance.CompletedFollow,
                action.EvidenceDigest, action.EvidenceDirectory, decision), action.Holder, action.HeadSha,
            claim is null ? null : ConductorClaimStore.GetClaimGeneration(claim), action.CompletionProof);
        ValidateLiveAdmission(source, decision, action.EvidenceDirectory, action.Holder, claim);
    }

    internal static QueueHostedIssuedAuthority BindIssuedAuthority(QueueItem source, QueueReplacementReviewAction action,
        Baton.Domain.FleetAttemptId attempt, string room, DateTimeOffset issuedAt)
    {
        var decision = ConductorFollowSession.ReadDecisionEvidence(action.EvidenceDirectory!)
            ?? throw new ConductorObligationStoreException("Completed follow decision evidence is missing.");
        return new(1, action.Repository, action.Holder, decision.ClaimGeneration,
            source.StoppedWorkJudgment!.FollowAttachmentId!, action.ObligationKey, action.SourceAttemptId, action.HeadSha,
            decision.RequestSha256, decision.ConfigurationSha256, decision.SessionId, decision.ResponseSha256,
            attempt, room, issuedAt);
    }

    internal static void ValidateIssuedCompletedFollowCompletion(
        ConductorObligation obligation, QueueItem source, QueueReplacementReviewAction action)
    {
        if (ReplacementReviewEvidenceProvenance.For(action) != ReplacementReviewEvidenceProvenance.CompletedFollow
            || action.EvidenceDirectory is null || action.EvidenceDigest is null || action.IssuedAuthority is not { } issued
            || issued.SchemaVersion != 1 || issued.Repository != action.Repository || issued.Holder != action.Holder
            || issued.ObligationKey != action.ObligationKey || issued.SourceAttemptId != action.SourceAttemptId
            || issued.HeadSha != action.HeadSha || issued.AttachmentId != source.StoppedWorkJudgment?.FollowAttachmentId
            || issued.AttemptId != action.ReplacementAttemptId || issued.RoomDirectory != action.ReplacementRoomDirectory
            || issued.IssuedAt == default || issued.IssuedAt.Offset != TimeSpan.Zero
            || issued.AttachmentId is not { Length: 32 } || !issued.AttachmentId.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(issued.ClaimGeneration))
            throw new ConductorObligationStoreException("Historical issued authority is missing or inconsistent; no new admission is permitted.");
        var decision = ConductorFollowSession.ReadDecisionEvidence(action.EvidenceDirectory)
            ?? throw new ConductorObligationStoreException("Completed follow decision evidence is missing.");
        if (issued.RequestSha256 != decision.RequestSha256 || issued.ConfigurationSha256 != decision.ConfigurationSha256
            || issued.SessionId != decision.SessionId || issued.ResponseSha256 != decision.ResponseSha256)
            throw new ConductorObligationStoreException("Issued decision binding changed.");
        ValidateCompletedFollowCore(obligation, source,
            new(ReplacementReviewEvidenceProvenance.CompletedFollow, action.EvidenceDigest, action.EvidenceDirectory, decision),
            action.Holder, action.HeadSha, issued.ClaimGeneration, action.CompletionProof);
    }

    private static void ValidateLiveAdmission(QueueItem source, ConductorFollowDecisionEvidence decision, string directory,
        string holder, ConductorClaimRecord? claim)
    {
        var registration = ConductorFollowSession.ValidateCurrentHostedAuthority(claim, decision.SourceRepository,
            holder, decision.ClaimGeneration, source.StoppedWorkJudgment?.FollowAttachmentId, BatonPaths.Root);
        var state = JsonSerializer.Deserialize<ConductorFollowState>(File.ReadAllText(Path.Combine(directory, "..", "..", "session.json")), Json);
        if (registration.RequestSha256 != decision.RequestSha256
            || state?.ProjectCeiling != ProjectCeilingStore.TryGet(state!.Workspace, Path.Combine(BatonPaths.Root, "project-ceilings.json")))
            throw new ConductorObligationStoreException("Current hosted trust or registration changed.");
    }

    private static void ValidateFiles(
        string directory, ConductorFollowDecisionEvidence decision,
        ConductorObligation obligation, QueueItem queueSource, string holder)
    {
        try
        {
            var response = Path.Combine(directory, "response.json");
            var source = Path.Combine(directory, "source.json");
            var identity = JsonSerializer.Deserialize<ConductorFollowEventIdentity>(
                File.ReadAllText(Path.Combine(directory, "identity.json")), Json)
                ?? throw new IOException("Missing follow identity.");
            var state = JsonSerializer.Deserialize<ConductorFollowState>(
                File.ReadAllText(Path.Combine(directory, "..", "..", "session.json")), Json)
                ?? throw new IOException("Missing follow state.");
            var request = JsonSerializer.Deserialize<ConductorFollowRequest>(
                File.ReadAllText(Path.Combine(directory, "..", "..", "request.json")), Json)
                ?? throw new IOException("Missing follow request.");
            var responseDigest = DigestFile(response);
            var sourceDigest = DigestFile(source);
            var receipt = File.ReadAllText(Path.Combine(directory, "receipt.txt"));
            var requestDigest = DigestFile(Path.Combine(directory, "..", "..", "request.json"));
            // JsonRequired checks presence, not nullability. Refuse corrupt persisted fields
            // before path, hash or ceiling operations, preserving an evidence refusal at admission.
            if (request.InitialInstructions is null || state.ProjectCeiling is null)
                throw new IOException("Completed follow evidence has missing required values.");
            if (identity.ObligationKey != decision.ObligationKey || identity.ObligationId != decision.ObligationId
                || identity.Repository != decision.SourceRepository
                || identity.ContextSha256 != decision.SourceContextSha256
                || identity.ClaimGeneration != decision.ClaimGeneration
                || identity.SessionId != decision.SessionId
                || identity.ConfigurationSha256 != decision.ConfigurationSha256
                || state.Repository != decision.SourceRepository || state.Holder != holder
                || state.ClaimGeneration != decision.ClaimGeneration || state.SessionId != decision.SessionId
                || state.Workspace != request.Workspace || state.Adapter != request.Adapter
                || state.Model != request.Model || state.Effort != request.Effort
                || state.TimeoutSeconds != request.TimeoutSeconds
                || state.InitialInstructionsSha256 != DigestText(request.InitialInstructions)
                || state.EffectiveGrant != request.PermissionGrant
                || state.ProjectCeiling.Cap(request.PermissionGrant) != request.PermissionGrant
                || ConductorFollowSession.ConfigurationDigest(state) != decision.ConfigurationSha256
                || responseDigest != decision.ResponseSha256
                || sourceDigest != decision.SourceDigest
                || requestDigest != decision.RequestSha256
                || receipt.Trim() != decision.Receipt
                || decision.Receipt != "follow-sha256:" + responseDigest
                || obligation.TargetProject != decision.SourceRepository)
                throw new IOException("Completed follow evidence binding changed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or ArgumentException)
        {
            throw new ConductorObligationStoreException(
                "Completed follow evidence is missing or inconsistent.", ex);
        }
    }

    private static string DigestFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string DigestText(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
