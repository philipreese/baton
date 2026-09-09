using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace Baton.Cli.Daemon;

/// <summary>
/// The static, same-origin install assets for the private Fleet Glass listener. They contain no
/// fleet data: the live projection continues to be read only from <c>/projection.json</c>.
/// </summary>
internal static class GlassWebAppAssets
{
    internal const string ManifestPath = "/fleet-glass.webmanifest";
    internal const string Icon192Path = "/icons/fleet-glass-192.png";
    internal const string Icon512Path = "/icons/fleet-glass-512.png";
    internal const string ServiceWorkerPath = "/fleet-glass-service-worker.js";

    private const string ManifestResourceName = "Baton.Cli.Daemon.fleet-glass.webmanifest";
    private const string ServiceWorkerResourceName = "Baton.Cli.Daemon.service-worker.js";

    private static readonly byte[] Icon192 = CreatePng(192);
    private static readonly byte[] Icon512 = CreatePng(512);
    private static string? _manifest;
    private static string? _serviceWorker;

    internal static string Manifest()
    {
        if (_manifest is not null)
        {
            return _manifest;
        }

        using var stream = typeof(GlassWebAppAssets).Assembly.GetManifestResourceStream(ManifestResourceName)
            ?? throw new InvalidOperationException(
                $"The Fleet Glass manifest resource '{ManifestResourceName}' is missing from " +
                $"{Assembly.GetExecutingAssembly().GetName().Name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        _manifest = reader.ReadToEnd();
        return _manifest;
    }

    internal static ReadOnlyMemory<byte> Icon(int size) => size switch
    {
        192 => Icon192,
        512 => Icon512,
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

    internal static string ServiceWorker()
    {
        if (_serviceWorker is not null)
        {
            return _serviceWorker;
        }

        using var stream = typeof(GlassWebAppAssets).Assembly.GetManifestResourceStream(ServiceWorkerResourceName)
            ?? throw new InvalidOperationException(
                $"The Fleet Glass service worker resource '{ServiceWorkerResourceName}' is missing from " +
                $"{Assembly.GetExecutingAssembly().GetName().Name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        _serviceWorker = reader.ReadToEnd();
        return _serviceWorker;
    }

    /// <summary>
    /// Produces a compact opaque RGBA PNG. Keeping these branded, dimension-specific raster bytes in
    /// the installed assembly avoids a second deploy location while keeping their manifest URLs
    /// ordinary same-origin PNG resources.
    /// </summary>
    private static byte[] CreatePng(int size)
    {
        var pixels = new byte[size * ((size * 4) + 1)];
        var edge = size * 3 / 16;
        var paneStart = size * 5 / 16;
        var paneEnd = size * 11 / 16;

        for (var y = 0; y < size; y++)
        {
            var row = y * ((size * 4) + 1);
            for (var x = 0; x < size; x++)
            {
                var offset = row + 1 + (x * 4);
                var inGlass = x >= edge && x < size - edge && y >= edge && y < size - edge;
                var inPane = x >= paneStart && x < paneEnd && y >= paneStart && y < paneEnd;
                pixels[offset] = inPane ? (byte)250 : inGlass ? (byte)96 : (byte)35;
                pixels[offset + 1] = inPane ? (byte)250 : inGlass ? (byte)213 : (byte)40;
                pixels[offset + 2] = inPane ? (byte)248 : inGlass ? (byte)208 : (byte)48;
                pixels[offset + 3] = byte.MaxValue;
            }
        }

        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, size);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var compressor = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            compressor.Write(pixels);
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = uint.MaxValue;
        foreach (var value in type.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0xEDB88320U);
            }
        }

        return ~crc;
    }
}
