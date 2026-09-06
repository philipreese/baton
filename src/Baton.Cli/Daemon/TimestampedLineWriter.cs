using System.Text;

namespace Baton.Cli.Daemon;

/// <summary>
/// #1981 — puts a UTC timestamp on the front of every line the daemon writes.
/// <para>
/// The 2026-09-06 stall could not be diagnosed from `daemon.log` at all: most lines carried no time,
/// so "when did this stop" and "what was the last thing it did" were both unanswerable from the one
/// artifact that outlives the process. The issue's own third item is this.
/// </para>
/// <para>
/// <b>Installed once, over <see cref="Console.Out"/>/<see cref="Console.Error"/>, rather than at each
/// call site.</b> `daemon.log` is `powershell`'s `*>>` capture of every stream the process writes, so
/// its lines come from three sources — this folder's own <c>Console.Error</c> calls, the host's
/// console logger, and any layer the daemon calls into (the last lines before the stall were
/// <c>VendorUsageCommandRun</c>'s, from <c>Baton.Vendors</c>). A per-call-site prefix would have
/// stamped only the first of those and left the ones that were actually still printing bare.
/// </para>
/// <para>
/// Nothing else about the format changes: the line's own text is passed through byte-for-byte,
/// including blank lines and the console logger's indented continuation lines, and the timestamp is
/// taken when a line's FIRST character is written rather than when its newline arrives, so a slow
/// multi-write line is stamped when it started.
/// </para>
/// </summary>
internal sealed class TimestampedLineWriter : TextWriter
{
    private readonly TextWriter _inner;
    private readonly Func<DateTimeOffset> _clock;
    private readonly StringBuilder _pending = new();
    private readonly object _gate = new();
    private DateTimeOffset _lineStartedAt;
    private bool _lineOpen;

    internal TimestampedLineWriter(TextWriter inner, Func<DateTimeOffset>? clock = null)
    {
        _inner = inner;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public override Encoding Encoding => _inner.Encoding;

    public override void Write(char value)
    {
        lock (_gate)
        {
            WriteChar(value);
        }
    }

    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var c in value)
            {
                WriteChar(c);
            }
        }
    }

    private void WriteChar(char value)
    {
        if (value == '\r')
        {
            // Swallowed rather than buffered: the inner writer supplies its own newline below, so
            // keeping the CR would emit "\r\r\n" on Windows.
            return;
        }

        if (value == '\n')
        {
            _inner.WriteLine($"[{(_lineOpen ? _lineStartedAt : _clock()):yyyy-MM-ddTHH:mm:ss.fffZ}] {_pending}");
            _pending.Clear();
            _lineOpen = false;
            return;
        }

        if (!_lineOpen)
        {
            _lineStartedAt = _clock();
            _lineOpen = true;
        }

        _pending.Append(value);
    }

    /// <summary>Flushes the inner writer, and with it any line already emitted. A line still being
    /// built is deliberately NOT flushed here — it has no newline yet, and emitting it would split one
    /// log line across two timestamped ones.</summary>
    public override void Flush()
    {
        lock (_gate)
        {
            _inner.Flush();
        }
    }

    /// <summary>A trailing partial line (a process exiting mid-write) is emitted rather than dropped:
    /// on 2026-09-06 the last thing the daemon did was exactly the kind of fact a dropped tail
    /// would have taken with it.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                if (_pending.Length > 0)
                {
                    _inner.WriteLine($"[{_lineStartedAt:yyyy-MM-ddTHH:mm:ss.fffZ}] {_pending}");
                    _pending.Clear();
                    _lineOpen = false;
                }

                _inner.Flush();
            }
        }

        base.Dispose(disposing);
    }
}
