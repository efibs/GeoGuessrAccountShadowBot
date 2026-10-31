// SPDX-License-Identifier: GPL-2.0-or-later

namespace GeoGuessrAccountShadowBot.Probe;

/// <summary>
/// Splits the command line into positional values, value options and bare flags.
/// </summary>
internal sealed class ProbeArguments
{
    private static readonly HashSet<string> Flags = ["--allow-write", "--no-heartbeat"];

    private readonly List<string> _positional = [];
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public ProbeArguments(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                _positional.Add(arg);
            }
            else if (Flags.Contains(arg))
            {
                _flags.Add(arg);
            }
            else if (i + 1 < args.Count)
            {
                if (!_options.TryGetValue(arg, out var values))
                {
                    values = [];
                    _options[arg] = values;
                }

                values.Add(args[++i]);
            }
            else
            {
                throw new ArgumentException($"Option {arg} needs a value.");
            }
        }
    }

    public IReadOnlyList<string> Positional => _positional;

    public bool AllowWrite => _flags.Contains("--allow-write");

    public bool NoHeartbeat => _flags.Contains("--no-heartbeat");

    public string? Get(string option) => _options.TryGetValue(option, out var values) ? values[^1] : null;

    public IReadOnlyList<string> GetAll(string option) => _options.TryGetValue(option, out var values) ? values : [];

    public int GetInt(string option, int fallback) => Get(option) is { } value ? int.Parse(value) : fallback;
}
