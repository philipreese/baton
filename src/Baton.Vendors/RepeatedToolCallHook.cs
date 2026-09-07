namespace Baton.Vendors;

/// <summary>
/// #2002 rule 2 on the vendors whose gate is a <c>PreToolUse</c> hook. The broker holds its
/// <see cref="RepeatedToolCallLedger"/> in memory for the life of a dispatch; a hook is a fresh
/// subprocess per tool call and holds nothing, so this loads and saves the same ledger from a file in
/// the execution's own output directory. One class, one set of predicates, two lifetimes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every failure here is an allow, and that is deliberate.</b> This rung removes waste; it is not a
/// permission boundary, and the two hooks it serves both wrap their decision in a catch that DENIES.
/// An unreadable, half-written or malformed ledger reaching that catch would turn a missed
/// deduplication into a refusal reading "the permission gate failed internally", which is a strictly
/// worse outcome than re-running a command. So each entry point swallows its own I/O and parse
/// failures and returns "allow" — the exception the repo's error-handling rule makes for a
/// best-effort mechanism, the same one <c>AgyHookCheckCommand.AppendVerdictLedgerLine</c> already
/// takes, and for the same reason.
/// </para>
/// <para>
/// <b>Concurrency:</b> <see cref="RepeatedToolCallLedger.Save"/> writes a temporary file and replaces
/// atomically, so a hook reading while another writes sees one whole state or the other. Two hooks
/// saving at once can still lose one's update; that costs a missed deduplication, which is the same
/// direction as every other failure here.
/// </para>
/// </remarks>
public static class RepeatedToolCallHook
{
    /// <summary>
    /// The deny sentence for a repeated <paramref name="commandLine"/>, or <see langword="null"/> to
    /// allow it. <paramref name="outputDirectory"/> is this execution's <c>BATON_OUTPUT_DIR</c>; a
    /// missing or non-rooted one disables the rung entirely, because there is nowhere to remember.
    /// </summary>
    public static string? JudgeCommand(string? outputDirectory, string? commandLine) =>
        string.IsNullOrWhiteSpace(commandLine)
            ? null
            : WithLedger(outputDirectory, ledger => ledger.ClassifyHookCommand(commandLine));

    /// <summary>
    /// The deny sentence for a re-read of <paramref name="path"/> whose mtime and length are unchanged
    /// since this room last read it, or <see langword="null"/> to allow. A path that cannot be stat'd
    /// — it does not exist yet, or this process cannot see it — allows: the read is about to fail or
    /// to produce something new, and either way there is no previous answer standing in for it.
    /// </summary>
    public static string? JudgeRead(string? outputDirectory, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return null;
        }

        return WithLedger(
            outputDirectory,
            ledger => ledger.ClassifyHookRead(info.FullName, info.LastWriteTimeUtc, info.Length));
    }

    /// <summary>
    /// Records that the room is about to write <paramref name="writeTarget"/>: the read entry for that
    /// path is forgotten, and every remembered command output with it, because after the tree changes
    /// no cached command output is the answer (<see cref="RepeatedToolCallLedger.ForgetAllCommands"/>).
    /// Called on the allow path of a write-family tool on both hooks, which is the only point either
    /// one learns that a write is coming.
    /// </summary>
    public static void NoteWrite(string? outputDirectory, string? writeTarget)
    {
        WithLedger(outputDirectory, ledger =>
        {
            ledger.ForgetAllCommands();
            if (!string.IsNullOrWhiteSpace(writeTarget))
            {
                ledger.ForgetRead(writeTarget);
            }

            return null;
        });
    }

    /// <summary>
    /// The ledger file for <paramref name="outputDirectory"/>, or <see langword="null"/> when this
    /// process cannot say where it is. Non-rooted is the same failure #668 named on the outbox: a
    /// relative path in a hook subprocess resolves against a working directory nobody chose.
    /// </summary>
    public static string? ResolvePath(string? outputDirectory) =>
        string.IsNullOrWhiteSpace(outputDirectory) || !Path.IsPathRooted(outputDirectory)
            ? null
            : Path.Combine(outputDirectory, RepeatedToolCallLedger.FileName);

    private static string? WithLedger(string? outputDirectory, Func<RepeatedToolCallLedger, string?> judge)
    {
        if (ResolvePath(outputDirectory) is not { } path)
        {
            return null;
        }

        try
        {
            var ledger = RepeatedToolCallLedger.Load(path);
            var verdict = judge(ledger);
            ledger.Save(path);
            return verdict;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or System.Text.Json.JsonException)
        {
            // Allow. See this type's remarks: a broken ledger must not become a permission denial.
            return null;
        }
    }
}
