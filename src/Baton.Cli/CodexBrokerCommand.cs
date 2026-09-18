using System.Text.Json;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>Hidden subprocess entry point used only by <see cref="CodexWorkerAdapter"/>.</summary>
internal static class CodexBrokerCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length != 3 || args[0] != "--config" || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine("Internal usage: baton codex-broker --config <path> <prompt>");
            return 64;
        }

        try
        {
            var json = await File.ReadAllTextAsync(args[1], cancellationToken).ConfigureAwait(false);
            var configuration = JsonSerializer.Deserialize<CodexBrokerConfiguration>(json)
                ?? throw new JsonException("Codex broker configuration was null.");
            return await CodexAppServerBroker.RunAsync(
                configuration, args[2], Console.Out, Console.Error, cancellationToken,
                ExecuteMemoryAddAsync).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Could not read Codex broker configuration: {ex.Message}");
            return 1;
        }
    }

    /// <summary>The production host-side executor for the broker's one admitted memory command.</summary>
    internal static async Task<MemoryAddCommandExecution> ExecuteMemoryAddAsync(
        MemoryAddCommandInvocation invocation, CancellationToken cancellationToken)
    {
        var writer = new StringWriter();
        var exitCode = await MemoryAddCommand.ExecuteAsync(
            MemoryAddOptionsParser.Parse([
                "--text", invocation.Text,
                "--kind", invocation.Kind,
                "--repository", invocation.Repository]),
            writer,
            cancellationToken: cancellationToken,
            brokerAuthority: invocation.HostAuthority)
            .ConfigureAwait(false);
        return new MemoryAddCommandExecution(exitCode == 0, writer.ToString());
    }
}
