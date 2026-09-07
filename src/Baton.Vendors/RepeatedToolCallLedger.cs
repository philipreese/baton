using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Vendors;

/// <summary>What a repeated call is answered with — see <see cref="RepeatedToolCallLedger"/>.</summary>
public enum RepeatVerdict
{
    /// <summary>Run it (or read it) for real. The first occurrence, and every occurrence of an exempt or changed thing.</summary>
    Execute,

    /// <summary>The second occurrence: the previous answer, prefixed with <see cref="RepeatDecision.Preamble"/>.</summary>
    Replay,

    /// <summary>The third and later occurrence: nothing runs, and the caller is told why.</summary>
    Refuse,
}

/// <summary>
/// One verdict. <paramref name="Preamble"/> is set on <see cref="RepeatVerdict.Replay"/> and
/// <paramref name="Reason"/> on <see cref="RepeatVerdict.Refuse"/>; both are null on
/// <see cref="RepeatVerdict.Execute"/>. <paramref name="ReplayedOutput"/> is the previous command
/// output, and is null for reads — see the ledger's remarks for why a read is re-read from disk
/// rather than served from memory.
/// </summary>
public sealed record RepeatDecision(
    RepeatVerdict Verdict, string? Preamble = null, string? Reason = null, string? ReplayedOutput = null)
{
    public static readonly RepeatDecision Execute = new(RepeatVerdict.Execute);
}

/// <summary>
/// #2002 rule 2 and 2b: the per-room memory of what this worker has already asked for, so a
/// byte-identical re-ask is answered rather than re-run. The 2026-09-06 cross-vendor audit on #2002
/// measured byte-identical repeated commands and reads at a median 17 % of a claude room's tool calls,
/// 34 % on agy and 9 % on codex — everyone's habit, not one vendor's, which is why this type names no
/// vendor. <c>spec/baton.md</c> §9 carries the ruling and the measurement's citation; this is its
/// mechanism.
/// <para>
/// <b>Commands and reads use different predicates on purpose.</b> A command is judged by a 60-second
/// clock, because nothing else can say whether the world it observed has moved. A read is judged by
/// the file's own <c>mtime</c> and length, because those CAN say it: a file a build rewrote is re-read
/// truthfully however fast the re-ask came, and a file nobody touched is never re-read however slowly.
/// A clock on reads would have got both of those wrong.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Two homes for the same class.</b> The broker holds one of these in memory per dispatch. The
/// claude and agy <c>PreToolUse</c> hooks are a fresh subprocess per tool call, so they load and save
/// one from <see cref="FileName"/> under this execution's own output directory instead — same class,
/// same predicates, different lifetime. <see cref="RepeatedToolCallHook"/> owns that file handling and
/// states what it does when the file cannot be read or written.
/// </para>
/// <para>
/// <b>A hook denies where the broker replays.</b> A <c>PreToolUse</c> hook can only allow or deny, so
/// there is no substitute tool result to hand back. What it CAN do is put the previous answer inside
/// the deny reason, and <see cref="HookCommandDenial"/> does exactly that whenever the ledger holds
/// one; holding none, it points at the transcript instead. <c>spec/baton.md</c> §9 states which of the
/// two each vendor gets today, and why.
/// </para>
/// <para>
/// <b>Memory bound:</b> <see cref="Capacity"/> keys, each holding at most one command output, which
/// the broker has already truncated to its own per-command character cap — so the worst case is
/// <see cref="Capacity"/> × that cap, and a room that re-issues one command a thousand times holds one
/// entry. Read entries hold a stat pair and no bytes.
/// </para>
/// </remarks>
public sealed class RepeatedToolCallLedger
{
    /// <summary>
    /// How many distinct keys are remembered. #2002 asks for at least 32; the excess buys nothing but
    /// costs nothing either, and 64 keeps a long gate run's worth of distinct commands in reach.
    /// </summary>
    public const int Capacity = 64;

    /// <summary>
    /// The file name the hook half persists under, inside this execution's output directory.
    /// Dot-prefixed so it can never be a declared output, and named in
    /// <c>Dispatch.ExecutionStreamLogger.IsStreamLogFileName</c> so it is filtered out of every
    /// artifact listing the way the agy verdict ledger already is.
    /// </summary>
    public const string FileName = ".baton-repeat-ledger.json";

    /// <summary>The window a byte-identical command repeat is judged inside. Reads do not use it.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The commands whose output is EXPECTED to differ between two identical asks, so re-issuing one
    /// is a legitimate observation rather than a poll. One named set, one home (record-once): every
    /// call site asks <see cref="IsVolatile"/> rather than carrying its own copy.
    /// <list type="bullet">
    /// <item><c>git status</c> — the worker's own edits change it between asks, and re-checking after
    /// a write is the correct habit this rule must not punish.</item>
    /// <item><c>git log</c> — moves on every commit the worker makes, and a lane commits a checkpoint
    /// per step.</item>
    /// <item><c>git diff</c> — same as <c>git status</c>, one level down.</item>
    /// <item><c>gh pr checks</c> — polls a REMOTE state Baton does not own; there is no synchronous
    /// form of it, so refusing the re-ask would leave the worker no way to learn CI went green.</item>
    /// <item><c>gh run view</c> — the same, for a workflow run.</item>
    /// </list>
    /// The last two are the honest exception to this issue's own thesis: they are polls, and they are
    /// polls of something outside this process, which is the one case where polling is the only
    /// instrument. #2002's measured offender was polling LOCAL processes it had backgrounded itself,
    /// which rule 1 removes at the source.
    /// <para>
    /// <b>This list is also the ledger's only proof that a command did not touch the tree.</b> Every
    /// one of these five reads and never writes, which is what lets an executing command on this list
    /// leave the other command entries alone — see <see cref="ForgetAllCommands"/>.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> VolatileCommandPrefixes =
        ["git status", "git log", "git diff", "gh pr checks", "gh run view"];

    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, Entry> _entries;
    private readonly LinkedList<string> _order = new();

    public RepeatedToolCallLedger(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="commandLine"/> is one of the <see cref="VolatileCommandPrefixes"/>.
    /// <b>Every top-level segment</b> must be, so an exempt prefix cannot launder a chained tail:
    /// <c>git status &amp;&amp; dotnet test</c> is not volatile, and neither is
    /// <c>dotnet test &amp;&amp; git status</c> — the two used to disagree, and the asymmetry was
    /// unintended. Within a segment it is still a prefix match on the trimmed text, so
    /// <c>git status --short</c> counts and <c>git stash</c> does not.
    /// </summary>
    public static bool IsVolatile(string commandLine)
    {
        var segments = SplitTopLevelSegments(commandLine);
        return segments.Count > 0 && segments.TrueForAll(segment =>
            VolatileCommandPrefixes.Any(prefix =>
                segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Judges a run-command ask and records the occurrence. Byte-identical means byte-identical: the
    /// line is the key, ordinal, untrimmed and unnormalised, because two lines that differ at all are
    /// two different questions.
    /// </summary>
    public RepeatDecision ClassifyCommand(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        if (IsVolatile(commandLine))
        {
            return RepeatDecision.Execute;
        }

        var now = _timeProvider.GetUtcNow();
        var key = CommandKey(commandLine);

        // No entry, a stale one, or one whose output we never recorded (the command failed to start,
        // or was itself refused) all read the same way: there is no previous answer to stand in for
        // this one, so it runs.
        if (!TryTouch(key, out var entry)
            || entry.Output is null
            || now - entry.ExecutedAt > Window)
        {
            Put(key, new Entry { ExecutedAt = now });
            return RepeatDecision.Execute;
        }

        entry.Served++;
        var ago = (int)Math.Round((now - entry.ExecutedAt).TotalSeconds);
        return entry.Served == 1
            ? new RepeatDecision(
                RepeatVerdict.Replay,
                Preamble: $"replayed: identical command {ago} s ago",
                ReplayedOutput: entry.Output)
            : new RepeatDecision(RepeatVerdict.Refuse, Reason: CommandRepeatRefusal);
    }

    /// <summary>
    /// The hook half of the same judgement, for a path that can only allow or deny: the deny sentence,
    /// or <see langword="null"/> to allow. The occurrence is recorded either way.
    /// <para>
    /// Two differences from <see cref="ClassifyCommand"/>, both forced by what a <c>PreToolUse</c> hook
    /// is. The entry's mere EXISTENCE is the predicate, rather than the bytes it holds: nothing on this
    /// path records an output without a <c>PostToolUse</c> hook, and neither vendor has one wired here,
    /// so keying on the bytes would switch the rung off entirely. And the second ask is DENIED rather
    /// than replayed — the deny reason carries the previous output when the ledger happens to hold one
    /// and points at the transcript when it does not (<see cref="HookCommandDenial"/> states which case
    /// is reachable where).
    /// </para>
    /// <para>
    /// An allowed command is about to change the tree, so it evicts the other command entries AND
    /// every read here, where the broker does both after the process exits
    /// (<see cref="ForgetAllCommands"/>, <see cref="ForgetAllReads"/>). The hook fires before the tool
    /// runs and never learns what it did, so before is the only point available. Dropping the read
    /// half would give this path the very defect the command half was added to fix: read a file, run
    /// <c>dotnet format</c>, re-read it — same byte count inside one filesystem tick — and be told it
    /// has not changed.
    /// </para>
    /// </summary>
    public string? ClassifyHookCommand(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        if (IsVolatile(commandLine))
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        var key = CommandKey(commandLine);

        if (!TryTouch(key, out var entry) || now - entry.ExecutedAt > Window)
        {
            Put(key, new Entry { ExecutedAt = now });
            ForgetAllCommands(commandLine);
            ForgetAllReads();
            return null;
        }

        entry.Served++;
        var ago = (int)Math.Round((now - entry.ExecutedAt).TotalSeconds);
        return entry.Served == 1 ? HookCommandDenial(ago, entry.Output) : CommandRepeatRefusal;
    }

    /// <summary>
    /// Stores what a <see cref="RepeatVerdict.Execute"/> command actually printed, so the next ask
    /// inside <see cref="Window"/> can be answered with it. A command whose output is never recorded
    /// simply executes again — the ledger never refuses on an answer it does not hold.
    /// </summary>
    public void RecordCommandOutput(string commandLine, string output)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(output);
        if (IsVolatile(commandLine))
        {
            return;
        }

        if (TryTouch(CommandKey(commandLine), out var entry))
        {
            entry.Output = output;
        }
    }

    /// <summary>
    /// Judges a file read and records the occurrence. <paramref name="lastWriteUtc"/> and
    /// <paramref name="length"/> are the caller's own stat of the file it is about to read, taken
    /// BEFORE serving — the whole predicate is that pair being unchanged since this room last read
    /// the same path.
    /// </summary>
    /// <param name="path">
    /// An already-resolved absolute path. Normalised here for case and trailing separator only, so two
    /// spellings of one file are one key.
    /// </param>
    public RepeatDecision ClassifyRead(string path, DateTimeOffset lastWriteUtc, long length)
    {
        ArgumentNullException.ThrowIfNull(path);
        var key = ReadKey(path);

        if (!TryTouch(key, out var entry) || entry.LastWriteUtc != lastWriteUtc || entry.Length != length)
        {
            Put(key, new Entry { ExecutedAt = _timeProvider.GetUtcNow(), LastWriteUtc = lastWriteUtc, Length = length });
            return RepeatDecision.Execute;
        }

        entry.Served++;
        return entry.Served == 1
            ? new RepeatDecision(
                RepeatVerdict.Replay,
                Preamble: "replayed: identical read — this file has not changed since you last read it")
            : new RepeatDecision(RepeatVerdict.Refuse, Reason: ReadRepeatRefusal);
    }

    /// <summary>
    /// The hook half of <see cref="ClassifyRead"/>, same shape as <see cref="ClassifyHookCommand"/>:
    /// the deny sentence, or <see langword="null"/> to allow. The stat pair is the whole predicate
    /// here exactly as it is for the broker, so this needs no clock and no recorded bytes — the one
    /// rung of #2002 that works identically on both paths.
    /// </summary>
    public string? ClassifyHookRead(string path, DateTimeOffset lastWriteUtc, long length)
    {
        ArgumentNullException.ThrowIfNull(path);
        var key = ReadKey(path);

        if (!TryTouch(key, out var entry) || entry.LastWriteUtc != lastWriteUtc || entry.Length != length)
        {
            Put(key, new Entry { ExecutedAt = _timeProvider.GetUtcNow(), LastWriteUtc = lastWriteUtc, Length = length });
            return null;
        }

        entry.Served++;
        return entry.Served == 1 ? HookReadDenial : ReadRepeatRefusal;
    }

    /// <summary>
    /// Forgets a path, so the next read of it executes. Called by the broker on every write it
    /// performs itself: a write that lands the same byte count inside the filesystem's timestamp
    /// granularity is invisible to the stat predicate, and that is the one case where a stale answer
    /// could be served as if it were the file. The room's own writes are the population the broker can
    /// see, and this closes it deterministically rather than hoping the clock ticks.
    /// </summary>
    public void ForgetRead(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Forget(ReadKey(path));
    }

    /// <summary>
    /// Forgets every read, so the next read of anything executes. Called after each command the broker
    /// actually RAN: <c>dotnet build</c>, <c>git checkout</c> and <c>pixi run fmt</c> rewrite files the
    /// room has already read, and they rewrite many of them, so the same-length-inside-one-tick case
    /// <see cref="ForgetRead"/> exists for arrives here with more force rather than less. Costs a
    /// re-read of anything cached across a command boundary, which is the correct direction — a
    /// command is exactly when the world moves.
    /// </summary>
    public void ForgetAllReads() => ForgetWherePrefix("read ");

    /// <summary>
    /// Forgets every remembered command output except, optionally, one — because after the tree
    /// changes, no cached command output is the answer (#2002 review HIGH).
    /// <para>
    /// Called on every write the room performs (with no exception), and by a command that is about to
    /// run or has just run (excepting itself). The exception is what keeps rule 2 alive: the command
    /// that did the writing observed the tree AFTER its own change, so its output is still the answer
    /// to an immediate re-ask, and that back-to-back re-ask is the whole measured population. Every
    /// other entry was recorded against a tree that no longer exists.
    /// </para>
    /// <para>
    /// A command on <see cref="VolatileCommandPrefixes"/> never calls this: those five are the only
    /// commands this ledger can prove read-only. Everything else is assumed to have written, which is
    /// the fail-closed direction — it costs a re-run, where the other direction cost a wrong answer
    /// reported as a fresh one.
    /// </para>
    /// </summary>
    public void ForgetAllCommands(string? exceptCommandLine = null)
    {
        var keep = exceptCommandLine is null ? null : CommandKey(exceptCommandLine);
        ForgetWherePrefix("cmd ", keep);
    }

    /// <summary>
    /// The third-and-later refusal for a repeated command. A constant rather than a literal at each
    /// site because the broker and both hooks emit the same sentence (record-once).
    /// <para>
    /// <b>Its first clause is a claim about state, and <see cref="ForgetAllCommands"/> is what makes it
    /// true.</b> Before that eviction existed, this sentence was affirmatively false whenever the room
    /// had written since the recorded run — the failure #2002's review found. Nothing but the eviction
    /// enforces it, which is why the two are worded together.
    /// </para>
    /// </summary>
    public const string CommandRepeatRefusal =
        "the previous run is still the answer — nothing this room did since could have changed it, " +
        "and nothing runs in the background here";

    /// <summary>The third-and-later refusal for a repeated read, same reason as its command sibling.</summary>
    public const string ReadRepeatRefusal =
        "the previous read is still the answer; this file has not changed since";

    /// <summary>
    /// The second-ask denial a hook emits for a repeated command. <paramref name="cachedOutput"/> is
    /// the previous run's output when the ledger holds it, and the denial then CARRIES that output —
    /// a deny reason is the only channel a <c>PreToolUse</c> hook has, so this is what replay looks
    /// like on that path (see this type's remarks).
    /// <para>
    /// <b>Null is the ordinary case on claude and agy today, and that is a scope statement, not a
    /// default.</b> An output only reaches the ledger through
    /// <see cref="RecordCommandOutput"/>, which the broker calls after the process it ran exits;
    /// neither hook vendor has a <c>PostToolUse</c> hook wired here, so on a room with no broker in it
    /// nothing ever records one and every denial takes the transcript-pointer form. Both branches are
    /// shipped rather than only the reachable one because the ledger file is per ROOM, not per vendor:
    /// whatever writes an output into it, the hook that reads it next is the thing holding an answer
    /// the model asked for, and dropping it on the floor would be the one avoidable waste this rung
    /// exists to remove.
    /// </para>
    /// </summary>
    public static string HookCommandDenial(int agoSeconds, string? cachedOutput = null) =>
        string.IsNullOrEmpty(cachedOutput)
            ? $"AER: this is byte-identical to the command {agoSeconds} s ago (its output is above in " +
              "your transcript). Baton refused the re-run rather than spending a tool step and a model " +
              "turn on an answer you already hold. Change the command if you need a genuinely new " +
              "observation."
            : $"AER: this is byte-identical to the command {agoSeconds} s ago, so Baton did not re-run " +
              "it. Its output, unchanged, is below — treat it as this call's result, and change the " +
              "command if you need a genuinely new observation.\n\n" + cachedOutput;

    /// <summary>The second-ask denial a hook emits for a repeated read; same shape as its command sibling.</summary>
    public const string HookReadDenial =
        "AER: this file has not changed since you last read it (its content is above in your " +
        "transcript). Baton refused the re-read rather than spending a tool step on bytes you already " +
        "hold.";

    /// <summary>
    /// Restores a ledger previously written by <see cref="Save"/>, or an empty one when
    /// <paramref name="path"/> does not exist. Throws on a file it cannot read or parse — the caller
    /// decides what a broken ledger means, and <see cref="RepeatedToolCallHook"/> reads it as "no
    /// memory", never as a denial.
    /// </summary>
    public static RepeatedToolCallLedger Load(string path, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var ledger = new RepeatedToolCallLedger(timeProvider);
        if (!File.Exists(path))
        {
            return ledger;
        }

        var state = JsonSerializer.Deserialize<PersistedLedger>(File.ReadAllText(path), SerializerOptions);
        foreach (var row in state?.Entries ?? [])
        {
            if (string.IsNullOrEmpty(row.Key))
            {
                continue;
            }

            ledger.Put(row.Key, new Entry
            {
                ExecutedAt = row.ExecutedAt,
                LastWriteUtc = row.LastWriteUtc,
                Length = row.Length,
                Served = row.Served,
                Output = row.Output,
            });
        }

        return ledger;
    }

    /// <summary>
    /// Writes this ledger to <paramref name="path"/> through a temporary file and an atomic replace,
    /// so a hook subprocess reading it concurrently sees either the whole previous state or the whole
    /// new one, never a torn file.
    /// </summary>
    public void Save(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var state = new PersistedLedger(
            _order.Select(key => new PersistedEntry(
                    key, _entries[key].ExecutedAt, _entries[key].LastWriteUtc, _entries[key].Length,
                    _entries[key].Served, _entries[key].Output))
                .ToArray());

        // Beside the target, because an atomic replace needs the same volume. Deleted in `finally`
        // whatever happens: a Move that fails because a concurrent hook holds the destination would
        // otherwise leave `<name>.<pid>.tmp` in the output directory, and only the exact ledger name
        // is filtered out of artifact listings — a leftover would surface as a worker deliverable.
        var temporary = path + "." + Environment.ProcessId + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, SerializerOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed record PersistedLedger(IReadOnlyList<PersistedEntry> Entries);

    private sealed record PersistedEntry(
        string Key, DateTimeOffset ExecutedAt, DateTimeOffset LastWriteUtc, long Length, int Served,
        string? Output);

    private static string CommandKey(string commandLine) => "cmd " + commandLine;

    private static string ReadKey(string path)
    {
        var normalised = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return "read " + (OperatingSystem.IsWindows() ? normalised.ToUpperInvariant() : normalised);
    }

    /// <summary>
    /// The trimmed top-level segments of <paramref name="commandLine"/>, split at unquoted <c>;</c>,
    /// <c>|</c> and <c>&amp;</c> (which covers <c>&amp;&amp;</c> and <c>||</c> — an empty segment
    /// between the two characters is dropped). Quote-aware so a separator inside
    /// <c>git log -S "a;b"</c> is not a boundary. Deliberately not
    /// <see cref="ShellCommandPatternMatcher"/>'s segmenter: that one refuses outright on a backslash
    /// or a <c>$</c> under a scoped grant, and this question — "is every part of this line one of five
    /// read-only commands" — has to be answerable for every line, including a Windows path.
    /// </summary>
    private static List<string> SplitTopLevelSegments(string commandLine)
    {
        var segments = new List<string>();
        var start = 0;
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i <= commandLine.Length; i++)
        {
            if (i == commandLine.Length)
            {
                AddSegment(segments, commandLine[start..]);
                break;
            }

            var c = commandLine[i];
            if (inSingle)
            {
                inSingle = c != '\'';
                continue;
            }

            if (inDouble)
            {
                inDouble = c != '"';
                continue;
            }

            switch (c)
            {
                case '\'':
                    inSingle = true;
                    continue;
                case '"':
                    inDouble = true;
                    continue;
                case ';' or '|' or '&' or '\n' or '\r':
                    AddSegment(segments, commandLine[start..i]);
                    start = i + 1;
                    continue;
            }
        }

        return segments;
    }

    private static void AddSegment(List<string> segments, string segment)
    {
        var trimmed = segment.Trim();
        if (trimmed.Length > 0)
        {
            segments.Add(trimmed);
        }
    }

    private void Forget(string key)
    {
        if (_entries.Remove(key, out _))
        {
            var node = _order.Find(key);
            if (node is not null)
            {
                _order.Remove(node);
            }
        }
    }

    private void ForgetWherePrefix(string prefix, string? keep = null)
    {
        foreach (var key in _entries.Keys
                     .Where(key => key.StartsWith(prefix, StringComparison.Ordinal)
                                   && !string.Equals(key, keep, StringComparison.Ordinal))
                     .ToArray())
        {
            Forget(key);
        }
    }

    private bool TryTouch(string key, out Entry entry)
    {
        if (!_entries.TryGetValue(key, out var found))
        {
            entry = null!;
            return false;
        }

        var node = _order.Find(key);
        if (node is not null)
        {
            _order.Remove(node);
            _order.AddLast(node);
        }

        entry = found;
        return true;
    }

    private void Put(string key, Entry entry)
    {
        if (!_entries.ContainsKey(key))
        {
            _order.AddLast(key);
        }

        _entries[key] = entry;

        while (_order.Count > Capacity && _order.First is { } oldest)
        {
            _order.RemoveFirst();
            _entries.Remove(oldest.Value);
        }
    }

    private sealed class Entry
    {
        public DateTimeOffset ExecutedAt;
        public DateTimeOffset LastWriteUtc;
        public long Length;

        /// <summary>How many times this entry has already stood in for a re-ask: 0 executed, 1 replayed, 2+ refused.</summary>
        public int Served;

        /// <summary>The command output held for replay. Always null on a read entry — a read is re-read.</summary>
        public string? Output;
    }
}
