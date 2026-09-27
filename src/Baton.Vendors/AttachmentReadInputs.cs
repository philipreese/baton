using Baton.Artifacts;
using Baton.Domain;

namespace Baton.Vendors;

/// <summary>Resolves the binding's ordered harness inputs inside its own room.</summary>
public static class AttachmentReadInputs
{
    public static void ValidateNames(IReadOnlyList<string>? names)
    {
        if (names is null)
        {
            return;
        }

        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || ReservedOutputNames.IsReserved(name)
                || ReservedOutputNames.IsPathTraversal(name) || name.Contains('\\')
                || name.Contains('/') || name.Contains(':') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || name.EndsWith(' ') || name.EndsWith('.') || IsDeviceName(name)
                || name is "." or ".." || !seen.Add(name))
            {
                throw new WorkerBindingConfigException($"Invalid or duplicate attachment file name '{name}'.");
            }
        }
    }

    private static bool IsDeviceName(string name)
    {
        var stem = name.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4 && stem[3] is >= '1' and <= '9'
                && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)));
    }

    public static IReadOnlyList<string> Resolve(IReadOnlyList<string>? names, string? roomDirectory)
    {
        ValidateNames(names);
        if (names is not { Count: > 0 })
        {
            return [];
        }
        if (string.IsNullOrWhiteSpace(roomDirectory))
        {
            throw new WorkerBindingConfigException("Attachment names require a current room binding path.");
        }

        var room = Path.GetFullPath(roomDirectory);
        var root = Path.GetFullPath(Path.Combine(room, ArtifactManager.ArtifactsDirectoryName, "attachments"));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var paths = new List<string>(names.Count);
        foreach (var name in names)
        {
            var path = Path.GetFullPath(Path.Combine(root, name));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison)
                || !File.Exists(path) || Directory.Exists(path) || CrossesLink(path))
            {
                throw new WorkerBindingConfigException($"Attachment '{name}' is not a regular file in this room.");
            }
            paths.Add(path);
        }
        return paths;
    }

    public static bool CrossesLink(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }
        return false;
    }
}
