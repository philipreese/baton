namespace Baton.Domain;

/// <summary>
/// A repository-relative path Baton generated or requested while preparing one delivery attempt.
/// The provenance is retained so a delivery refusal can identify the owning producer rather than
/// reducing the rule to a global filename blacklist.
/// </summary>
public sealed record DeliveryArtifactPath(string Path, string Provenance);
