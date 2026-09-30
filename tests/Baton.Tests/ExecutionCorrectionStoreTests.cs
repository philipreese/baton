using Baton.Steering;

namespace Baton.Tests.Steering;

public sealed class ExecutionCorrectionStoreTests : IDisposable
{
    private readonly string _room = Directory.CreateTempSubdirectory("baton-correction-").FullName;
    private ExecutionCorrectionStore Store => new(_room);
    private static CorrectionRequest Request => new("message", "execution", "hash", "claude", "session",
        "target", 123, new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc), "principal", DateTimeOffset.UtcNow);

    [Fact]
    public void Claim_is_durable_idempotent_and_exclusive_per_execution()
    {
        var request = Request;
        Assert.Null(Store.Query(request.ExecutionId));
        Assert.True(Store.TryClaim(request));
        Assert.False(Store.TryClaim(request with { RequestedUtc = request.RequestedUtc.AddMinutes(1) }));
        Assert.Equal(request, Store.Query(request.ExecutionId)!.Request);
        Assert.Equal(SteeringReceiptState.OutcomeUnknown, Store.Query(request.ExecutionId)!.State);
        Assert.Throws<InvalidOperationException>(() => Store.TryClaim(request with { MessageId = "another" }));
        Assert.Throws<InvalidOperationException>(() => Store.TryClaim(request with { PayloadSha256 = "other" }));
        Assert.Throws<InvalidOperationException>(() => Store.TryClaim(request with { ProcessId = 124 }));
        foreach (var conflict in new[]
        {
            request with { Adapter = "other" }, request with { SessionId = "other" },
            request with { Target = "other" }, request with { OsPrincipal = "other" },
            request with { ProcessStartUtc = request.ProcessStartUtc.AddSeconds(1) },
        })
        {
            Assert.Throws<InvalidOperationException>(() => Store.TryClaim(conflict));
            Assert.Throws<InvalidOperationException>(() => Store.TryAdmitTool(conflict));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Admission_and_native_answer_survive_reconstruction(bool accepted)
    {
        var request = Request;
        Assert.Throws<InvalidOperationException>(() => Store.TryAdmitTool(request));
        Assert.True(Store.TryClaim(request));
        Assert.Throws<InvalidOperationException>(() => Store.RecordAnswer(request, accepted, "ack", "reason"));
        Assert.Throws<InvalidOperationException>(() => Store.TryAdmitTool(request with { SessionId = "other" }));
        Assert.True(Store.TryAdmitTool(request));
        Assert.False(Store.TryAdmitTool(request));
        Assert.Equal(SteeringReceiptState.OutcomeUnknown, Store.Query(request.ExecutionId)!.State);
        var answer = Store.RecordAnswer(request, accepted, "ack", "reason");
        Assert.Equal(accepted ? SteeringReceiptState.TransportAcknowledged : SteeringReceiptState.Rejected, answer.State);
        Assert.Equal(answer, Store.Query(request.ExecutionId));
        Assert.Equal(answer, Store.RecordAnswer(request, accepted, "ack", "reason"));
        Assert.Throws<InvalidOperationException>(() => Store.RecordAnswer(request, !accepted, "ack", "reason"));
        Assert.Throws<InvalidOperationException>(() => Store.RecordAnswer(request, accepted, "other", "reason"));
        Assert.False(Store.TryAdmitTool(request));
        Assert.False(Store.TryClaim(request));
    }

    [Fact]
    public async Task Concurrent_instances_claim_and_admit_only_once()
    {
        var request = Request;
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Store.TryClaim(request))));
        Assert.Single(claims, claimed => claimed);
        var admissions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Store.TryAdmitTool(request))));
        Assert.Single(admissions, admitted => admitted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{broken\n")]
    [InlineData("{}\n")]
    public void Corrupt_claim_never_becomes_a_new_claim(string corrupt)
    {
        var request = Request;
        Store.TryClaim(request);
        var journal = Assert.Single(Directory.GetFiles(Path.Combine(_room, "steering")));
        File.WriteAllText(journal, corrupt);
        Assert.Throws<InvalidOperationException>(() => Store.Query(request.ExecutionId));
        Assert.Throws<InvalidOperationException>(() => Store.TryClaim(request));
        Assert.Throws<InvalidOperationException>(() => Store.TryAdmitTool(request));
        Assert.Equal(corrupt, File.ReadAllText(journal));
    }

    [Theory]
    [InlineData("{\"Kind\":\"future\"}\n")]
    [InlineData("{\"Kind\":\"admitted\"")]
    public void Interrupted_or_unknown_append_blocks_replay(string suffix)
    {
        var request = Request;
        Store.TryClaim(request);
        var journal = Assert.Single(Directory.GetFiles(Path.Combine(_room, "steering")));
        File.AppendAllText(journal, suffix);
        Assert.Throws<InvalidOperationException>(() => Store.Query(request.ExecutionId));
        Assert.Throws<InvalidOperationException>(() => Store.TryAdmitTool(request));
    }

    [Fact]
    public void Execution_ids_are_hashed_and_independent()
    {
        var request = Request with { ExecutionId = "../../escape" };
        Assert.True(Store.TryClaim(request));
        Assert.True(Store.TryClaim(Request));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_room, "steering")).Length);
        Assert.Equal(request, Store.Query(request.ExecutionId)!.Request);
    }

    [Fact]
    public void Duplicate_admission_fact_and_truncated_answer_fail_closed()
    {
        var request = Request;
        Store.TryClaim(request);
        Store.TryAdmitTool(request);
        var journal = Assert.Single(Directory.GetFiles(Path.Combine(_room, "steering")));
        var admittedText = File.ReadAllText(journal);
        File.AppendAllText(journal, File.ReadAllLines(journal)[1] + "\n");
        Assert.Throws<InvalidOperationException>(() => Store.RecordAnswer(request, true, "ack", null));
        File.WriteAllText(journal, admittedText);
        Store.RecordAnswer(request, true, "ack", null);
        var answeredText = File.ReadAllText(journal);
        File.WriteAllText(journal, answeredText[..^1]);
        Assert.Throws<InvalidOperationException>(() => Store.RecordAnswer(request, true, "ack", null));
        Assert.Throws<InvalidOperationException>(() => Store.TryAdmitTool(request));
    }

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_room);
}
