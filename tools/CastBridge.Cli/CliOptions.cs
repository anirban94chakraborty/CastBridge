namespace CastBridge.Cli;

/// <summary>
/// Command line arguments, parsed up front so every command sees the same view of them.
/// Everything is optional except the command itself, so a missing switch is never fatal.
/// </summary>
internal sealed record CliOptions
{
    public string Command { get; private init; } = "help";

    public string[] Args { get; private init; } = Array.Empty<string>();

    public bool Verbose { get; private init; }

    public bool NoMdns { get; private init; }

    public bool NoSubnet { get; private init; }

    public bool MdnsOnly { get; private init; }

    public bool SubnetOnly { get; private init; }

    public int MediaPort { get; private init; } = 45455;

    public int ScanSeconds { get; private init; } = 6;

    public string? Ffmpeg { get; private init; }

    public string? LiveDevice { get; private init; }

    public string? LiveQuality { get; private init; }

    /// <summary>Seconds the media server stays alive after a cast, so playback can actually finish.</summary>
    public int Hold { get; private init; }

    public string? Target { get; private init; }

    /// <summary>Attempts the cast even when the device advertises that it cannot play local files.</summary>
    public bool Force { get; private init; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        var positional = new List<string>();

        foreach (var arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            options = arg switch
            {
                "--verbose" or "-v" => options with { Verbose = true },
                "--no-mdns" => options with { NoMdns = true },
                "--no-subnet" => options with { NoSubnet = true },
                "--mdns" => options with { MdnsOnly = true },
                "--subnet" => options with { SubnetOnly = true },
                _ => ApplyValue(arg, options),
            };
        }

        return options with
        {
            Command = positional.Count > 0 ? positional[0].ToLowerInvariant() : "help",
            Args = positional.Skip(1).ToArray(),
        };
    }

    /// <summary>Handles the <c>--name=value</c> switches; unknown ones are ignored on purpose.</summary>
    private static CliOptions ApplyValue(string arg, CliOptions options)
    {
        var separator = arg.IndexOf('=');
        if (separator < 0)
            return options;

        var name = arg[..separator];
        var value = arg[(separator + 1)..];

        return name switch
        {
            "--port" when int.TryParse(value, out var port) => options with { MediaPort = port },
            "--scan-timeout" when int.TryParse(value, out var seconds) => options with { ScanSeconds = seconds },
            "--ffmpeg" => options with { Ffmpeg = value },
            "--live-device" => options with { LiveDevice = value },
            "--live-quality" => options with { LiveQuality = value },
            "--hold" when int.TryParse(value, out var hold) => options with { Hold = hold },                "--target" => options with { Target = value },
                "--force" => options with { Force = true },
            _ => options,
        };
    }
}
