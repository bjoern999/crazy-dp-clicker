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

    // Erkanntes Gegner-Setup
    public string EnemySetupName { get; init; } = "";
    public bool EnemySetupIsNew { get; init; }
    public int EnemyBlueHits { get; init; }
    public int EnemyTotalClicks { get; init; }
    public int TotalSavedEnemies { get; init; }

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

    // Ergebnis des letzten Klicks
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

    // Klick-Visualisierung (sofort beim Ausloesen des Buttons)
    private Bitmap? _clickMomentBitmap;
    private int _clickCount;
    private int _clickTachoNumber;
    private bool _clickWasGreenButton;
    private DateTime _clickTimestampLocal = DateTime.MinValue;
    private DateTime _flashUntilUtc = DateTime.MinValue;
    private readonly System.Windows.Forms.Timer _flashTimer;

    public OverlayForm()
    {
        Text = "TachoClicker Live-Inspektor (Gegner-Erkennung + Klick-Moment)";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(40, 40);
        ClientSize = new Size(575, 434);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(24, 26, 30);

        _flashTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _flashTimer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow >= _flashUntilUtc)
            {
                _flashTimer.Stop();
                Invalidate();
            }
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        TopMost = true;
        BringToFront();
        Activate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _flashTimer.Dispose();
            lock (_lock)
            {
                _clickMomentBitmap?.Dispose();
                DisposeBitmaps(_snapshot);
            }
        }
        base.Dispose(disposing);
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

    /// <summary>
    /// Sofortige visuelle Rueckmeldung im exakten Moment des Button-Klicks:
    /// Button blinkt 450 ms gelb auf + eingefrorenes Foto "IM KLICK-MOMENT" wird angezeigt.
    /// </summary>
    public void FlashButtonClick(bool isGreenButton, int tachoNumber, Bitmap? clickMomentBitmap)
    {
        Bitmap? oldBmp;
        lock (_lock)
        {
            oldBmp = _clickMomentBitmap;
            _clickMomentBitmap = clickMomentBitmap;
            _clickCount++;
            _clickTachoNumber = tachoNumber;
            _clickWasGreenButton = isGreenButton;
            _clickTimestampLocal = DateTime.Now;
            _flashUntilUtc = DateTime.UtcNow.AddMilliseconds(450);
        }
        oldBmp?.Dispose();

        if (!IsHandleCreated || IsDisposed)
            return;

        BeginInvoke(() =>
        {
            if (IsDisposed) return;
            if (!Visible) Visible = true;
            _flashTimer.Start();
            Invalidate();
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
        using var clickFlashFont = new Font("Consolas", 11.0f, FontStyle.Bold);
        using var borderWaiting = new Pen(Color.Gold, 2);
        using var borderArmed = new Pen(Color.Cyan, 3);
        using var borderCooldown = new Pen(Color.Gray, 2);
        using var clickFlashPen = new Pen(Color.Yellow, 5);
        using var crossPen = new Pen(Color.Red, 2);

        using var redBadgeBg = new SolidBrush(Color.FromArgb(200, 35, 35));
        using var greenBadgeBg = new SolidBrush(Color.FromArgb(25, 145, 55));
        using var blueBadgeBg = new SolidBrush(Color.FromArgb(20, 115, 210));
        using var whiteBadgeBg = new SolidBrush(Color.FromArgb(145, 150, 160));
        using var neutralBadgeBg = new SolidBrush(Color.FromArgb(45, 48, 55));
        using var flashBannerBg = new SolidBrush(Color.FromArgb(225, 255, 215, 0));

        lock (_lock)
        {
            var snap = _snapshot;
            if (snap == null)
                return;

            bool isFlashing = DateTime.UtcNow < _flashUntilUtc;

            string statusText = snap.Running
                ? $"AKTIV: Tacho {snap.ActiveTachoIndex + 1}/{snap.TotalTachos}"
                : $"PAUSIERT (F8) | Vorschau T{snap.ActiveTachoIndex + 1}/{snap.TotalTachos}";
            Brush statusBrush = snap.Running ? Brushes.LimeGreen : Brushes.Orange;

            g.DrawString($"{statusText}  ({snap.ActiveProfile}er-Modus)  |  Offsets: {snap.AllOffsetsSummary}", titleFont, statusBrush, 12, 8);

            // Prominenter Gegner-Setup-Balken (Name/Nummer des erkannten Setups)
            using var enemyKnownBg = new SolidBrush(Color.FromArgb(20, 95, 130));
            using var enemyNewBg = new SolidBrush(Color.FromArgb(145, 95, 15));
            bool hasEnemy = !string.IsNullOrEmpty(snap.EnemySetupName);
            Brush enemyBg = !hasEnemy ? neutralBadgeBg : (snap.EnemySetupIsNew ? enemyNewBg : enemyKnownBg);

            string enemyLine;
            if (!hasEnemy)
            {
                enemyLine = $"TACHO-SETUP: noch nicht erkannt (gespeicherte Setups: {snap.TotalSavedEnemies})";
            }
            else
            {
                string tag = snap.EnemySetupIsNew ? "NEU GESPEICHERT" : "ERKANNT";
                string rate = snap.EnemyTotalClicks > 0
                    ? $" | Blau: {snap.EnemyBlueHits}/{snap.EnemyTotalClicks} ({100 * snap.EnemyBlueHits / snap.EnemyTotalClicks}%)"
                    : "";
                enemyLine = $"TACHO-SETUP: {snap.EnemySetupName} [{tag}]{rate} | Gesamt: {snap.TotalSavedEnemies}";
            }

            g.FillRectangle(enemyBg, 12, 28, ClientSize.Width - 24, 24);
            g.DrawRectangle(hasEnemy ? Pens.Cyan : Pens.Gray, 12, 28, ClientSize.Width - 24, 24);
            g.DrawString(enemyLine, badgeFont, Brushes.White, 18, 32);

            g.DrawString("Links: Live aktiver Tacho | Mitte: Foto IM KLICK-MOMENT | Rechts: Button-Klick-Blitz", infoFont, Brushes.LightGray, 12, 56);

            int tileSize = 195;
            int y = 76;

            // 1. Links: Nur der aktuell aktive Tacho (Live)
            int xLive = 12;
            var pen = snap.State switch
            {
                DetectorState.Armed => borderArmed,
                DetectorState.Cooldown => borderCooldown,
                _ => borderWaiting
            };

            if (snap.ActivePreview != null)
            {
                g.DrawImage(snap.ActivePreview, new Rectangle(xLive, y, tileSize, tileSize));
            }
            else
            {
                g.FillRectangle(Brushes.Black, xLive, y, tileSize, tileSize);
                g.DrawString("Nicht gesetzt\n(F9 druecken)", infoFont, Brushes.Gray, xLive + 40, y + 80);
            }
            g.DrawRectangle(pen, xLive, y, tileSize, tileSize);

            string btnName = snap.IsLastTacho ? "-> GRUEN" : "-> BLAU";
            g.DrawString($"Live T{snap.ActiveTachoIndex + 1} ({btnName}) [{snap.ClickOffsetMs:F1} ms]", titleFont, Brushes.White, xLive, y + tileSize + 6);
            g.DrawString($"Pos: ({snap.CenterX}, {snap.CenterY}) | G={snap.GreenCount} W={snap.WhiteCount}", infoFont, Brushes.Silver, xLive, y + tileSize + 24);
            g.DrawString(
                snap.SpeedDegPerSec > 10
                    ? $"Speed: {snap.SpeedDegPerSec:F0}°/s (n={snap.CheckpointCount})"
                    : $"Status: {snap.State} (+/- fuer Offset)",
                infoFont, snap.SpeedDegPerSec > 10 ? Brushes.Cyan : Brushes.Silver, xLive, y + tileSize + 40);

            // 2. Mitte: Eingefrorenes Foto exakt IM KLICK-MOMENT
            int xClick = xLive + tileSize + 14;
            if (_clickMomentBitmap != null)
            {
                g.DrawImage(_clickMomentBitmap, new Rectangle(xClick, y, tileSize, tileSize));
            }
            else
            {
                g.FillRectangle(Brushes.Black, xClick, y, tileSize, tileSize);
                g.DrawString("Foto im Klick-Moment\nerscheint beim 1. Klick", infoFont, Brushes.Gray, xClick + 16, y + 80);
            }
            g.DrawRectangle(isFlashing ? clickFlashPen : Pens.Silver, xClick, y, tileSize, tileSize);

            if (isFlashing)
            {
                g.FillRectangle(flashBannerBg, xClick + 4, y + 4, tileSize - 8, 24);
                g.DrawString($"KLICK #{_clickCount} -> {(_clickWasGreenButton ? "GRUEN!" : "BLAU!")}", clickFlashFont, Brushes.Black, xClick + 10, y + 7);
            }

            string clickTitle = _clickCount > 0
                ? $"Klick #{_clickCount} (T{_clickTachoNumber} -> {(_clickWasGreenButton ? "GRUEN" : "BLAU")})"
                : "Noch kein Klick erfolgt";
            g.DrawString(clickTitle, titleFont, isFlashing ? Brushes.Yellow : Brushes.White, xClick, y + tileSize + 6);
            g.DrawString(
                _clickCount > 0
                    ? $"Uhrzeit: {_clickTimestampLocal:HH:mm:ss.fff}"
                    : "Uhrzeit: -",
                infoFont, Brushes.Silver, xClick, y + tileSize + 24);
            g.DrawString("Gruene Linie = Ziel (Gruen-Ende)", infoFont, Brushes.LightGreen, xClick, y + tileSize + 40);

            // 3. Rechts: Button-Vorschauen mit sofortigem gelbem KLICK!-Blitz
            int btnX = xClick + tileSize + 16;
            int btnSize = 90;

            bool flashBlue = isFlashing && !_clickWasGreenButton;
            bool flashGreen = isFlashing && _clickWasGreenButton;

            DrawButtonPreview(g, snap.BlueButtonPreview, "Blau (F10)", btnX, y, btnSize, Brushes.DeepSkyBlue, infoFont, crossPen, flashBlue, clickFlashPen, flashBannerBg, badgeFont);
            DrawButtonPreview(g, snap.GreenButtonPreview, "Gruen (F6)", btnX, y + btnSize + 28, btnSize, Brushes.LimeGreen, infoFont, crossPen, flashGreen, clickFlashPen, flashBannerBg, badgeFont);

            // 4. Unten: Farb-Box fuer die direkt nach dem Klick gemessenen unteren 9x9 Pixel
            int badgeY = y + tileSize + 64;
            int badgeW = ClientSize.Width - 24;
            string counts = $"B={snap.PatchBlue} G={snap.PatchGreen} R={snap.PatchRed} W={snap.PatchWhite} (von 81 px unten)";
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
                    "Nach letztem Klick: noch keine Farbe erkannt",
                    snap.LastClickedTachoNumber > 0 ? $"{tLabel}: {counts}" : "Warte auf ersten Klick...")
            };

            g.FillRectangle(badgeBrush, 12, badgeY, badgeW, 44);
            g.DrawRectangle(Pens.WhiteSmoke, 12, badgeY, badgeW, 44);
            g.DrawString(line1, badgeFont, Brushes.White, 20, badgeY + 5);
            g.DrawString(line2, infoFont, Brushes.White, 20, badgeY + 24);
        }
    }

    private static void DrawButtonPreview(
        Graphics g, Bitmap? bmp, string label, int x, int y, int size, Brush labelBrush,
        Font font, Pen crossPen, bool isFlashing, Pen flashPen, Brush flashBg, Font badgeFont)
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

        g.DrawRectangle(isFlashing ? flashPen : Pens.Gray, x, y, size, size);

        if (isFlashing)
        {
            g.FillRectangle(flashBg, x + 3, y + size / 2 - 11, size - 6, 22);
            g.DrawString("KLICK!", badgeFont, Brushes.Black, x + 16, y + size / 2 - 8);
        }

        g.DrawString(label, font, isFlashing ? Brushes.Yellow : labelBrush, x, y + size + 3);
    }
}
