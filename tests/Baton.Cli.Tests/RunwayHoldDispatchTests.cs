using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Runway;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1848 at the entry point it guards: <c>baton dispatch</c> holds new work when the vendor's runway
/// is short, and <c>--override-runway "&lt;reason&gt;"</c> is the only way past it. The gate itself is
/// unit-tested against the real vendor parsers in <c>Baton.Vendors.Tests.RunwayGateTests</c>; what these
/// arms pin is the wiring — refuse before the room is provisioned, print the flag once, and record the
/// override durably on the room's own <c>bindings.json</c>.
/// </summary>
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class RunwayHoldDispatchTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["fake"] = new ContractOutputWorkerAdapter(satisfyOutputs: true) };

    private static readonly IReadOnlyList<RunwayCounter> Counters =
        [new("week (all models)", 87), new("session", 12)];

    private static RunwayDecision Hold(string vendor) =>
        new(vendor, RunwayDisposition.Hold, "'week (all models)' is at 87% (holds at 85%)", Counters);

    private static RunwayDecision Admit(string vendor) =>
        new(vendor, RunwayDisposition.Admit, Reason: null, Counters);

    private readonly IsolatedBatonHome _batonHome = new();
    private readonly IDisposable _catalogScope;

    public RunwayHoldDispatchTests()
    {
        _catalogScope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Current with
        {
            WorkerRolesPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerRoles.json"),
            WorkerTiersPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"),
            WorkflowTemplatesPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkflowTemplates.json"),
        });
    }

    public void Dispose()
    {
        _catalogScope.Dispose();
        _batonHome.Dispose();
    }

    [Fact]
    public async Task A_held_vendor_refuses_the_dispatch_with_the_counters_and_the_flag()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"runway-hold-{Guid.NewGuid():N}");
        try
        {
            var (options, _) = await BuildDispatchAsync(testRoot, overrideReason: null);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: Hold));

            Assert.Contains("Runway hold", ex.Message);
            Assert.Contains("week (all models) 87%", ex.Message);
            Assert.Contains("--override-runway", ex.TryInvocation);

            // Refused before anything was provisioned: no workflow/bindings for a dispatch that never ran.
            Assert.False(File.Exists(Path.Combine(options.RoomDirectoryPath, "bindings.json")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task An_admitted_vendor_dispatches_with_no_override_flag()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"runway-admit-{Guid.NewGuid():N}");
        try
        {
            var (options, _) = await BuildDispatchAsync(testRoot, overrideReason: null);

            var result = await DispatchCommand.ExecuteAsync(
                options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: Admit);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(options.RoomDirectoryPath, "bindings.json"), TestContext.Current.CancellationToken);
            Assert.Null(bindings.Values.Single().RunwayOverride);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task An_override_dispatches_the_held_vendor_and_records_the_reason_and_counters()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"runway-override-{Guid.NewGuid():N}");
        try
        {
            var (options, _) = await BuildDispatchAsync(testRoot, overrideReason: "conductor lane, week resets in 2h");

            var result = await DispatchCommand.ExecuteAsync(
                options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: Hold);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(options.RoomDirectoryPath, "bindings.json"), TestContext.Current.CancellationToken);
            var recorded = Assert.IsType<RunwayOverride>(bindings.Values.Single().RunwayOverride);
            Assert.Equal("conductor lane, week resets in 2h", recorded.Reason);
            Assert.True(recorded.Used);
            Assert.Equal("fake", recorded.Vendor);
            Assert.Contains(recorded.Counters, c => c.Window == "week (all models)" && c.PercentUsed == 87);
            Assert.Contains("87%", recorded.HoldReason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// The polarity arm of the one above: the same flag, the same recording site, an Admit instead of a
    /// Hold. The record exists and says the override bypassed nothing, which is what makes
    /// "offered and unused" distinguishable from "never offered" in the room's own audit trail.
    /// </summary>
    [Fact]
    public async Task An_override_on_an_admitted_vendor_is_recorded_as_unused()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"runway-unused-{Guid.NewGuid():N}");
        try
        {
            var (options, _) = await BuildDispatchAsync(testRoot, overrideReason: "belt and braces");

            await DispatchCommand.ExecuteAsync(
                options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: Admit);

            var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(
                Path.Combine(options.RoomDirectoryPath, "bindings.json"), TestContext.Current.CancellationToken);
            var recorded = Assert.IsType<RunwayOverride>(bindings.Values.Single().RunwayOverride);
            Assert.False(recorded.Used);
            Assert.Null(recorded.HoldReason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// <c>--continue</c> rehires a worker the fleet already admitted, so it is not a new admission and
    /// the gate is not consulted — the ruling holds new work rather than interrupting work in flight.
    /// Asserted by dispatching with an evaluator that would throw if it were ever called.
    /// </summary>
    [Fact]
    public async Task The_gate_is_not_consulted_for_a_continue_dispatch()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"runway-continue-{Guid.NewGuid():N}");
        try
        {
            // First, a normal dispatch to produce a terminal room with a recorded session id.
            var (first, specPath) = await BuildDispatchAsync(testRoot, overrideReason: null);
            await DispatchCommand.ExecuteAsync(first, Adapters, TestContext.Current.CancellationToken, evaluateRunway: Admit);

            var second = first with
            {
                RoomDirectoryPath = Path.Combine(testRoot, "second"),
                ContinueFromRoomDirectoryPath = first.RoomDirectoryPath,
                SpecFilePath = specPath,
            };

            var thrownIfGated = await Record.ExceptionAsync(() => DispatchCommand.ExecuteAsync(
                second, Adapters, TestContext.Current.CancellationToken,
                evaluateRunway: _ => throw new InvalidOperationException("the runway gate must not run for --continue")));

            Assert.DoesNotContain(
                "the runway gate must not run",
                thrownIfGated?.Message ?? string.Empty,
                StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1848 review: the flag used to be accepted and silently dropped on a <c>--continue</c> dispatch,
    /// because the gate returns before it stamps anything. An audited bypass that can be passed and
    /// leave no record is worse than one that refuses, so the combination is a typed argument error —
    /// and the message names both flags, since either one is the half the operator may have meant.
    /// </summary>
    [Fact]
    public async Task An_override_passed_with_continue_is_refused_rather_than_discarded()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"runway-continue-override-{Guid.NewGuid():N}");
        try
        {
            var (first, specPath) = await BuildDispatchAsync(testRoot, overrideReason: null);
            await DispatchCommand.ExecuteAsync(first, Adapters, TestContext.Current.CancellationToken, evaluateRunway: Admit);

            var second = first with
            {
                RoomDirectoryPath = Path.Combine(testRoot, "second"),
                ContinueFromRoomDirectoryPath = first.RoomDirectoryPath,
                SpecFilePath = specPath,
                OverrideRunwayReason = "belt and braces",
            };

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                second, Adapters, TestContext.Current.CancellationToken, evaluateRunway: Admit));

            Assert.Contains("--override-runway", ex.Message, StringComparison.Ordinal);
            Assert.Contains("--continue", ex.Message, StringComparison.Ordinal);
            Assert.Contains("--override-runway", ex.TryInvocation!, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(second.RoomDirectoryPath, "bindings.json")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task<(DispatchOptions Options, string SpecPath)> BuildDispatchAsync(string testRoot, string? overrideReason)
    {
        Directory.CreateDirectory(testRoot);
        var specPath = Path.Combine(testRoot, "spec.md");
        await File.WriteAllTextAsync(specPath, "Weigh the options for X.", TestContext.Current.CancellationToken);

        return (
            new DispatchOptions(
                "advise", specPath, Path.Combine(testRoot, "task"), Adapter: "fake", OverrideRunwayReason: overrideReason),
            specPath);
    }
}
