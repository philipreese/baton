using Baton;

namespace Baton.Vendors;

/// <summary>
/// A canonical skill package failed the format lint or could not be read as one (#1151 §4.5). The
/// message always names the <b>offending file</b> and the <b>rule</b>, because the operator's next
/// action is to edit that file and nothing else in the package tells them which one.
/// </summary>
/// <remarks>
/// <b>Identity and format faults fail fast</b> (#1151 §4.6): a package named by <c>--skill</c> that
/// does not parse must never produce a run that silently lacks the capability. The tolerant discovery
/// path (<see cref="SkillPackageReader.DiscoverPackages"/>, which scans a directory nobody named)
/// catches this and skips the package with a stderr warning instead — see that method for why the
/// two paths differ.
/// </remarks>
public sealed class SkillPackageFormatException : BatonFlowException
{
    /// <summary>The package directory name — the package's identity (#1151 §3.1).</summary>
    public string PackageName { get; }

    /// <summary>The rule slug that refused it, e.g. <c>vendor-placeholder</c>.</summary>
    public string Rule { get; }

    /// <summary>The file the rule was measured on.</summary>
    public string OffendingFile { get; }

    public SkillPackageFormatException(string packageName, string rule, string offendingFile, string what, string? remedy = null)
        : base($"Skill package '{packageName}' fails the '{rule}' rule in '{offendingFile}': {what}")
    {
        PackageName = packageName;
        Rule = rule;
        OffendingFile = offendingFile;
        TryInvocation = remedy;
    }
}
