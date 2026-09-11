using Baton.Queue;
using Baton.Vendors;

namespace Baton.Cli.Tests;

public sealed class TaskRequirementPreflightTests
{
    [Fact]
    public void A_GitHub_write_requirement_honours_the_runtime_option_token_deny()
    {
        var admission = TaskRequirementPreflight.Evaluate(
            [TaskRequirements.GitHubWrite],
            new PermissionGrant(
                RunShellCommands: true,
                DeniedShellOptionTokens: ["--body"]),
            ["report.md"]);

        Assert.Equal(TaskRequirementAdmission.Refused, admission.Result);
        Assert.Equal([TaskRequirements.GitHubWrite], admission.Missing);
        Assert.DoesNotContain(TaskRequirements.GitHubWrite, admission.EffectiveGrant);
    }
}
