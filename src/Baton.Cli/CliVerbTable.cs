namespace Baton.Cli;

/// <summary>
/// CLI verb classification for #2114. Read entries declare the handler and argument surface that
/// performs no filesystem or ledger mutation. Changing a handler's sub-verbs requires revisiting
/// this declaration; mixed groups list their write probes beside their read surface.
/// Program's known verbs and the architecture test's read/write populations derive from this table.
/// </summary>
public static class CliVerbTable
{
    public sealed record ReadOnlyVerb(string Pattern, Type Handler);
    public sealed record Verb(string Name, bool Hidden, ReadOnlyVerb[] Reads, string[] Writes);

    public static IReadOnlyList<Verb> All { get; } =
    [
        new("run", false, [], ["baton run C:/rooms/other-room --workflow wf.json"]),
        new("dispatch", false, [], ["baton dispatch --role implement --spec brief.md"]),
        new("redispatch", false, [], ["baton redispatch room-1"]),
        new("cancel", false, [], ["baton cancel room-1"]),
        new("decide", false, [], ["baton decide C:/rooms/other-room --execution e1 --type resume --bindings b.json"]),
        new("resolve", false, [], ["baton resolve room-1 --accept-capture"]),
        new("supply", false, [], ["baton supply room-1 --worker implement --output changes.md --file x.md --bindings b.json"]),
        new("resume", false, [], ["baton resume room-1"]),
        new("status", false, [new("baton status*", typeof(StatusCommand))], []),
        new("watch", false, [], ["baton watch room-1 --command notify.exe"]),
        new("deliver", false, [], ["baton deliver --title x --file y.md"]),
        new("templates", false, [new("baton templates*", typeof(TemplatesCommand))], []),
        new("keep", false, [], ["baton keep room-1"]),
        new("unkeep", false, [], ["baton unkeep room-1"]),
        new("trust", false, [new("baton trust --list", typeof(TrustCommand))],
            ["baton trust C:/repo", "baton trust C:/repo --ceiling all", "baton trust C:/repo --revoke",
                "baton trust C:/repo --forget", "baton trust --list --revoke C:/repo",
                "baton trust --list --ceiling all C:/repo", "baton trust --list --forget C:/repo"]),
        new("room", false, [], ["baton room delete room-1"]),
        new("rooms", false, [], ["baton rooms prune"]),
        new("ledger", false, [new("baton ledger*", typeof(LedgerViewCommand))],
            ["baton ledger --rebuild", "baton ledger backfill", "baton ledger export C:/repo"]),
        new("memory", false, [new("baton memory audit*", typeof(MemoryAuditCommand))],
            ["baton memory import", "baton memory sync --apply", "baton memory add --text x",
                "baton memory retract 0123456789abcdef0123456789abcdef --reason x"]),
        new("audit", false, [new("baton audit lanes*", typeof(AuditLanesCommand))], []),
        new("queue", false, [], ["baton queue add fix-2114 --role implement --spec brief.md --issue 2114", "baton queue hold"]),
        new("mcp", false, [], ["baton mcp --memory-proposal-tool"]),
        new("daemon", false, [], ["baton daemon"]),
        new("--version", true, [new("baton --version*", typeof(VersionInfo))], []),
        new("hook-check", true, [], ["baton hook-check"]),
        new("agy-hook-check", true, [], ["baton agy-hook-check"]),
        new("codex-broker", true, [], ["baton codex-broker"]),
    ];

    public static IEnumerable<string> KnownSubcommands => All.Where(verb => !verb.Hidden).Select(verb => verb.Name);
    public static IEnumerable<ReadOnlyVerb> ReadOnlyVerbs => All.SelectMany(verb => verb.Reads);
    public static IEnumerable<string> DaemonWriteCommands => All.SelectMany(verb => verb.Writes);
}
