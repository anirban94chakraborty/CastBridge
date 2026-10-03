using CastBridge.Core.Cast;

namespace CastBridge.Cli;

/// <summary>
/// Receiver app probing.
///
/// Chromium picks a receiver app per source: YouTube and Spotify have their own, and only generic
/// HTTP media goes to the Default Media Receiver. These commands exist to find out which of those a
/// given speaker will actually launch, which is the difference between "casting is broken" and
/// "this speaker only accepts some kinds of content".
/// </summary>
internal sealed partial class Cli
{
    private static readonly (string Id, string Name)[] KnownReceivers =
    [
        (DefaultMediaReceiverAppId, "Default Media Receiver"),
        ("233637DE", "YouTube"),
        ("1DB65F1D", "Spotify"),
        ("C3DE6BC2", "Google Play Music"),
        ("A41B766D", "Google Photos"),
    ];

    private const string DefaultMediaReceiverAppId = "CC1AD845";

    /// <summary>Tries each known receiver app on one device and reports which ones launch.</summary>
    private async Task<int> ReceiversAsync()
    {
        var devices = _devices.Devices;
        if (devices.Count == 0)
        {
            Console.WriteLine("No devices found.");
            return 1;
        }

        var device = Resolve(_options.Args.FirstOrDefault() ?? devices[0].Name);
        if (device is null)
            return 1;

        Console.WriteLine();
        Console.WriteLine($"Testing which receiver apps {device.Name} will launch...");
        Console.WriteLine(new string('-', 78));

        var client = await _devices.GetClientAsync(device.Id);
        var launched = new List<string>();

        foreach (var (id, name) in KnownReceivers)
        {
            Console.Write($"  {id}  {name,-28} ");

            try
            {
                var snapshot = await client.LaunchReceiverAppAsync(id);
                Console.WriteLine($"running (session {Short(snapshot.SessionId)})");
                launched.Add(id);

                // Leave the device as we found it, so the next candidate starts from a clean state.
                await client.StopReceiverAppAsync();
                await Task.Delay(1500);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message.Contains("could not be started", StringComparison.OrdinalIgnoreCase)
                    ? "refused"
                    : $"failed: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(launched.Count == 0
            ? $"{device.Name} will launch no known receiver app."
            : $"{device.Name} will launch: {string.Join(", ", launched)}");

        return launched.Count > 0 ? 0 : 1;
    }

    /// <summary>Launches one app by id, for trying an id that is not in the built-in list.</summary>
    private async Task<int> LaunchAsync()
    {
        if (_options.Args.Length < 2)
        {
            Console.WriteLine("usage: launch <device> <app-id>");
            return 1;
        }

        var device = Resolve(_options.Args[0]);
        if (device is null)
            return 1;

        var client = await _devices.GetClientAsync(device.Id);
        var snapshot = await client.LaunchReceiverAppAsync(_options.Args[1]);

        Console.WriteLine($"{device.Name}: {(_options.Args[1])} is running (session {Short(snapshot.SessionId)}).");
        Console.WriteLine("It stays running until you stop it.");
        return 0;
    }

    private static string Short(string? sessionId) =>
        string.IsNullOrEmpty(sessionId) ? "-" : sessionId.Length <= 8 ? sessionId : sessionId[..8];
}