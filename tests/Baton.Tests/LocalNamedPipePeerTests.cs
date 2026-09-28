using System.IO.Pipes;
using System.Security.Principal;
using Baton.Core.Internal;

namespace Baton.Tests.Steering;

public sealed class LocalNamedPipePeerTests
{
    [Fact]
    public async Task A_local_peer_is_verified_by_actual_user_sid_not_the_pipe_name()
    {
        if (!OperatingSystem.IsWindows()) return;
        var name = $"baton-local-peer-{Guid.NewGuid():N}";
        await using var server = LocalNamedPipePeer.CreateServer(name);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        var connected = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        await connected;
        await client.WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken);
        var one = new byte[1];
        Assert.Equal(1, await server.ReadAsync(one, TestContext.Current.CancellationToken));
        var sid = LocalNamedPipePeer.CurrentUserSid();
        Assert.NotNull(sid);
        Assert.True(LocalNamedPipePeer.IsSameUser(server, sid));
        Assert.False(LocalNamedPipePeer.IsSameUser(server, "S-1-5-32-544"));
    }
}
