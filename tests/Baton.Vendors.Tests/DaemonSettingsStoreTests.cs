using Baton.Vendors.Tests.TestSupport;
namespace Baton.Vendors.Tests;

/// <summary>
/// #1298: <see cref="DaemonSettingsStore"/>'s load/save round trip, and its deliberate departure from
/// <see cref="BatonProfileStore"/>'s "malformed throws" precedent -- a bad concurrency cap must never
/// stop the daemon from starting, so both absent and malformed resolve to defaults here.
/// </summary>
public class DaemonSettingsStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"baton-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Loading_a_missing_file_resolves_to_defaults()
    {
        var path = TempPath();

        var settings = await DaemonSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(DaemonSettings.DefaultGlobalConcurrencyCap, settings.GlobalConcurrencyCap);
        Assert.Equal(DaemonSettings.DefaultPerVendorConcurrencyCap, settings.PerVendorConcurrencyCap);
    }

    [Fact]
    public async Task Saving_then_loading_round_trips_the_caps()
    {
        var path = TempPath();
        try
        {
            var original = new DaemonSettings { GlobalConcurrencyCap = 7, PerVendorConcurrencyCap = 4 };

            await DaemonSettingsStore.SaveAsync(original, path, TestContext.Current.CancellationToken);
            var loaded = await DaemonSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(original, loaded);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task Loading_a_malformed_file_resolves_to_defaults_rather_than_throwing()
    {
        var path = TempPath();
        try
        {
            await File.WriteAllTextAsync(path, "{ not valid json", TestContext.Current.CancellationToken);

            var settings = await DaemonSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(DaemonSettings.DefaultGlobalConcurrencyCap, settings.GlobalConcurrencyCap);
            Assert.Equal(DaemonSettings.DefaultPerVendorConcurrencyCap, settings.PerVendorConcurrencyCap);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    // #2111: RoomsRetentionDays defaults to 30 now -- see DaemonSettings.DefaultRoomsRetentionDays for
    // the decision-round measurement that moved this off #1659's original "operator opts in" default.
    [Fact]
    public async Task Loading_a_missing_file_resolves_RoomsRetentionDays_to_the_default()
    {
        var path = TempPath();

        var settings = await DaemonSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(DaemonSettings.DefaultRoomsRetentionDays, settings.RoomsRetentionDays);
    }

    // #2111: an operator can still turn it off explicitly -- unlike an absent file (which reads the
    // default above), an explicit null in settings.json is an opt-out System.Text.Json still honors,
    // since RoomsRetentionDays carries no non-null coalescing on deserialize the way _runwayHold does.
    [Fact]
    public async Task Explicit_null_RoomsRetentionDays_in_settings_json_is_honored_as_off()
    {
        var path = TempPath();
        try
        {
            await File.WriteAllTextAsync(path, "{\"RoomsRetentionDays\":null}", TestContext.Current.CancellationToken);

            var settings = await DaemonSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken);

            Assert.Null(settings.RoomsRetentionDays);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task Saving_then_loading_round_trips_RoomsRetentionDays()
    {
        var path = TempPath();
        try
        {
            var original = new DaemonSettings { RoomsRetentionDays = 14 };

            await DaemonSettingsStore.SaveAsync(original, path, TestContext.Current.CancellationToken);
            var loaded = await DaemonSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(14, loaded.RoomsRetentionDays);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task Saving_creates_the_parent_directory_if_it_does_not_exist_yet()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"baton-settings-dir-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        try
        {
            await DaemonSettingsStore.SaveAsync(new DaemonSettings(), path, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }
}
