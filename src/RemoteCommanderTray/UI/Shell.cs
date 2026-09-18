using System.Diagnostics;
using System.Runtime.Versioning;

namespace RemoteCommanderTray.UI;

/// <summary>Thin wrappers over "ask Windows to open this", with their failures swallowed.</summary>
[SupportedOSPlatform("windows")]
internal static class Shell
{
    /// <summary>Opens an http(s) URL in the default browser. Anything else is ignored.</summary>
    public static bool OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        return Launch(uri.AbsoluteUri);
    }

    /// <summary>Opens a local file with its default handler.</summary>
    public static bool OpenFile(string path)
        => File.Exists(path) && Launch(path);

    /// <summary>Opens Explorer with the file selected, falling back to the folder.</summary>
    public static bool RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true,
                })?.Dispose();
                return true;
            }

            var folder = Path.GetDirectoryName(path);
            return folder is not null && Directory.Exists(folder) && Launch(folder);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static bool Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
