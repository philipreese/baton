namespace Baton.Vendors.Tests;

public class WorkerBindingConfigParserTests
{
    private const string ValidJson = """
        {
          "architect": {
            "Adapter": "echo",
            "Contract": {
              "WorkerName": "architect",
              "RequiredInputs": [],
              "ProducedOutputs": [{ "Name": "plan" }],
              "OptionalMetadata": []
            },
            "PromptTemplate": "Draft a plan and write it to your output file.",
            "Timeout": "00:05:00",
            "Model": "claude-opus-4",
            "PermissionScope": "write-only"
          }
        }
        """;

    [Fact]
    public void A_valid_config_parses_into_one_entry_per_worker_name()
    {
        var config = WorkerBindingConfigParser.Parse(ValidJson);

        var entry = Assert.Single(config).Value;
        Assert.Equal("architect", config.Keys.Single());
        Assert.Equal("echo", entry.Adapter);
        Assert.Equal("architect", entry.Contract.WorkerName);
        Assert.Equal(["plan"], entry.Contract.ProducedOutputs.Select(o => o.Name));
        Assert.Equal("Draft a plan and write it to your output file.", entry.PromptTemplate);
        Assert.Equal(TimeSpan.FromMinutes(5), entry.Timeout);
        Assert.Equal("claude-opus-4", entry.Model);
        Assert.Equal("write-only", entry.PermissionScope);
    }

    [Fact]
    public void A_bindings_file_omitting_AllowsSubagents_reads_false()
    {
        // #1811 review: the baton run/resume/decide path -- a hand-authored bindings.json that never
        // mentions the field must not be able to spawn a subagent.
        var config = WorkerBindingConfigParser.Parse(ValidJson);

        var entry = Assert.Single(config).Value;
        Assert.False(entry.AllowsSubagents);
    }

    [Fact]
    public void A_bindings_file_omitting_VerifiesWorkspace_reads_true()
    {
        // #2029 review: the fail-closed direction WorkerBindingConfigEntry.VerifiesWorkspace's own
        // comment promises, on the baton run/resume/decide path -- a hand-authored bindings.json that
        // never mentions the field keeps being graded by the workspace's declaration rather than
        // silently skipping the gate. The writer always emits the key, so a round-trip test cannot
        // fail if the record's declared default stops surviving deserialization; this can.
        var config = WorkerBindingConfigParser.Parse(ValidJson);

        var entry = Assert.Single(config).Value;
        Assert.True(entry.VerifiesWorkspace);
    }

    [Fact]
    public void Model_and_permission_scope_are_optional()
    {
        const string json = """
            {
              "critic": {
                "Adapter": "echo",
                "Contract": {
                  "WorkerName": "critic",
                  "RequiredInputs": ["plan"],
                  "ProducedOutputs": [{ "Name": "review" }],
                  "OptionalMetadata": []
                },
                "PromptTemplate": "Review the plan.",
                "Timeout": "00:01:00"
              }
            }
            """;

        var config = WorkerBindingConfigParser.Parse(json);

        var entry = config["critic"];
        Assert.Null(entry.Model);
        Assert.Null(entry.PermissionScope);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    public void Malformed_json_throws(string json)
    {
        Assert.Throws<WorkerBindingConfigException>(() => WorkerBindingConfigParser.Parse(json));
    }

    [Fact]
    public void Null_document_throws()
    {
        Assert.Throws<WorkerBindingConfigException>(() => WorkerBindingConfigParser.Parse("null"));
    }

    [Fact]
    public void An_entry_missing_Adapter_throws()
    {
        const string json = """
            {
              "architect": {
                "Adapter": "",
                "Contract": { "WorkerName": "architect", "RequiredInputs": [], "ProducedOutputs": [], "OptionalMetadata": [] },
                "PromptTemplate": "Draft a plan.",
                "Timeout": "00:05:00"
              }
            }
            """;

        Assert.Throws<WorkerBindingConfigException>(() => WorkerBindingConfigParser.Parse(json));
    }

    [Fact]
    public void An_entry_missing_Contract_throws()
    {
        const string json = """
            {
              "architect": {
                "Adapter": "echo",
                "PromptTemplate": "Draft a plan.",
                "Timeout": "00:05:00"
              }
            }
            """;

        Assert.Throws<WorkerBindingConfigException>(() => WorkerBindingConfigParser.Parse(json));
    }

    [Fact]
    public void An_entry_missing_PromptTemplate_throws()
    {
        const string json = """
            {
              "architect": {
                "Adapter": "echo",
                "Contract": { "WorkerName": "architect", "RequiredInputs": [], "ProducedOutputs": [], "OptionalMetadata": [] },
                "Timeout": "00:05:00"
              }
            }
            """;

        Assert.Throws<WorkerBindingConfigException>(() => WorkerBindingConfigParser.Parse(json));
    }

    [Fact]
    public void WorkingDirectory_is_optional_and_defaults_to_null()
    {
        var config = WorkerBindingConfigParser.Parse(ValidJson);

        Assert.Null(config["architect"].WorkingDirectory);
    }

    [Fact]
    public void A_configured_WorkingDirectory_parses_through()
    {
        const string json = """
            {
              "architect": {
                "Adapter": "echo",
                "Contract": { "WorkerName": "architect", "RequiredInputs": [], "ProducedOutputs": [], "OptionalMetadata": [] },
                "PromptTemplate": "Draft a plan.",
                "Timeout": "00:05:00",
                "WorkingDirectory": "myproject"
              }
            }
            """;

        var config = WorkerBindingConfigParser.Parse(json);

        Assert.Equal("myproject", config["architect"].WorkingDirectory);
    }

    [Fact]
    public void A_blank_WorkingDirectory_throws()
    {
        const string json = """
            {
              "architect": {
                "Adapter": "echo",
                "Contract": { "WorkerName": "architect", "RequiredInputs": [], "ProducedOutputs": [], "OptionalMetadata": [] },
                "PromptTemplate": "Draft a plan.",
                "Timeout": "00:05:00",
                "WorkingDirectory": "   "
              }
            }
            """;

        Assert.Throws<WorkerBindingConfigException>(() => WorkerBindingConfigParser.Parse(json));
    }

    [Fact]
    public async Task LoadFromFileAsync_names_the_file_in_a_malformed_json_error()
    {
        // #562: a raw System.Text.Json exception gave no indication which file was bad.
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{ not valid json", TestContext.Current.CancellationToken);
        try
        {
            var ex = await Assert.ThrowsAsync<WorkerBindingConfigException>(
                () => WorkerBindingConfigParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken));

            Assert.Contains(path, ex.Message);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task LoadFromFileAsync_reads_and_parses_a_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, ValidJson, TestContext.Current.CancellationToken);
        try
        {
            var config = await WorkerBindingConfigParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);
            Assert.Single(config);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// A non-positive <c>Timeout</c> is not a slow worker, it is an unrunnable one: it reaches
    /// <c>BatonTask.WithTimeout</c> as a zero (or negative) <see cref="TimeSpan"/>, and the timeout
    /// monitor inside <c>BatonProcessRunner.Run</c> kills the process tree as soon as its delay
    /// elapses -- immediately, for zero. Before this check the file parsed happily and the worker
    /// died on startup with nothing naming the cause.
    /// </summary>
    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:05:00")]
    public void Parse_rejects_a_non_positive_Timeout(string timeout)
    {
        var json = $$"""
            {
              "architect": {
                "Adapter": "claude",
                "PromptTemplate": "Draft a plan.",
                "Timeout": "{{timeout}}",
                "Contract": {
                  "WorkerName": "architect",
                  "RequiredInputs": [],
                  "ProducedOutputs": [{ "Name": "plan.md" }],
                  "OptionalMetadata": []
                }
              }
            }
            """;

        var ex = Assert.Throws<WorkerBindingConfigException>(() => WorkerBindingConfigParser.Parse(json));
        Assert.Contains("Timeout", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The omission case specifically, because it is the likely one: a missing <c>Timeout</c>
    /// deserializes to <c>default(TimeSpan)</c> and lands in exactly the same place as an explicit
    /// zero, so forgetting the field used to be indistinguishable from asking for an instant kill.
    /// </summary>
    [Fact]
    public void Parse_rejects_an_entry_that_omits_Timeout_entirely()
    {
        const string json = """
            {
              "architect": {
                "Adapter": "claude",
                "PromptTemplate": "Draft a plan.",
                "Contract": {
                  "WorkerName": "architect",
                  "RequiredInputs": [],
                  "ProducedOutputs": [{ "Name": "plan.md" }],
                  "OptionalMetadata": []
                }
              }
            }
            """;

        Assert.Throws<WorkerBindingConfigException>(() => WorkerBindingConfigParser.Parse(json));
    }

    /// <summary>The polarity control: a positive Timeout still parses, and round-trips its value.</summary>
    [Fact]
    public void Parse_accepts_a_positive_Timeout()
    {
        const string json = """
            {
              "architect": {
                "Adapter": "claude",
                "PromptTemplate": "Draft a plan.",
                "Timeout": "00:20:00",
                "Contract": {
                  "WorkerName": "architect",
                  "RequiredInputs": [],
                  "ProducedOutputs": [{ "Name": "plan.md" }],
                  "OptionalMetadata": []
                }
              }
            }
            """;

        Assert.Equal(TimeSpan.FromMinutes(20), WorkerBindingConfigParser.Parse(json)["architect"].Timeout);
    }

    [Fact]
    public async Task LoadFromFileAsync_on_a_missing_file_throws_the_typed_exception_not_a_raw_FileNotFound()
    {
        // Missing --bindings -> the typed WorkerBindingConfigException; run/decide/supply/cancel all read
        // through here with no existence check, so this loader is their shared missing-file guard.
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-bindings-{Guid.NewGuid():N}.json");

        var ex = await Assert.ThrowsAsync<WorkerBindingConfigException>(
            () => WorkerBindingConfigParser.LoadFromFileAsync(missing, TestContext.Current.CancellationToken));
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void A_FallbackOnExhaustion_naming_the_entrys_own_adapter_is_refused()
    {
        const string json = """
            {
              "architect": {
                "Adapter": "agy",
                "Contract": { "WorkerName": "architect", "RequiredInputs": [], "ProducedOutputs": [], "OptionalMetadata": [] },
                "PromptTemplate": "Draft a plan.",
                "Timeout": "00:05:00",
                "FallbackOnExhaustion": { "Adapter": "agy" }
              }
            }
            """;

        var ex = Assert.Throws<WorkerBindingConfigException>(() => WorkerBindingConfigParser.Parse(json));
        Assert.Contains("cannot fall back to itself", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_FallbackOnExhaustion_naming_a_different_adapter_parses()
    {
        const string json = """
            {
              "architect": {
                "Adapter": "agy",
                "Contract": { "WorkerName": "architect", "RequiredInputs": [], "ProducedOutputs": [], "OptionalMetadata": [] },
                "PromptTemplate": "Draft a plan.",
                "Timeout": "00:05:00",
                "FallbackOnExhaustion": { "Adapter": "claude", "Model": "sonnet" }
              }
            }
            """;

        var entry = WorkerBindingConfigParser.Parse(json)["architect"];

        Assert.NotNull(entry.FallbackOnExhaustion);
        Assert.Equal("claude", entry.FallbackOnExhaustion.Adapter);
        Assert.Equal("sonnet", entry.FallbackOnExhaustion.Model);
    }
}
