using System.Globalization;
using System.Net;
using System.Text;
using Baton.Status;
using Baton.Vendors;
using Microsoft.Extensions.Hosting;

namespace Baton.Cli.Daemon;

/// <summary>
/// #1946 slice 1 — the tailnet plane's listener (spec/baton.md §11 C-11): GET routes serving
/// <see cref="GlassPage"/>, its static install metadata, the fleet projection the daemon already
/// writes, and a stream of that file's changes. Off unless <see cref="GlassListenerSettings.Listen"/>
/// is set.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE SLICE BOUNDARY, and it is the whole reason this plane exists.</b> Apart from the page's
/// static install metadata, the route table below serves the FLEET ROW ONLY — the same payload the
/// mailbox already carries. No stdout tail beyond what the projection file itself contains, no room
/// artifacts, no per-room timeline endpoint, no arrest verb. Drill-down (live stdout tail, full
/// timeline, room artifacts) is slice 2, and C-11 rules that it may live ONLY on this plane: it must
/// never be added to the worker-served or artifact copies, whose secret gate and KV write cap are
/// the two walls that forced a second plane in the first place.
/// </para>
/// <para>
/// <b>Read-only, and structurally so.</b> Every route is a GET; a request with any other method is
/// refused with 405 before it reaches a handler. C-11's arrest reflexes (cancel,
/// redispatch-unchanged) are explicitly NOT in this slice, and origination stays orchestrator-only.
/// </para>
/// <para>
/// <b><see cref="HttpListener"/>, not a web framework.</b> Baton.Cli's whole project graph carries
/// one PackageReference (see the csproj); this route table is three GETs and an SSE loop, which the
/// BCL's listener serves without adding ASP.NET to a CLI. One listener per bound prefix, each with
/// its own accept loop, so a prefix that cannot be bound (the URL-ACL case below) costs its own
/// reachability and nothing else.
/// </para>
/// <para>
/// <b>On Windows, loopback was observed to need a URL reservation too, not just the tailnet
/// prefixes.</b> Measured 2026-09-07 on the operator's machine: an unelevated process binds
/// <c>http://127.0.0.1:PORT/</c> fine and gets "Access is denied" for <c>http://100.x.y.z:PORT/</c>
/// with no reservation at all. That looked like a loopback-is-always-free rule; measured again
/// 2026-09-08, after the operator granted urlacl reservations for the two tailnet prefixes, the
/// same unelevated process started getting "Access is denied" for <c>http://127.0.0.1:PORT/</c>
/// too — a prefix that had bound cleanly for the prior day. This is one operator machine on one
/// day, not an independently verified account of HTTP.sys's ACL mechanics, but it is enough that
/// no prefix here is treated as needing no reservation. Each is a one-time elevated
/// <c>netsh http add urlacl</c>, which the per-prefix
/// failure log below prints verbatim for whichever prefix actually failed; a bind refusal on one
/// prefix costs only that prefix's reachability, never the daemon.
/// </para>
/// </remarks>
internal sealed class GlassHttpService : BackgroundService
{
    /// <summary>Kept well under a phone's and a proxy's idle timeouts so a sleeping client's stream
    /// is held open by traffic rather than by hope; an <c>EventSource</c> ignores comment frames.</summary>
    private static readonly TimeSpan SseKeepAlive = TimeSpan.FromSeconds(20);

    private readonly DaemonSettings _settings;
    private readonly string _projectionPath;
    private readonly GlassProjectionWatcher _watcher;
    private readonly TextWriter _log;

    public GlassHttpService(DaemonSettings settings)
        : this(settings, BatonPaths.FleetProjectionFile, null, null)
    {
    }

    /// <summary>Test seam: a temp projection path, a fast poll interval, and a private log sink, so a
    /// test never depends on the machine's own <c>~/.baton</c> nor races xunit's parallel collections
    /// on <see cref="Console.Error"/>.</summary>
    internal GlassHttpService(
        DaemonSettings settings, string projectionPath, TimeSpan? pollInterval, TextWriter? log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(projectionPath);
        _settings = settings;
        _projectionPath = projectionPath;
        _watcher = new GlassProjectionWatcher(projectionPath, pollInterval);
        _log = log ?? Console.Out;
    }

    /// <summary>The prefixes actually bound, in bind order — empty until <see cref="ExecuteAsync"/>
    /// has started, and empty forever when the listener is off or every bind was refused.</summary>
    internal IReadOnlyList<string> BoundPrefixes { get; private set; } = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Glass.Listen)
        {
            return;
        }

        var port = _settings.Glass.EffectivePort;
        var listeners = new List<HttpListener>();
        var bound = new List<string>();

        var selection = GlassBindPolicy.SelectBindAddresses();
        foreach (var refusal in selection.Refusals)
        {
            _log.WriteLine(refusal);
        }

        foreach (var address in selection.Addresses)
        {
            var prefix = GlassBindPolicy.PrefixFor(address, port);
            var listener = new HttpListener();
            try
            {
                // Prefixes.Add is inside the try because it can reject a prefix outright
                // (ArgumentException), and this loop now builds a shape no run has exercised: the
                // bracketed IPv6 prefix an interface-gated ULA produces. A prefix costing its own
                // reachability is this class's stated contract; a prefix taking the whole daemon down
                // with it would not be.
                listener.Prefixes.Add(prefix);
                listener.Start();
                listeners.Add(listener);
                bound.Add(prefix);
            }
            catch (Exception ex) when (ex is HttpListenerException or ArgumentException)
            {
                listener.Close();
                // Loud, once, with the exact remediation -- an operator who sees only "access is
                // denied" has no way to know a URL reservation is what is missing. Stated for
                // whichever prefix actually failed, loopback included: loopback is not exempt once
                // other reservations exist on the port (see this class's own remarks).
                _log.WriteLine(
                    $"GlassHttpService: could not bind {prefix} ({ex.Message}). Grant the reservation " +
                    $"once from an elevated prompt: " +
                    $"netsh http add urlacl url={prefix} user={Environment.UserDomainName}\\{Environment.UserName}");
            }
        }

        BoundPrefixes = bound;
        // Surfaced on the heartbeat (spec/baton.md §7) so a reverse proxy pointed at the wrong
        // address is diagnosable from the glass itself rather than only from this log line.
        DaemonTickLedger.Instance.RecordGlassBoundPrefixes(bound);
        if (listeners.Count == 0)
        {
            _log.WriteLine("GlassHttpService: no address could be bound; the glass is not being served.");
            return;
        }

        // Logged once at start, as the issue asks: the operator's bookmark is one of these URLs.
        _log.WriteLine($"GlassHttpService: serving the fleet glass at {string.Join(", ", bound)}");

        try
        {
            var pumps = listeners.Select(l => AcceptLoopAsync(l, stoppingToken)).ToList();
            pumps.Add(_watcher.RunAsync(stoppingToken));
            await Task.WhenAll(pumps).ConfigureAwait(false);
        }
        finally
        {
            foreach (var listener in listeners)
            {
                listener.Close();
            }
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // The listener was closed under us (shutdown), or the OS dropped the accept. Either
                // way this loop is done; the daemon must not fault on it.
                return;
            }

            // Deliberately not awaited: an /events subscriber holds its response open for as long as
            // the phone is on the page, and awaiting it here would serve one client at a time.
            _ = HandleAsync(context, stoppingToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken stoppingToken)
    {
        try
        {
            if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                // Read-only is enforced here, before routing: there is no handler a non-GET could
                // reach even if one were added below (C-11 -- this page originates nothing).
                await WriteTextAsync(context, HttpStatusCode.MethodNotAllowed, "text/plain; charset=utf-8", "GET only.")
                    .ConfigureAwait(false);
                return;
            }

            // ROUTE TABLE -- fleet row only; see this class's own remarks for the slice boundary and
            // why drill-down must not be added here or to the artifact/worker copies.
            switch (context.Request.Url?.AbsolutePath)
            {
                case "/":
                case "/index.html":
                    await WriteTextAsync(context, HttpStatusCode.OK, "text/html; charset=utf-8", GlassPage.Html())
                        .ConfigureAwait(false);
                    return;

                case GlassWebAppAssets.ManifestPath:
                    await WriteTextAsync(
                            context, HttpStatusCode.OK, "application/manifest+json; charset=utf-8",
                            GlassWebAppAssets.Manifest())
                        .ConfigureAwait(false);
                    return;

                case GlassWebAppAssets.Icon192Path:
                    await WriteBytesAsync(context, HttpStatusCode.OK, "image/png", GlassWebAppAssets.Icon(192))
                        .ConfigureAwait(false);
                    return;

                case GlassWebAppAssets.Icon512Path:
                    await WriteBytesAsync(context, HttpStatusCode.OK, "image/png", GlassWebAppAssets.Icon(512))
                        .ConfigureAwait(false);
                    return;

                case "/projection.json":
                    await WriteProjectionAsync(context).ConfigureAwait(false);
                    return;

                case "/events":
                    await WriteEventStreamAsync(context, stoppingToken).ConfigureAwait(false);
                    return;

                default:
                    await WriteTextAsync(context, HttpStatusCode.NotFound, "text/plain; charset=utf-8", "Not found.")
                        .ConfigureAwait(false);
                    return;
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException
                                       or OperationCanceledException)
        {
            // A client that walked away mid-response (a phone locking its screen is the common case)
            // must not surface as a daemon fault. Anything else propagates.
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
            {
                // Already closed by the abandoned-client path above.
            }
        }
    }

    /// <summary>The share mode <c>spec/baton.md</c> §7 states for every C# reader of this file, named
    /// so a test pins the production value rather than a re-typed copy of it.
    /// <para>
    /// <see cref="FileShare.Delete"/> is the flag that matters, and what it protects is the writer's
    /// <i>rename-over</i> rather than the writer's write —
    /// <see cref="FleetProjectionWriter.WriteAtomic"/> replaces this path rather than writing into it,
    /// so delete-sharing, not write-sharing, is the permission that replace needs.
    /// <see cref="FileShare.ReadWrite"/> alone grants the one it does not.
    /// </para>
    /// <para>
    /// <b>The residual, measured 2026-09-07 and recorded on
    /// <see cref="FleetProjectionWriter.WriteAtomic"/>:</b> this share mode is necessary and not
    /// sufficient — <c>File.Move(overwrite: true)</c> refuses a target that has <i>any</i> open handle,
    /// delete-sharing granted or not. So what actually keeps a tick from being skipped is that this
    /// route holds the handle only for an in-memory copy and releases it before responding, plus
    /// <c>WriteAtomic</c>'s retries. The flag is what makes that hold survivable at all, and it is the
    /// contract <c>spec/baton.md</c> §7 states.
    /// </para>
    /// </summary>
    internal const FileShare ProjectionShare = FileShare.ReadWrite | FileShare.Delete;

    /// <summary>
    /// <see cref="BatonPaths.FleetProjectionFile"/> served as-is — never reshaped, never re-derived —
    /// opened <see cref="ProjectionShare"/> and copied straight out, so an in-flight atomic rewrite is
    /// never skipped on this reader's account.
    /// </summary>
    private async Task WriteProjectionAsync(HttpListenerContext context)
    {
        byte[] payload;
        try
        {
            await using var stream = new FileStream(
                _projectionPath, FileMode.Open, FileAccess.Read, ProjectionShare);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer).ConfigureAwait(false);
            payload = buffer.ToArray();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // 404 rather than an empty 200: the page's own "no projection yet" state has to be
            // distinguishable from "the daemon has never written one", and a fabricated empty
            // projection would render as an empty fleet.
            await WriteTextAsync(
                    context, HttpStatusCode.NotFound, "text/plain; charset=utf-8",
                    "No fleet projection has been written yet.")
                .ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await WriteTextAsync(
                    context, HttpStatusCode.ServiceUnavailable, "text/plain; charset=utf-8",
                    "The fleet projection could not be read.")
                .ConfigureAwait(false);
            return;
        }

        NoStore(context.Response);
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = payload.Length;
        await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
    }

    /// <summary>
    /// Server-Sent Events: one <c>projection</c> event per observed change to the projection file,
    /// and nothing else but keep-alive comments. C-11 chose SSE over a WebSocket precisely because
    /// the primary client is a phone that sleeps constantly and <c>EventSource</c> gives reconnect
    /// for free; the event id carried here is the watcher's version, which a reconnecting client
    /// sends back as <c>Last-Event-ID</c> so an event that landed while it was asleep is delivered
    /// rather than silently skipped.
    /// </summary>
    private async Task WriteEventStreamAsync(HttpListenerContext context, CancellationToken stoppingToken)
    {
        NoStore(context.Response);
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.SendChunked = true;

        var known = _watcher.Version;
        if (long.TryParse(
                context.Request.Headers["Last-Event-ID"], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var resumeFrom) && resumeFrom >= 0)
        {
            known = Math.Min(known, resumeFrom);
        }

        var stream = context.Response.OutputStream;
        await WriteAsciiAsync(stream, ": connected\n\n").ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            long version;
            try
            {
                version = await _watcher.WaitForChangeAsync(known, stoppingToken)
                    .WaitAsync(SseKeepAlive, stoppingToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // No change within the keep-alive window -- a comment frame, which is NOT an event:
                // "emits on a change and not otherwise" is a property of `event:` frames, and a client
                // sees nothing here.
                await WriteAsciiAsync(stream, ": keep-alive\n\n").ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            known = version;
            await WriteAsciiAsync(
                    stream,
                    $"id: {version.ToString(CultureInfo.InvariantCulture)}\nevent: projection\ndata: " +
                    $"{{\"version\":{version.ToString(CultureInfo.InvariantCulture)}}}\n\n")
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteAsciiAsync(Stream stream, string frame)
    {
        var bytes = Encoding.UTF8.GetBytes(frame);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(
        HttpListenerContext context, HttpStatusCode status, string contentType, string body)
    {
        NoStore(context.Response);
        var payload = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = payload.Length;
        await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
    }

    private static async Task WriteBytesAsync(
        HttpListenerContext context, HttpStatusCode status, string contentType, ReadOnlyMemory<byte> body)
    {
        NoStore(context.Response);
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
    }

    /// <summary>The projection is a live reading; a cached copy on a phone is a lie about the fleet
    /// that looks exactly like the truth.</summary>
    private static void NoStore(HttpListenerResponse response) =>
        response.Headers["Cache-Control"] = "no-store";
}
