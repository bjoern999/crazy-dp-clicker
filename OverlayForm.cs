using System.Drawing;
using System.Windows.Forms;

namespace TachoClicker;

internal sealed class OverlaySnapshot
{
    public bool Running { get; init; }
    public int ActiveProfile { get; init; }
    public Bitmap? BlueButtonPreview { get; init; }
    public Bitmap? GreenButtonPreview { get; init; }
    public List<TachoOverlayItem> Tachos { get; init; } = new();
}

internal sealed class TachoOverlayItem
{
    public int Index { get; init; }
    public int CenterX { get; init; }
    public int CenterY { get; init; }
    public bool IsLast { get; init; }
    public DetectorState State { get; init; }
    public int BlueCount { get; init; }
    public int WhiteCount { get; init; }
    public double RemainingDeg { get; init; }
    public double TachoLeadDeg { get; init; }
    public double EffectiveLeadDeg { get; init; }
    public double SpeedDegPerSec { get; init; }
    public TachoFeedback LastFeedback { get; init; }
    public int PeakRed { get; init; }
    public int PeakGreen { get; init; }
    public int PeakBlue { get; init; }
    public int PeakWhite { get; init; }
    public double LastDelta { get; init; }
    public Bitmap? Preview { get; init; }
}

internal sealed class OverlayForm : Form
{
    private readonly object _lock = new();
    private OverlaySnapshot? _snapshot;

    public OverlayForm()
    {
        Text = "TachoClicker Live-Inspektor (F8=Start/Pause | F9=Tacho | F10=Blau | F6=Gruen | +/-=Vorlauf)";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(40, 40);
        ClientSize = new Size(650, 395);
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
        snap.BlueButtonPreview?.Dispose();
        snap.GreenButtonPreview?.Dispose();
        foreach (var t in snap.Tachos)
            t.Preview?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);

        using var titleFont = new Font("Consolas", 10.5f, FontStyle.Bold);
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

            string statusText = snap.Running ? "STATUS: AKTIV (F8)" : "STATUS: PAUSIERT (F8)";
            Brush statusBrush = snap.Running ? Brushes.LimeGreen : Brushes.Orange;
            string tachoAngles = string.Join(" | ", snap.Tachos.Select(t => $"T{t.Index + 1}={t.TachoLeadDeg:F1}°"));
            g.DrawString(
                $"{statusText}  |  Profil: {snap.ActiveProfile}er (F2/F3)  |  Vorlauf (unabhaengig): {tachoAngles}",
                titleFont, statusBrush, 12, 8);
            g.DrawString(
                "Legende: Magenta = Gruener Bereich | Gelb = Weisser Balken | Gruene Linie = Gruen-Ende | Rote Linie = Ausloeser",
                infoFont, Brushes.LightGray, 12, 28);

            int tileSize = 160;
            int startX = 12;
            int startY = 50;

            for (int i = 0; i < snap.Tachos.Count; i++)
            {
                var t = snap.Tachos[i];
                int x = startX + i * (tileSize + 14);
                int y = startY;

                var pen = t.State switch
                {
                    DetectorState.Armed => borderArmed,
                    DetectorState.Cooldown => borderCooldown,
                    _ => borderWaiting
                };

                if (t.Preview != null)
                {
                    g.DrawImage(t.Preview, new Rectangle(x, y, tileSize, tileSize));
                }
                else
                {
                    g.FillRectangle(Brushes.Black, x, y, tileSize, tileSize);
                    g.DrawString("Nicht gesetzt\n(F9 druecken)", infoFont, Brushes.Gray, x + 20, y + 60);
                }

                g.DrawRectangle(pen, x, y, tileSize, tileSize);

                string btnName = t.IsLast ? "-> GRUEN" : "-> BLAU";
                g.DrawString($"Tacho {t.Index + 1} ({btnName}) [{t.TachoLeadDeg:F1}°]", titleFont, Brushes.White, x, y + tileSize + 4);
                g.DrawString($"Pos : ({t.CenterX}, {t.CenterY})", infoFont, Brushes.Silver, x, y + tileSize + 22);
                g.DrawString($"Gruen: {t.BlueCount} px | Weiss: {t.WhiteCount} px", infoFont,
                    t.BlueCount > 0 ? Brushes.Magenta : Brushes.Silver, x, y + tileSize + 36);
                g.DrawString($"Rest: {(t.RemainingDeg < 350 ? $"{t.RemainingDeg:F0}°" : "-")} | {(t.SpeedDegPerSec > 10 ? $"{t.SpeedDegPerSec:F0}°/s -> {t.EffectiveLeadDeg:F0}°" : $"{t.State}")}", infoFont,
                    t.State == DetectorState.Armed ? Brushes.Cyan : Brushes.Silver, x, y + tileSize + 50);

                // Farbige Feedback-Box fuer die nach dem Klick erkannte Tacho-Farbe
                int badgeY = y + tileSize + 68;
                var (badgeBrush, line1, line2) = t.LastFeedback switch
                {
                    TachoFeedback.OptimalBlue => (
                        blueBadgeBg,
                        "FARBE: BLAU (OPTIMAL)",
                        $"Vorlauf ±0.0° (B={t.PeakBlue} R={t.PeakRed} G={t.PeakGreen})"),
                    TachoFeedback.TooEarlyGreen => (
                        greenBadgeBg,
                        "FARBE: GRUEN (FRUEH)",
                        $"Vorlauf {t.LastDelta:+0.0;-0.0}° (G={t.PeakGreen} B={t.PeakBlue} R={t.PeakRed})"),
                    TachoFeedback.TooLateRed => (
                        redBadgeBg,
                        "FARBE: ROT (ZU SPAET!)",
                        $"Vorlauf {t.LastDelta:+0.0;-0.0}° (R={t.PeakRed} G={t.PeakGreen} B={t.PeakBlue})"),
                    TachoFeedback.WayTooEarlyWhite => (
                        whiteBadgeBg,
                        "FARBE: WEISS (VIEL ZU FRUEH)",
                        $"Reset auf Startwert ({t.TachoLeadDeg:F1}° | W={t.PeakWhite})"),
                    _ => (
                        neutralBadgeBg,
                        "FARBE NACH KLICK: -",
                        t.PeakRed + t.PeakGreen + t.PeakBlue > 0
                            ? $"Letzte Px: R={t.PeakRed} G={t.PeakGreen} B={t.PeakBlue}"
                            : "Noch kein Klick ausgewertet")
                };

                g.FillRectangle(badgeBrush, x, badgeY, tileSize, 38);
                g.DrawRectangle(Pens.WhiteSmoke, x, badgeY, tileSize, 38);
                g.DrawString(line1, badgeFont, Brushes.White, x + 4, badgeY + 3);
                g.DrawString(line2, infoFont, Brushes.White, x + 4, badgeY + 20);
            }

            // Button-Vorschauen rechts
            int btnX = startX + 3 * (tileSize + 14) + 6;
            int btnSize = 84;

            DrawButtonPreview(g, snap.BlueButtonPreview, "Blau (F10)", btnX, startY, btnSize, Brushes.DeepSkyBlue, infoFont, crossPen);
            DrawButtonPreview(g, snap.GreenButtonPreview, "Gruen (F6)", btnX, startY + btnSize + 34, btnSize, Brushes.LimeGreen, infoFont, crossPen);
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
