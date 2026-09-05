using Baton.Memory;
using Baton.Tests.Shared;

namespace Baton.Tests.Memory;

/// <summary>
/// #1852 phase A: the decoder's ambiguity, and the session-<c>cwd</c> ground truth that resolves it.
/// </summary>
/// <remarks>
/// <b>The control arm is the point of this file.</b> "Session cwd wins" is a claim about an ORDERING,
/// and an ordering test proves nothing unless the loser would have given a different answer — a
/// decoder that happened to be right anyway makes the ground-truth read look load-bearing when it is
/// decoration. So every ordering assertion below is paired with an assertion that the decoder alone
/// is genuinely ambiguous for the same name.
/// </remarks>
public sealed class MemoryRootPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"baton-1852-path-{Guid.NewGuid():N}");

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_root);

    /// <summary>
    /// The live name from the #1852 survey: <c>-</c> encodes both a separator and a literal hyphen, so
    /// <c>...repos-aer-aer-flow</c> reads as <c>repos\aer\aer-flow</c> AND as <c>repos\aer-aer-flow</c>.
    /// </summary>
    [Fact]
    public void The_decoder_alone_is_ambiguous_for_a_name_whose_hyphens_could_be_separators()
    {
        const string Name = "C--Users-pbree-source-repos-aer-aer-flow";

        var candidates = MemoryRootPath.DecodeCandidates(Name).ToList();

        Assert.Contains(@"C:\Users\pbree\source\repos\aer\aer-flow", candidates);
        Assert.Contains(@"C:\Users\pbree\source\repos\aer-aer-flow", candidates);
        Assert.True(MemoryRootPath.IsAmbiguousByName(Name));

        // With nothing on disk to break the tie and no transcripts to read, the resolver must SAY
        // ambiguous and carry no chosen path -- not pick the first reading and look confident.
        var resolved = MemoryRootPath.Resolve(Name, [], _ => false);
        Assert.Equal(MemoryPathSource.Ambiguous, resolved.Source);
        Assert.Null(resolved.CheckoutPath);
        Assert.True(resolved.Candidates.Count > 1);
    }

    [Fact]
    public void A_session_cwd_resolves_the_name_the_decoder_cannot()
    {
        const string Name = "C--Users-pbree-source-repos-aer-aer-flow";
        const string Truth = @"C:\Users\pbree\source\repos\aer\aer-flow";

        var resolved = MemoryRootPath.Resolve(Name, [Truth], _ => false);

        Assert.Equal(MemoryPathSource.SessionCwd, resolved.Source);
        Assert.Equal(Truth, resolved.CheckoutPath);

        // The other half of the polarity: the SAME name, same absent disk, different transcript ->
        // a different answer. Without this the assertion above passes on a resolver that ignores its
        // ground-truth argument and happens to decode to that path first.
        const string OtherTruth = @"C:\Users\pbree\source\repos\aer-aer-flow";
        Assert.Equal(OtherTruth, MemoryRootPath.Resolve(Name, [OtherTruth], _ => false).CheckoutPath);
    }

    /// <summary>
    /// The negative the decoder's own remarks state: <c>.</c> and <c>_</c> also encode to <c>-</c>, so
    /// no reading of <c>C--Users-pbree--baton</c> produces <c>C:\Users\pbree\.baton</c>. Pinned as a
    /// test rather than left as prose — it is the reason the session read is the ground truth and not
    /// merely a nicety.
    /// </summary>
    [Fact]
    public void The_decoder_cannot_recover_a_dot_and_says_so_by_not_producing_one()
    {
        const string Name = "C--Users-pbree--baton";

        Assert.DoesNotContain(@"C:\Users\pbree\.baton", MemoryRootPath.DecodeCandidates(Name));

        var resolved = MemoryRootPath.Resolve(Name, [@"C:\Users\pbree\.baton"], _ => false);
        Assert.Equal(MemoryPathSource.SessionCwd, resolved.Source);
        Assert.Equal(@"C:\Users\pbree\.baton", resolved.CheckoutPath);
    }

    [Fact]
    public void Disk_breaks_a_decoder_tie_when_exactly_one_reading_exists()
    {
        const string Name = "C--Users-pbree-source-repos-aer-aer-flow";
        const string OnDisk = @"C:\Users\pbree\source\repos\aer\aer-flow";

        var resolved = MemoryRootPath.Resolve(
            Name, [], path => string.Equals(path, OnDisk, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(MemoryPathSource.DecodedExisting, resolved.Source);
        Assert.Equal(OnDisk, resolved.CheckoutPath);

        // Control: two readings on disk is not a tie broken, it is a tie observed.
        var both = MemoryRootPath.Resolve(
            Name,
            [],
            path => string.Equals(path, OnDisk, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, @"C:\Users\pbree\source\repos\aer-aer-flow", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(MemoryPathSource.Ambiguous, both.Source);
        Assert.Null(both.CheckoutPath);
    }

    [Fact]
    public void A_name_with_one_segment_decodes_unambiguously()
    {
        var resolved = MemoryRootPath.Resolve("C--baton", [], _ => false);

        Assert.Equal(MemoryPathSource.DecodedUnique, resolved.Source);
        Assert.Equal(@"C:\baton", resolved.CheckoutPath);
        Assert.False(MemoryRootPath.IsAmbiguousByName("C--baton"));
    }

    [Fact]
    public void Session_transcripts_that_disagree_are_an_ambiguity_not_a_first_wins()
    {
        var resolved = MemoryRootPath.Resolve("C--baton", [@"C:\one", @"C:\two"], _ => false);

        Assert.Equal(MemoryPathSource.Ambiguous, resolved.Source);
        Assert.Null(resolved.CheckoutPath);
        Assert.Equal([@"C:\one", @"C:\two"], resolved.Candidates);
    }

    [Fact]
    public void The_working_directory_is_read_from_a_transcripts_first_line()
    {
        var project = Path.Combine(_root, "projects", "C--Users-pbree-source-repos-aer-aer-flow");
        Directory.CreateDirectory(project);
        File.WriteAllText(
            Path.Combine(project, "049bc602.jsonl"),
            """
            {"type":"summary","cwd":"C:\\Users\\pbree\\source\\repos\\aer\\aer-flow"}
            {"type":"user","cwd":"C:\\Users\\pbree\\source\\repos\\aer\\aer-flow"}
            """);

        Assert.Equal(
            [@"C:\Users\pbree\source\repos\aer\aer-flow"],
            MemoryRootPath.ReadSessionWorkingDirectories(project));

        // Control: a directory with no transcripts yields nothing, so the decoder is what runs -- the
        // assertion above is not passing on a hardcoded default.
        Assert.Empty(MemoryRootPath.ReadSessionWorkingDirectories(Path.Combine(_root, "projects", "absent")));
    }

    [Fact]
    public void A_transcript_that_is_not_json_contributes_no_ground_truth()
    {
        var project = Path.Combine(_root, "projects", "C--broken");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "a.jsonl"), "not json at all\n");

        Assert.Empty(MemoryRootPath.ReadSessionWorkingDirectories(project));
    }
}
