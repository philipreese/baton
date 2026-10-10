using System.Diagnostics;
using Baton.Accounting;
using Baton.Conductor;
using Baton.Queue;

namespace Baton.Cli;

internal sealed record GlassRetainedJudgment(string EventIdentity, string ObligationId, string ObligationKey,
    string SourceRepository, string ClaimGeneration, string Tag, string SourceAttempt, string SourceHeadSha,
    DateTimeOffset SourceObservedAt, string Decision, string? ActionDisposition);

internal sealed record GlassRetainedJudgmentHistory(IReadOnlyList<GlassRetainedJudgment> Judgments,
    int InvalidEvents = 0, int ExcludedEvents = 0, int OmittedJudgments = 0, string? Diagnostic = null);

internal sealed partial class ConductorFollowSession
{
    private static readonly AsyncLocal<HistoryEvidenceBudget?> HistoryReadBudget = new();

    private sealed class HistoryBudgetException() : IOException("inspection incomplete; additional history not checked");

    // Charges actual evidence reads, including repeated proof/digest reads, conservatively.
    // AsyncLocal keeps this read policy scoped to the history caller, away from delivery/recovery.
    private sealed class HistoryEvidenceBudget(long bytes, Func<TimeSpan> elapsed)
    {
        internal void Check()
        {
            if (bytes <= 0 || elapsed() >= TimeSpan.FromSeconds(1)) throw new HistoryBudgetException();
        }

        internal byte[] ReadBytes(string path, int bound)
        {
            Check();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, RetainedReadShare);
            if (stream.Length > bytes) throw new HistoryBudgetException();
            if (stream.Length > bound) throw new IOException("Evidence exceeds its bound.");
            using var result = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                Check();
                var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, Math.Min(bytes, bound + 1L - result.Length)));
                bytes -= count;
                if (count == 0) break;
                result.Write(buffer, 0, count);
                if (result.Length > bound) throw new IOException("Evidence exceeds its bound.");
            }
            Check();
            return result.ToArray();
        }
    }

    internal static GlassRetainedJudgmentHistory ReadGlassHistory(RepositoryIdentity identity,
        string root, IReadOnlyList<QueueItem>? items = null, long byteLimit = 8 * 1024 * 1024,
        Func<TimeSpan>? elapsed = null, CancellationToken token = default)
    {
        var rows = new List<GlassRetainedJudgment>();
        var invalid = 0;
        var excluded = 0;
        var omitted = 0;
        var timer = Stopwatch.StartNew();
        var budget = new HistoryEvidenceBudget(Math.Min(byteLimit, 8 * 1024 * 1024),
            () => token.IsCancellationRequested ? TimeSpan.FromSeconds(1) : elapsed?.Invoke() ?? timer.Elapsed);
        var previous = HistoryReadBudget.Value;
        HistoryReadBudget.Value = budget;
        try
        {
            var registrationPath = Path.Combine(root, "conductor-follow", identity.FileSlug, "registration.json");
            if (!File.Exists(registrationPath))
                return new(rows, Diagnostic: "No retained automatic delivery registration.");
            var registration = Read<ConductorFollowAttachment>(registrationPath);
            ValidateControlRegistration(identity, root, registration, registration.Holder, registration.ClaimGeneration, registration.Id);
            var directory = Path.Combine(Path.GetFullPath(root), "conductor-follow", identity.FileSlug,
                Digest(identity.Value + "\n" + registration.ClaimGeneration));
            var request = Read<ConductorFollowRequest>(Path.Combine(directory, "request.json"));
            ValidateRequest(request);
            var state = Read<ConductorFollowState>(Path.Combine(directory, "session.json"));
            if (state.ProjectCeiling is null) throw new IOException("Retained configuration is missing.");
            var session = new ConductorFollowSession(Path.GetFullPath(root), identity, request, state.ProjectCeiling,
                registration.ClaimGeneration, (_, _, _, _, _, _, _, _) => throw new InvalidOperationException("History cannot launch."));
            session.ValidateState(state);
            if (state.ProjectCeiling.Cap(request.PermissionGrant) != SupportedGrant)
                throw new IOException("Retained grant drifted.");
            var events = Path.Combine(directory, "events");
            RejectLinks(events);
            if (!Directory.Exists(events)) return new(rows);
            // Never sort an unbounded enumeration. The extra name is solely the overflow sentinel.
            var names = Directory.EnumerateDirectories(events).Take(101).ToArray();
            budget.Check();
            if (names.Length > 100) return new(rows, Diagnostic: "history inspection limit reached");
            var journal = new Dictionary<string, ConductorFollowJournalEntry>(StringComparer.Ordinal);
            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            if (File.Exists(session.JournalPath))
            {
                foreach (var line in ReadText(session.JournalPath, MaxResponseBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    budget.Check();
                    try
                    {
                        var entry = Deserialize<ConductorFollowJournalEntry>(line);
                        if (entry.SchemaVersion != SchemaVersion || string.IsNullOrWhiteSpace(entry.ObligationId)) continue;
                        if (!journal.TryAdd(entry.ObligationId, entry)) duplicates.Add(entry.ObligationId);
                    }
                    catch (Exception ex) when (IsRefusal(ex)) { /* Only a complete matching entry proves an event. */ }
                }
            }
            foreach (var duplicate in duplicates) journal.Remove(duplicate);
            foreach (var eventDirectory in names.OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                budget.Check();
                try
                {
                    RejectLinks(eventDirectory);
                    var eventIdentity = Read<ConductorFollowEventIdentity>(Path.Combine(eventDirectory, "identity.json"));
                    ValidateRetainedEventIdentity(eventDirectory, eventIdentity, state);
                    if (IsCorrection(eventIdentity)) { excluded++; continue; }
                    var decision = session.ValidateCompletedRetainedEvent(eventDirectory, eventIdentity, state, journal);
                    if (decision is null) { excluded++; continue; }
                    var source = Read<ConductorFollowRetainedSource>(Path.Combine(eventDirectory, "source.json"));
                    var context = source.Context ?? throw new IOException("Retained source is missing.");
                    if (SafeLabel(context.Tag) != context.Tag || SafeLabel(context.AttemptId.Value) != context.AttemptId.Value
                        || SafeLabel(eventIdentity.ObligationId) != eventIdentity.ObligationId
                        || SafeLabel(state.ClaimGeneration) != state.ClaimGeneration)
                        throw new IOException("Source identity cannot be displayed safely.");
                    var disposition = MatchingHistoricalAction(items ?? [], registration, eventDirectory, source, decision);
                    budget.Check();
                    if (rows.Count == 20) { omitted++; continue; }
                    rows.Add(new(Path.GetFileName(eventDirectory), eventIdentity.ObligationId, eventIdentity.ObligationKey,
                        context.Repository, state.ClaimGeneration, context.Tag, context.AttemptId.Value, decision.SourceHeadSha,
                        context.ObservedAt, decision.Decision, disposition));
                }
                catch (HistoryBudgetException) { throw; }
                catch (Exception ex) when (IsRefusal(ex)) { invalid++; }
            }
            return new(rows, invalid, excluded, omitted);
        }
        catch (HistoryBudgetException ex) { return new(rows, invalid, excluded, omitted, ex.Message); }
        catch (Exception ex) when (IsRefusal(ex))
        {
            return new(rows, invalid, excluded, omitted, "Retained judgment history could not be verified.");
        }
        finally { HistoryReadBudget.Value = previous; }
    }

    private static string? MatchingHistoricalAction(IReadOnlyList<QueueItem> items, ConductorFollowAttachment registration,
        string directory, ConductorFollowRetainedSource source, ConductorFollowDecisionEvidence decision)
    {
        if (decision.Decision != "ReplaceReview") return null;
        var matches = items.Where(item => item.Repository == source.Context.Repository && item.Tag == source.Context.Tag)
            .Select(item => item.ReplacementReviewAction).Where(action => action is not null
                && action.Repository == source.Context.Repository && action.Tag == source.Context.Tag
                && action.Holder == source.Owner && action.ObligationKey == source.IdempotencyKey
                && action.SourceAttemptId == source.Context.AttemptId && action.SourceStage == source.Context.Stage
                && action.HeadSha == decision.SourceHeadSha && action.EvidenceDirectory == directory
                && action.EvidenceProvenance == ReplacementReviewEvidenceProvenance.CompletedFollow
                && action.AdviceDigest == string.Empty && action.EvidenceDigest == decision.ResponseSha256
                && (action.IssuedAuthority is null && action.ReplacementAttemptId is null
                    || action.IssuedAuthority is { } issued && issued.SchemaVersion == SchemaVersion
                    && issued.Repository == decision.SourceRepository && issued.Holder == source.Owner
                    && issued.ClaimGeneration == decision.ClaimGeneration && issued.AttachmentId == registration.Id
                    && issued.ObligationKey == decision.ObligationKey && issued.SourceAttemptId == source.Context.AttemptId
                    && issued.HeadSha == decision.SourceHeadSha && issued.RequestSha256 == decision.RequestSha256
                    && issued.ConfigurationSha256 == decision.ConfigurationSha256 && issued.SessionId == decision.SessionId
                    && issued.ResponseSha256 == decision.ResponseSha256 && issued.AttemptId == action.ReplacementAttemptId
                    && issued.RoomDirectory == action.ReplacementRoomDirectory && issued.IssuedAt != default))
            .Take(2).ToArray();
        if (matches.Length != 1) return null;
        var matched = matches[0]!;
        return matched.ActionObservedAt is not null ? "observed" : matched.ReplacementAttemptId is null ? "retained-unlaunched"
            : matched.TerminalObservation is not null ? "terminal-unresolved" : "issued";
    }
}
