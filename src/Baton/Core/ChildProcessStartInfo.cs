using System.Diagnostics;

namespace Baton;

/// <summary>
/// Constructs start information for every child process Baton owns. Window creation and shell
/// execution are disabled here so a windowless Baton host cannot surface its children through the
/// operator's default terminal application.
/// </summary>
public static class ChildProcessStartInfo
{
    public static ProcessStartInfo Create(string fileName, Action<ProcessStartInfo>? configure = null)
    {
        var startInfo = new ProcessStartInfo(fileName);
        configure?.Invoke(startInfo);
        startInfo.CreateNoWindow = true;
        startInfo.UseShellExecute = false;
        return startInfo;
    }
}
