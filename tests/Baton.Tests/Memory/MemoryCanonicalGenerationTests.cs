using System.Diagnostics;
using Baton.Memory;
using Baton.Tests.Shared;

namespace Baton.Tests.Memory;

public sealed class MemoryCanonicalGenerationTests
{
    private static string CreateRoot() =>
        Directory.CreateTempSubdirectory($"baton-memory-generation-{Guid.NewGuid():N}-").FullName;

    private static void WithRoot(Action<string> test)
    {
        var root = CreateRoot();
        try
        {
            test(root);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    private static string GenerationPath(string root) => Path.Combine(root, "memory-generation");

    private static void AssertNoGenerationTemps(string root)
    {
        Assert.Empty(Directory.EnumerateFiles(root, "memory-generation.*.tmp"));
    }

    private static void AssertReplacementFailure(Exception error)
    {
        Assert.True(
            error is IOException or UnauthorizedAccessException,
            $"Expected an actual replacement failure, got {error.GetType().FullName}: {error.Message}");
    }

    [Fact]
    public void A_transient_real_holder_is_retried_and_publishes_once()
    {
        if (!OperatingSystem.IsWindows()) return;

        WithRoot(root =>
        {
            Assert.Equal(7, MemoryCanonicalGeneration.Mutate(root, () => 7));
            var oldGeneration = MemoryCanonicalGeneration.Capture(root);
            var generationPath = GenerationPath(root);
            using var holder = new FileStream(
                generationPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            Assert.Equal(oldGeneration, MemoryCanonicalGeneration.Capture(root));

            var replacementFailures = 0;
            var actionCalls = 0;
            var result = MemoryCanonicalGeneration.Mutate(
                root,
                () =>
                {
                    actionCalls++;
                    return "published";
                },
                TimeSpan.FromSeconds(1),
                error =>
                {
                    AssertReplacementFailure(error);
                    replacementFailures++;
                    if (replacementFailures == 1)
                        holder.Dispose();
                });

            Assert.Equal("published", result);
            Assert.Equal(1, actionCalls);
            Assert.Equal(1, replacementFailures);

            var newGeneration = MemoryCanonicalGeneration.Capture(root);
            Assert.NotEqual(oldGeneration, newGeneration);

            var staleActionCalls = 0;
            Assert.Throws<IOException>(() => MemoryCanonicalGeneration.ReadCurrent(
                root,
                oldGeneration,
                () =>
                {
                    staleActionCalls++;
                    return 0;
                }));
            Assert.Equal(0, staleActionCalls);
            AssertNoGenerationTemps(root);
        });
    }

    [Fact]
    public void A_persistent_real_holder_fails_bounded_without_mutating_state_or_action()
    {
        if (!OperatingSystem.IsWindows()) return;

        WithRoot(root =>
        {
            MemoryCanonicalGeneration.Mutate(root, () => 1);
            var oldGeneration = MemoryCanonicalGeneration.Capture(root);
            var generationPath = GenerationPath(root);
            var oldBytes = File.ReadAllBytes(generationPath);
            var canonicalPath = Path.Combine(root, "canonical.jsonl");
            File.WriteAllText(canonicalPath, "retained canonical bytes\n");
            var canonicalBytes = File.ReadAllBytes(canonicalPath);
            using var holder = new FileStream(
                generationPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var replacementFailures = 0;
            var actionCalls = 0;
            var stopwatch = Stopwatch.StartNew();
            Exception? thrown = null;
            try
            {
                MemoryCanonicalGeneration.Mutate(
                    root,
                    () =>
                    {
                        actionCalls++;
                        File.WriteAllText(canonicalPath, "must not happen");
                        return 2;
                    },
                    TimeSpan.FromMilliseconds(60),
                    error =>
                    {
                        AssertReplacementFailure(error);
                        replacementFailures++;
                    });
            }
            catch (Exception error)
            {
                thrown = error;
            }

            Assert.NotNull(thrown);
            AssertReplacementFailure(thrown!);
            Assert.True(replacementFailures > 0);
            Assert.Equal(0, actionCalls);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"Persistent replacement exceeded the bounded test ceiling: {stopwatch.Elapsed}.");

            Assert.Equal(oldGeneration, MemoryCanonicalGeneration.Capture(root));
            Assert.Equal(oldBytes, File.ReadAllBytes(generationPath));
            Assert.Equal(canonicalBytes, File.ReadAllBytes(canonicalPath));
            Assert.Equal("unchanged", MemoryCanonicalGeneration.ReadCurrent(
                root, oldGeneration, () => "unchanged"));
            AssertNoGenerationTemps(root);
        });
    }

    [Fact]
    public void A_zero_retry_budget_still_makes_one_real_replacement_attempt()
    {
        if (!OperatingSystem.IsWindows()) return;

        WithRoot(root =>
        {
            MemoryCanonicalGeneration.Mutate(root, () => 1);
            var oldGeneration = MemoryCanonicalGeneration.Capture(root);
            var generationPath = GenerationPath(root);
            using var holder = new FileStream(
                generationPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var replacementFailures = 0;
            var actionCalls = 0;
            Exception? thrown = null;
            try
            {
                MemoryCanonicalGeneration.Mutate(
                    root,
                    () =>
                    {
                        actionCalls++;
                        return 2;
                    },
                    TimeSpan.Zero,
                    error =>
                    {
                        AssertReplacementFailure(error);
                        replacementFailures++;
                    });
            }
            catch (Exception error)
            {
                thrown = error;
            }

            Assert.NotNull(thrown);
            AssertReplacementFailure(thrown!);
            Assert.Equal(1, replacementFailures);
            Assert.Equal(0, actionCalls);
            Assert.Equal(oldGeneration, MemoryCanonicalGeneration.Capture(root));
            AssertNoGenerationTemps(root);
        });
    }

    [Fact]
    public void Corrupt_generation_fails_before_staging_replacement_or_action()
    {
        WithRoot(root =>
        {
            var generationPath = GenerationPath(root);
            const string corrupt = "not-a-guid";
            File.WriteAllText(generationPath, corrupt);
            var replacementFailures = 0;
            var actionCalls = 0;

            Assert.Throws<InvalidDataException>(() => MemoryCanonicalGeneration.Mutate(
                root,
                () =>
                {
                    actionCalls++;
                    return 1;
                },
                TimeSpan.FromMilliseconds(60),
                _ => replacementFailures++));

            Assert.Equal(corrupt, File.ReadAllText(generationPath));
            Assert.Equal(0, replacementFailures);
            Assert.Equal(0, actionCalls);
            AssertNoGenerationTemps(root);
        });
    }

    [Fact]
    public void An_absent_generation_is_created_by_a_natural_success()
    {
        WithRoot(root =>
        {
            var actionCalls = 0;
            var result = MemoryCanonicalGeneration.Mutate(
                root,
                () =>
                {
                    actionCalls++;
                    return "created";
                });

            Assert.Equal("created", result);
            Assert.Equal(1, actionCalls);
            Assert.True(Guid.TryParseExact(
                MemoryCanonicalGeneration.Capture(root), "N", out _));
            Assert.True(File.Exists(GenerationPath(root)));
            AssertNoGenerationTemps(root);
        });
    }

    [Fact]
    public void A_throwing_action_keeps_the_new_generation_and_refuses_the_stale_reader()
    {
        WithRoot(root =>
        {
            MemoryCanonicalGeneration.Mutate(root, () => 1);
            var oldGeneration = MemoryCanonicalGeneration.Capture(root);
            var expected = new InvalidOperationException("canonical action failed");
            var actionCalls = 0;

            var thrown = Assert.Throws<InvalidOperationException>(() =>
                MemoryCanonicalGeneration.Mutate<int>(
                    root,
                    () =>
                    {
                        actionCalls++;
                        throw expected;
                    },
                    TimeSpan.FromMilliseconds(60)));

            Assert.Same(expected, thrown);
            Assert.Equal(1, actionCalls);
            var newGeneration = MemoryCanonicalGeneration.Capture(root);
            Assert.NotEqual(oldGeneration, newGeneration);

            var staleActionCalls = 0;
            Assert.Throws<IOException>(() => MemoryCanonicalGeneration.ReadCurrent(
                root,
                oldGeneration,
                () =>
                {
                    staleActionCalls++;
                    return 0;
                }));
            Assert.Equal(0, staleActionCalls);
            AssertNoGenerationTemps(root);
        });
    }

    [Fact]
    public void A_throwing_replacement_observer_preserves_the_real_failure_and_state()
    {
        if (!OperatingSystem.IsWindows()) return;

        WithRoot(root =>
        {
            MemoryCanonicalGeneration.Mutate(root, () => 1);
            var oldGeneration = MemoryCanonicalGeneration.Capture(root);
            var generationPath = GenerationPath(root);
            var canonicalPath = Path.Combine(root, "canonical.jsonl");
            File.WriteAllText(canonicalPath, "observer sentinel\n");
            var canonicalBytes = File.ReadAllBytes(canonicalPath);
            using var holder = new FileStream(
                generationPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var knownObserverFailure = new InvalidOperationException("observer deliberately stopped retry");
            Exception? actualReplacementFailure = null;
            var actionCalls = 0;
            var thrown = Assert.Throws<InvalidOperationException>(() =>
                MemoryCanonicalGeneration.Mutate<int>(
                    root,
                    () =>
                    {
                        actionCalls++;
                        return 2;
                    },
                    TimeSpan.FromSeconds(1),
                    error =>
                    {
                        AssertReplacementFailure(error);
                        actualReplacementFailure = error;
                        throw knownObserverFailure;
                    }));

            Assert.Same(knownObserverFailure, thrown);
            Assert.Same(actualReplacementFailure, thrown.Data["MemoryGenerationReplacementFailure"]);
            Assert.NotNull(actualReplacementFailure);
            Assert.Equal(0, actionCalls);
            Assert.Equal(oldGeneration, MemoryCanonicalGeneration.Capture(root));
            Assert.Equal(canonicalBytes, File.ReadAllBytes(canonicalPath));
            AssertNoGenerationTemps(root);
        });
    }

    [Fact]
    public void Cleanup_failure_does_not_mask_the_first_real_replacement_failure()
    {
        if (!OperatingSystem.IsWindows()) return;

        WithRoot(root =>
        {
            MemoryCanonicalGeneration.Mutate(root, () => 1);
            var oldGeneration = MemoryCanonicalGeneration.Capture(root);
            var generationPath = GenerationPath(root);
            var canonicalPath = Path.Combine(root, "canonical.jsonl");
            File.WriteAllText(canonicalPath, "cleanup sentinel\n");
            var canonicalBytes = File.ReadAllBytes(canonicalPath);
            using var holder = new FileStream(
                generationPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            FileStream? stagedHolder = null;
            string? stagedPath = null;
            var replacementFailures = 0;
            var actionCalls = 0;
            Exception? firstReplacementFailure = null;
            Exception? thrown = null;
            try
            {
                try
                {
                    MemoryCanonicalGeneration.Mutate<int>(
                        root,
                        () =>
                        {
                            actionCalls++;
                            return 2;
                        },
                        TimeSpan.Zero,
                        error =>
                        {
                            AssertReplacementFailure(error);
                            replacementFailures++;
                            firstReplacementFailure = error;
                            stagedPath = Assert.Single(
                                Directory.EnumerateFiles(root, "memory-generation.*.tmp"));
                            stagedHolder = new FileStream(
                                stagedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        });
                }
                catch (Exception error)
                {
                    thrown = error;
                }

                Assert.NotNull(thrown);
                Assert.Same(firstReplacementFailure, thrown);
                Assert.Equal(1, replacementFailures);
                Assert.Equal(0, actionCalls);
                AssertReplacementFailure(thrown!);
                var cleanupFailure = Assert.IsAssignableFrom<Exception>(
                    thrown!.Data["MemoryGenerationCleanupFailure"]);
                AssertReplacementFailure(cleanupFailure);
                Assert.NotNull(stagedHolder);
                Assert.True(File.Exists(stagedPath));
                Assert.Equal(oldGeneration, MemoryCanonicalGeneration.Capture(root));
                Assert.Equal(canonicalBytes, File.ReadAllBytes(canonicalPath));
            }
            finally
            {
                stagedHolder?.Dispose();
                if (stagedPath is not null && File.Exists(stagedPath))
                    FileCleanup.Delete(stagedPath);
            }

            AssertNoGenerationTemps(root);
        });
    }
}

