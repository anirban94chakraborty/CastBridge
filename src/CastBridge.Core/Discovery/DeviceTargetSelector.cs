namespace CastBridge.Core.Discovery;

using CastBridge.Core.Cast;

/// <summary>
/// Chooses which device the app should control by default.
///
/// A stereo pair shows up as three devices: two members plus the group. The group is what people
/// actually want to reach for, because it is the thing that plays as one unit, and it is the only
/// one whose volume moves both speakers at once. Individual members are still selectable.
/// </summary>
public static class DeviceTargetSelector
{
    /// <summary>Returns the device the popup should bind to on startup.</summary>
    public static CastDevice? ChooseDefault(IReadOnlyList<CastDevice> devices)
    {
        if (devices.Count == 0)
            return null;

        // A group beats an individual speaker every time.
        var group = devices.FirstOrDefault(IsGroup);
        if (group is not null)
            return group;

        // Otherwise prefer a named speaker over a TV or display.
        return devices.FirstOrDefault(d => d.Kind == CastDeviceKind.Speaker)
            ?? devices.FirstOrDefault(d => d.Kind == CastDeviceKind.Display)
            ?? devices[0];
    }

    /// <summary>A multi-room or multichannel group, which is what casts as one unit.</summary>
    public static bool IsGroup(CastDevice device)
    {
        if (device.Kind == CastDeviceKind.Group)
            return true;

        // Some devices are only classified as a group by their advertised receiver name.
        return device.ExtraInfo.TryGetValue("md", out var model) &&
               model.Contains("group", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The other speakers that belong to the same group, by shared address or name.</summary>
    public static IReadOnlyList<CastDevice> MembersOf(CastDevice group, IReadOnlyList<CastDevice> all)
    {
        if (!IsGroup(group))
            return Array.Empty<CastDevice>();

        // A stereo pair's members share the group's IP address on a different Cast port.
        return all
            .Where(d => !d.Id.Equals(group.Id, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.IpAddress == group.IpAddress)
            .ToArray();
    }
}