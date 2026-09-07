using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Baton.Status;

namespace Baton.Tests.Status;

/// <summary>
/// <see cref="MutexGuardedFileLock"/> is <c>RoomRegistryStore</c>'s own mutex primitive, extracted so
/// <see cref="QuotaLedgerStore"/> (#1570) can share it. The name-format pin below is the regression
/// guard for that extraction — see the type's own remarks for why a renamed lock is a silent hazard,
/// not just a cosmetic diff: every in-process test would still pass against it.
/// </summary>
public sealed class MutexGuardedFileLockTests
{
    [Fact]
    public void BuildMutexName_matches_the_literal_format_RoomRegistryStore_shipped_before_the_extraction()
    {
        var path = @"C:\Users\test\.baton\room-registry.jsonl";
        var expectedDigest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(BatonPaths.RecordKey(path).ToUpperInvariant())));
        var expected = $"Global\\baton-room-registry-{expectedDigest}";

        var actual = MutexGuardedFileLock.BuildMutexName(path, "baton-room-registry");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildMutexName_is_insensitive_to_path_spelling_the_same_way_RecordKey_is()
    {
        var forward = MutexGuardedFileLock.BuildMutexName(@"C:\home\.baton\room-registry.jsonl", "baton-room-registry");
        var trailingSeparator = MutexGuardedFileLock.BuildMutexName(@"C:\home\.baton\room-registry.jsonl\", "baton-room-registry");
        var differentCasing = MutexGuardedFileLock.BuildMutexName(@"c:\HOME\.baton\ROOM-REGISTRY.jsonl", "baton-room-registry");

        Assert.Equal(forward, trailingSeparator);
        Assert.Equal(forward, differentCasing);
    }

    [Fact]
    public void Distinct_lock_name_prefixes_against_the_same_path_never_collide()
    {
        var path = @"C:\home\.baton\shared-name.jsonl";

        var registryName = MutexGuardedFileLock.BuildMutexName(path, "baton-room-registry");
        var ledgerName = MutexGuardedFileLock.BuildMutexName(path, "baton-quota-ledger");

        Assert.NotEqual(registryName, ledgerName);
    }

    [Fact]
    public void RunUnderLock_returns_the_action_result_and_releases_for_the_next_caller()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mutex-guarded-{Guid.NewGuid():N}.jsonl");

        var first = MutexGuardedFileLock.RunUnderLock(path, "test-prefix", TimeSpan.FromSeconds(5), () => 41);
        var second = MutexGuardedFileLock.RunUnderLock(path, "test-prefix", TimeSpan.FromSeconds(5), () => first + 1);

        Assert.Equal(42, second);
    }

    [Fact]
    public void RunUnderLock_releases_even_when_the_action_throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mutex-guarded-{Guid.NewGuid():N}.jsonl");

        Assert.Throws<InvalidOperationException>(() =>
            MutexGuardedFileLock.RunUnderLock(path, "test-prefix", TimeSpan.FromSeconds(5), () =>
            {
                throw new InvalidOperationException("boom");
            }));

        // If the throwing call above leaked the mutex, this second acquisition on the same
        // (path, prefix) would hang until the test's own timeout rather than return promptly.
        var result = MutexGuardedFileLock.RunUnderLock(path, "test-prefix", TimeSpan.FromSeconds(5), () => "released");
        Assert.Equal("released", result);
    }

    /// <summary>
    /// #1942's retry budget scaled down to milliseconds, with the backoff deliberately an order of
    /// magnitude larger than the kernel wait: the two backoff sleeps alone are ≥ 600 ms, so an elapsed
    /// time past that floor cannot be produced by a policy that stopped early — three attempts without
    /// any backoff would be ≈ 60 ms, and two attempts with backoff at most ≈ 420 ms.
    /// </summary>
    private static readonly LockWaitPolicy GiveUpPolicy = new(
        AttemptTimeout: TimeSpan.FromMilliseconds(20), MaxAttempts: 3, BackoffBase: TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// <c>Thread.Sleep</c> never returns early, so the sum of the guaranteed (un-jittered) backoff gaps
    /// is a hard floor on a run that used every attempt: 200 ms after attempt 1, 400 ms after attempt 2.
    /// </summary>
    private static readonly TimeSpan BackoffFloor = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// #1942, the property the retry must not have cost us: a policy that <em>retries</em> and never
    /// gets the lock still gives up. Both of the store-level give-up arms use
    /// <c>LockWaitPolicy.Single</c>, which skips the backoff sleep entirely, so this is the only arm
    /// that walks the loop's giving-up direction with <c>MaxAttempts &gt; 1</c>. It pins all three
    /// halves of "bounded": every attempt was made (elapsed past <see cref="BackoffFloor"/>), the
    /// backoff sleep between them was reached (that floor is backoff time, not wait time), and the
    /// whole thing still terminates inside the policy's own worst case rather than blocking on the
    /// holder. <see cref="A_single_attempt_against_the_same_never_released_lock_gives_up_before_the_retry_budget"/>
    /// is the control: same never-released holder, one attempt, and it returns below the floor — so a
    /// passing arm here is about the retry rather than about a slow machine.
    /// </summary>
    [Fact]
    public void A_retrying_policy_that_never_gets_the_lock_still_gives_up_inside_its_own_budget()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mutex-guarded-{Guid.NewGuid():N}.jsonl");
        using var holder = new NeverReleasedLock(path, "test-prefix");

        var started = Stopwatch.StartNew();
        var timeout = Assert.Throws<IOException>(() =>
            MutexGuardedFileLock.RunUnderLock(path, "test-prefix", GiveUpPolicy, () => "never runs"));
        var elapsed = started.Elapsed;

        Assert.Contains(GiveUpPolicy.MaxTotalWait.ToString(), timeout.Message, StringComparison.Ordinal);
        Assert.True(
            elapsed >= BackoffFloor,
            $"Gave up after {elapsed}, which is short of the {BackoffFloor} of backoff a full three-attempt run must sleep.");
        // The policy's own worst case: three kernel waits, plus two backoff gaps each at most twice its
        // base. The added second is scheduling slop for the thread pool, not part of the budget.
        var worstCase = GiveUpPolicy.MaxTotalWait + (4 * GiveUpPolicy.BackoffBase) + TimeSpan.FromSeconds(1);
        Assert.True(elapsed < worstCase, $"Gave up after {elapsed}, past its own {worstCase} ceiling.");
    }

    /// <summary>
    /// The control for <see cref="A_retrying_policy_that_never_gets_the_lock_still_gives_up_inside_its_own_budget"/>:
    /// the pre-#1942 single-shot wait against the identical never-released holder. It throws the same
    /// <see cref="IOException"/>, but well inside the backoff floor — which is what makes the retry
    /// arm's elapsed time evidence about the retry loop instead of about the fixture.
    /// </summary>
    [Fact]
    public void A_single_attempt_against_the_same_never_released_lock_gives_up_before_the_retry_budget()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mutex-guarded-{Guid.NewGuid():N}.jsonl");
        using var holder = new NeverReleasedLock(path, "test-prefix");

        var started = Stopwatch.StartNew();
        Assert.Throws<IOException>(() => MutexGuardedFileLock.RunUnderLock(
            path, "test-prefix", LockWaitPolicy.Single(GiveUpPolicy.AttemptTimeout), () => "never runs"));
        var elapsed = started.Elapsed;

        Assert.True(
            elapsed < BackoffFloor,
            $"One attempt took {elapsed}, so the retry arm's floor of {BackoffFloor} no longer discriminates.");
    }

    [Fact]
    public void MaxTotalWait_reports_every_attempt_not_one_slice()
    {
        var policy = new LockWaitPolicy(TimeSpan.FromSeconds(20), MaxAttempts: 3, BackoffBase: TimeSpan.FromMilliseconds(100));

        Assert.Equal(TimeSpan.FromSeconds(60), policy.MaxTotalWait);
        Assert.Equal(TimeSpan.FromSeconds(5), LockWaitPolicy.Single(TimeSpan.FromSeconds(5)).MaxTotalWait);
    }

    [Fact]
    public void BackoffAfterAttempt_grows_linearly_and_stays_inside_one_base_of_jitter()
    {
        var policy = new LockWaitPolicy(TimeSpan.FromSeconds(1), MaxAttempts: 5, BackoffBase: TimeSpan.FromMilliseconds(100));

        // Sampled, because the jitter is random: every draw for attempt n must land in [100n, 100(n+1)).
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            for (var draw = 0; draw < 50; draw++)
            {
                var backoff = policy.BackoffAfterAttempt(attempt);
                Assert.InRange(
                    backoff,
                    TimeSpan.FromMilliseconds(100 * attempt),
                    TimeSpan.FromMilliseconds((100 * (attempt + 1)) - 1));
            }
        }
    }

    [Fact]
    public void A_policy_with_no_backoff_never_sleeps()
    {
        Assert.Equal(TimeSpan.Zero, LockWaitPolicy.Single(TimeSpan.FromSeconds(5)).BackoffAfterAttempt(1));
        Assert.Equal(TimeSpan.Zero, LockWaitPolicy.Single(TimeSpan.FromSeconds(5)).BackoffAfterAttempt(7));
    }

    [Fact]
    public void A_policy_with_fewer_than_one_attempt_is_rejected_rather_than_silently_never_waiting()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mutex-guarded-{Guid.NewGuid():N}.jsonl");
        var zeroAttempts = new LockWaitPolicy(TimeSpan.FromSeconds(1), MaxAttempts: 0, BackoffBase: TimeSpan.Zero);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MutexGuardedFileLock.RunUnderLock(path, "test-prefix", zeroAttempts, () => "never runs"));
    }

    /// <summary>
    /// Holds the real lock for <paramref name="filePath"/>/<paramref name="lockNamePrefix"/> on a thread
    /// of its own and never hands it back until disposed. A dedicated thread rather than a task because
    /// of the ownership affinity <see cref="MutexGuardedFileLock"/>'s own remarks state.
    /// Acquired through the shipped <see cref="MutexGuardedFileLock.RunUnderLock{T}(string,string,TimeSpan,Func{T})"/>
    /// rather than a <see cref="Mutex"/> built here, so the holder can only ever contend on the same
    /// kernel object a second process would.
    /// </summary>
    private sealed class NeverReleasedLock : IDisposable
    {
        private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(30);

        private readonly Thread thread;
        private readonly ManualResetEventSlim acquired = new(false);
        private readonly ManualResetEventSlim releaseRequested = new(false);

        internal NeverReleasedLock(string filePath, string lockNamePrefix)
        {
            thread = new Thread(() => MutexGuardedFileLock.RunUnderLock(
                filePath, lockNamePrefix, TimeSpan.FromSeconds(30), () =>
                {
                    acquired.Set();
                    releaseRequested.Wait();
                    return 0;
                }))
            {
                IsBackground = true,
                Name = "never-released-lock",
            };
            thread.Start();
            Assert.True(acquired.Wait(JoinTimeout), "The holder thread never acquired the lock.");
        }

        public void Dispose()
        {
            releaseRequested.Set();
            thread.Join(JoinTimeout);
            acquired.Dispose();
            releaseRequested.Dispose();
        }
    }
}
