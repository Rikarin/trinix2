using System.Globalization;

namespace Trinix.Bundle.Tool;

/// <summary>
/// A very small argument parser: <c>--name value</c>, <c>--flag</c>, and
/// positional arguments in order.
/// </summary>
/// <remarks>
/// Unknown options are an error rather than being ignored. A typo in
/// <c>--identity</c> that silently produced an unsigned bundle would be
/// discovered by a user, on a machine, months later.
/// </remarks>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    private readonly List<string> _positional = [];

    private CommandLine() { }

    internal IReadOnlyList<string> Positional => _positional;

    /// <summary>
    /// Parse, given the set of options this verb understands and which of them
    /// take a value.
    /// </summary>
    internal static CommandLine Parse(IReadOnlyList<string> arguments, IReadOnlySet<string> valued, IReadOnlySet<string> flags)
    {
        var result = new CommandLine();

        for (int i = 0; i < arguments.Count; i++)
        {
            string argument = arguments[i];

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                result._positional.Add(argument);
                continue;
            }

            string name = argument[2..];
            string? inline = null;

            int equals = name.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                inline = name[(equals + 1)..];
                name = name[..equals];
            }

            if (flags.Contains(name))
            {
                if (inline is not null) { throw new UsageException($"--{name} does not take a value"); }
                result._options[name] = null;
                continue;
            }

            if (!valued.Contains(name)) { throw new UsageException($"unknown option --{name}"); }

            if (inline is null)
            {
                if (i + 1 >= arguments.Count) { throw new UsageException($"--{name} needs a value"); }
                inline = arguments[++i];
            }
            result._options[name] = inline;
        }

        return result;
    }

    internal bool Has(string name) => _options.ContainsKey(name);

    internal string? Value(string name) => _options.TryGetValue(name, out string? value) ? value : null;

    internal string Required(string name) =>
        Value(name) ?? throw new UsageException($"--{name} is required");

    internal string PositionalAt(int index, string what) =>
        index < _positional.Count
            ? _positional[index]
            : throw new UsageException(string.Create(CultureInfo.InvariantCulture, $"expected {what}"));
}

/// <summary>The user wrote the command wrong. Distinct from the command failing.</summary>
internal sealed class UsageException(string message) : Exception(message);
