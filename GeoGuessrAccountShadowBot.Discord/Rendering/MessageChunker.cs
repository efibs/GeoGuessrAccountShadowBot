// SPDX-License-Identifier: GPL-2.0-or-later

namespace GeoGuessrAccountShadowBot.Discord.Rendering;

/// <summary>
/// Splits long text into Discord messages. It runs after escaping, so it must not cut between a
/// backslash and the character it protects, or inside a surrogate pair.
/// </summary>
public static class MessageChunker
{
    public const int DefaultLimit = 1900;

    public static IReadOnlyList<string> Chunk(string text, int limit = DefaultLimit)
    {
        var chunks = new List<string>();
        var remaining = text.AsSpan();

        while (remaining.Length > limit)
        {
            var cut = FindCut(remaining, limit);
            chunks.Add(remaining[..cut].ToString());
            remaining = remaining[cut..].TrimStart('\n');
        }

        if (remaining.Length > 0 || chunks.Count == 0)
        {
            chunks.Add(remaining.ToString());
        }

        return chunks;
    }

    private static int FindCut(ReadOnlySpan<char> text, int limit)
    {
        var lineBreak = text[..limit].LastIndexOf('\n');
        if (lineBreak > limit / 2)
        {
            return lineBreak;
        }

        var cut = limit;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        // An odd number of backslashes means the last one escapes the character after the cut.
        var backslashes = 0;
        while (cut - backslashes > 0 && text[cut - backslashes - 1] == '\\')
        {
            backslashes++;
        }

        return backslashes % 2 == 1 ? cut - 1 : cut;
    }
}
