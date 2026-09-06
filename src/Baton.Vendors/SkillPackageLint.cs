namespace Baton.Vendors;

/// <summary>
/// The canonical skill package format lint (#1151 §3.3/§4.5) — three rules, each closing a hazard whose
/// alternative is silent. Run over a package's own bytes, before any realization exists, so a refusal
/// costs nothing but an edit.
/// </summary>
/// <remarks>
/// <b>Why each rule exists, once:</b>
/// <list type="number">
/// <item><c>vendor-placeholder</c> — <c>${CLAUDE_SKILL_DIR}</c>/<c>${CLAUDE_PROJECT_DIR}</c> and their
///   agy equivalents are substituted by ONE vendor. A canonical package carrying one is portable in
///   name only: on the other vendor the literal text reaches the model. Baton's own
///   <c>${BATON_SKILL_DIR}</c> is the portable spelling, and the refusal names it.</item>
/// <item><c>bash-injection</c> — claude documents a skill body carrying <c>!`command`</c> whose command
///   is pre-approved by the same skill's <c>allowed-tools</c> rule, and <c>/skillname</c> is documented
///   to bypass <c>PreToolUse</c> — the gate decision 0029 makes mandatory. Whether an injected command
///   in a realized skill actually fires that hook is UNMEASURED on both vendors; refusing the syntax
///   closes the path without needing the measurement.</item>
/// <item><c>executable-asset-without-shell</c> — a package that bundles a script while its manifest
///   declares <c>"run_shell_commands": false</c> is incoherent: the asset can only be read, never run.
///   Refused at load, the same species of coherence refusal
///   <see cref="IncoherentPermissionGrantException"/> already applies to a grant. Keyed on an EXPLICIT
///   false, never on an omitted key — see <see cref="SkillRequirements"/> for why that distinction is
///   what keeps every manifest-less package working.</item>
/// </list>
/// <para>
/// The lint deliberately does NOT read the operator's permission grant. That comparison is the
/// requirement check (<see cref="SkillRequirements.MissingFrom"/>), which happens at bind time against
/// a real binding; this runs at package load, where no binding exists yet.
/// </para>
/// </remarks>
public static class SkillPackageLint
{
    public const string VendorPlaceholderRule = "vendor-placeholder";
    public const string BashInjectionRule = "bash-injection";
    public const string ExecutableAssetWithoutShellRule = "executable-asset-without-shell";

    /// <summary>The one portable placeholder a canonical package may use for its own realized directory.</summary>
    public const string BatonSkillDirectoryPlaceholder = "${BATON_SKILL_DIR}";

    /// <summary>
    /// Vendor-native substitutions refused by <see cref="VendorPlaceholderRule"/>. Matched as literal
    /// prefixes of a <c>${…}</c> token rather than by a general expression: this list is what the rule
    /// IS, and a reader has to be able to see it.
    /// </summary>
    private static readonly string[] VendorPlaceholderPrefixes =
    [
        "${CLAUDE_",
        "${GEMINI_",
        "${AGY_",
        "${ANTIGRAVITY_",
    ];

    /// <summary>
    /// File extensions treated as executable assets. Extension-based on purpose: Windows is the only
    /// platform this project builds for (spec/baton.md §11 C-10), where there is no executable bit to
    /// read, so the name is the only signal available.
    /// </summary>
    private static readonly HashSet<string> ExecutableAssetExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".ps1", ".psm1", ".sh", ".bash", ".bat", ".cmd", ".py", ".exe" };

    /// <summary>
    /// Refuses <paramref name="package"/> if it breaks any rule. Returns normally otherwise.
    /// </summary>
    /// <param name="package">The package to lint — its instructions content and its own directory.</param>
    /// <exception cref="SkillPackageFormatException">A rule refused it; the exception names the file and the rule.</exception>
    public static void Refuse(SkillPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        RefuseVendorPlaceholder(package);
        RefuseBashInjection(package);
        RefuseExecutableAssetWithoutShell(package);
    }

    /// <summary>
    /// <see cref="Refuse"/>'s non-throwing form: the exception a lint failure WOULD raise, or null when
    /// the package is clean — for a caller that wants the verdict without the control flow.
    /// </summary>
    public static SkillPackageFormatException? Check(SkillPackage package)
    {
        try
        {
            Refuse(package);
            return null;
        }
        catch (SkillPackageFormatException ex)
        {
            return ex;
        }
    }

    private static void RefuseVendorPlaceholder(SkillPackage package)
    {
        foreach (var prefix in VendorPlaceholderPrefixes)
        {
            var index = package.Content.IndexOf(prefix, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var token = ReadPlaceholderToken(package.Content, index);
            throw new SkillPackageFormatException(
                package.Name,
                VendorPlaceholderRule,
                package.SkillFilePath,
                $"it carries the vendor-native placeholder '{token}', which only one vendor substitutes — "
                + "on the other, the literal text reaches the model.",
                $"replace it with '{BatonSkillDirectoryPlaceholder}', which every realization substitutes with this package's own realized directory.");
        }
    }

    private static void RefuseBashInjection(SkillPackage package)
    {
        // claude's documented injection syntax inside a skill body: a bang immediately followed by a
        // backtick-delimited command. Matched literally rather than parsed -- the rule refuses the
        // SYNTAX, so anything that looks like it is enough.
        var index = package.Content.IndexOf("!`", StringComparison.Ordinal);
        if (index < 0)
        {
            return;
        }

        throw new SkillPackageFormatException(
            package.Name,
            BashInjectionRule,
            package.SkillFilePath,
            "it carries load-time bash-injection syntax (!`…`), which a vendor may execute on its own "
            + "before Baton's mandatory PreToolUse gate sees it.",
            "state the command as text the worker is asked to run, so it goes through the gate like every other tool call.");
    }

    private static void RefuseExecutableAssetWithoutShell(SkillPackage package)
    {
        if (package.Manifest?.Requires?.RunShellCommands != false)
        {
            return;
        }

        foreach (var asset in EnumerateAssets(package.DirectoryPath))
        {
            if (!ExecutableAssetExtensions.Contains(Path.GetExtension(asset)))
            {
                continue;
            }

            throw new SkillPackageFormatException(
                package.Name,
                ExecutableAssetWithoutShellRule,
                asset,
                "the package bundles an executable asset while its manifest declares "
                + "\"run_shell_commands\": false — the worker could read the script but never run it.",
                "set \"run_shell_commands\": true in skill.json if the script is meant to be run, or drop the script.");
        }
    }

    private static IEnumerable<string> EnumerateAssets(string packageDirectory)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(packageDirectory, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Fail open but not silently, the same rule SkillScanner's own catch states: an unreadable
            // package directory must not decide a lint verdict either way.
            Console.Error.WriteLine($"Warning: could not enumerate skill package assets under '{packageDirectory}': {ex.Message}");
            return Array.Empty<string>();
        }

        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }

    /// <summary>The whole <c>${…}</c> token starting at <paramref name="start"/>, for an error message that quotes what the author typed.</summary>
    private static string ReadPlaceholderToken(string content, int start)
    {
        var end = content.IndexOf('}', start);
        return end < 0
            ? content[start..Math.Min(content.Length, start + 40)]
            : content[start..(end + 1)];
    }
}
