namespace Baton.Vendors;

/// <summary>
/// How a canonical skill package asks to be realized (#1151 §3.2/§4.4).
/// </summary>
/// <remarks>
/// <b>Both values behave identically today, and this is the one place in the CODE that says so.</b>
/// The floor — instructions carried into the worker's brief and assets projected where the vendor
/// reads them — is what every realization does in this slice; <see cref="NativePreferred"/> is
/// RECORDED off the manifest and acts on nothing yet. spec/baton.md §9's "What is recorded but does
/// not act yet" is the register for which later slice changes that and what gates it. A package
/// setting it is stating an intent, not selecting a behaviour that exists.
/// </remarks>
public enum SkillRealization
{
    /// <summary>
    /// The default (operator ruling Q2, 2026-09-01, amending decision 0010 — spec/baton.md §9):
    /// instructions unconditionally in the brief, assets readable, no dependence on the model choosing
    /// to activate a skill under <c>-p</c>.
    /// </summary>
    Floor,

    /// <summary>
    /// Opt-in per package: prefer the vendor's own skill registry once #1151's S3/S4 build it. Behaves
    /// exactly as <see cref="Floor"/> until then.
    /// </summary>
    NativePreferred,
}

/// <summary>
/// What a canonical skill package declares it needs, in Baton's OWN grant vocabulary — field for field
/// the <see cref="PermissionGrant"/> categories plus its scoped-shell pattern list, so the bind-time
/// check is a comparison rather than a translation (#1151 §3.2).
/// </summary>
/// <remarks>
/// <b>Every field is nullable, and null means "not declared" rather than "declared false".</b> That
/// distinction is load-bearing twice: a package with no <c>skill.json</c> at all (every package #1929
/// shipped) declares nothing and therefore fails no check, and a manifest that omits
/// <c>run_shell_commands</c> is not asserting that its bundled scripts are un-runnable — only an
/// explicit <c>false</c> is, which is what the <c>executable-asset-without-shell</c> lint keys on.
/// <para>
/// <b>Requirements are checked, never applied.</b> Nothing here can widen a grant: the only outcomes
/// are "the grant already carries this" and <see cref="SkillRequirementUnsatisfiedException"/>. There
/// is deliberately no <c>allowed-tools</c> equivalent; spec/baton.md §9 states why, citing decision 0033.
/// </para>
/// </remarks>
public sealed record SkillRequirements(
    bool? ReadFiles = null,
    bool? WriteFiles = null,
    bool? RunShellCommands = null,
    IReadOnlyList<string>? ShellCommandPatterns = null,
    bool? NetworkAccess = null)
{
    /// <summary>Nothing declared — what a package with no <c>skill.json</c> resolves to.</summary>
    public static readonly SkillRequirements None = new();

    /// <summary>
    /// The categories this package requires that <paramref name="grant"/> withholds, named exactly as
    /// <see cref="PermissionGrant"/>'s own members are so an error message can be acted on. Empty when
    /// every declared requirement is satisfied — including the case where nothing is declared.
    /// </summary>
    /// <remarks>
    /// A null <paramref name="grant"/> is the raw <c>PermissionScope</c> escape hatch: there is no
    /// structured grant to compare against, so there is no claim to check, the same reading
    /// <see cref="WorkerBindingResolver.RefuseIfTheContractCannotBeWritten"/> already applies.
    /// </remarks>
    public IReadOnlyList<string> MissingFrom(PermissionGrant? grant)
    {
        if (grant is null)
        {
            return Array.Empty<string>();
        }

        var missing = new List<string>();
        if (ReadFiles == true && !grant.ReadFiles)
        {
            missing.Add(nameof(PermissionGrant.ReadFiles));
        }

        if (WriteFiles == true && !grant.WriteFiles)
        {
            missing.Add(nameof(PermissionGrant.WriteFiles));
        }

        if (RunShellCommands == true && !grant.RunShellCommands)
        {
            missing.Add(nameof(PermissionGrant.RunShellCommands));
        }

        if (NetworkAccess == true && !grant.NetworkAccess)
        {
            missing.Add(nameof(PermissionGrant.NetworkAccess));
        }

        return missing;
    }
}

/// <summary>
/// The typed <c>skill.json</c> manifest — <b>the only file in a canonical skill package Baton parses</b>
/// (#1151 §3.2, operator ruling Q1 2026-09-01). Its sibling instructions file is markdown Baton copies
/// or inlines but never interprets, which is what keeps a YAML parser out of this tree.
/// </summary>
/// <param name="Name">
/// Advisory only. The package's identity is its DIRECTORY name, matching claude's own rule that a
/// directory-sourced skill takes its command name from the directory rather than from frontmatter — so
/// a manifest whose <c>name</c> disagrees with its directory cannot make the two mean different things.
/// </param>
/// <param name="Version">The package author's own integer, bumped by them. Not a schema version.</param>
/// <param name="Description">
/// Required and non-empty when a manifest is present: on both vendors this is what a model reads to
/// judge relevance, and it is what a roster renders. A manifest-less package falls back to the
/// <c>description:</c> line <see cref="SkillScanner"/> scrapes from front matter.
/// </param>
/// <param name="Instructions">The instructions file, relative to the package directory. Defaults to <c>SKILL.md</c> — the SKILL.md-compatible shape Q1 ratified.</param>
/// <param name="Assets">Advisory list of bundled files. The realizations project or reference whatever is actually in the directory, so this never decides what ships.</param>
/// <param name="Requires">See <see cref="SkillRequirements"/>. Omitted means nothing is declared.</param>
/// <param name="Realization">See <see cref="SkillRealization"/>. Omitted means <see cref="SkillRealization.Floor"/>.</param>
public sealed record SkillManifest(
    string? Name = null,
    int Version = 1,
    string? Description = null,
    string? Instructions = null,
    IReadOnlyList<string>? Assets = null,
    SkillRequirements? Requires = null,
    SkillRealization Realization = SkillRealization.Floor)
{
    /// <summary>
    /// The two wire words <c>"realization"</c> accepts, and the enum each maps to. Mapped by hand
    /// rather than through <c>JsonStringEnumConverter</c> for the reason
    /// <see cref="WorkerRoleCatalog"/>'s own output-schema switch gives: the wire form is
    /// hyphenated (<c>native-preferred</c>), and an unrecognized value must fail loudly naming the
    /// package rather than silently defaulting to <see cref="SkillRealization.Floor"/> — a package
    /// that asked for native and silently got the floor is exactly the silent-capability-loss shape
    /// this feature exists to remove.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, SkillRealization> RealizationWords =
        new Dictionary<string, SkillRealization>(StringComparer.Ordinal)
        {
            ["floor"] = SkillRealization.Floor,
            ["native-preferred"] = SkillRealization.NativePreferred,
        };

    /// <summary>The manifest's file name inside a package directory.</summary>
    public const string FileName = "skill.json";

    /// <summary>The instructions file this manifest names, or <see cref="SkillScanner.SkillFileName"/> when it names none.</summary>
    public string InstructionsFileName =>
        string.IsNullOrWhiteSpace(Instructions) ? SkillScanner.SkillFileName : Instructions;
}
