using System.Runtime.Versioning;

namespace RemoteCommanderTray.UI;

/// <summary>
/// The one window this app has, and it only opens on request.
/// </summary>
/// <remarks>
/// Built in code rather than with a designer file so the whole UI stays reviewable as
/// plain C#.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class AboutForm : Form
{
    private const string ProjectUrl = "https://github.com/PeterShanxin/Remote-Commander-Tray";
    private const string UpstreamUrl = "https://github.com/wonderwhy-er/DesktopCommanderMCP";

    public AboutForm(string version, Icon icon, string dataFolder)
    {
        Text = "About Remote Commander Tray";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(430, 260);
        Padding = new Padding(16);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
        };

        layout.Controls.Add(new Label
        {
            Text = "Remote Commander Tray",
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        });

        layout.Controls.Add(new Label
        {
            Text = $"Version {version}   -   MIT licensed",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12),
        });

        layout.Controls.Add(new Label
        {
            Text = "A tray companion that runs and supervises the official Desktop "
                   + "Commander Remote Device. It never handles sign-in tokens: "
                   + "authentication, device identity and the Remote MCP connection all "
                   + "stay with the official CLI.",
            AutoSize = true,
            MaximumSize = new Size(390, 0),
            Margin = new Padding(0, 0, 0, 12),
        });

        layout.Controls.Add(CreateLink("Project page", ProjectUrl));
        layout.Controls.Add(CreateLink("Desktop Commander (upstream)", UpstreamUrl));

        var folderLink = new LinkLabel
        {
            Text = $"Data folder: {dataFolder}",
            AutoSize = true,
            MaximumSize = new Size(390, 0),
            Margin = new Padding(0, 8, 0, 12),
        };
        folderLink.LinkClicked += (_, _) => Shell.RevealInExplorer(dataFolder);
        layout.Controls.Add(folderLink);

        var close = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
        };
        layout.Controls.Add(close);

        Controls.Add(layout);
        AcceptButton = close;
        CancelButton = close;
    }

    private static LinkLabel CreateLink(string text, string url)
    {
        var link = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 2),
        };
        link.LinkClicked += (_, _) => Shell.OpenUrl(url);
        return link;
    }
}
