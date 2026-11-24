using System.Runtime.InteropServices;

namespace RemoteCommanderTray.Windows;

/// <summary>Reads Windows display-language preferences without requiring .NET globalization.</summary>
internal static class WindowsUiLanguages
{
    private const uint LanguageName = 0x8;
    private const uint MaxBufferLength = 32_768;

    public static IReadOnlyList<string> GetPreferred()
    {
        uint length = 0;
        if (!GetUserPreferredUILanguages(LanguageName, out _, null, ref length) ||
            length is < 2 or > MaxBufferLength)
        {
            return Array.Empty<string>();
        }

        var buffer = new char[length];
        if (!GetUserPreferredUILanguages(LanguageName, out _, buffer, ref length))
        {
            return Array.Empty<string>();
        }

        return new string(buffer, 0, (int)length).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserPreferredUILanguages(
        uint flags, out uint languageCount, [Out] char[]? languagesBuffer, ref uint bufferLength);
}
