using CastBridge.Core.Cast;
using CastBridge.Media.Compatibility;

namespace CastBridge.Cli;

internal sealed partial class Cli
{
    /// <summary>
    /// Reports whether each speaker can actually play audio from this PC. This is the command that
    /// replaces guessing: it casts a second of near-silence and watches whether the bytes come back.
    /// </summary>
    private async Task<int> CheckAsync()
    {
        var devices = _devices.Devices;
        if (devices.Count == 0)
        {
            Console.WriteLine("No devices found.");
            return 1;
        }

        var force = _options.Force;
        var named = _options.Args.FirstOrDefault();
        var targets = named is null
            ? devices
            : Resolve(named) is { } single ? new[] { single } : Array.Empty<CastDevice>();

        if (targets.Count == 0)
            return 1;

        Console.WriteLine();
        Console.WriteLine($"Testing {targets.Count} device(s). This briefly starts playback on each one, then stops it.");
        Console.WriteLine(new string('-', 88));

        var ready = 0;
        var interactive = !Console.IsOutputRedirected;

        foreach (var device in targets)
        {
            // An in-place "testing…" only makes sense on a terminal; when the output is piped into
            // a file or another program, a carriage return would just leave both lines on screen.
            if (interactive)
                Console.Write($"  {device.Name,-24} testing…" + new string(' ', 30));

            var result = await _probe.ProbeAsync(device, force);
            if (result.CanPlay)
                ready++;

            if (interactive)
                Console.Write("\r");

            Console.WriteLine($"  {device.Name,-24} {CompatibilityResult.Describe(result.Verdict),-42} {(result.CanPlay ? "yes" : "no")}");
            Console.WriteLine($"      {result.Detail}");
        }

        Console.WriteLine();
        Console.WriteLine($"{ready} of {targets.Count} device(s) can play audio from this PC.");
        return ready > 0 ? 0 : 1;
    }
}