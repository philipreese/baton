using System.Text.Json;
using Baton.Vendors;

namespace Baton.Vendors.Tests;

public sealed class ClaudeCorrectionEndpointTests
{
    [Fact]
    public void Descriptor_requires_independently_observed_native_session_and_lives_outside_outbox()
    {
        var room = Path.Combine(Path.GetTempPath(), "baton-correction-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(room, "artifacts", "execution_example");
        Directory.CreateDirectory(output);
        try
        {
            ClaudeCorrectionEndpoint.PublishAddress(output, "session-one", @"\\.\pipe\LOCAL\cc-msg-owned", "owner");
            Assert.Null(ClaudeCorrectionEndpoint.ReadVerified(room, "example", "owner"));
            var observe = ClaudeCorrectionEndpoint.CreateObserver(output);
            observe(JsonSerializer.Serialize(new { type = "system", subtype = "init", session_id = "session-one" }));
            var endpoint = ClaudeCorrectionEndpoint.ReadVerified(room, "example", "owner");
            Assert.NotNull(endpoint);
            Assert.Equal(@"uds:\\.\pipe\LOCAL\cc-msg-owned", endpoint.Target);
            Assert.Null(ClaudeCorrectionEndpoint.ReadVerified(room, "example", "other-owner"));
            Assert.Empty(Directory.GetFiles(output));
        }
        finally { Directory.Delete(room, true); }
    }

    [Fact]
    public void Mismatched_or_republished_identity_is_not_accepted()
    {
        var room = Path.Combine(Path.GetTempPath(), "baton-correction-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(room, "artifacts", "execution_example");
        Directory.CreateDirectory(output);
        try
        {
            ClaudeCorrectionEndpoint.PublishAddress(output, "first", @"\\.\pipe\LOCAL\cc-msg-owned", "owner");
            Assert.Throws<IOException>(() => ClaudeCorrectionEndpoint.PublishAddress(output, "second", @"\\.\pipe\LOCAL\cc-msg-other", "owner"));
            ClaudeCorrectionEndpoint.CreateObserver(output)("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"second\"}");
            Assert.Null(ClaudeCorrectionEndpoint.ReadVerified(room, "example", "owner"));
        }
        finally { Directory.Delete(room, true); }
    }
}
