using Microsoft.Win32;

namespace FanControl.MinisforumUM780XTX;

internal sealed record HostIdentitySnapshot(
    string Product,
    string Board,
    string BoardVersion,
    string BiosVersion,
    int EmbeddedControllerMajorVersion,
    int EmbeddedControllerMinorVersion);

internal static class HostIdentity
{
    private const string BiosKey =
        @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS";

    internal static HostIdentitySnapshot Read() => new(
        ReadString("SystemProductName"),
        ReadString("BaseBoardProduct"),
        ReadString("BaseBoardVersion"),
        ReadString("BIOSVersion"),
        ReadInteger("ECFirmwareMajorRelease"),
        ReadInteger("ECFirmwareMinorRelease"));

    private static string ReadString(string name) => Convert.ToString(
        Registry.GetValue(BiosKey, name, null))?.Trim() ?? string.Empty;

    private static int ReadInteger(string name)
    {
        object? value = Registry.GetValue(BiosKey, name, null);
        return value is null ? -1 : Convert.ToInt32(value);
    }
}

internal static class HostIdentityGate
{
    internal static void Assert(HostIdentitySnapshot identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!string.Equals(identity.Product, F7bsdProfile.Product, StringComparison.Ordinal) ||
            !string.Equals(identity.Board, F7bsdProfile.Board, StringComparison.Ordinal) ||
            !string.Equals(
                identity.BoardVersion,
                F7bsdProfile.BoardVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                identity.BiosVersion,
                F7bsdProfile.BiosVersion,
                StringComparison.Ordinal) ||
            identity.EmbeddedControllerMajorVersion !=
                F7bsdProfile.EmbeddedControllerMajorVersion ||
            identity.EmbeddedControllerMinorVersion !=
                F7bsdProfile.EmbeddedControllerMinorVersion)
        {
            throw new PlatformNotSupportedException(
                $"Expected {F7bsdProfile.Product}/{F7bsdProfile.Board} " +
                $"revision {F7bsdProfile.BoardVersion}, BIOS " +
                $"{F7bsdProfile.BiosVersion}, EC " +
                $"{F7bsdProfile.EmbeddedControllerMajorVersion}." +
                $"{F7bsdProfile.EmbeddedControllerMinorVersion}; found " +
                $"{identity.Product}/{identity.Board} revision " +
                $"{identity.BoardVersion}, BIOS {identity.BiosVersion}, EC " +
                $"{identity.EmbeddedControllerMajorVersion}." +
                $"{identity.EmbeddedControllerMinorVersion}.");
        }
    }
}
