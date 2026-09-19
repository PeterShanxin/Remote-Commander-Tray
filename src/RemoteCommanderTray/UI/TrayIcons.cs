using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteCommanderTray.Core;

namespace RemoteCommanderTray.UI;

/// <summary>
/// Draws the tray status icons at runtime.
/// </summary>
/// <remarks>
/// Each state gets a distinct <em>shape</em>, not just a distinct colour - a check, a row
/// of dots, a key, a cross, a pause bar - so the status is readable for colour-blind
/// users and against any taskbar theme. Drawing them rather than shipping a sprite sheet
/// also means they come out crisp at whatever size the current DPI asks for.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class TrayIcons : IDisposable
{
    private static readonly Color Green = Color.FromArgb(0x16, 0xA3, 0x4A);
    private static readonly Color Amber = Color.FromArgb(0xD9, 0x77, 0x06);
    private static readonly Color Violet = Color.FromArgb(0x7C, 0x3A, 0xED);
    private static readonly Color Red = Color.FromArgb(0xDC, 0x26, 0x26);
    private static readonly Color Gray = Color.FromArgb(0x6B, 0x72, 0x80);

    private readonly Dictionary<(AgentState State, int Size), Icon> _cache = [];
    private bool _disposed;

    /// <summary>Returns the icon for a state, drawn at the current small-icon size.</summary>
    public Icon Get(AgentState state)
    {
        var size = Math.Clamp(SystemInformation.SmallIconSize.Width, 16, 64);
        var key = (state, size);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var icon = Render(state, size);
        _cache[key] = icon;
        return icon;
    }

    private static Icon Render(AgentState state, int size)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            using var disc = new SolidBrush(BackgroundFor(state));
            g.FillEllipse(disc, 0.5f, 0.5f, size - 1.5f, size - 1.5f);

            var stroke = Math.Max(1.4f, size * 0.12f);
            using var pen = new Pen(Color.White, stroke)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };

            DrawGlyph(g, pen, state, size);
        }

        return FromBitmap(bitmap);
    }

    private static Color BackgroundFor(AgentState state) => state switch
    {
        AgentState.Online => Green,
        AgentState.Connecting or AgentState.Starting => Amber,
        AgentState.AuthenticationRequired => Violet,
        AgentState.Error => Red,
        _ => Gray,
    };

    private static void DrawGlyph(Graphics g, Pen pen, AgentState state, int size)
    {
        float P(double fraction) => (float)(fraction * size);

        switch (state)
        {
            case AgentState.Online:
                // Check mark.
                g.DrawLines(pen, [
                    new PointF(P(0.27), P(0.52)),
                    new PointF(P(0.43), P(0.69)),
                    new PointF(P(0.75), P(0.33)),
                ]);
                break;

            case AgentState.Starting:
            case AgentState.Connecting:
                // Three dots: "working on it".
                using (var dots = new SolidBrush(Color.White))
                {
                    var radius = P(0.085);
                    foreach (var x in new[] { 0.27, 0.5, 0.73 })
                    {
                        g.FillEllipse(dots, P(x) - radius, P(0.5) - radius, radius * 2, radius * 2);
                    }
                }

                break;

            case AgentState.AuthenticationRequired:
                // Key: ring plus a toothed shaft.
                var ringSize = P(0.30);
                g.DrawEllipse(pen, P(0.22), P(0.48), ringSize, ringSize);
                g.DrawLine(pen, P(0.50), P(0.52), P(0.76), P(0.26));
                g.DrawLine(pen, P(0.62), P(0.40), P(0.70), P(0.48));
                break;

            case AgentState.Error:
                g.DrawLine(pen, P(0.32), P(0.32), P(0.68), P(0.68));
                g.DrawLine(pen, P(0.68), P(0.32), P(0.32), P(0.68));
                break;

            default:
                // Stopped: pause bars.
                g.DrawLine(pen, P(0.38), P(0.30), P(0.38), P(0.70));
                g.DrawLine(pen, P(0.62), P(0.30), P(0.62), P(0.70));
                break;
        }
    }

    /// <summary>
    /// <see cref="Bitmap.GetHicon"/> hands back an unmanaged HICON that the caller owns,
    /// so the managed copy is cloned and the handle destroyed straight away.
    /// </summary>
    private static Icon FromBitmap(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var icon in _cache.Values)
        {
            icon.Dispose();
        }

        _cache.Clear();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
