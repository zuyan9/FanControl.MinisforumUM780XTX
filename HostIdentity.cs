using Microsoft.Win32;

namespace FanControl.MinisforumUM780XTX;

internal static class HostIdentity
{
    private const string BiosKey =
        @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS";

    internal static void AssertSupported()
    {
        string product = ReadString("SystemProductName");
        string board = ReadString("BaseBoardProduct");
        string boardVersion = ReadString("BaseBoardVersion");
        string biosVersion = ReadString("BIOSVersion");
        int ecMajor = ReadInteger("ECFirmwareMajorRelease");
        int ecMinor = ReadInteger("ECFirmwareMinorRelease");

        if (!string.Equals(product, F7bsdProfile.Product, StringComparison.Ordinal) ||
            !string.Equals(board, F7bsdProfile.Board, StringComparison.Ordinal) ||
            !string.Equals(
                boardVersion,
                F7bsdProfile.BoardVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                biosVersion,
                F7bsdProfile.BiosVersion,
                StringComparison.Ordinal) ||
            ecMajor != F7bsdProfile.EmbeddedControllerMajorVersion ||
            ecMinor != F7bsdProfile.EmbeddedControllerMinorVersion)
        {
            throw new PlatformNotSupportedException(
                $"Expected {F7bsdProfile.Product}/{F7bsdProfile.Board} revision " +
                $"{F7bsdProfile.BoardVersion}, BIOS {F7bsdProfile.BiosVersion}, EC " +
                $"{F7bsdProfile.EmbeddedControllerMajorVersion}." +
                $"{F7bsdProfile.EmbeddedControllerMinorVersion}; found " +
                $"{product}/{board} revision {boardVersion}, BIOS {biosVersion}, " +
                $"EC {ecMajor}.{ecMinor}.");
        }
    }

    private static string ReadString(string name) => Convert.ToString(
        Registry.GetValue(BiosKey, name, null))?.Trim() ?? string.Empty;

    private static int ReadInteger(string name)
    {
        object? value = Registry.GetValue(BiosKey, name, null);
        return value is null ? -1 : Convert.ToInt32(value);
    }
}
