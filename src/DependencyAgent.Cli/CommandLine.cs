namespace DependencyAgent.Cli;

/// <summary>Tiny, dependency-free argument parser: <c>command --name value --flag</c>.</summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string?> options = new(StringComparer.OrdinalIgnoreCase);

    private CommandLine(string command)
    {
        Command = command;
    }

    public string Command { get; }

    public static CommandLine Parse(IReadOnlyList<string> args, IReadOnlySet<string> flags)
    {
        if (args.Count == 0 || args[0].StartsWith('-'))
        {
            return new CommandLine(args.Count > 0 && args[0] is "--help" or "-h" ? "help" : "help");
        }

        var result = new CommandLine(args[0].ToLowerInvariant());
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{arg}'.");
            }

            var name = arg[2..];
            string? value = null;
            var eq = name.IndexOf('=');
            if (eq >= 0)
            {
                value = name[(eq + 1)..];
                name = name[..eq];
            }
            else if (!flags.Contains(name))
            {
                if (i + 1 >= args.Count)
                {
                    throw new ArgumentException($"Option --{name} needs a value.");
                }

                value = args[++i];
            }

            result.options[name] = value ?? "true";
        }

        return result;
    }

    public string? Get(string name) => options.TryGetValue(name, out var v) ? v : null;

    public string Get(string name, string fallback) => Get(name) ?? fallback;

    public string Require(string name) => Get(name) ?? throw new ArgumentException($"Missing required option --{name}.");

    public bool Flag(string name) => Get(name) is { } v && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
}
