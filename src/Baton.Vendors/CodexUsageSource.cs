using System.Globalization;
using System.Text.Json.Nodes;

namespace Baton.Vendors;

/// <summary>
/// Reads Codex subscription counters from app-server's authenticated
/// <c>account/rateLimits/read</c> method (#1904). The request reuses
/// <see cref="CodexAppServerBroker"/>'s approved process, isolated home, and initialize/initialized
/// handshake and starts no thread or model turn.
/// </summary>
/// <remarks>
/// <para>
/// <b>The multi-bucket map is authoritative when present.</b>
/// <c>rateLimitsByLimitId</c> is parsed instead of the legacy <c>rateLimits</c> alias, never in
/// addition to it, so the account-wide <c>codex</c> bucket is not counted twice. The legacy object is
/// retained as a compatibility arm for installed versions that do not expose the map.
/// </para>
/// <para>
/// <b>Window identity comes from the response, not property position.</b> <c>primary</c> and
/// <c>secondary</c> are carried as source identities, while product semantics use the measured
/// <c>windowDurationMins</c>. In the 0.153.2 capture the account-wide primary is 10,080 minutes, so
/// this source never assumes that "primary" means five hours. A null window becomes an unavailable
/// window with null percentage, duration, and reset; it is never filled with zero.
/// </para>
/// <para>
/// <b>No token allowance is inferred.</b> <see cref="VendorUsageWindow.PercentUsed"/> is the vendor's
/// <c>usedPercent</c> unchanged. Per-thread <c>thread/tokenUsage/updated</c> messages belong to
/// execution accounting and are not read here. A successful but unrecognized response produces an
/// empty snapshot; a protocol/spawn/error failure returns null so the harvester preserves last-good
/// state.
/// </para>
/// </remarks>
public sealed class CodexUsageSource : IVendorUsageSource
{
    public const string AccountLimitId = "codex";
    public const int FiveHourDurationMins = 300;
    public const int WeeklyDurationMins = 10_080;

    private readonly Func<CancellationToken, Task<JsonObject?>> _readRateLimits;
    private readonly Func<DateTimeOffset> _utcNow;

    public CodexUsageSource()
        : this(CodexAppServerBroker.ReadRateLimitsAsync, () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>Test seam for the already-initialized app-server response and harvest clock.</summary>
    internal CodexUsageSource(
        Func<CancellationToken, Task<JsonObject?>> readRateLimits,
        Func<DateTimeOffset>? utcNow = null)
    {
        _readRateLimits = readRateLimits;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public string Vendor => AccountLimitId;

    public async Task<VendorUsageSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        var result = await _readRateLimits(cancellationToken).ConfigureAwait(false);
        return result is null ? null : Parse(result, _utcNow());
    }

    /// <summary>
    /// Parses the <c>result</c> object returned by <c>account/rateLimits/read</c>. This is deliberately
    /// tolerant at field level: malformed or absent numeric fields stay null while other buckets and
    /// windows remain usable.
    /// </summary>
    public static VendorUsageSnapshot Parse(JsonObject result, DateTimeOffset harvestedAt)
    {
        ArgumentNullException.ThrowIfNull(result);

        List<VendorUsageWindow> windows = [];
        if (result.TryGetPropertyValue("rateLimitsByLimitId", out var byIdNode)
            && byIdNode is JsonObject byId)
        {
            foreach (var (mapKey, bucketNode) in byId)
            {
                if (bucketNode is JsonObject bucket)
                {
                    ParseBucket(bucket, mapKey, windows);
                }
            }
        }
        else if (result["rateLimits"] is JsonObject legacy)
        {
            ParseBucket(legacy, AccountLimitId, windows);
        }

        return new VendorUsageSnapshot(
            AccountLimitId,
            harvestedAt,
            Caveat: null,
            windows,
            VendorUsageProvenance.Vendor);
    }

    private static void ParseBucket(
        JsonObject bucket,
        string fallbackLimitId,
        ICollection<VendorUsageWindow> windows)
    {
        var limitId = StringValue(bucket["limitId"]) ?? fallbackLimitId;
        var limitName = StringValue(bucket["limitName"]);

        ParseWindow(bucket, limitId, limitName, "primary", windows);
        ParseWindow(bucket, limitId, limitName, "secondary", windows);
    }

    private static void ParseWindow(
        JsonObject bucket,
        string limitId,
        string? limitName,
        string kind,
        ICollection<VendorUsageWindow> windows)
    {
        if (!bucket.TryGetPropertyValue(kind, out var node))
        {
            return;
        }

        if (node is null)
        {
            windows.Add(new VendorUsageWindow(
                Name(limitId, limitName, kind, durationMins: null),
                PercentUsed: null,
                ResetsAt: null,
                RawLine: "null",
                LimitId: limitId,
                WindowKind: kind,
                WindowDurationMins: null));
            return;
        }

        if (node is not JsonObject window)
        {
            windows.Add(new VendorUsageWindow(
                Name(limitId, limitName, kind, durationMins: null),
                PercentUsed: null,
                ResetsAt: null,
                RawLine: node.ToJsonString(),
                LimitId: limitId,
                WindowKind: kind,
                WindowDurationMins: null));
            return;
        }

        var percentUsed = IntValue(window["usedPercent"]);
        if (percentUsed is < 0 or > 100)
        {
            percentUsed = null;
        }

        var durationMins = IntValue(window["windowDurationMins"]);
        if (durationMins <= 0)
        {
            durationMins = null;
        }

        DateTimeOffset? resetsAt = null;
        if (LongValue(window["resetsAt"]) is { } unixSeconds)
        {
            try
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                // An invalid vendor instant is unknown, not a failed harvest or an invented boundary.
            }
        }

        windows.Add(new VendorUsageWindow(
            Name(limitId, limitName, kind, durationMins),
            percentUsed,
            resetsAt,
            window.ToJsonString(),
            limitId,
            kind,
            durationMins));
    }

    private static string Name(string limitId, string? limitName, string kind, int? durationMins)
    {
        var bucket = string.IsNullOrWhiteSpace(limitName) ? limitId : limitName;
        var duration = durationMins switch
        {
            FiveHourDurationMins => "5h",
            WeeklyDurationMins => "7d",
            { } mins => $"{mins.ToString(CultureInfo.InvariantCulture)}m",
            _ => "unavailable",
        };
        return $"{bucket} · {duration} ({kind})";
    }

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var parsed)
            && !string.IsNullOrWhiteSpace(parsed)
            ? parsed
            : null;

    private static int? IntValue(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        return value.TryGetValue<long>(out var longValue)
            && longValue is >= int.MinValue and <= int.MaxValue
            ? (int)longValue
            : null;
    }

    private static long? LongValue(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }

        return value.TryGetValue<int>(out var intValue) ? intValue : null;
    }
}
