using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Baton.Core.Internal;

namespace Baton.Vendors;

public static class AgyCorrectionClient
{
    public const string Transport = "agy-stream-v1";
    public static async Task<AgyCorrectionReceipt?> ExecuteAsync(string room, string execution, string messageId,
        string? text, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("AGY correction is Windows-only.");
        var store = new AgyCorrectionStore(room);
        var principal = LocalNamedPipePeer.CurrentUserSid() ?? throw new InvalidOperationException("OS principal unavailable.");
        var prior = store.Query(execution);
        if (prior is not null)
        {
            if (prior.Request.MessageId != messageId || prior.Request.Identity.OsPrincipal != principal
                || (text is not null && prior.Request.PayloadSha256 != AgyCorrectionStore.Digest(text)))
                throw new InvalidOperationException("AGY correction conflicts with the immutable request.");
            if (!await prior.Request.Identity.MatchesJournalAsync(false, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Retained AGY correction provenance cannot be validated.");
            return prior;
        }
        if (text is null) return null;
        if (string.IsNullOrEmpty(text) || text.Length > 16_384)
            throw new InvalidOperationException("Steering text must contain 1 to 16384 characters.");
        var identity = AgyStreamingHost.ReadEndpoint(room, execution);
        if (identity is null || identity.OsPrincipal != principal
            || !await identity.MatchesJournalAsync(true, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The exact AGY execution has no verified live correction endpoint. Nothing was sent.");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            using var pipe = new NamedPipeClientStream(".", identity.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Impersonation);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(new AgyCorrectionWireRequest(identity, messageId, text))).ConfigureAwait(false);
            _ = await AgyStreamingHost.ReadFrameAsync(reader, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // The write-ahead store is authoritative after an ambiguous connection or response.
        }
        return store.Query(execution);
    }
}
