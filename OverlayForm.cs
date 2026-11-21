using System.Drawing;
using System.Windows.Forms;

namespace TachoClicker;

internal sealed class OverlaySnapshot
{
    public bool Running { get; init; }
    public int ActiveProfile { get; init; }
    public int ActiveTachoIndex { get; init; }
    public int TotalTachos { get; init; }
    public string AllOffsetsSummary { get; init; } = "";

    public int CenterX { get; init; }
    public int CenterY { get; init; }
    public bool IsLastTacho { get; init; }
    public DetectorState State { get; init; }
    public int GreenCount { get; init; }
    public int WhiteCount { get; init; }
    public double ClickOffsetMs { get; init; }
    public double SpeedDegPerSec { get; init; }
    public int CheckpointCount { get; init; }
    public Bitmap? ActivePreview { get; init; }

    // Ergebnis des letzten Klicks (Tacho-Nummer, Farbe auf den unteren Pixeln, Korrektur)
    public int LastClickedTachoNumber { get; init; }
    public TachoFeedback LastFeedback { get; init; }
    public int PatchRed { get; init; }
    public int PatchGreen { get; init; }
    public int PatchBlue { get; init; }
    public int PatchWhite { get; init; }
    public double LastDeltaMs { get; init; }
    public double LastResultNewOffsetMs { get; init; }

    public Bitmap? BlueButtonPreview { get; init; }
    public Bitmap? GreenButtonPreview { get; init; }
}

internal sealed class OverlayForm : Form
{
    private readonly object _lock = new();
    private OverlaySnapshot? _snapshot;

    public OverlayForm()
    {
        Text = "TachoClicker Live-Inspektor (Nur aktiver Tacho)";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(40, 40);
        ClientSize = new Size(370, 395);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(24, 26, 30);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        TopMost = true;
        BringToFront();
        Activate();
    }

    public void EnsureVisibleOnTop()
    {
        if (!IsHandleCreated || IsDisposed)
            return;

        BeginInvoke(() =>
        {
            if (IsDisposed) return;
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Visible = true;
            TopMost = false;
            TopMost = true;
            BringToFront();
        });
    }

    public void UpdateSnapshot(OverlaySnapshot snapshot)
    {
        OverlaySnapshot? old;
        lock (_lock)
        {
            old = _snapshot;
            _snapshot = snapshot;
        }

        DisposeBitmaps(old);

        if (!IsHandleCreated || IsDisposed)
            return;

        BeginInvoke(() =>
        {
            if (IsDisposed) return;
            if (!Visible) Visible = true;
            Invalidate();
        });
    }

    private static void DisposeBitmaps(OverlaySnapshot? snap)
    {
        if (snap == null) return;
        snap.ActivePreview?.Dispose();
        snap.BlueButtonPreview?.Dispose();
        snap.GreenButtonPreview?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);

        using var titleFont = new Font("Consolas", 10.0f, FontStyle.Bold);
        using var badgeFont = new Font("Consolas", 9.5f, FontStyle.Bold);
        using var infoFont = new Font("Consolas", 8.5f, FontStyle.Regular);
        using var borderWaiting = new Pen(Color.Gold, 2);
        using var borderArmed = new Pen(Color.Cyan, 3);
        using var borderCooldown = new Pen(Color.Gray, 2);
        using var crossPen = new Pen(Color.Red, 2);

        using var redBadgeBg = new SolidBrush(Color.FromArgb(200, 35, 35));
        using var greenBadgeBg = new SolidBrush(Color.FromArgb(25, 145, 55));
        using var blueBadgeBg = new SolidBrush(Color.FromArgb(20, 115, 210));
        using var whiteBadgeBg = new SolidBrush(Color.FromArgb(145, 150, 160));
        using var neutralBadgeBg = new SolidBrush(Color.FromArgb(45, 48, 55));

        OverlaySnapshot? snap;
        lock (_lock)
        {
            snap = _snapshot;
            if (snap == null)
                return;

            string statusText = snap.Running
                ? $"AKTIV: Tacho {snap.ActiveTachoIndex + 1}/{snap.TotalTachos}"
                : $"PAUSIERT (F8) | Vorschau T{snap.ActiveTachoIndex + 1}/{snap.TotalTachos}";
            Brush statusBrush = snap.Running ? Brushes.LimeGreen : Brushes.Orange;

            g.DrawString($"{statusText}  ({snap.ActiveProfile}er-Modus)", titleFont, statusBrush, 12, 8);
            g.DrawString($"Offsets: {snap.AllOffsetsSummary}", infoFont, Brushes.LightGray, 12, 26);

            // Nur der aktuell aktive Tacho wird gross angezeigt
            int tileSize = 210;
            int x = 12;
            int y = 48;

            var pen = snap.State switch
            {
                DetectorState.Armed => borderArmed,
                DetectorState.Cooldown => borderCooldown,
                _ => borderWaiting
            };

            if (snap.ActivePreview != null)
            {
                g.DrawImage(snap.ActivePreview, new Rectangle(x, y, tileSize, tileSize));
            }
            else
            {
                g.FillRectangle(Brushes.Black, x, y, tileSize, tileSize);
                g.DrawString("Nicht gesetzt\n(F9 druecken)", infoFont, Brushes.Gray, x + 45, y + 85);
            }

            g.DrawRectangle(pen, x, y, tileSize, tileSize);

            string btnName = snap.IsLastTacho ? "-> GRUEN" : "-> BLAU";
            g.DrawString($"Tacho {snap.ActiveTachoIndex + 1} ({btnName})  [{snap.ClickOffsetMs:F1} ms]", titleFont, Brushes.White, x, y + tileSize + 6);
            g.DrawString($"Pos: ({snap.CenterX}, {snap.CenterY}) | Gruen: {snap.GreenCount} | Weiss: {snap.WhiteCount}", infoFont, Brushes.Silver, x, y + tileSize + 24);
            g.DrawString(
                snap.SpeedDegPerSec > 10
                    ? $"Speed: {snap.SpeedDegPerSec:F0}°/s (n={snap.CheckpointCount} Messpunkte)"
                    : $"Orange Box unten = Messpunkt nach Klick ({snap.State})",
                infoFont, snap.SpeedDegPerSec > 10 ? Brushes.Cyan : Brushes.Silver, x, y + tileSize + 40);

            // Button-Vorschauen rechts neben dem aktiven Tacho
            int btnX = x + tileSize + 18;
            int btnSize = 92;

            DrawButtonPreview(g, snap.BlueButtonPreview, "Blau (F10)", btnX, y, btnSize, Brushes.DeepSkyBlue, infoFont, crossPen);
            DrawButtonPreview(g, snap.GreenButtonPreview, "Gruen (F6)", btnX, y + btnSize + 26, btnSize, Brushes.LimeGreen, infoFont, crossPen);

            // Farb-Box fuer die direkt nach dem letzten Klick gemessenen unteren Tacho-Pixel
            int badgeY = y + tileSize + 62;
            int badgeW = ClientSize.Width - 24;
            string counts = $"B={snap.PatchBlue} G={snap.PatchGreen} R={snap.PatchRed} W={snap.PatchWhite} (von 81 px)";
            string tLabel = snap.LastClickedTachoNumber > 0 ? $"T{snap.LastClickedTachoNumber}" : "-";

            var (badgeBrush, line1, line2) = snap.LastFeedback switch
            {
                TachoFeedback.OptimalBlue => (
                    blueBadgeBg,
                    $"{tLabel} nach Klick: BLAU (OPTIMAL!)",
                    $"Offset bleibt {snap.LastResultNewOffsetMs:F1} ms | {counts}"),
                TachoFeedback.TooEarlyGreen => (
                    greenBadgeBg,
                    $"{tLabel} nach Klick: GRUEN (ETWAS FRUEH)",
                    $"Offset {snap.LastDeltaMs:+0.0;-0.0} ms -> {snap.LastResultNewOffsetMs:F1} ms | {counts}"),
                TachoFeedback.TooLateRed => (
                    redBadgeBg,
                    $"{tLabel} nach Klick: ROT (ZU SPAET!)",
                    $"Offset {snap.LastDeltaMs:+0.0;-0.0} ms -> {snap.LastResultNewOffsetMs:F1} ms | {counts}"),
                TachoFeedback.WayTooEarlyWhite => (
                    whiteBadgeBg,
                    $"{tLabel} nach Klick: WEISS (VIEL ZU FRUEH)",
                    $"Reset -> {snap.LastResultNewOffsetMs:F1} ms | {counts}"),
                _ => (
                    neutralBadgeBg,
                    "Nach letztem Klick: noch keine Farbe",
                    snap.LastClickedTachoNumber > 0 ? $"{tLabel}: {counts}" : "Warte auf ersten Klick...")
            };

            g.FillRectangle(badgeBrush, x, badgeY, badgeW, 44);
            g.DrawRectangle(Pens.WhiteSmoke, x, badgeY, badgeW, 44);
            g.DrawString(line1, badgeFont, Brushes.White, x + 8, badgeY + 5);
            g.DrawString(line2, infoFont, Brushes.White, x + 8, badgeY + 24);
        }
    }

    private static void DrawButtonPreview(
        Graphics g, Bitmap? bmp, string label, int x, int y, int size, Brush labelBrush, Font font, Pen crossPen)
    {
        if (bmp != null)
        {
            g.DrawImage(bmp, new Rectangle(x, y, size, size));
            int mid = size / 2;
            g.DrawLine(crossPen, x + mid - 10, y + mid, x + mid + 10, y + mid);
            g.DrawLine(crossPen, x + mid, y + mid - 10, x + mid, y + mid + 10);
        }
        else
        {
            g.FillRectangle(Brushes.Black, x, y, size, size);
        }

        g.DrawRectangle(Pens.Gray, x, y, size, size);
        g.DrawString(label, font, labelBrush, x, y + size + 3);
    }
}
