namespace Baton.Architecture.Tests;

/// <summary>
/// The one way this assembly finds the repository root: walk up from the test binary to the
/// directory holding <c>Baton.slnx</c>. One helper, one sentinel — #2128's review found a second
/// copy keyed on <c>pixi.toml</c>, which is the shape that ends with the two disagreeing after a
/// restructure.
/// </summary>
internal static class RepoRoot
{
    public static string Locate()
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
