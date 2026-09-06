namespace Baton.Vendors;

/// <summary>
/// How a canonical skill package asks to be realized (#1151's design comment, sections 3.2 and 4.4).
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
/// check is a comparison rather than a translation (#1151's design comment, section 3.2).
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
    /// <see cref="PermissionGrant"/>'s own members are so an error message can be acted on — except
    /// <see cref="ShellCommandPatterns"/>, which appends the specific unsatisfied patterns in
    /// parentheses, because the member name alone would not tell an operator which pattern to add.
    /// Empty when every declared requirement is satisfied — including the case where nothing is declared.
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

        if (UnsatisfiedShellPatterns(grant) is { Count: > 0 } unsatisfiedPatterns)
        {
            missing.Add(
                $"{nameof(PermissionGrant.ShellCommandPatterns)} ({string.Join(", ", unsatisfiedPatterns)})");
        }

        return missing;
    }

    /// <summary>
    /// The declared shell patterns <paramref name="grant"/> does not carry (#1941 review MEDIUM — the
    /// field parsed and documented as part of the comparison while nothing read it, which reads as
    /// checked and is not).
    /// </summary>
    /// <remarks>
    /// <b>The two halves compare differently, and #1941's re-review is why.</b>
    /// <para>
    /// <b>Allow side: exact membership.</b> A required pattern is satisfied only when the grant lists
    /// that same string, compared <see cref="StringComparison.Ordinal"/>. The cost is a false refusal in
    /// the covered case — a grant carrying <c>gh:*</c> refuses a package requiring <c>gh pr:*</c>. That
    /// direction is the safe one: an error message naming both strings, never a skill that passes here
    /// and is then denied mid-lane.
    /// </para>
    /// <para>
    /// <b>Deny side: the runtime predicate itself.</b> Exact membership INVERTS here — a deny entry that
    /// COVERS the required pattern (<c>gh label*</c> against a required <c>gh label list*</c>, both live
    /// on the <c>implement</c> role's unscoped-shell grant in <c>WorkerRoles.json</c>) is not equal to
    /// it, so membership alone bound the package and the gate then denied every such command mid-lane —
    /// exactly the failure this method exists to catch. Stripping a required pattern's trailing <c>*</c>
    /// yields the shortest command line that pattern admits, so handing THAT to
    /// <see cref="ShellCommandPatternMatcher.IsDenied"/> asks the gate's own predicate whether the family
    /// is denied. Membership is kept as well rather than replaced: <c>IsDenied</c> fails closed on a line
    /// it cannot parse (its metacharacter scan), so a de-starred pattern carrying one would otherwise
    /// lose even the exact-equality catch. Composite, this side is never laxer than the gate's
    /// <em>pattern</em> rung; the gate has two more this method does not model —
    /// <see cref="PermissionGrant.DeniedShellOptionTokens"/> and the unscoped runtime deny grammar — so
    /// a package can still bind here and be denied mid-lane by either of those. It can also be stricter
    /// (a deny entry NARROWER than the required pattern denies only part of the family and is not caught
    /// here — the residual, and it is a mid-lane deny of the narrow case only).
    /// </para>
    /// <para>
    /// Three rules, in the order they fire:
    /// </para>
    /// <list type="number">
    /// <item>no shell at all in the grant — every declared pattern is unsatisfied. A manifest declaring
    ///   patterns while omitting <c>run_shell_commands</c> is asking for a scoped shell it never named
    ///   (<see cref="PermissionGrant.ShellCommandPatterns"/>: patterns are "only meaningful when
    ///   RunShellCommands is set"), so it refuses here rather than passing on a technicality;</item>
    /// <item>an UNSCOPED granted shell (<see cref="PermissionGrant.ShellCommandPatterns"/> null or
    ///   empty) means "any command", so every declared pattern is satisfied by it;</item>
    /// <item>a pattern <see cref="PermissionGrant.DeniedShellCommandPatterns"/> lists <em>or covers</em>
    ///   is unsatisfied even when the allowlist also carries it, and even when the granted shell is
    ///   unscoped — deny-over-allow is that field's own rule, and a package requiring a standing "never"
    ///   must not bind.</item>
    /// </list>
    /// </remarks>
    private IReadOnlyList<string> UnsatisfiedShellPatterns(PermissionGrant grant)
    {
        if (ShellCommandPatterns is not { Count: > 0 } required)
        {
            return Array.Empty<string>();
        }

        var granted = grant.ShellCommandPatterns ?? Array.Empty<string>();
        var denied = grant.DeniedShellCommandPatterns ?? Array.Empty<string>();
        var unsatisfied = new List<string>();
        foreach (var pattern in required)
        {
            var allowed = grant.RunShellCommands
                && (granted.Count == 0 || granted.Contains(pattern, StringComparer.Ordinal));
            // Exact membership OR the gate's own predicate on the shortest line the pattern admits --
            // the remark above states why both, and why neither alone is enough.
            var deniedByGrant = denied.Contains(pattern, StringComparer.Ordinal)
                || ShellCommandPatternMatcher.IsDenied(pattern.TrimEnd('*'), denied);
            if (!allowed || deniedByGrant)
            {
                unsatisfied.Add(pattern);
            }
        }

        return unsatisfied;
    }
}

/// <summary>
/// The typed <c>skill.json</c> manifest — <b>the only file in a canonical skill package Baton parses</b>
/// (operator ruling Q1 on #1151, 2026-09-01). spec/baton.md §9 states what that buys.
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
