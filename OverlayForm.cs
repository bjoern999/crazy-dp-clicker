using System.Drawing;
using System.Windows.Forms;

namespace TachoClicker;

internal sealed class OverlaySnapshot
{
    public bool Running { get; init; }
    public int ActiveProfile { get; init; }
    public double LeadAngleDeg { get; init; }
    public double ExtraLeadPerTachoDeg { get; init; }
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
    public TachoFeedback LastFeedback { get; init; }
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
        ClientSize = new Size(650, 365);
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
        using var infoFont = new Font("Consolas", 9f, FontStyle.Regular);
        using var borderWaiting = new Pen(Color.Gold, 2);
        using var borderArmed = new Pen(Color.Cyan, 3);
        using var borderCooldown = new Pen(Color.Gray, 2);
        using var crossPen = new Pen(Color.Red, 2);

        OverlaySnapshot? snap;
        lock (_lock)
        {
            snap = _snapshot;
            if (snap == null)
                return;

            string statusText = snap.Running ? "STATUS: AKTIV (F8)" : "STATUS: PAUSIERT (F8)";
            Brush statusBrush = snap.Running ? Brushes.LimeGreen : Brushes.Orange;
            g.DrawString(
                $"{statusText} | Profil: {snap.ActiveProfile}er (F2/F3) | Basis-Vorlauf: {snap.LeadAngleDeg:F0}° (+/-) | +Stufe: +{snap.ExtraLeadPerTachoDeg:F0}° (Bild\u2191/\u2193)",
                titleFont, statusBrush, 12, 8);
            g.DrawString(
                "Legende: Magenta = Gruener Bereich | Gelb = Weisser Balken | Gruene Linie = Gruen-Ende | Rote Linie = Ausloeser",
                infoFont, Brushes.LightGray, 12, 28);

            int tileSize = 160;
            int startX = 12;
            int startY = 52;

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
                g.DrawString($"Tacho {t.Index + 1} ({btnName}) [{t.TachoLeadDeg:F0}°]", titleFont, Brushes.White, x, y + tileSize + 4);
                g.DrawString($"Pos : ({t.CenterX}, {t.CenterY})", infoFont, Brushes.Silver, x, y + tileSize + 22);
                g.DrawString($"Gruen: {t.BlueCount} px | Weiss: {t.WhiteCount} px", infoFont,
                    t.BlueCount > 0 ? Brushes.Magenta : Brushes.Silver, x, y + tileSize + 38);
                g.DrawString($"Rest: {(t.RemainingDeg < 350 ? $"{t.RemainingDeg:F0}°" : "-")} | {t.State}", infoFont,
                    t.State == DetectorState.Armed ? Brushes.Cyan : Brushes.Silver, x, y + tileSize + 54);

                var (fbText, fbBrush) = t.LastFeedback switch
                {
                    TachoFeedback.OptimalBlue => ("Feedback: BLAU (Optimal!)", Brushes.DeepSkyBlue),
                    TachoFeedback.TooEarlyGreen => ("Feedback: GRUEN (-0.7°)", Brushes.LimeGreen),
                    TachoFeedback.TooLateRed => ("Feedback: ROT (+1.5°)", Brushes.OrangeRed),
                    _ => ("Feedback: -", Brushes.Gray)
                };
                g.DrawString(fbText, infoFont, fbBrush, x, y + tileSize + 70);
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
