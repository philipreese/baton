using System.Diagnostics;
using Baton.Cli.Tests.TestSupport;

namespace Baton.Cli.Tests;

public sealed class TaskQueueHelpTests
{
    public static IEnumerable<object[]> ExactHelpForms()
    {
        yield return new object[] { new[] { "task", "--help" }, TaskOptionsParser.Usage };
        yield return new object[] { new[] { "task", "submit", "--help" }, TaskOptionsParser.Usage };
        yield return new object[] { new[] { "queue", "--help" }, QueueOptionsParser.Usage };
        yield return new object[] { new[] { "queue", "add", "--help" }, QueueOptionsParser.Usage };
    }

    public static IEnumerable<object[]> RefusedHelpForms()
    {
        yield return new object[] { new[] { "task", "--help", "--unknown" }, (int)RunExitCode.ValidationRefused };
        yield return new object[] { new[] { "task", "submit", "--help", "--unknown" }, (int)RunExitCode.ValidationRefused };
        yield return new object[] { new[] { "queue", "--help", "--unknown" }, 1 };
        yield return new object[] { new[] { "queue", "add", "--help", "--unknown" }, 1 };
        yield return new object[] { new[] { "task", "--help", "--help" }, (int)RunExitCode.ValidationRefused };
        yield return new object[] { new[] { "queue", "add", "--help", "--help" }, 1 };
        yield return new object[] { new[] { "task", "--help=1" }, (int)RunExitCode.ValidationRefused };
        yield return new object[] { new[] { "queue", "--HELP" }, 1 };
        yield return new object[] { new[] { "submit", "task", "--help" }, 64 };
        yield return new object[] { new[] { "add", "queue", "--help" }, 64 };
        yield return new object[]
        {
            new[] { "task", "--help", "submit", "--issue", "1", "--project", "project", "--declared-size", "small", "--size-rationale", "why" },
            (int)RunExitCode.ValidationRefused,
        };
        yield return new object[]
        {
            new[] { "task", "submit", "--issue", "1", "--project", "project", "--declared-size", "small", "--size-rationale", "why", "--help" },
            (int)RunExitCode.ValidationRefused,
        };
        yield return new object[]
        {
            new[] { "queue", "--help", "add", "tag", "--role", "implement", "--spec", "spec.md", "--workspace", "workspace" },
            1,
        };
        yield return new object[]
        {
            new[] { "queue", "add", "--help", "tag", "--role", "implement", "--spec", "spec.md", "--workspace", "workspace" },
            1,
        };
        yield return new object[] { new[] { "queue", "add", "tag", "--help" }, 1 };
        yield return new object[] { new[] { "queue", "list", "--help" }, 1 };
        yield return new object[] { new[] { "queue", "hold", "--help" }, 1 };
    }

    [Theory]
    [MemberData(nameof(ExactHelpForms))]
    public async Task Exact_help_forms_print_canonical_usage_without_touching_an_absent_home(
        string[] args, string expectedUsage)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"task-queue-help-{Guid.NewGuid():N}");
        var workingDirectory = Path.Combine(testRoot, "outside-repository");
        var batonHome = Path.Combine(testRoot, "absent-baton-home");
        try
        {
            Directory.CreateDirectory(workingDirectory);

            var result = await RunCliAsync(batonHome, workingDirectory, args);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(expectedUsage + Environment.NewLine, result.Stdout);
            Assert.Equal(string.Empty, result.Stderr);
            Assert.False(Directory.Exists(batonHome));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Exact_help_forms_leave_a_seeded_home_byte_identical_and_do_not_invoke_operations()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"task-queue-help-seeded-{Guid.NewGuid():N}");
        var workingDirectory = Path.Combine(testRoot, "outside-repository");
        var batonHome = Path.Combine(testRoot, "seeded-baton-home");
        try
        {
            Directory.CreateDirectory(workingDirectory);
            Directory.CreateDirectory(Path.Combine(batonHome, "nested"));
            await File.WriteAllTextAsync(Path.Combine(batonHome, "existing.txt"), "keep me", TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(
                Path.Combine(batonHome, "nested", "bytes.bin"), [0, 1, 2, 255], TestContext.Current.CancellationToken);
            var before = SnapshotHome(batonHome);

            foreach (var invocation in new[]
            {
                (new[] { "task", "--help" }, TaskOptionsParser.Usage),
                (new[] { "task", "submit", "--help" }, TaskOptionsParser.Usage),
                (new[] { "queue", "--help" }, QueueOptionsParser.Usage),
                (new[] { "queue", "add", "--help" }, QueueOptionsParser.Usage),
            })
            {
                var result = await RunCliAsync(batonHome, workingDirectory, invocation.Item1);
                Assert.Equal(0, result.ExitCode);
                Assert.Equal(invocation.Item2 + Environment.NewLine, result.Stdout);
                Assert.Equal(string.Empty, result.Stderr);
            }

            AssertHomeSnapshotEqual(before, SnapshotHome(batonHome));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Theory]
    [MemberData(nameof(RefusedHelpForms))]
    public async Task Near_miss_help_forms_remain_refused_with_their_existing_exit_code(
        string[] args, int expectedExitCode)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"task-queue-help-negative-{Guid.NewGuid():N}");
        var workingDirectory = Path.Combine(testRoot, "outside-repository");
        var batonHome = Path.Combine(testRoot, "absent-baton-home");
        try
        {
            Directory.CreateDirectory(workingDirectory);

            var result = await RunCliAsync(batonHome, workingDirectory, args);

            Assert.Equal(expectedExitCode, result.ExitCode);
            Assert.NotEmpty(result.Stderr);
            Assert.False(Directory.Exists(batonHome));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Task_status_help_remains_a_status_id_not_a_new_help_route()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"task-queue-help-status-{Guid.NewGuid():N}");
        var workingDirectory = Path.Combine(testRoot, "outside-repository");
        var batonHome = Path.Combine(testRoot, "absent-baton-home");
        try
        {
            Directory.CreateDirectory(workingDirectory);

            var result = await RunCliAsync(batonHome, workingDirectory, "task", "status", "--help");

            Assert.Equal((int)RunExitCode.ValidationRefused, result.ExitCode);
            Assert.Contains("No retained task has ID '--help'.", result.Stderr, StringComparison.Ordinal);
            Assert.False(Directory.Exists(batonHome));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(
        string batonHome, string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        startInfo.Environment["BATON_HOME"] = batonHome;
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(typeof(TaskOptionsParser).Assembly.Location);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start 'baton'.");
        var (stdout, stderr) = await BoundedProcessWait.RunToExitAsync(
            process, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        return (process.ExitCode, stdout, stderr);
    }

    private static HomeSnapshot SnapshotHome(string root)
    {
        var directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                File.ReadAllBytes,
                StringComparer.Ordinal);
        return new HomeSnapshot(directories, files);
    }

    private static void AssertHomeSnapshotEqual(HomeSnapshot expected, HomeSnapshot actual)
    {
        Assert.Equal(expected.Directories, actual.Directories);
        Assert.Equal(expected.Files.Keys.OrderBy(path => path, StringComparer.Ordinal), actual.Files.Keys.OrderBy(path => path, StringComparer.Ordinal));
        foreach (var (path, expectedBytes) in expected.Files)
        {
            Assert.Equal(expectedBytes, actual.Files[path]);
        }
    }

    private sealed record HomeSnapshot(
        IReadOnlyList<string> Directories,
        IReadOnlyDictionary<string, byte[]> Files);
}
