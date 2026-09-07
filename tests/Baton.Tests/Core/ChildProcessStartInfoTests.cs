namespace Baton.Tests.Core;

public class ChildProcessStartInfoTests
{
    [Fact]
    public void Created_start_info_is_windowless_and_does_not_use_the_shell()
    {
        var startInfo = ChildProcessStartInfo.Create("child-program", startInfo =>
        {
            startInfo.CreateNoWindow = false;
            startInfo.UseShellExecute = true;
        });

        Assert.True(startInfo.CreateNoWindow);
        Assert.False(startInfo.UseShellExecute);
    }
}
