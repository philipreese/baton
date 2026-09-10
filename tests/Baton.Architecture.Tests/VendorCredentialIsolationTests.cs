using System.Text.RegularExpressions;

namespace Baton.Architecture.Tests;

/// <summary>
/// docs/agents/developing-baton.md architecture rule 4: AER must never read, copy, forward, or store a vendor credential.
/// It spawns the vendor's own first-party CLI, which authenticates itself; AER is a keyboard, not a
/// client.
/// </summary>
/// <remarks>
/// <para>
/// This has always been true, but only as an emergent property of nobody having needed a key —
/// exactly the kind of invariant that erodes silently the first time someone adds
/// <c>ANTHROPIC_API_KEY</c> to a child process's environment to make a test pass.
/// </para>
/// <para>
/// It is the product premise made structural. docs/agents/developing-baton.md: the project works against
/// <em>subscriptions</em>, not API keys, and the adapters "deliberately own no key-handling code".
/// Both vendors' SDKs were evaluated and rejected precisely because they are API-key transports
/// (<c>docs/vendor-doc-audit.md</c>). A key read anywhere in <c>src/</c> would mean the thing the
/// project exists to avoid had arrived through the back door — and would do it quietly, since
/// nothing else in the build would object.
/// </para>
/// <para>
/// Scope is deliberately narrow and honest. Matching quoted credential names catches the realistic
/// regression (someone plumbs a key through) and not a determined author who builds the string at
/// runtime. Like <see cref="ReferenceDirectionTests"/>, it asserts the structurally checkable part
/// and leaves the rest to review. Pure file reading over the repo — no project references, no
/// network — so it runs identically on every CI platform.
/// </para>
/// </remarks>
public class VendorCredentialIsolationTests
{
    // Vendor credential material, by the names it actually travels under. AER's own secrets (the
    // daemon pairing token, BATON_HOME) are deliberately absent: this guards the vendor boundary, not
    // the notion of a secret in general.
    private static readonly string[] ForbiddenCredentialNames =
    [
        // Anthropic
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "CLAUDE_CODE_OAUTH_TOKEN",
        // Google / Antigravity — GEMINI_API_KEY and the Vertex pair are the two auth paths the
        // google-antigravity SDK accepts, i.e. exactly what choosing the CLI instead avoids.
        "GEMINI_API_KEY",
        "GOOGLE_API_KEY",
        "GOOGLE_APPLICATION_CREDENTIALS",
        "GOOGLE_CLOUD_PROJECT",
        "GOOGLE_CLOUD_LOCATION",
        // On-disk credential stores belonging to the vendor CLIs.
        "antigravity-oauth-token",
        ".credentials.json",
        "oauth_creds.json",
        // OpenAI Codex
        "auth.json",
    ];

    // OS credential-store entry points. Reaching a vendor's keyring entry is the single act that
    // would turn AER into the thing Google's example names.
    private static readonly string[] ForbiddenCredentialApis =
    [
        "CredReadW",
        "CredRead",
        "SecretService",
        "SecKeychain",
        "libsecret",
    ];

    [Fact]
    public void No_source_file_reads_a_vendor_credential_by_name()
    {
        var offenders = new List<string>();

        foreach (var (path, code) in ProductionSources())
        {
            foreach (var name in ForbiddenCredentialNames)
            {
                // Quoted literal only: prose in a doc comment explaining why we do NOT do this is
                // the correct thing to have in the tree, and must not fail the gate.
                if (Regex.IsMatch(code, "\"" + Regex.Escape(name) + "\"", RegexOptions.IgnoreCase))
                {
                    offenders.Add($"{path}: \"{name}\"");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "AER must never read or forward a vendor credential — it spawns the vendor CLI, which "
            + "authenticates itself (docs/agents/developing-baton.md rule 4 — subscriptions, not API keys). Found:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void No_source_file_reaches_into_an_os_credential_store()
    {
        var offenders = new List<string>();

        foreach (var (path, code) in ProductionSources())
        {
            foreach (var api in ForbiddenCredentialApis)
            {
                if (Regex.IsMatch(code, @"\b" + Regex.Escape(api) + @"\b"))
                {
                    offenders.Add($"{path}: {api}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "AER must not read the OS credential store — the vendor CLIs own their own logins "
            + "(docs/agents/developing-baton.md rule 4). Found:\n  " + string.Join("\n  ", offenders));
    }

    // A scanner that reads nothing passes both assertions above vacuously — the same stale-and-
    // unchecked failure ReferenceDirectionTests guards against. Anchor on files and a literal known
    // to exist so a broken glob fails loudly instead of going green.
    [Fact]
    public void The_source_scanner_is_not_silently_returning_nothing()
    {
        var sources = ProductionSources().ToList();

        Assert.True(sources.Count > 50, $"Expected the whole of src/ to be scanned, saw {sources.Count} files.");
        Assert.Contains(sources, file => file.Path.EndsWith("ClaudeWorkerAdapter.cs", StringComparison.Ordinal));
        Assert.Contains(sources, file => file.Path.EndsWith("AgyWorkerAdapter.cs", StringComparison.Ordinal));
        Assert.Contains(sources, file => file.Path.EndsWith("CodexWorkerAdapter.cs", StringComparison.Ordinal));

        // BATON_HOME is a real quoted literal in BatonPaths.cs. If the reader is returning empty strings
        // rather than file contents, this fails while the credential scans above would not.
        Assert.Contains(sources, file => file.Code.Contains("\"BATON_HOME\"", StringComparison.Ordinal));
    }

    private static IEnumerable<(string Path, string Code)> ProductionSources()
    {
        var src = Path.Combine(RepoRoot(), "src");

        foreach (var path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            // Build output under bin/obj contains generated copies that would double-report.
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (Path.GetRelativePath(RepoRoot(), path), File.ReadAllText(path));
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Baton.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate the repo root (Baton.slnx) by walking up from " + AppContext.BaseDirectory);
    }
}
