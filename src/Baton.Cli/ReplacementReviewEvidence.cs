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

    internal static string For(QueueReplacementReviewAction action) =>
        action.EvidenceProvenance ?? LegacyAdvice;
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
        string holder, string expectedHead, string? claimGeneration = null)
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
            || evidence.Decision.RequestSha256.Length != 64
            || evidence.Decision.ClaimGeneration != claimGeneration && claimGeneration is not null
            || evidence.Decision.SessionId.Length == 0
            || evidence.Decision.ConfigurationSha256.Length != 64
            || evidence.Digest != evidence.Decision.ResponseSha256
            || obligation.Status is not (ConductorObligationStatus.Pending or ConductorObligationStatus.Submitted)
            || obligation.TransportReceipt is not null)
            throw new ConductorObligationStoreException("Completed follow evidence is not an authentic replacement decision.");

        var directory = evidence.EvidenceDirectory;
        var retained = ConductorFollowSession.ReadDecisionEvidence(directory);
        if (retained != evidence.Decision)
            throw new ConductorObligationStoreException("Completed follow decision evidence changed.");
        ValidateFiles(directory, evidence.Decision, obligation, holder);
    }

    internal static void ValidateCompletedFollowAction(
        ConductorObligation obligation, QueueItem source, QueueReplacementReviewAction action,
        string? claimGeneration = null)
    {
        if (ReplacementReviewEvidenceProvenance.For(action) != ReplacementReviewEvidenceProvenance.CompletedFollow
            || action.EvidenceDigest is null || action.EvidenceDirectory is null)
            throw new ConductorObligationStoreException("Replacement review follow provenance is incomplete.");
        var decision = ConductorFollowSession.ReadDecisionEvidence(action.EvidenceDirectory)
            ?? throw new ConductorObligationStoreException("Completed follow decision evidence is missing.");
        ValidateCompletedFollow(obligation, source,
            new ReplacementReviewEvidenceInput(ReplacementReviewEvidenceProvenance.CompletedFollow,
                action.EvidenceDigest, action.EvidenceDirectory, decision), action.Holder, action.HeadSha,
            claimGeneration);
    }

    private static void ValidateFiles(
        string directory, ConductorFollowDecisionEvidence decision,
        ConductorObligation obligation, string holder)
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
                || state.ProjectCeiling != ProjectCeilingStore.TryGet(request.Workspace,
                    Path.Combine(BatonPaths.Root, "project-ceilings.json"))
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
            or JsonException or InvalidOperationException)
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
