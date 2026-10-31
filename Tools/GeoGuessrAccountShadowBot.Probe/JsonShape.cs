// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GeoGuessrAccountShadowBot.Probe;

/// <summary>
/// Describes a JSON document by its keys and value types, never its values, so the structure of a
/// response can be read on the console without printing anyone's data.
/// </summary>
internal static partial class JsonShape
{
    private const int MaxDepth = 6;

    public static string Describe(string json)
    {
        using var document = JsonDocument.Parse(json);
        var builder = new StringBuilder();
        Append(builder, document.RootElement, 0);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, JsonElement element, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object when depth >= MaxDepth:
                builder.Append("{…}");
                break;
            case JsonValueKind.Object:
                builder.Append("{ ");
                foreach (var property in element.EnumerateObject())
                {
                    // Dictionaries keyed by user id would otherwise print one entry per id.
                    builder.Append(IdLikeKeyRegex().IsMatch(property.Name) ? "<id>" : property.Name).Append(": ");
                    Append(builder, property.Value, depth + 1);
                    builder.Append(", ");
                    if (IdLikeKeyRegex().IsMatch(property.Name))
                    {
                        break;
                    }
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                var length = element.GetArrayLength();
                builder.Append('[').Append(length).Append("]");
                if (length > 0)
                {
                    Append(builder, element[0], depth + 1);
                }

                break;
            default:
                builder.Append(element.ValueKind.ToString().ToLowerInvariant());
                break;
        }
    }

    [GeneratedRegex("^[0-9a-f]{24}$")]
    private static partial Regex IdLikeKeyRegex();
}
