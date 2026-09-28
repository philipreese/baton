using System.Text.Json;
using Baton.Domain;
using Baton.Status;

namespace Baton.Store;

/// <summary>
/// Reads <c>room.jsonl</c> back into ordered <see cref="RoomEvent"/> lists (#798).
/// Missing required constructor parameters fail replay loudly via <see cref="FlowEventLogReadException"/>.
/// </summary>
public sealed class RoomEventLogReader(string logFilePath) : IRoomEventLogReader
{
    public async Task<IReadOnlyList<RoomEvent>> ReadAllRoomEventsAsync(CancellationToken cancellationToken = default)
    {
        var entries = await ReadAllEntriesAsync(cancellationToken).ConfigureAwait(false);

        var events = new List<RoomEvent>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is LogEntry.RoomLogEntry { Event: not RoomEvent.UnknownRoomEvent } roomLogEntry)
            {
                events.Add(roomLogEntry.Event);
            }
        }

        return events;
    }

    private async Task<IReadOnlyList<LogEntry>> ReadAllEntriesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(logFilePath))
        {
            return [];
        }

        string text;
        await using (var stream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var lastNewline = text.LastIndexOf('\n');
        var completeText = lastNewline >= 0 ? text[..(lastNewline + 1)] : string.Empty;
        var lines = completeText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var entries = new List<LogEntry>(lines.Length);
        foreach (var line in lines)
        {
            LogEntry? entry;
            try
            {
                entry = FlowEventLogJson.DeserializeLine(line);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // NotSupportedException is System.Text.Json's wart, not ours: a polymorphic
                // abstract payload (e.g. EscalationSubject) missing its type discriminator throws
                // NSE where every other parse failure throws JsonException. Both are the same
                // fact -- this line cannot be replayed -- so both wrap into the one loud contract.
                // Proven by RoomEventLogReaderCorruptionTests: the raw NSE escaped before this
                // clause existed.
                throw new FlowEventLogReadException(
                    $"Malformed {BatonPaths.RoomLogFileName} line: {line}", ex);
            }

            if (entry is null)
            {
                throw new FlowEventLogReadException($"Deserialized null LogEntry from line: {line}");
            }

            entries.Add(entry);
        }

        return entries;
    }
}
