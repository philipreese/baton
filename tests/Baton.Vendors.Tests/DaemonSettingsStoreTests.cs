using Baton.Vendors.Tests.TestSupport;
namespace Baton.Vendors.Tests;

/// <summary>
/// #1298: <see cref="DaemonSettingsStore"/>'s load/save round trip, and its deliberate departure from
/// <see cref="BatonProfileStore"/>'s "malformed throws" precedent -- a bad concurrency cap must never
/// stop the daemon from starting, so both absent and malformed resolve to defaults here.
/// </summary>
[Collection(ConsoleErrorCaptureCollection.Name)]
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
    public async Task Retired_codex_ceiling_setting_round_trips_for_rollback_compatibility()
    {
        var path = TempPath();
        try
        {
            var original = new DaemonSettings
            {
                CodexPlanCeiling = new CodexPlanCeilingSettings
                {
                    FiveHourTokens = 5_000_000,
                    WeeklyTokens = 120_000_000,
                },
            };

            await DaemonSettingsStore.SaveAsync(original, path, TestContext.Current.CancellationToken);
            var loaded = await DaemonSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(original.CodexPlanCeiling, loaded.CodexPlanCeiling);
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

    [Fact]
    public async Task Loading_a_locked_file_warns_and_resolves_to_defaults()
    {
        var path = TempPath();
        var originalError = Console.Error;
        using var error = new StringWriter();
        try
        {
            await File.WriteAllTextAsync(path, "{\"GlobalConcurrencyCap\":99}", TestContext.Current.CancellationToken);
            await using var fileLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            Console.SetError(error);

            var settings = await DaemonSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(DaemonSettings.DefaultGlobalConcurrencyCap, settings.GlobalConcurrencyCap);
            Assert.Equal(DaemonSettings.DefaultPerVendorConcurrencyCap, settings.PerVendorConcurrencyCap);
            Assert.Contains("Malformed or unreadable settings", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task Camel_case_execution_limit_profiles_load_and_validate_rows()
    {
        var path = TempPath();
        try
        {
            await File.WriteAllTextAsync(
                path,
                "{\"executionLimitProfiles\":[{\"adapter\":\"claude\",\"model\":\"sonnet\",\"role\":\"review\",\"declaredTaskSize\":\"medium\",\"timeout\":\"00:10:00\",\"tokenBudget\":1000,\"maxToolSteps\":10}]}",
                TestContext.Current.CancellationToken);

            var loaded = await DaemonSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken);

            Assert.NotNull(loaded.ExecutionLimitProfiles);
            Assert.Equal(1000, loaded.ExecutionLimitProfiles[0].TokenBudget);
            Assert.Equal(TimeSpan.FromMinutes(10), loaded.ExecutionLimitProfiles[0].Timeout);

            await File.WriteAllTextAsync(
                path,
                "{\"executionLimitProfiles\":[{\"adapter\":\"claude\",\"model\":\"sonnet\",\"role\":\"review\",\"declaredTaskSize\":\"medium\",\"timeout\":\"00:10:00\",\"tokenBudget\":0,\"maxToolSteps\":10}]}",
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<ExecutionLimitProfileConfigurationException>(
                () => DaemonSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task Null_execution_limit_profiles_remains_legacy_compatible()
    {
        var path = TempPath();
        try
        {
            await File.WriteAllTextAsync(path, "{\"executionLimitProfiles\":null}", TestContext.Current.CancellationToken);

            var settings = await DaemonSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken);

            Assert.Null(settings.ExecutionLimitProfiles);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task Duplicate_case_variant_execution_limit_profiles_sections_are_rejected()
    {
        var path = TempPath();
        try
        {
            await File.WriteAllTextAsync(
                path,
                "{\"ExecutionLimitProfiles\":null,\"executionLimitProfiles\":null}",
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<ExecutionLimitProfileConfigurationException>(
                () => DaemonSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken));
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
    public async Task Saving_then_loading_round_trips_the_Glass_operator_login()
    {
        var path = TempPath();
        try
        {
            var original = new DaemonSettings
            {
                Glass = new GlassListenerSettings
                {
                    Listen = true,
                    Port = 8420,
                    OperatorLogin = "operator@example.com",
                },
            };

            await DaemonSettingsStore.SaveAsync(original, path, TestContext.Current.CancellationToken);
            var loaded = await DaemonSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(original.Glass, loaded.Glass);
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

    [Fact]
    public async Task Execution_limit_profiles_round_trip_and_reject_incomplete_rows()
    {
        var path = TempPath();
        try
        {
            var original = new DaemonSettings
            {
                ExecutionLimitProfiles =
                [
                    new ExecutionLimitProfile
                    {
                        Adapter = "claude",
                        Model = "sonnet",
                        Role = "review",
                        DeclaredTaskSize = "unknown",
                        Timeout = TimeSpan.FromMinutes(10),
                        TokenBudget = 1000,
                        MaxToolSteps = 10,
                    },
                ],
            };

            await DaemonSettingsStore.SaveAsync(original, path, TestContext.Current.CancellationToken);
            var loaded = await DaemonSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(original.ExecutionLimitProfiles, loaded.ExecutionLimitProfiles);

            await File.WriteAllTextAsync(
                path,
                "{\"ExecutionLimitProfiles\":[{\"Adapter\":\"claude\",\"Model\":\"sonnet\",\"Role\":\"review\",\"DeclaredTaskSize\":\"unknown\",\"Timeout\":\"00:10:00\",\"TokenBudget\":0,\"MaxToolSteps\":10}]}",
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<ExecutionLimitProfileConfigurationException>(
                () => DaemonSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task Execution_limit_profiles_with_json_syntax_error_throws_configuration_exception()
    {
        var path = TempPath();
        try
        {
            await File.WriteAllTextAsync(
                path,
                "{\"ExecutionLimitProfiles\": [ { \"Adapter\": \"claude\", ",
                TestContext.Current.CancellationToken);

            var ex = await Assert.ThrowsAsync<ExecutionLimitProfileConfigurationException>(
                () => DaemonSettingsStore.LoadAsync(path, TestContext.Current.CancellationToken));

            Assert.IsAssignableFrom<BatonFlowException>(ex);
            Assert.Contains("ExecutionLimitProfiles is malformed", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }
}
