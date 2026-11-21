using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;

namespace TachoClicker;

internal enum DetectorState
{
    WaitingForBlueZone,
    Armed,
    Cooldown
}

internal enum TachoFeedback
{
    None,
    OptimalBlue,
    TooEarlyGreen,
    TooLateRed,
    WayTooEarlyWhite
}

internal readonly struct AngleCheckpoint
{
    public double TimeMs { get; init; }
    public double RemainingDeg { get; init; }
}

internal sealed class TachoDetector : IDisposable
{
    private Bitmap? _bitmap;
    private Graphics? _graphics;
    private Bitmap? _patchBitmap;
    private Graphics? _patchGraphics;
    private int _size;
    private bool[] _lastGreenNow = Array.Empty<bool>();
    private bool[] _lastWhiteNow = Array.Empty<bool>();

    public DetectorState State { get; private set; } = DetectorState.WaitingForBlueZone;
    public int LastGreenCount { get; private set; }
    public int LastWhiteCount { get; private set; }
    public double LastRemainingDeg { get; private set; } = 360.0;
    public double LastTargetEndDeg => _greenEndDeg;
    public bool JustArmed { get; private set; }

    public TachoFeedback LastFeedback { get; private set; } = TachoFeedback.None;
    public int LastPatchRed { get; private set; }
    public int LastPatchGreen { get; private set; }
    public int LastPatchBlue { get; private set; }
    public int LastPatchWhite { get; private set; }
    public int LastAvgR { get; private set; }
    public int LastAvgG { get; private set; }
    public int LastAvgB { get; private set; }
    public int LastPatchScreenX { get; private set; }
    public int LastPatchScreenY { get; private set; }
    public double LastCorrectionDeltaMs { get; set; }

    public double LastSpeedDegPerSec { get; private set; }
    public double LastAccelDegPerSec2 { get; private set; }
    public int LastCheckpointCount => _checkpoints.Count;

    // Telemetry fields for diagnosing timing precision
    public string LastTriggerReason { get; private set; } = "";
    public double LastRemDegAtTrigger { get; private set; }
    public double LastWaitMsAtTrigger { get; private set; }
    public double LastRegressionR2 { get; private set; }
    public double LastGreenWidthDeg { get; private set; }
    public double LastBlueWidthDeg { get; private set; }
    public int LastBlueSliverCount { get; private set; }
    public int LastInitialGreenCount { get; private set; }
    public double LastUsedClickOffsetMs { get; private set; }
    public TachoFingerprint LastFingerprint { get; private set; }
    public bool JustAbortedWithoutClick { get; private set; }
    public string LastAbortReason { get; private set; } = "";
    public string LastAbortCheckpointSummary { get; private set; } = "";
    public double LastAbortMinRemDeg { get; private set; }
    public int LastAbortRedOnBottom { get; private set; }

    public string FormatCheckpointsSummary()
    {
        if (_checkpoints.Count == 0) return "[]";
        double t0 = _checkpoints[0].TimeMs;
        return "[" + string.Join(", ", _checkpoints.Select(c => $"{c.TimeMs - t0:F0}ms:{c.RemainingDeg:F0}°")) + "]";
    }

    // Exact measured bottom-of-ring pixel position (6 o'clock) in local box coordinates
    public int BottomSpotLocalX { get; private set; }
    public int BottomSpotLocalY { get; private set; }

    private readonly List<AngleCheckpoint> _checkpoints = new(32);
    private double _nextCheckpointDeg = -1;
    private double _greenEndDeg;
    private int _greenStableFrames;
    private bool _greenEndLocked;
    private bool _setupResolvedForSpin;
    private double _activeSpinClickOffsetMs;
    private double _prevRemainingDeg = 360.0;
    private int _zeroGreenFrames;
    private DateTime _armedSince = DateTime.MinValue;
    private DateTime _cooldownUntil = DateTime.MinValue;

    private static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    public void Reset()
    {
        State = DetectorState.WaitingForBlueZone;
        Array.Clear(_lastGreenNow);
        Array.Clear(_lastWhiteNow);
        LastGreenCount = 0;
        LastWhiteCount = 0;
        LastRemainingDeg = 360.0;
        JustArmed = false;
        _zeroGreenFrames = 0;
        _prevRemainingDeg = 360.0;
        _greenStableFrames = 0;
        _greenEndLocked = false;
        _setupResolvedForSpin = false;
        _checkpoints.Clear();
        _nextCheckpointDeg = -1;
    }

    public bool Update(
        int centerX,
        int centerY,
        double defaultClickOffsetMs,
        AppConfig cfg,
        Func<TachoFingerprint, double>? onFingerprintReady = null)
    {
        JustArmed = false;
        JustAbortedWithoutClick = false;

        if (State == DetectorState.Cooldown)
        {
            if (DateTime.UtcNow < _cooldownUntil)
                return false;

            Reset();
        }

        if (centerX <= 0 || centerY <= 0)
            return false;

        EnsureBitmaps(cfg.TachoBoxSize);

        int half = _size / 2;
        int left = Math.Max(0, centerX - half);
        int top = Math.Max(0, centerY - half);

        // Real Tacho track sits strictly at radius 43..72 px in a 180x180 box (24%..40%).
        // Staying inside 23%..39.5% completely excludes the pale-blue outer glow ring at radius 78..90 px!
        double minR = _size * 0.23;
        double maxR = _size * 0.395;
        double minRSq = minR * minR;
        double maxRSq = maxR * maxR;

        _graphics!.CopyFromScreen(left, top, 0, 0, new Size(_size, _size), CopyPixelOperation.SourceCopy);
        double captureTimeMs = NowMs();

        var rect = new Rectangle(0, 0, _size, _size);
        BitmapData data = _bitmap!.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        int greenCount = 0;
        int blueSliverCount = 0;
        int whiteCount = 0;
        int bottomRedCount = 0;
        int centerPixels = 0;
        int centerWhitePixels = 0;
        int bottomWhiteCount = 0;
        long bottomWhiteSumX = 0;
        long bottomWhiteSumY = 0;

        double greenSinSum = 0;
        double greenCosSum = 0;

        Span<int> greenAngleBins = stackalloc int[360];
        Span<int> blueAngleBins = stackalloc int[360];
        Span<int> whiteAngleBins = stackalloc int[360];
        greenAngleBins.Clear();
        blueAngleBins.Clear();
        whiteAngleBins.Clear();

        try
        {
            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                int stride = data.Stride;

                for (int y = 0; y < _size; y++)
                {
                    byte* row = ptr + (y * stride);
                    int rowOffset = y * _size;
                    double dy = y - half;
                    double dySq = dy * dy;

                    for (int x = 0; x < _size; x++)
                    {
                        int idx = rowOffset + x;
                        double dx = x - half;
                        double distSq = dx * dx + dySq;

                        if (distSq < minRSq)
                        {
                            _lastGreenNow[idx] = false;
                            _lastWhiteNow[idx] = false;
                            centerPixels++;
                            byte cb = row[x * 4 + 0];
                            byte cg = row[x * 4 + 1];
                            byte cr = row[x * 4 + 2];
                            if (IsTachoWhite(cr, cg, cb)) centerWhitePixels++;
                            continue;
                        }

                        if (distSq > maxRSq)
                        {
                            _lastGreenNow[idx] = false;
                            _lastWhiteNow[idx] = false;
                            continue;
                        }

                        byte b = row[x * 4 + 0];
                        byte g = row[x * 4 + 1];
                        byte r = row[x * 4 + 2];

                        bool isGreen = IsTachoGreen(r, g, b);
                        bool isWhite = IsTachoWhite(r, g, b);

                        _lastGreenNow[idx] = isGreen;
                        _lastWhiteNow[idx] = isWhite;

                        if (isGreen)
                        {
                            greenCount++;
                            double rad = Math.Atan2(dy, dx);
                            greenSinSum += Math.Sin(rad);
                            greenCosSum += Math.Cos(rad);

                            double deg = rad * (180.0 / Math.PI);
                            if (deg < 0) deg += 360.0;
                            int bin = ((int)Math.Round(deg)) % 360;
                            greenAngleBins[bin]++;
                        }
                        else if (IsFeedbackBlue(r, g, b))
                        {
                            blueSliverCount++;
                            double deg = Math.Atan2(dy, dx) * (180.0 / Math.PI);
                            if (deg < 0) deg += 360.0;
                            int bin = ((int)Math.Round(deg)) % 360;
                            blueAngleBins[bin]++;
                        }
                        else if (dy > 15 && Math.Abs(dx) <= 12 && IsFeedbackRed(r, g, b))
                        {
                            bottomRedCount++;
                        }

                        if (isWhite)
                        {
                            whiteCount++;
                            // Locate exact 6-o'clock bottom of the ring (|dx| <= 12, dy > 15)
                            if (dy > 15 && Math.Abs(dx) <= 12)
                            {
                                bottomWhiteCount++;
                                bottomWhiteSumX += x;
                                bottomWhiteSumY += y;
                            }

                            double deg = Math.Atan2(dy, dx) * (180.0 / Math.PI);
                            if (deg < 0) deg += 360.0;
                            int bin = ((int)Math.Round(deg)) % 360;
                            whiteAngleBins[bin]++;
                        }
                    }
                }
            }
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }

        LastGreenCount = greenCount;
        LastWhiteCount = whiteCount;

        if (bottomWhiteCount >= 6)
        {
            BottomSpotLocalX = (int)(bottomWhiteSumX / bottomWhiteCount);
            BottomSpotLocalY = (int)(bottomWhiteSumY / bottomWhiteCount);
        }
        else
        {
            BottomSpotLocalX = half;
            BottomSpotLocalY = half + (int)(_size * 0.315); // r = 57 px -> dead-center in real track (43..72 px)
        }

        // Ignore bright white menu cards (a real Tacho always has a dark gray center hole!)
        bool centerLooksLikeMenu = centerPixels > 0 && ((double)centerWhitePixels / centerPixels) > 0.25;
        if (centerLooksLikeMenu)
        {
            if (State == DetectorState.Armed)
                Reset();
            return false;
        }

        int minGreen = Math.Max(4, cfg.MinGreenPixels);

        if (State == DetectorState.WaitingForBlueZone)
        {
            // Reject full-screen green attack/victory effects (real Tacho green zone has Width <= 85° and GreenPx <= 2800)
            if (greenCount >= minGreen && greenCount <= 2800 &&
                TryComputeGreenArc(greenSinSum, greenCosSum, greenAngleBins, out double endDeg, out double widthDeg) &&
                widthDeg <= 85.0)
            {
                _greenEndDeg = endDeg;
                LastGreenWidthDeg = widthDeg;
                LastBlueWidthDeg = ComputeBlueSliverWidthDeg(endDeg, blueAngleBins);
                LastInitialGreenCount = greenCount;
                LastBlueSliverCount = blueSliverCount;
                _greenStableFrames = 1;
                _greenEndLocked = false;
                _setupResolvedForSpin = false;
                _activeSpinClickOffsetMs = defaultClickOffsetMs;
                _zeroGreenFrames = 0;
                _prevRemainingDeg = 360.0;
                _armedSince = DateTime.UtcNow;
                _checkpoints.Clear();
                _nextCheckpointDeg = -1;
                LastSpeedDegPerSec = 0;
                LastAccelDegPerSec2 = 0;
                LastRegressionR2 = 0;
                State = DetectorState.Armed;
                JustArmed = true;
            }
            return false;
        }

        // State == Armed: refine _greenEndDeg for the first 3 frames before locking it
        if (greenCount >= minGreen && greenCount <= 2800)
        {
            _zeroGreenFrames = 0;
            if (!_greenEndLocked &&
                TryComputeGreenArc(greenSinSum, greenCosSum, greenAngleBins, out double updatedEndDeg, out double updatedWidthDeg) &&
                updatedWidthDeg <= 85.0)
            {
                _greenEndDeg = updatedEndDeg;
                LastGreenWidthDeg = updatedWidthDeg;
                double bw = ComputeBlueSliverWidthDeg(updatedEndDeg, blueAngleBins);
                if (bw > LastBlueWidthDeg) LastBlueWidthDeg = bw;
                if (greenCount > LastInitialGreenCount) LastInitialGreenCount = greenCount;
                if (blueSliverCount > LastBlueSliverCount) LastBlueSliverCount = blueSliverCount;
                _greenStableFrames++;
                if (_greenStableFrames >= 3)
                    _greenEndLocked = true;
            }
        }
        else
        {
            _zeroGreenFrames++;
        }

        double minRemainingDeg = FindWhiteFrontRemainingDeg(_greenEndDeg, whiteAngleBins, _prevRemainingDeg);
        LastRemainingDeg = minRemainingDeg;

        // 1. Record timestamped checkpoints every CheckpointStepDeg (default 10°)
        double stepDeg = Math.Clamp(cfg.CheckpointStepDeg, 5.0, 20.0);
        if (_greenEndLocked && whiteCount >= 15 && minRemainingDeg < 280.0 && minRemainingDeg > 2.0)
        {
            if (_nextCheckpointDeg < 0)
            {
                _checkpoints.Add(new AngleCheckpoint { TimeMs = captureTimeMs, RemainingDeg = minRemainingDeg });
                _nextCheckpointDeg = Math.Floor(minRemainingDeg / stepDeg) * stepDeg - stepDeg;
            }
            else if (minRemainingDeg <= _nextCheckpointDeg)
            {
                var last = _checkpoints[_checkpoints.Count - 1];
                double dt = captureTimeMs - last.TimeMs;
                double dDeg = last.RemainingDeg - minRemainingDeg;
                if (dt >= 4.0 && dDeg >= 3.0)
                {
                    double instSpeed = dDeg / dt;
                    if (instSpeed >= 0.08 && instSpeed <= 2.4)
                    {
                        _checkpoints.Add(new AngleCheckpoint { TimeMs = captureTimeMs, RemainingDeg = minRemainingDeg });
                    }
                    while (_nextCheckpointDeg >= minRemainingDeg)
                        _nextCheckpointDeg -= stepDeg;
                }
            }
        }

        _prevRemainingDeg = minRemainingDeg;

        // 2. Extrapolate the exact arrival time at RemainingDeg = 0° (end of green / blue optimum)
        //    using Quadratic Regression (models ease-in acceleration r(t) = a*t^2 + b*t + c)
        if (bottomWhiteCount >= 6 &&
            TryPredictArrivalTimeMs(out double predictedZeroTimeMs, out double endSpeedDegPerSec, out double accelDegPerSec2, out double r2, out bool usedQuadratic))
        {
            LastSpeedDegPerSec = endSpeedDegPerSec;
            LastAccelDegPerSec2 = accelDegPerSec2;
            LastRegressionR2 = r2;

            // Identify enemy setup ONCE mid-spin (~200-350 ms BEFORE clicking!) so its saved offset is used for this very click
            if (!_setupResolvedForSpin && onFingerprintReady != null && usedQuadratic && _checkpoints.Count >= 7 && minRemainingDeg <= 145.0)
            {
                _setupResolvedForSpin = true;
                var fp = new TachoFingerprint
                {
                    GreenEndDeg = _greenEndDeg,
                    GreenWidthDeg = LastGreenWidthDeg,
                    BlueWidthDeg = LastBlueWidthDeg,
                    EndSpeedDegPerSec = endSpeedDegPerSec,
                    AccelDegPerSec2 = accelDegPerSec2
                };
                LastFingerprint = fp;
                _activeSpinClickOffsetMs = onFingerprintReady(fp);
            }

            double usedOffset = _setupResolvedForSpin ? _activeSpinClickOffsetMs : defaultClickOffsetMs;
            double now = NowMs();
            double clickTargetTimeMs = predictedZeroTimeMs - usedOffset;
            double waitMs = clickTargetTimeMs - now;

            if (minRemainingDeg <= 105.0 && waitMs <= 26.0)
            {
                // If not resolved yet (e.g. fast tacho), resolve right before clicking
                if (!_setupResolvedForSpin && onFingerprintReady != null)
                {
                    _setupResolvedForSpin = true;
                    var fp = new TachoFingerprint
                    {
                        GreenEndDeg = _greenEndDeg,
                        GreenWidthDeg = LastGreenWidthDeg,
                        BlueWidthDeg = LastBlueWidthDeg,
                        EndSpeedDegPerSec = endSpeedDegPerSec,
                        AccelDegPerSec2 = accelDegPerSec2
                    };
                    LastFingerprint = fp;
                    usedOffset = onFingerprintReady(fp);
                    _activeSpinClickOffsetMs = usedOffset;
                    clickTargetTimeMs = predictedZeroTimeMs - usedOffset;
                    waitMs = clickTargetTimeMs - NowMs();
                }

                LastUsedClickOffsetMs = usedOffset;
                LastTriggerReason = usedQuadratic ? "QUAD" : "LINEAR";
                LastRemDegAtTrigger = minRemainingDeg;
                LastWaitMsAtTrigger = waitMs;

                if (waitMs > 0.5)
                {
                    PreciseWaitUntil(clickTargetTimeMs);
                }

                State = DetectorState.Cooldown;
                _cooldownUntil = DateTime.UtcNow.AddMilliseconds(cfg.CooldownMs);
                return true;
            }
        }
        else if (_checkpoints.Count >= 2 && whiteCount >= 20 && bottomWhiteCount >= 6 && minRemainingDeg <= 16.0)
        {
            LastUsedClickOffsetMs = _setupResolvedForSpin ? _activeSpinClickOffsetMs : defaultClickOffsetMs;
            LastTriggerReason = $"FALLBACK(n={_checkpoints.Count})";
            LastRemDegAtTrigger = minRemainingDeg;
            LastWaitMsAtTrigger = 0;
            State = DetectorState.Cooldown;
            _cooldownUntil = DateTime.UtcNow.AddMilliseconds(cfg.CooldownMs);
            return true;
        }

        if (_zeroGreenFrames > 18 || (DateTime.UtcNow - _armedSince).TotalSeconds > 3.5)
        {
            JustAbortedWithoutClick = true;
            LastAbortReason = _zeroGreenFrames > 18 ? "ZERO_GREEN_TIMEOUT" : "MAX_TIME_3.5S";
            LastAbortCheckpointSummary = FormatCheckpointsSummary();
            LastAbortMinRemDeg = _prevRemainingDeg;
            LastAbortRedOnBottom = bottomRedCount;
            Reset();
            return false;
        }

        return false;
    }

    /// <summary>
    /// Identical post-click color check for EVERY Tacho:
    /// Called ONLY immediately after clicking the button.
    /// Reads ONLY a tiny 9x9 pixel patch on the bottom (6 o'clock) of the Tacho ring (~15..125 ms after click)
    /// and returns immediately as soon as the bottom pixels change from White to Blue, Green, or Red (or stay White).
    /// </summary>
    public Bitmap? CaptureClickMomentSnapshot(int centerX, int centerY, AppConfig cfg)
    {
        EnsureBitmaps(cfg.TachoBoxSize);
        int half = _size / 2;
        int left = Math.Max(0, centerX - half);
        int top = Math.Max(0, centerY - half);

        var snap = new Bitmap(_size, _size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(snap);
        g.CopyFromScreen(left, top, 0, 0, new Size(_size, _size), CopyPixelOperation.SourceCopy);

        int maxR = (int)(_size * 0.395);
        double endRad = _greenEndDeg * (Math.PI / 180.0);
        using var endPen = new Pen(Color.Lime, 2);
        g.DrawLine(endPen, half, half,
            half + (int)(Math.Cos(endRad) * maxR),
            half + (int)(Math.Sin(endRad) * maxR));

        int sx = BottomSpotLocalX > 0 ? BottomSpotLocalX : half;
        int sy = BottomSpotLocalY > 0 ? BottomSpotLocalY : half + (int)(_size * 0.315);
        using var spotPen = new Pen(Color.Orange, 2);
        g.DrawRectangle(spotPen, sx - 5, sy - 5, 10, 10);

        return snap;
    }

    public TachoFeedback MeasureBottomColorAfterClick(int tachoNumber, int centerX, int centerY, AppConfig cfg)
    {
        EnsureBitmaps(cfg.TachoBoxSize);

        const int patchSize = 9; // 81 pixels right on the bottom of the Tacho ring
        const int patchHalf = patchSize / 2;

        int boxHalf = _size / 2;
        int boxLeft = Math.Max(0, centerX - boxHalf);
        int boxTop = Math.Max(0, centerY - boxHalf);

        int spotX = BottomSpotLocalX > 0 ? BottomSpotLocalX : boxHalf;
        int spotY = BottomSpotLocalY > 0 ? BottomSpotLocalY : boxHalf + (int)(_size * 0.315);

        int screenPatchLeft = Math.Max(0, boxLeft + spotX - patchHalf);
        int screenPatchTop = Math.Max(0, boxTop + spotY - patchHalf);
        LastPatchScreenX = screenPatchLeft + patchHalf;
        LastPatchScreenY = screenPatchTop + patchHalf;

        int bestRed = 0;
        int bestGreen = 0;
        int bestBlue = 0;
        int whiteFrames = 0;
        int lastWhiteCount = 0;
        int avgR = 0, avgG = 0, avgB = 0;

        DateTime start = DateTime.UtcNow;
        Thread.Sleep(15);

        while ((DateTime.UtcNow - start).TotalMilliseconds <= 135.0)
        {
            _patchGraphics!.CopyFromScreen(screenPatchLeft, screenPatchTop, 0, 0, new Size(patchSize, patchSize), CopyPixelOperation.SourceCopy);

            var rect = new Rectangle(0, 0, patchSize, patchSize);
            BitmapData data = _patchBitmap!.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            int rCount = 0;
            int gCount = 0;
            int bCount = 0;
            int wCount = 0;
            long sumR = 0, sumG = 0, sumB = 0;

            try
            {
                unsafe
                {
                    byte* ptr = (byte*)data.Scan0;
                    int stride = data.Stride;
                    for (int y = 0; y < patchSize; y++)
                    {
                        byte* row = ptr + (y * stride);
                        for (int x = 0; x < patchSize; x++)
                        {
                            byte b = row[x * 4 + 0];
                            byte g = row[x * 4 + 1];
                            byte r = row[x * 4 + 2];

                            sumR += r;
                            sumG += g;
                            sumB += b;

                            if (IsTachoWhite(r, g, b)) wCount++;
                            else if (IsFeedbackRed(r, g, b)) rCount++;
                            else if (IsFeedbackGreen(r, g, b)) gCount++;
                            else if (IsFeedbackBlue(r, g, b)) bCount++;
                        }
                    }
                }
            }
            finally
            {
                _patchBitmap.UnlockBits(data);
            }

            avgR = (int)(sumR / (patchSize * patchSize));
            avgG = (int)(sumG / (patchSize * patchSize));
            avgB = (int)(sumB / (patchSize * patchSize));

            if (rCount > bestRed) bestRed = rCount;
            if (gCount > bestGreen) bestGreen = gCount;
            if (bCount > bestBlue) bestBlue = bCount;
            lastWhiteCount = wCount;

            int colorMax = Math.Max(rCount, Math.Max(gCount, bCount));
            if (colorMax >= 25)
            {
                LastPatchRed = rCount;
                LastPatchGreen = gCount;
                LastPatchBlue = bCount;
                LastPatchWhite = wCount;
                LastAvgR = avgR;
                LastAvgG = avgG;
                LastAvgB = avgB;

                SaveDebugAfterClickSnapshot(tachoNumber, boxLeft, boxTop, spotX, spotY);

                if (bCount == colorMax) LastFeedback = TachoFeedback.OptimalBlue;
                else if (gCount == colorMax) LastFeedback = TachoFeedback.TooEarlyGreen;
                else LastFeedback = TachoFeedback.TooLateRed;

                return LastFeedback;
            }

            if ((DateTime.UtcNow - start).TotalMilliseconds >= 50.0 && wCount >= 40)
            {
                whiteFrames++;
            }

            if ((DateTime.UtcNow - start).TotalMilliseconds >= 45.0 && wCount < 10 && colorMax < 10)
            {
                break;
            }

            Thread.Sleep(5);
        }

        LastPatchRed = bestRed;
        LastPatchGreen = bestGreen;
        LastPatchBlue = bestBlue;
        LastPatchWhite = lastWhiteCount;
        LastAvgR = avgR;
        LastAvgG = avgG;
        LastAvgB = avgB;

        SaveDebugAfterClickSnapshot(tachoNumber, boxLeft, boxTop, spotX, spotY);

        int maxColorPeak = Math.Max(bestRed, Math.Max(bestGreen, bestBlue));
        if (maxColorPeak >= 18)
        {
            if (bestBlue == maxColorPeak) LastFeedback = TachoFeedback.OptimalBlue;
            else if (bestGreen == maxColorPeak) LastFeedback = TachoFeedback.TooEarlyGreen;
            else LastFeedback = TachoFeedback.TooLateRed;
            return LastFeedback;
        }

        if (whiteFrames >= 8)
        {
            LastFeedback = TachoFeedback.WayTooEarlyWhite;
            return LastFeedback;
        }

        return TachoFeedback.None;
    }

    private void SaveDebugAfterClickSnapshot(int tachoNumber, int boxLeft, int boxTop, int spotX, int spotY)
    {
        try
        {
            using var snap = new Bitmap(_size, _size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(snap))
            {
                g.CopyFromScreen(boxLeft, boxTop, 0, 0, new Size(_size, _size), CopyPixelOperation.SourceCopy);
                using var pen = new Pen(Color.Yellow, 2);
                g.DrawRectangle(pen, spotX - 5, spotY - 5, 10, 10);
            }

            string projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
            string targetDir = Directory.Exists(projectRoot) ? projectRoot : AppContext.BaseDirectory;
            snap.Save(Path.Combine(targetDir, $"debug_T{tachoNumber}_after_click.png"), ImageFormat.Png);
        }
        catch
        {
            // Ignore debug snapshot errors
        }
    }

    private static double FindWhiteFrontRemainingDeg(double greenEndDeg, ReadOnlySpan<int> whiteBins, double prevRemainingDeg)
    {
        int endBin = ((int)Math.Round(greenEndDeg) % 360 + 360) % 360;
        int startRem = prevRemainingDeg < 45.0 ? -15 : 0;

        for (int minPx = 4; minPx >= 2; minPx -= 2)
        {
            for (int rem = startRem; rem <= 295; rem++)
            {
                int bin = (endBin + rem + 360) % 360;
                if (whiteBins[bin] >= minPx)
                    return rem;
            }
        }

        return 360.0;
    }

    /// <summary>
    /// Fits a quadratic ease-in curve r(t) = a*t^2 + b*t + c over the most recent up to 14 checkpoints
    /// (capturing constant angular acceleration) and solves for r(t_zero) = 0°.
    /// Falls back cleanly to linear regression if fewer than 6 checkpoints exist or curvature is non-accelerating.
    /// </summary>
    private bool TryPredictArrivalTimeMs(
        out double zeroTimeMs,
        out double endSpeedDegPerSec,
        out double accelDegPerSec2,
        out double rSquared,
        out bool usedQuadratic)
    {
        zeroTimeMs = 0;
        endSpeedDegPerSec = 0;
        accelDegPerSec2 = 0;
        rSquared = 0;
        usedQuadratic = false;

        int total = _checkpoints.Count;
        if (total < 4)
            return false;

        // 1. Try Quadratic Regression when >= 6 checkpoints are available
        if (total >= 6 && TryFitQuadratic(total, out zeroTimeMs, out endSpeedDegPerSec, out accelDegPerSec2, out rSquared))
        {
            usedQuadratic = true;
            return true;
        }

        // 2. Fallback: Linear Regression over most recent up to 8 checkpoints
        int startIdx = Math.Max(0, total - 8);
        int n = total - startIdx;
        double t0 = _checkpoints[startIdx].TimeMs;

        double sumX = 0;
        double sumY = 0;
        for (int i = startIdx; i < total; i++)
        {
            sumX += (_checkpoints[i].TimeMs - t0);
            sumY += _checkpoints[i].RemainingDeg;
        }

        double meanX = sumX / n;
        double meanY = sumY / n;

        double num = 0;
        double den = 0;
        double ssTot = 0;
        for (int i = startIdx; i < total; i++)
        {
            double dx = (_checkpoints[i].TimeMs - t0) - meanX;
            double dy = _checkpoints[i].RemainingDeg - meanY;
            num += dx * dy;
            den += dx * dx;
            ssTot += dy * dy;
        }

        if (den <= 1e-6)
            return false;

        double slope = num / den; // deg / ms (negative)
        if (slope >= -0.08 || slope <= -2.4)
            return false;

        double ssRes = 0;
        for (int i = startIdx; i < total; i++)
        {
            double pred = meanY + slope * ((_checkpoints[i].TimeMs - t0) - meanX);
            double err = _checkpoints[i].RemainingDeg - pred;
            ssRes += err * err;
        }
        rSquared = ssTot > 1e-6 ? Math.Max(0.0, 1.0 - (ssRes / ssTot)) : 0.0;

        double speedDegPerMs = -slope;
        endSpeedDegPerSec = speedDegPerMs * 1000.0;
        accelDegPerSec2 = 0;
        double xZero = meanX + (meanY / speedDegPerMs);
        zeroTimeMs = t0 + xZero;
        return true;
    }

    private bool TryFitQuadratic(
        int total,
        out double zeroTimeMs,
        out double endSpeedDegPerSec,
        out double accelDegPerSec2,
        out double rSquared)
    {
        zeroTimeMs = 0;
        endSpeedDegPerSec = 0;
        accelDegPerSec2 = 0;
        rSquared = 0;

        int startIdx = Math.Max(0, total - 14);
        int n = total - startIdx;
        double t0 = _checkpoints[startIdx].TimeMs;

        // Time x in seconds relative to t0 for clean numerical conditioning
        double s0 = n, s1 = 0, s2 = 0, s3 = 0, s4 = 0;
        double y0 = 0, y1 = 0, y2 = 0;

        for (int i = startIdx; i < total; i++)
        {
            double x = (_checkpoints[i].TimeMs - t0) / 1000.0;
            double y = _checkpoints[i].RemainingDeg;
            double x2 = x * x;
            s1 += x;
            s2 += x2;
            s3 += x2 * x;
            s4 += x2 * x2;
            y0 += y;
            y1 += x * y;
            y2 += x2 * y;
        }

        // Solve 3x3 normal equations via Gaussian elimination with partial pivoting
        double[,] m =
        {
            { s0, s1, s2, y0 },
            { s1, s2, s3, y1 },
            { s2, s3, s4, y2 }
        };

        for (int col = 0; col < 3; col++)
        {
            int pivotRow = col;
            for (int r = col + 1; r < 3; r++)
            {
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivotRow, col]))
                    pivotRow = r;
            }

            if (Math.Abs(m[pivotRow, col]) < 1e-9)
                return false;

            if (pivotRow != col)
            {
                for (int j = col; j < 4; j++)
                    (m[col, j], m[pivotRow, j]) = (m[pivotRow, j], m[col, j]);
            }

            double piv = m[col, col];
            for (int j = col; j < 4; j++)
                m[col, j] /= piv;

            for (int r = 0; r < 3; r++)
            {
                if (r == col) continue;
                double factor = m[r, col];
                for (int j = col; j < 4; j++)
                    m[r, j] -= factor * m[col, j];
            }
        }

        double c = m[0, 3];
        double b = m[1, 3];
        double a = m[2, 3];

        // Require accelerating or near-linear decreasing curve (a <= +15 deg/s^2)
        if (a > 15.0)
            return false;

        double xLast = (_checkpoints[total - 1].TimeMs - t0) / 1000.0;
        double xZeroSec;

        if (Math.Abs(a) < 1e-4)
        {
            if (b >= -80.0) return false;
            xZeroSec = -c / b;
        }
        else
        {
            double disc = b * b - 4.0 * a * c;
            if (disc < 0)
                return false;

            double sqrtDisc = Math.Sqrt(disc);
            double root1 = (-b - sqrtDisc) / (2.0 * a);
            double root2 = (-b + sqrtDisc) / (2.0 * a);

            // Choose the root that lies right ahead of (or just near) xLast
            bool r1Valid = root1 >= xLast - 0.05 && root1 <= xLast + 1.5;
            bool r2Valid = root2 >= xLast - 0.05 && root2 <= xLast + 1.5;

            if (r1Valid && r2Valid)
                xZeroSec = Math.Min(root1, root2);
            else if (r1Valid)
                xZeroSec = root1;
            else if (r2Valid)
                xZeroSec = root2;
            else
                return false;
        }

        double endSpeed = -(b + 2.0 * a * xZeroSec);
        if (endSpeed < 80.0 || endSpeed > 2400.0)
            return false;

        double meanY = y0 / n;
        double ssTot = 0, ssRes = 0;
        for (int i = startIdx; i < total; i++)
        {
            double x = (_checkpoints[i].TimeMs - t0) / 1000.0;
            double y = _checkpoints[i].RemainingDeg;
            double pred = c + b * x + a * x * x;
            ssTot += (y - meanY) * (y - meanY);
            ssRes += (y - pred) * (y - pred);
        }

        rSquared = ssTot > 1e-6 ? Math.Max(0.0, 1.0 - (ssRes / ssTot)) : 0.0;
        if (rSquared < 0.96)
            return false;

        zeroTimeMs = t0 + xZeroSec * 1000.0;
        endSpeedDegPerSec = endSpeed;
        accelDegPerSec2 = -2.0 * a;
        return true;
    }

    private static void PreciseWaitUntil(double targetTimeMs)
    {
        while (true)
        {
            double rem = targetTimeMs - NowMs();
            if (rem <= 0)
                break;

            if (rem > 3.0)
                Thread.Sleep(1);
            else
                Thread.SpinWait(50);
        }
    }

    private static double ComputeBlueSliverWidthDeg(double greenEndDeg, ReadOnlySpan<int> blueBins)
    {
        int endBin = ((int)Math.Round(greenEndDeg) % 360 + 360) % 360;
        int blueDegCount = 0;
        // Count degree bins with royal-blue pixels right around the counter-clockwise end of the green zone
        for (int offset = -20; offset <= 10; offset++)
        {
            int bin = (endBin + offset + 360) % 360;
            if (blueBins[bin] >= 2)
                blueDegCount++;
        }
        return blueDegCount;
    }

    private static bool TryComputeGreenArc(double sinSum, double cosSum, ReadOnlySpan<int> bins, out double endDeg, out double widthDeg)
    {
        double meanDeg = Math.Atan2(sinSum, cosSum) * (180.0 / Math.PI);
        if (meanDeg < 0) meanDeg += 360.0;

        int centerBin = ((int)Math.Round(meanDeg)) % 360;
        int minOffset = 0;
        int maxOffset = 0;
        bool foundMin = false;
        bool foundMax = false;

        for (int minBinCount = 2; minBinCount >= 1 && !foundMin; minBinCount--)
        {
            for (int offset = -100; offset <= 20; offset++)
            {
                int bin = (centerBin + offset + 360) % 360;
                if (bins[bin] >= minBinCount)
                {
                    minOffset = offset;
                    foundMin = true;
                    break;
                }
            }
            for (int offset = 100; offset >= -20; offset--)
            {
                int bin = (centerBin + offset + 360) % 360;
                if (bins[bin] >= minBinCount)
                {
                    maxOffset = offset;
                    foundMax = true;
                    break;
                }
            }
        }

        if (!foundMin)
        {
            endDeg = meanDeg;
            widthDeg = 0;
            return false;
        }

        endDeg = (centerBin + minOffset + 360.0) % 360.0;
        widthDeg = foundMax ? Math.Max(1.0, maxOffset - minOffset) : 0;
        return true;
    }

    public Bitmap? CreateDiagnosticBitmap()
    {
        if (_bitmap == null || _size <= 0)
            return null;

        var copy = new Bitmap(_size, _size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(copy))
        {
            g.DrawImageUnscaled(_bitmap, 0, 0);
        }

        var rect = new Rectangle(0, 0, _size, _size);
        BitmapData data = copy.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                int stride = data.Stride;

                for (int y = 0; y < _size; y++)
                {
                    byte* row = ptr + (y * stride);
                    int rowOffset = y * _size;
                    for (int x = 0; x < _size; x++)
                    {
                        int idx = rowOffset + x;
                        if (_lastGreenNow[idx])
                        {
                            row[x * 4 + 0] = 255;
                            row[x * 4 + 1] = 0;
                            row[x * 4 + 2] = 255;
                        }
                        else if (_lastWhiteNow[idx])
                        {
                            row[x * 4 + 0] = 40;
                            row[x * 4 + 1] = 255;
                            row[x * 4 + 2] = 255;
                        }
                    }
                }
            }
        }
        finally
        {
            copy.UnlockBits(data);
        }

        using (var g = Graphics.FromImage(copy))
        {
            int half = _size / 2;
            int minR = (int)(_size * 0.23);
            int maxR = (int)(_size * 0.395);

            using var ringPen = new Pen(Color.FromArgb(140, 255, 255, 0), 1);
            g.DrawEllipse(ringPen, half - minR, half - minR, minR * 2, minR * 2);
            g.DrawEllipse(ringPen, half - maxR, half - maxR, maxR * 2, maxR * 2);

            // Mark the exact 9x9 bottom pixel spot used for post-click color detection
            int sx = BottomSpotLocalX > 0 ? BottomSpotLocalX : half;
            int sy = BottomSpotLocalY > 0 ? BottomSpotLocalY : half + (int)(_size * 0.315);
            using var spotPen = new Pen(Color.Orange, 2);
            g.DrawRectangle(spotPen, sx - 5, sy - 5, 10, 10);

            if (LastGreenCount > 0 || State == DetectorState.Armed)
            {
                double endRad = _greenEndDeg * (Math.PI / 180.0);
                using var endPen = new Pen(Color.Lime, 2);
                g.DrawLine(endPen, half, half,
                    half + (int)(Math.Cos(endRad) * maxR),
                    half + (int)(Math.Sin(endRad) * maxR));
            }
        }

        return copy;
    }

    private static bool IsTachoGreen(byte r, byte g, byte b)
    {
        return g >= 110 && r <= 80 && b <= 115 && (g - r) >= 50 && (g - b) >= 30;
    }

    private static bool IsFeedbackRed(byte r, byte g, byte b)
    {
        return r >= 115 && (r - g) >= 35 && (r - b) >= 35;
    }

    private static bool IsFeedbackGreen(byte r, byte g, byte b)
    {
        return g >= 105 && (g - r) >= 30 && (g - b) >= 25;
    }

    private static bool IsFeedbackBlue(byte r, byte g, byte b)
    {
        // Reject pale-blue outer glow ring (R=177..198); match vivid royal/sky blue feedback only
        return b >= 135 && r <= 120 && (b - r) >= 50 && b >= (g - 25);
    }

    private static bool IsTachoWhite(byte r, byte g, byte b)
    {
        // Pure neutral white progress bar; rejects pale-blue outer glow ring (R=198, B=255 -> B-R=57)
        return r >= 215 && g >= 215 && b >= 215 && Math.Abs(r - b) <= 25;
    }

    private void EnsureBitmaps(int size)
    {
        if (_patchBitmap == null)
        {
            _patchBitmap = new Bitmap(9, 9, PixelFormat.Format32bppArgb);
            _patchGraphics = Graphics.FromImage(_patchBitmap);
        }

        if (_bitmap != null && _size == size)
            return;

        _graphics?.Dispose();
        _bitmap?.Dispose();

        _size = size;
        _bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        _graphics = Graphics.FromImage(_bitmap);
        _lastGreenNow = new bool[size * size];
        _lastWhiteNow = new bool[size * size];
    }

    public void Dispose()
    {
        _patchGraphics?.Dispose();
        _patchBitmap?.Dispose();
        _graphics?.Dispose();
        _bitmap?.Dispose();
    }
}
