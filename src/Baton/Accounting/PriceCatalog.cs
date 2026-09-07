using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Accounting;

/// <summary>The token dimensions a price can be quoted per. JSON names are the catalog's own keys.</summary>
public enum PriceDimension
{
    Input,
    Output,
    CacheRead,
    CacheCreation,
    Thinking,
}

/// <summary>
/// One vendor list price for one model/dimension, valid over a date range.
/// <see cref="EffectiveTo"/> absent means "still current".
/// </summary>
/// <param name="Source">
/// Where this number came from, in enough detail for a later reader to re-check it. A catalog entry
/// with no citable source does not belong here at all — see <see cref="PriceCatalog"/>'s remarks.
/// <c>[JsonRequired]</c> is what enforces that on a parsed catalog rather than leaving it to the prose
/// (#1883 review F6): without it a <c>"source"</c>-less entry deserialized to a null this non-nullable
/// property claims cannot happen, and nothing on the pricing path ever reads it to notice.
/// </param>
public sealed record PricePoint(
    [property: JsonPropertyName("effectiveFrom")] DateTime EffectiveFrom,
    [property: JsonPropertyName("usdPerMillion")] decimal UsdPerMillion,
    [property: JsonPropertyName("source")][property: JsonRequired] string Source,
    [property: JsonPropertyName("effectiveTo")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTime? EffectiveTo = null);

/// <summary>
/// A repository-owned, versioned catalog of vendor <b>list</b> prices, keyed vendor → model →
/// dimension → effective ranges. Every dollar figure derived from it is an <i>API-equivalent
/// estimate</i>, never an invoice and never subscription-meter consumption (#1849's own non-goal).
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Default"/> ships with no priced models, and that is the finding rather than an
/// omission.</b> Nothing in this repository cites a per-dimension list price for any model Baton
/// routes to: <c>benchmarks/deepswe/2026-09-04</c>'s <c>avg_api_cost_usd</c> is a blended
/// whole-task dollar proxy for a third-party harness, not a $/M rate, and dividing it by that row's
/// <c>output_tokens</c> would manufacture a rate no source states. Per #1849's ruling — "unknown
/// model → both estimates absent; never borrow a neighbouring model's price" — an unciteable price
/// stays out, so every phase-A row is <see cref="EstimateStatus.Unpriced"/> until real per-dimension
/// list prices with sources are added here. The numbers are a separate, sourced edit; what already
/// works without them is everything this type does apart from holding a rate.
/// </para>
/// <para>
/// <b>Effective dates are UTC.</b> They are compared against an execution's own exit timestamp, which
/// is <c>LogEntry.WriterUtcTimestamp</c>. An entry written with a local-time or unqualified literal is
/// silently off by the author's offset, which picks the wrong range near a boundary — write them with
/// a trailing <c>Z</c>.
/// </para>
/// <para>
/// <b>Reproducibility.</b> Every row records the <see cref="Id"/> and <see cref="Version"/> that
/// produced its estimate. Re-pricing an old row against a newer catalog is therefore a visible
/// mismatch rather than a silent rewrite — a catalog edit changes what NEW rows say and nothing else.
/// Bump <see cref="Version"/> on every content change; never edit a shipped entry's numbers in place.
/// </para>
/// </remarks>
public sealed record PriceCatalog(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("vendors")] IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<PricePoint>>>> Vendors)
{
    /// <summary>
    /// The catalog Baton ships with. See the type remarks for why it currently prices nothing.
    /// </summary>
    public static PriceCatalog Default { get; } = new(
        Id: "baton-list-prices",
        Version: "2026-09-04.0",
        Vendors: new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<PricePoint>>>>(
            StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Parses a catalog from JSON — the seam that lets a test build a catalog with two effective
    /// ranges (and a later, edited version of the same catalog) without editing the shipped one.
    /// Throws <see cref="JsonException"/> on malformed input: a catalog that cannot be read is a
    /// programming error at its call site, not something to degrade past silently — including an entry
    /// with no <c>source</c>, which <see cref="PricePoint.Source"/>'s own doc explains.
    /// <para>
    /// #1883 review F9: every one of the three dictionary levels is rebuilt case-insensitively.
    /// <see cref="Default"/> is built with <see cref="StringComparer.OrdinalIgnoreCase"/>, so without
    /// this a catalog loaded from JSON would miss a vendor, model or dimension the shipped one would
    /// have matched — the same document behaving differently depending on where it came from. Rebuilding
    /// only the outer level would leave that live at the level that actually varies in spelling.
    /// </para>
    /// </summary>
    public static PriceCatalog Parse(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        var parsed = JsonSerializer.Deserialize<PriceCatalog>(json)
            ?? throw new JsonException("A price catalog document deserialized to null.");

        // System.Text.Json passes default(T) for a missing positional member, so a document with no
        // "vendors" arrives here as null; the doc above promises JsonException for malformed input,
        // not a NullReferenceException one line later.
        if (parsed.Vendors is null)
        {
            throw new JsonException("A price catalog document has no \"vendors\" member.");
        }

        var vendors = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<PricePoint>>>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (vendor, models) in parsed.Vendors)
        {
            var byModel = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<PricePoint>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (model, dimensions) in models ?? EmptyModels)
            {
                byModel[model] = dimensions is null
                    ? EmptyDimensions
                    : new Dictionary<string, IReadOnlyList<PricePoint>>(dimensions, StringComparer.OrdinalIgnoreCase);
            }

            vendors[vendor] = byModel;
        }

        return parsed with { Vendors = vendors };
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<PricePoint>>> EmptyModels =
        new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<PricePoint>>>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<PricePoint>> EmptyDimensions =
        new Dictionary<string, IReadOnlyList<PricePoint>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The USD-per-million rate in force for <paramref name="vendor"/>/<paramref name="model"/>'s
    /// <paramref name="dimension"/> at <paramref name="at"/>, or <see langword="null"/> when the
    /// vendor, model, dimension, or effective range does not cover it. Ranges are half-open —
    /// <c>effectiveFrom &lt;= at &lt; effectiveTo</c> — so two adjacent ranges sharing an instant
    /// resolve to exactly one of them rather than to whichever the file happened to list first. The
    /// LAST matching range wins when a catalog author overlaps two, which makes a correction appended
    /// below an earlier entry take effect without having to edit the earlier one.
    /// </summary>
    public decimal? TryRate(string? vendor, string? model, PriceDimension dimension, DateTime at)
    {
        if (vendor is not { Length: > 0 } || model is not { Length: > 0 })
        {
            return null;
        }

        if (!Vendors.TryGetValue(vendor, out var models) || models is null)
        {
            return null;
        }

        if (!models.TryGetValue(model, out var dimensions) || dimensions is null)
        {
            return null;
        }

        if (!dimensions.TryGetValue(DimensionKey(dimension), out var points) || points is null)
        {
            return null;
        }

        decimal? rate = null;
        foreach (var point in points)
        {
            if (point.EffectiveFrom <= at && (point.EffectiveTo is not { } to || at < to))
            {
                rate = point.UsdPerMillion;
            }
        }

        return rate;
    }

    /// <summary>
    /// The API-equivalent estimate for <paramref name="tokens"/>, or <see langword="null"/> when ANY
    /// dimension the usage actually reports has no price in force. Deliberately all-or-nothing: a
    /// partial sum silently under-reports by whichever dimension was unpriced, and #1849 rules that
    /// an incomplete price is unknown rather than a smaller number.
    /// Input and cache-read are priced separately; <see cref="Status.CodexUsageParser"/>'s usage
    /// remark documents the normalization required before applying this formula to Codex input.
    /// </summary>
    public decimal? TryEstimateUsd(string? vendor, string? model, TokenDimensions tokens, DateTime at)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        decimal total = 0m;
        var priced = false;

        foreach (var (dimension, count) in tokens.Present())
        {
            if (TryRate(vendor, model, dimension, at) is not { } rate)
            {
                return null;
            }

            total += rate * count / 1_000_000m;
            priced = true;
        }

        return priced ? total : null;
    }

    internal static string DimensionKey(PriceDimension dimension) => dimension switch
    {
        PriceDimension.Input => "input",
        PriceDimension.Output => "output",
        PriceDimension.CacheRead => "cacheRead",
        PriceDimension.CacheCreation => "cacheCreation",
        PriceDimension.Thinking => "thinking",
        _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "Unknown price dimension."),
    };
}

/// <summary>
/// The per-dimension token counts one estimate is computed over. Every dimension is independently
/// nullable, and <see cref="Present"/> yields only the ones the vendor actually reported — an absent
/// dimension contributes nothing to an estimate and, crucially, does not make the estimate unpriced
/// (that is only what a REPORTED dimension with no rate does).
/// </summary>
public sealed record TokenDimensions(
    long? Input = null,
    long? Output = null,
    long? CacheRead = null,
    long? CacheCreation = null,
    long? Thinking = null)
{
    /// <summary>The dimensions this usage reports, in catalog order.</summary>
    public IEnumerable<(PriceDimension Dimension, long Count)> Present()
    {
        if (Input is { } input)
        {
            yield return (PriceDimension.Input, input);
        }

        if (Output is { } output)
        {
            yield return (PriceDimension.Output, output);
        }

        if (CacheRead is { } cacheRead)
        {
            yield return (PriceDimension.CacheRead, cacheRead);
        }

        if (CacheCreation is { } cacheCreation)
        {
            yield return (PriceDimension.CacheCreation, cacheCreation);
        }

        if (Thinking is { } thinking)
        {
            yield return (PriceDimension.Thinking, thinking);
        }
    }
}
