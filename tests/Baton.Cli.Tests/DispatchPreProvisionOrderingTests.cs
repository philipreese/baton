using Baton.Cli.Daemon;
using Baton.Cli.Tests.TestSupport;
using Baton.Runway;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// The ordering <c>QueueLauncher</c> reads as a discriminator: every pre-provision refusal
/// <see cref="DispatchCommand.ExecuteAsync"/> can make happens <em>above</em> its
/// <c>Directory.CreateDirectory(options.RoomDirectoryPath)</c>, so "the room now exists" tells a
/// refusal apart from a launch (<c>QueueLauncher.LaunchAsync</c>'s refusal-window poll, and the
/// <see cref="QueueLauncher.RefusalWindow"/> backstop behind it).
/// <para>
/// Pinned here because that claim was prose only (#1939 review, first round's LOW and second round's):
/// nothing failed if a future edit provisioned earlier, and the launcher would then have reported a
/// refusal as a running lane — an item marked launched against a worker that never started, resolvable
/// only by the roomless sweep it no longer qualifies for.
/// </para>
/// <para>
/// The arms assert on the <b>directory</b>, not on <c>bindings.json</c> (which
/// <c>RunwayHoldDispatchTests</c> checks for its own, narrower purpose): the directory is what the
/// launcher actually polls, and a room created empty would pass a file-level check while breaking the
/// discriminator. The runway-hold arm is the load-bearing one — it is the refusal that sits closest to
/// the provisioning line — and the admitted control below is what rules out the whole class of "the
/// dispatch never got far enough to create anything".
/// </para>
/// </summary>
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class DispatchPreProvisionOrderingTests : IDisposable
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

    public DispatchPreProvisionOrderingTests()
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

    /// <param name="expectedInMessage">
    /// Asserted so each arm is pinned to the refusal it means to exercise. Without it a theory arm that
    /// silently started refusing for some other reason — a resolution failure ahead of the gate under
    /// test — would keep passing while measuring nothing about that gate's placement.
    /// </param>
    [Theory]
    [InlineData("runway-hold", "Runway hold")]
    [InlineData("unknown-role", "not-a-role-in-any-catalog")]
    [InlineData("missing-spec", "gone.md")]
    public async Task A_pre_provision_refusal_leaves_no_room_directory_behind(string refusal, string expectedInMessage)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-order-{refusal}-{Guid.NewGuid():N}");
        try
        {
            var options = await BuildDispatchAsync(testRoot);
            var evaluate = Admit;

            options = refusal switch
            {
                "runway-hold" => Assign(options, ref evaluate, Hold),
                "unknown-role" => options with { Name = "not-a-role-in-any-catalog" },
                _ => options with { SpecFilePath = Path.Combine(testRoot, "gone.md") },
            };

            var refused = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: evaluate));

            Assert.Contains(expectedInMessage, refused.Message, StringComparison.Ordinal);
            Assert.False(
                Directory.Exists(options.RoomDirectoryPath),
                $"'{refusal}' refused after provisioning the room, so QueueLauncher would read it as a launch.");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// The control the three arms above need: with nothing refusing, the same options through the same
    /// call DO create the room. Without it, a broken catalog scope (or any other failure ahead of the
    /// refusal under test) would satisfy every assertion above for the wrong reason.
    /// </summary>
    [Fact]
    public async Task An_admitted_dispatch_does_create_the_room_directory()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-order-admit-{Guid.NewGuid():N}");
        try
        {
            var options = await BuildDispatchAsync(testRoot);

            await DispatchCommand.ExecuteAsync(
                options, Adapters, TestContext.Current.CancellationToken, evaluateRunway: Admit);

            Assert.True(Directory.Exists(options.RoomDirectoryPath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>Swaps the evaluator in as part of a switch arm, which cannot otherwise assign to a ref local.</summary>
    private static DispatchOptions Assign(
        DispatchOptions options, ref Func<string, RunwayDecision> evaluate, Func<string, RunwayDecision> replacement)
    {
        evaluate = replacement;
        return options;
    }

    private static async Task<DispatchOptions> BuildDispatchAsync(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        var specPath = Path.Combine(testRoot, "spec.md");
        await File.WriteAllTextAsync(specPath, "Weigh the options for X.", TestContext.Current.CancellationToken);

        return new DispatchOptions("advise", specPath, Path.Combine(testRoot, "task"), Adapter: "fake");
    }
}
