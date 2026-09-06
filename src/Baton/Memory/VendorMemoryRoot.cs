using System.Text.Json.Serialization;

namespace Baton.Memory;

/// <summary>Who owns a vendor memory root's <b>directory</b> — not who owns the memories in it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VendorMemoryScope>))]
public enum VendorMemoryScope
{
    /// <summary>A third-party CLI's own home on this machine (<c>~/.codex</c>, <c>~/.gemini</c>).</summary>
    [JsonStringEnumMemberName("vendor")] Vendor,

    /// <summary>
    /// A store Baton itself created by pointing a vendor CLI at a home of Baton's own
    /// (<c>~/.baton/codex-home</c>, reached via <c>CODEX_HOME</c>). Q5 (operator, 2026-09-05) ruled
    /// this Baton's own first beta rather than a third-party surface; the scope is what keeps the two
    /// apart in one list instead of collapsing them into one <c>codex</c> row.
    /// </summary>
    [JsonStringEnumMemberName("baton-managed")] BatonManaged,
}

/// <summary>
/// What was learned about a root's contents. Absent, present-and-empty and populated are the three
/// states a <b>completed</b> walk can produce, and collapsing the middle one into either neighbour is
/// the misreading this enum was first written to prevent; <see cref="Capped"/> and
/// <see cref="Unreadable"/> are the two states a walk that did NOT complete produces.
/// </summary>
/// <remarks>
/// <para>
/// A vendor that ships a directory it never fills looks identical to a vendor that has no such
/// surface if emptiness is inferred from a zero file count — and phase C's scope rests on telling
/// those apart. Measured 2026-09-05: <c>antigravity/knowledge</c> exists on both Antigravity roots
/// holding nothing but a zero-byte <c>knowledge.lock</c>, and <c>codex/memories/rollout_summaries</c>
/// exists holding nothing at all. Reporting either as absent would say the vendor lacks a surface it
/// in fact ships unused.
/// </para>
/// <para>
/// <b>The two failure states exist for the same reason, one level up: a walk that could not finish
/// must not be reported as one that did.</b> Reporting a denied ACL as <see cref="Empty"/> asserts a
/// selector result nobody obtained, and reporting a walk abandoned at its ceiling as
/// <see cref="Populated"/> attaches an authoritative file count to a partial gather.
/// Both fail closed instead: the count, total size and
/// newest mtime are <b>absent</b> on those rows (<see cref="VendorMemoryRoot.FileCount"/>), because a
/// number that is only most of the truth reads exactly like the whole of it.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<VendorMemoryPresence>))]
public enum VendorMemoryPresence
{
    /// <summary>The directory does not exist on this machine.</summary>
    [JsonStringEnumMemberName("absent")] Absent,

    /// <summary>The directory exists and the family's own selector matched nothing in it.</summary>
    [JsonStringEnumMemberName("empty")] Empty,

    /// <summary>The directory exists and the selector matched at least one file.</summary>
    [JsonStringEnumMemberName("populated")] Populated,

    /// <summary>
    /// The walk reached <see cref="VendorRootWalkLimits"/>' entry ceiling or time budget and was
    /// abandoned. What was gathered before that point is discarded rather than reported: it is a
    /// prefix of the directory, not a measurement of it.
    /// </summary>
    [JsonStringEnumMemberName("capped")] Capped,

    /// <summary>
    /// The directory exists and a listing of it (or of something under it) failed — a denied ACL, an
    /// I/O error, a tree a vendor process is holding. Distinct from <see cref="Empty"/> on purpose:
    /// "nothing matched" and "nothing could be read" are opposite facts about phase C's scope.
    /// </summary>
    [JsonStringEnumMemberName("unreadable")] Unreadable,
}

/// <summary>
/// One non-Claude memory root family: where it lives under a user's home, and which files inside it
/// are the family's own population. #1852 phase A2.
/// </summary>
/// <remarks>
/// <para>
/// <b>The selector is narrow on purpose, and the bound is the reason.</b> These directories sit
/// inside whole vendor homes: <c>~/.baton/codex-home</c> holds a 102 MB <c>logs_2.sqlite</c> and a
/// 118 MB <c>thread_history_1.sqlite</c> beside its 40 KB <c>memories_1.sqlite</c>, and
/// <c>antigravity-cli/brain</c> held a five-figure file count when this was measured — the exact
/// number is the register's (<c>docs/vendor-doc-audit.md</c> §"#1852 phase A2"), stated there once so
/// the next probe re-measures one place rather than three. A directory walk would digest all of it on
/// every <c>baton memory audit</c>, so each family names the files it means and nothing else.
/// </para>
/// <para>
/// <b><see cref="Inventoried"/> is the second half of that bound.</b> A family whose files are
/// located but deliberately not opened reports its count and no digests — see
/// <see cref="VendorMemoryRootTable"/> for which family that is and what was measured about it.
/// </para>
/// </remarks>
/// <param name="Family">Stable slug naming the format family, shared by roots of the same shape.</param>
/// <param name="SourceVendor">The vendor whose format this is (<c>codex</c>, <c>antigravity</c>).</param>
/// <param name="SourceScope">Whether the directory is the vendor's own or Baton's.</param>
/// <param name="RelativeDirectory">
/// The root's path, forward-slash separated, relative to the base <see cref="SourceScope"/> selects:
/// the user's home for a vendor root, and <b>Baton's own root</b> for a Baton-managed one. The two
/// are not the same directory whenever <c>BATON_HOME</c> is set, and resolving a Baton-managed root
/// off the user profile would report a relocated store as <see cref="VendorMemoryPresence.Absent"/>
/// — the exact misreading <see cref="VendorMemoryPresence"/> exists to prevent, one layer above it.
/// </param>
/// <param name="FilePattern">Glob matched against file names inside the root.</param>
/// <param name="Recursive">Whether <paramref name="FilePattern"/> applies to subdirectories too.</param>
/// <param name="Inventoried">
/// <see langword="true"/> to record path/size/mtime/sha256 per file; <see langword="false"/> to
/// record the count only and open nothing.
/// </param>
public sealed record VendorMemoryFamily(
    string Family,
    string SourceVendor,
    VendorMemoryScope SourceScope,
    string RelativeDirectory,
    string FilePattern,
    bool Recursive,
    bool Inventoried);

/// <summary>
/// One non-Claude memory root as measured: what it is, whether it is there, and what is in it.
/// </summary>
/// <param name="Family">The family slug from <see cref="VendorMemoryFamily"/>.</param>
/// <param name="SourceVendor">The vendor whose format this is.</param>
/// <param name="SourceScope">Vendor-owned or Baton-managed.</param>
/// <param name="DirectoryPath">The absolute directory this row was measured at.</param>
/// <param name="Presence">
/// Absent, empty, populated, capped or unreadable — never inferred from <paramref name="FileCount"/>.
/// Read it before the numbers: the last two mean the walk did not finish, and nothing else on the row
/// says so.
/// </param>
/// <param name="FileCount">
/// How many files the family's selector matched — present for every family whose walk COMPLETED,
/// including the one whose files are never opened, so "located and rejected" still carries a number.
/// <b>Null on a capped or unreadable row</b>, where a partially gathered count would be read as an
/// authoritative one; <c>0</c> on an absent row, where the zero is a measurement.
/// </param>
/// <param name="TotalBytes">Their total size, 0 when nothing was matched, null when the walk did not finish.</param>
/// <param name="NewestModifiedUtc">The most recent modification time among them, or null.</param>
/// <param name="Files">
/// One row per file, when the family is inventoried and its walk completed; <b>empty</b> otherwise.
/// A reader tells those apart by <paramref name="Inventoried"/> and <paramref name="Presence"/>,
/// never by this list being empty.
/// </param>
/// <param name="Inventoried">Whether <paramref name="Files"/> was populated at all.</param>
/// <param name="CappedAtEntries">
/// The ceiling the walk was abandoned at, on a <see cref="VendorMemoryPresence.Capped"/> row whose
/// <see cref="VendorRootWalkLimits.EntryCeiling"/> is what stopped it, and nowhere else. It is a fact
/// about the LIMIT, never about the directory: "capped at 50,000 entries" says how far the walk got
/// to go, and says nothing about how many files are actually there.
/// <b>Null on a capped row stopped by the time budget</b> — see <paramref name="CappedAfter"/>, and
/// <see cref="VendorRootWalkLimits"/> for why the two bounds are independent. Stamping the ceiling on
/// a budget-stopped row would report a number the walk never came close to as the reason it stopped.
/// </param>
/// <param name="CappedAfter">
/// The wall-clock budget the walk exhausted, on a <see cref="VendorMemoryPresence.Capped"/> row whose
/// <see cref="VendorRootWalkLimits.Budget"/> is what stopped it, and nowhere else. Like
/// <paramref name="CappedAtEntries"/> it is the LIMIT, not a measurement: it says how long the walk
/// was allowed, not how long the directory would have taken. Exactly one of the two is non-null on a
/// capped row, and both are null on every other row.
/// </param>
public sealed record VendorMemoryRoot(
    string Family,
    string SourceVendor,
    VendorMemoryScope SourceScope,
    string DirectoryPath,
    VendorMemoryPresence Presence,
    int? FileCount,
    long? TotalBytes,
    DateTime? NewestModifiedUtc,
    IReadOnlyList<MemoryFile> Files,
    bool Inventoried,
    int? CappedAtEntries = null,
    TimeSpan? CappedAfter = null);

/// <summary>
/// What bounds one family's directory walk. A vendor tree is not this tool's to trust: it is written
/// by a live third-party process, can hold a junction pointing back at its own parent, and can grow
/// without anything here noticing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two independent bounds, because a walk fails slowly in two different ways.</b>
/// <see cref="EntryCeiling"/> catches a tree that is merely enormous; <see cref="Budget"/> catches
/// one whose entries are cheap to count and expensive to reach (a network path, a filter driver, a
/// disk under load). Either one hit reports <see cref="VendorMemoryPresence.Capped"/> — but WHICH one
/// hit is carried out of the walk and onto the row (<see cref="VendorMemoryRoot.CappedAtEntries"/> /
/// <see cref="VendorMemoryRoot.CappedAfter"/>), because they are independent: a walk stopped after
/// nine hundred entries by a slow disk is not evidence that the tree holds fifty thousand, and telling
/// an operator otherwise sends them to raise the wrong bound.
/// </para>
/// <para>
/// <b>Cycles need no visited-set here.</b> The walk never descends into a reparse point (junction or
/// symbolic link), which is the only way a directory tree on Windows can contain itself, so a cycle
/// cannot be entered in the first place; the ceiling is the backstop for anything that shape of
/// reasoning misses. A reparse point is also not counted — it names a file elsewhere, and this
/// inventory reports what a root holds.
/// </para>
/// </remarks>
/// <param name="EntryCeiling">
/// The most directory entries (files and directories alike) one family's walk may visit. The default
/// sits above the largest tree measured on a real host — see <c>VendorMemoryRootTable</c>'s
/// <c>antigravity-brain</c> rows and the register they point at — so an ordinary audit reports a
/// count rather than a cap, and a tree that has grown by an order of magnitude reports the cap.
/// </param>
/// <param name="Budget">Wall-clock ceiling for the same walk.</param>
public sealed record VendorRootWalkLimits(int EntryCeiling, TimeSpan Budget)
{
    /// <summary>What <c>baton memory audit</c> uses. Tests pass tiny limits to exercise the cap.</summary>
    public static VendorRootWalkLimits Default { get; } = new(50_000, TimeSpan.FromSeconds(30));
}

/// <summary>
/// The enumerated non-Claude memory root families — #1852 phase A2's answer to the plan's
/// "inventory all known vendor memory roots".
/// </summary>
/// <remarks>
/// <para>
/// <b>A fixed table, not a search.</b> Nothing here hunts for directories that look memory-shaped:
/// each row is a path a probe actually visited on 2026-09-05, and a family absent from this table is
/// one this tool cannot see at all. Growing it is a deliberate edit — the same property
/// <see cref="MemorySubjectVocabulary"/> is pinned for, and for the same reason: a root's report must
/// not depend on what else happens to sit on the machine.
/// </para>
/// <para>
/// <b>What each family was measured to be</b> is recorded once, in <c>docs/vendor-doc-audit.md</c>
/// (§"#1852 phase A2"), and the enumerated families are named in <c>spec/baton.md</c> §12. The
/// four one-line glosses below say only what the selector is for; they do not restate the findings.
/// </para>
/// </remarks>
public static class VendorMemoryRootTable
{
    /// <summary>
    /// The family slug of Codex's markdown memories — <b>the one non-Claude family phase B imports
    /// FROM</b> (the evening ruling of 2026-09-05: the markdown is what the CLI reads as memory).
    /// Named here rather than spelled at the importer, so the two can never drift apart silently.
    /// </summary>
    public const string CodexMarkdownFamily = "codex-markdown";

    /// <summary>
    /// The family slug of Codex's sqlite stores — <b>machinery, never a memory source</b>: A2 measured
    /// them to be the pipeline that produces the markdown above (docs/vendor-doc-audit.md, §"#1852
    /// phase A2"). Phase B records them in its manifest for provenance and reads nothing out of them.
    /// </summary>
    public const string CodexSqliteFamily = "codex-sqlite";

    /// <summary>Every family, in report order: Codex first, then Antigravity.</summary>
    public static IReadOnlyList<VendorMemoryFamily> Families { get; } =
    [
        // Codex's markdown memories. Free-form .md with no schema, so the selector is the extension.
        new(CodexMarkdownFamily, "codex", VendorMemoryScope.Vendor, ".codex/memories", "*.md", Recursive: true, Inventoried: true),

        // The same family under Baton's own Codex home. DERIVED BY MIRRORING the row above under
        // BatonPaths.Root, exactly as the Baton-managed `codex-sqlite` row below is -- CODEX_HOME
        // relocates a whole Codex root, so a family's relative path is the same on both sides of it.
        // NOT independently probed on 2026-09-05: A2 visited this directory for `memories_*.sqlite`
        // and nothing else, so `absent` here is a measurement of a host rather than a claim that the
        // vendor never writes markdown under a relocated home. It is enumerated because Q5 (operator,
        // 2026-09-05) puts this store's memories in phase B's import population and the evening ruling
        // of the same day makes MARKDOWN the only memory source -- without this row that population is
        // unreachable, and would stay silently unreachable if the directory ever filled.
        new(CodexMarkdownFamily, "codex", VendorMemoryScope.BatonManaged, "codex-home/memories", "*.md", Recursive: true, Inventoried: true),

        // Codex's sqlite memory stores, one per Codex home. Top-level only and `memories_*` only:
        // every other .sqlite in these directories is a log, a queue or a thread history, and two of
        // them are over 100 MB.
        new(CodexSqliteFamily, "codex", VendorMemoryScope.Vendor, ".codex", "memories_*.sqlite", Recursive: false, Inventoried: true),
        // Relative to BatonPaths.Root, not to the user home -- BATON_HOME moves it.
        new(CodexSqliteFamily, "codex", VendorMemoryScope.BatonManaged, "codex-home", "memories_*.sqlite", Recursive: false, Inventoried: true),

        // Antigravity's per-conversation working directories. LOCATED AND COUNTED, NEVER OPENED --
        // the register (docs/vendor-doc-audit.md, "#1852 phase A2") has what was found in them, the
        // measured file count, and why it is not durable memory. What that costs HERE is the point:
        // digesting them would spend a five-figure file walk per audit to re-learn a conclusion
        // already recorded.
        new("antigravity-brain", "antigravity", VendorMemoryScope.Vendor, ".gemini/antigravity/brain", "*", Recursive: true, Inventoried: false),
        new("antigravity-brain", "antigravity", VendorMemoryScope.Vendor, ".gemini/antigravity-cli/brain", "*", Recursive: true, Inventoried: false),

        // Antigravity's knowledge directories. Small enough to inventory whole, and the lock file it
        // holds is exactly the evidence that "empty" is not "absent".
        new("antigravity-knowledge", "antigravity", VendorMemoryScope.Vendor, ".gemini/antigravity/knowledge", "*", Recursive: true, Inventoried: true),
        new("antigravity-knowledge", "antigravity", VendorMemoryScope.Vendor, ".gemini/antigravity-cli/knowledge", "*", Recursive: true, Inventoried: true),

        // Antigravity's protobuf-text annotations, one per conversation id.
        new("antigravity-pbtxt", "antigravity", VendorMemoryScope.Vendor, ".gemini/antigravity/annotations", "*.pbtxt", Recursive: false, Inventoried: true),
        new("antigravity-pbtxt", "antigravity", VendorMemoryScope.Vendor, ".gemini/antigravity-cli/annotations", "*.pbtxt", Recursive: false, Inventoried: true),
    ];
}
