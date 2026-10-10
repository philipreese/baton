using Baton.Queue;
using System.Security.Cryptography;
using System.Text;

namespace Baton.Compatibility068;

/// <summary>Runs exact historical store and scheduling source; the only launcher is a test callback.</summary>
public static class IsolatedQueueProbe
{
    // Git blobs from exact v0.68.0, 5a6d5fdfa3dbdd6c926f60d37bc9c63948d3282a.
    public static bool HasExactSource()
    {
        var blobs = new Dictionary<string, string>
        {
            ["QueueStore"] = "24889cde43ecdfaac1ae43ebda5bdaae53afb605",
            ["QueueItem"] = "32e35c83ce0d7054391fdbf90089b9896fd98562",
            ["QueueStageSelection"] = "d2e8604620115ec8772bebba8b37906583aa5666",
            ["QueueScheduler"] = "1e31aeca1e3bb58cf2285bdb528f0e94a4a26386",
            ["QueueBoard"] = "504bc5e431a803805418a7f9f21e35f52432df5d",
            ["QueueTierTable"] = "71d337bd5319e085a17b9d8f4241233fcd6ab02e",
            ["QueueWeights"] = "4c529c6e90bbe00f1b137395cfeda6eab7ccb23c",
        };
        foreach (var (name, expected) in blobs)
        {
            using var source = typeof(IsolatedQueueProbe).Assembly.GetManifestResourceStream(
                $"Baton.Compatibility068.Source.{name}.cs.txt")!;
            using var bytes = new MemoryStream();
            source.CopyTo(bytes);
            var header = Encoding.UTF8.GetBytes($"blob {bytes.Length}\0");
            var actual = Convert.ToHexString(SHA1.HashData([.. header, .. bytes.ToArray()])).ToLowerInvariant();
            if (actual != expected) return false;
        }
        return true;
    }

    public static async Task<bool> ReadRefusedAsync(string fixture)
    {
        try { await QueueStore.LoadAsync(fixture); return false; }
        catch (QueueStoreException) { return true; }
    }

    public static async Task<bool> MutateAndLaunchAsync(string fixture, Action mutationEntered, Action fakeLaunch)
    {
        try
        {
            await QueueStore.MutateAsync(fixture, snapshot =>
            {
                mutationEntered();
                return snapshot with { Held = false };
            });
            var snapshot = await QueueStore.LoadAsync(fixture);
            var decision = QueueScheduler.Decide(DateTimeOffset.UtcNow, snapshot.Items, 0, 16,
                new QueueSettings(), null, snapshot.Held);
            if (decision.Kind == QueueDecisionKind.Launch)
                fakeLaunch();
            return true;
        }
        catch (QueueStoreException) { return false; }
    }
}
