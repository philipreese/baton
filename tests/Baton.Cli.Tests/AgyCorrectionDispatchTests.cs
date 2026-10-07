using Baton.Cli.Tests.TestSupport;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Tests;

[Collection(SerializedEnvironmentCollection.Name)]
public sealed class AgyCorrectionDispatchTests : IDisposable
{
    private readonly IsolatedBatonHome _batonHome = new();
    private readonly IDisposable _catalogScope;

    public AgyCorrectionDispatchTests()
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
    public async Task Ordinary_dispatch_propagates_the_opt_in_through_binding_resolution()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-agy-correction-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var specPath = Path.Combine(testRoot, "spec.md");
            await File.WriteAllTextAsync(specPath, "Perform the bounded task.", TestContext.Current.CancellationToken);
            var room = Path.Combine(testRoot, "room");
            var adapter = new ResumeObservingWorkerAdapter();
            var options = new DispatchOptions(
                "advise", specPath, room, Adapter: "agy", EnableAgyCorrection: true);

            await DispatchCommand.ExecuteAsync(
                options,
                new Dictionary<string, IWorkerAdapter> { ["agy"] = adapter },
                TestContext.Current.CancellationToken,
                evaluateRunway: RunwayTestGate.Admit);

            var invocation = Assert.Single(adapter.ObservedInvocations);
            Assert.True(invocation.EnableAgyCorrection);
            var binding = Assert.Single(await WorkerBindingConfigParser.LoadFromFileAsync(
                BatonPaths.RoomBindingsFile(room), TestContext.Current.CancellationToken)).Value;
            Assert.True(binding.EnableAgyCorrection);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task The_opt_in_refuses_a_template_before_provisioning()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-agy-correction-template-{Guid.NewGuid():N}");
        try
        {
            var options = new DispatchOptions(
                "implement-review", null, Path.Combine(testRoot, "room"), EnableAgyCorrection: true);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                options,
                new Dictionary<string, IWorkerAdapter>(),
                TestContext.Current.CancellationToken));

            Assert.Contains("workflow template", ex.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(options.RoomDirectoryPath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task The_opt_in_validates_the_actual_resolved_non_agy_adapter_before_provisioning()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-agy-correction-adapter-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var specPath = Path.Combine(testRoot, "spec.md");
            await File.WriteAllTextAsync(specPath, "Perform the bounded task.", TestContext.Current.CancellationToken);
            var room = Path.Combine(testRoot, "room");
            var options = new DispatchOptions(
                "advise", specPath, room, Adapter: "fake", EnableAgyCorrection: true);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                options,
                new Dictionary<string, IWorkerAdapter> { ["fake"] = new ResumeObservingWorkerAdapter() },
                TestContext.Current.CancellationToken,
                evaluateRunway: RunwayTestGate.Admit));

            Assert.Contains("actual resolved adapter", ex.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(room));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }
}
