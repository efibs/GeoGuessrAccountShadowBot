// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text.Json;

namespace GeoGuessrAccountShadowBot.Probe;

/// <summary>
/// Appends everything a run observes to an NDJSON file under the gitignored <c>captures</c> folder.
/// Captures hold real message text and user ids, so they stay local until scrubbed.
/// </summary>
internal sealed class CaptureWriter : IDisposable
{
    private readonly StreamWriter _writer;

    public CaptureWriter(string name)
    {
        Directory.CreateDirectory(CaptureDirectory);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        // The id keeps two runs within the same second from overwriting each other.
        Path = System.IO.Path.Combine(CaptureDirectory, $"{stamp}-{name}-{Guid.NewGuid():N}.ndjson");
        _writer = new StreamWriter(Path) { AutoFlush = true };
    }

    public static string CaptureDirectory => System.IO.Path.Combine(Environment.CurrentDirectory, "captures");

    public string Path { get; }

    public void Write(string direction, string text)
    {
        var line = new CaptureLine(DateTimeOffset.UtcNow, direction, text);
        _writer.WriteLine(JsonSerializer.Serialize(line, CaptureLine.SerializerOptions));
    }

    public void Dispose() => _writer.Dispose();
}

/// <summary>
/// One observed frame, or a <c>meta</c> note about the connection.
/// </summary>
internal sealed record CaptureLine(DateTimeOffset Time, string Direction, string Text)
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
}
