using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;

namespace TachoClicker;

internal enum DetectorState
{
    WaitingForBlueZone,
    Armed,
    AwaitFeedback,
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
    private Bitmap? _flashSnapshot;
    private int _size;
    private bool[] _lastGreenNow = Array.Empty<bool>();
    private bool[] _lastWhiteNow = Array.Empty<bool>();

    public DetectorState State { get; private set; } = DetectorState.WaitingForBlueZone;
    public int LastBlueCount { get; private set; } // Stores detected green pixel count
    public int LastWhiteCount { get; private set; }
    public double LastRemainingDeg { get; private set; } = 360.0;
    public double LastTargetEndDeg => _greenEndDeg;
    public bool JustArmed { get; private set; }

    public TachoFeedback LastFeedback { get; private set; } = TachoFeedback.None;
    public int LastPeakRed { get; private set; }
    public int LastPeakGreen { get; private set; }
    public int LastPeakBlue { get; private set; }
    public int LastPeakWhite { get; private set; }
    public double LastCorrectionDeltaMs { get; set; }

    public double LastSpeedDegPerSec { get; private set; }
    public int LastCheckpointCount => _checkpoints.Count;

    private TachoFeedback _pendingFeedback = TachoFeedback.None;
    private readonly List<AngleCheckpoint> _checkpoints = new(32);
    private double _nextCheckpointDeg = -1;
    private double _greenEndDeg;
    private int _greenStableFrames;
    private bool _greenEndLocked;
    private double _prevRemainingDeg = 360.0;
    private int _zeroGreenFrames;
    private DateTime _armedSince = DateTime.MinValue;
    private DateTime _clickTime = DateTime.MinValue;
    private DateTime _lastFeedbackSample = DateTime.MinValue;
    private DateTime _cooldownUntil = DateTime.MinValue;

    private int _peakRed;
    private int _peakGreen;
    private int _peakBlue;
    private int _peakWhite;
    private int _bestFlashScore;

    private static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    public void Reset()
    {
        State = DetectorState.WaitingForBlueZone;
        Array.Clear(_lastGreenNow);
        Array.Clear(_lastWhiteNow);
        LastBlueCount = 0;
        LastWhiteCount = 0;
        LastRemainingDeg = 360.0;
        JustArmed = false;
        _zeroGreenFrames = 0;
        _prevRemainingDeg = 360.0;
        _greenStableFrames = 0;
        _greenEndLocked = false;
        _checkpoints.Clear();
        _nextCheckpointDeg = -1;
        _pendingFeedback = TachoFeedback.None;
    }

    public bool ConsumeFeedback(out TachoFeedback feedback)
    {
        if (_pendingFeedback != TachoFeedback.None)
        {
            feedback = _pendingFeedback;
            _pendingFeedback = TachoFeedback.None;
            return true;
        }
        feedback = TachoFeedback.None;
        return false;
    }

    public void StartPostClickFeedback(AppConfig cfg)
    {
        State = DetectorState.AwaitFeedback;
        _clickTime = DateTime.UtcNow;
        _lastFeedbackSample = DateTime.MinValue;
        _cooldownUntil = _clickTime.AddMilliseconds(cfg.CooldownMs);
        _peakRed = 0;
        _peakGreen = 0;
        _peakBlue = 0;
        _peakWhite = 0;
        _bestFlashScore = 0;
    }

    /// <summary>
    /// Non-blocking post-click color feedback check (30 ms .. 165 ms after click).
    /// Catches even very short flashes on Tacho 2 without blocking the next Tacho!
    /// </summary>
    public void PollPostClickFeedback(int centerX, int centerY, AppConfig cfg)
    {
        if (State == DetectorState.Cooldown)
        {
            if (DateTime.UtcNow >= _cooldownUntil)
                Reset();
            return;
        }

        if (State != DetectorState.AwaitFeedback || centerX <= 0 || centerY <= 0)
            return;

        var now = DateTime.UtcNow;
        double elapsedMs = (now - _clickTime).TotalMilliseconds;

        // Wait 30 ms after ClickAt so the game renders the first post-click flash frame
        if (elapsedMs < 30.0)
            return;

        if (elapsedMs > 165.0)
        {
            FinalizeFeedback();
            State = DetectorState.Cooldown;
            return;
        }

        if ((now - _lastFeedbackSample).TotalMilliseconds < 10.0)
            return;
        _lastFeedbackSample = now;

        EnsureBitmap(cfg.TachoBoxSize);

        int half = _size / 2;
        int left = Math.Max(0, centerX - half);
        int top = Math.Max(0, centerY - half);

        double minR = _size * 0.12;
        double maxR = _size * 0.47;
        double minRSq = minR * minR;
        double maxRSq = maxR * maxR;

        _graphics!.CopyFromScreen(left, top, 0, 0, new Size(_size, _size), CopyPixelOperation.SourceCopy);

        var rect = new Rectangle(0, 0, _size, _size);
        BitmapData data = _bitmap!.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        int leftRed = 0;
        int leftGreen = 0;
        int leftBlue = 0;
        int totalGreen = 0;
        int totalWhite = 0;
        int rightWhite = 0;

        try
        {
            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                int stride = data.Stride;

                for (int y = 0; y < _size; y++)
                {
                    byte* row = ptr + (y * stride);
                    double dy = y - half;
                    double dySq = dy * dy;

                    for (int x = 0; x < _size; x++)
                    {
                        double dx = x - half;
                        double distSq = dx * dx + dySq;
                        if (distSq < minRSq || distSq > maxRSq)
                            continue;

                        byte b = row[x * 4 + 0];
                        byte g = row[x * 4 + 1];
                        byte r = row[x * 4 + 2];

                        if (IsTachoWhite(r, g, b))
                        {
                            totalWhite++;
                            if (dx > 6) rightWhite++;
                            continue;
                        }

                        if (IsFeedbackRed(r, g, b))
                        {
                            if (dx < -4) leftRed++;
                        }
                        else if (IsFeedbackGreen(r, g, b))
                        {
                            totalGreen++;
                            if (dx < -4) leftGreen++;
                        }
                        else if (IsFeedbackBlue(r, g, b))
                        {
                            if (dx < -4) leftBlue++;
                        }
                    }
                }
            }
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }

        // Before the click, the left half (dx < -4) is 100% WHITE (0 Red, 0 Green, 0 Blue).
        // When the whole Tacho flashes its result color, the left half immediately turns Red, Green, or Blue!
        // For White (way too early), the whole ring jumps to white (right half white, green gone).
        int fullWhiteScore = (totalGreen < 12 && rightWhite >= 40 && totalWhite >= 120) ? totalWhite / 2 : 0;

        if (leftRed > _peakRed) _peakRed = leftRed;
        if (leftGreen > _peakGreen) _peakGreen = leftGreen;
        if (leftBlue > _peakBlue) _peakBlue = leftBlue;
        if (fullWhiteScore > _peakWhite) _peakWhite = fullWhiteScore;

        int colorScore = Math.Max(leftRed, Math.Max(leftGreen, leftBlue));
        int frameScore = Math.Max(colorScore, fullWhiteScore);

        if (frameScore > _bestFlashScore && frameScore >= 25)
        {
            _bestFlashScore = frameScore;
            _flashSnapshot?.Dispose();
            _flashSnapshot = (Bitmap)_bitmap.Clone();
            using var g = Graphics.FromImage(_flashSnapshot);
            int minRi = (int)minR;
            int maxRi = (int)maxR;
            using var ringPen = new Pen(Color.FromArgb(140, 255, 255, 0), 1);
            g.DrawEllipse(ringPen, half - minRi, half - minRi, minRi * 2, minRi * 2);
            g.DrawEllipse(ringPen, half - maxRi, half - maxRi, maxRi * 2, maxRi * 2);
        }

        // If we already captured a clear color flash (>= 60 px on the left ring half) and it started fading, finish immediately
        if (elapsedMs >= 75.0 && _bestFlashScore >= 60 && frameScore < _bestFlashScore / 2)
        {
            FinalizeFeedback();
            State = DetectorState.Cooldown;
        }
    }

    private void FinalizeFeedback()
    {
        LastPeakRed = _peakRed;
        LastPeakGreen = _peakGreen;
        LastPeakBlue = _peakBlue;
        LastPeakWhite = _peakWhite;

        int maxColor = Math.Max(_peakRed, Math.Max(_peakGreen, _peakBlue));
        if (maxColor >= 25)
        {
            if (_peakBlue == maxColor)
                _pendingFeedback = TachoFeedback.OptimalBlue;
            else if (_peakGreen == maxColor)
                _pendingFeedback = TachoFeedback.TooEarlyGreen;
            else
                _pendingFeedback = TachoFeedback.TooLateRed;

            LastFeedback = _pendingFeedback;
            return;
        }

        if (_peakWhite >= 60)
        {
            _pendingFeedback = TachoFeedback.WayTooEarlyWhite;
            LastFeedback = _pendingFeedback;
        }
    }

    public bool Update(int centerX, int centerY, double clickOffsetMs, AppConfig cfg)
    {
        JustArmed = false;

        if (State == DetectorState.AwaitFeedback || State == DetectorState.Cooldown)
        {
            PollPostClickFeedback(centerX, centerY, cfg);
            return false;
        }

        if (centerX <= 0 || centerY <= 0)
            return false;

        EnsureBitmap(cfg.TachoBoxSize);

        int half = _size / 2;
        int left = Math.Max(0, centerX - half);
        int top = Math.Max(0, centerY - half);

        double minR = _size * 0.12;
        double maxR = _size * 0.47;
        double minRSq = minR * minR;
        double maxRSq = maxR * maxR;

        _graphics!.CopyFromScreen(left, top, 0, 0, new Size(_size, _size), CopyPixelOperation.SourceCopy);
        double captureTimeMs = NowMs();

        var rect = new Rectangle(0, 0, _size, _size);
        BitmapData data = _bitmap!.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        int greenCount = 0;
        int whiteCount = 0;
        int bottomWhiteCount = 0;

        double greenSinSum = 0;
        double greenCosSum = 0;

        Span<int> greenAngleBins = stackalloc int[360];
        Span<int> whiteAngleBins = stackalloc int[360];
        greenAngleBins.Clear();
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

                        if (distSq < minRSq || distSq > maxRSq)
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

                        if (isWhite)
                        {
                            whiteCount++;
                            if (dy > 15) bottomWhiteCount++;

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

        LastBlueCount = greenCount;
        LastWhiteCount = whiteCount;

        int minGreen = Math.Max(4, cfg.MinGreenPixels);

        if (State == DetectorState.WaitingForBlueZone)
        {
            if (greenCount >= minGreen && TryComputeGreenEndDeg(greenSinSum, greenCosSum, greenAngleBins, out double endDeg))
            {
                _greenEndDeg = endDeg;
                _greenStableFrames = 1;
                _greenEndLocked = false;
                _zeroGreenFrames = 0;
                _prevRemainingDeg = 360.0;
                _armedSince = DateTime.UtcNow;
                _checkpoints.Clear();
                _nextCheckpointDeg = -1;
                LastSpeedDegPerSec = 0;
                _flashSnapshot?.Dispose();
                _flashSnapshot = null;
                State = DetectorState.Armed;
                JustArmed = true;
            }
            return false;
        }

        // State == Armed: refine _greenEndDeg for the first 3 frames so pop-in animation is complete before locking it
        if (greenCount >= minGreen)
        {
            _zeroGreenFrames = 0;
            if (!_greenEndLocked && TryComputeGreenEndDeg(greenSinSum, greenCosSum, greenAngleBins, out double updatedEndDeg))
            {
                _greenEndDeg = updatedEndDeg;
                _greenStableFrames++;
                if (_greenStableFrames >= 3)
                    _greenEndLocked = true;
            }
        }
        else
        {
            _zeroGreenFrames++;
        }

        // Compute noise-free leading front edge of the white progress bar using whiteAngleBins (>= 4 px per degree bin)
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

        // 2. Extrapolate the exact arrival time at RemainingDeg = 0° (end of green / blue optimum) using linear regression
        if (bottomWhiteCount >= 6 && TryPredictArrivalTimeMs(out double predictedZeroTimeMs, out double speedDegPerMs))
        {
            LastSpeedDegPerSec = speedDegPerMs * 1000.0;
            double now = NowMs();
            double clickTargetTimeMs = predictedZeroTimeMs - clickOffsetMs;
            double waitMs = clickTargetTimeMs - now;

            // Once we are within <= 26 ms of the exact extrapolated click moment, wait with sub-ms Stopwatch precision
            if (minRemainingDeg <= 105.0 && waitMs <= 26.0)
            {
                if (waitMs > 0.5)
                {
                    PreciseWaitUntil(clickTargetTimeMs);
                }

                return true;
            }
        }
        else if (whiteCount >= 20 && bottomWhiteCount >= 6 && minRemainingDeg <= 16.0)
        {
            // Safety fallback if too few checkpoints were collected
            return true;
        }

        // Safety reset: if green disappeared for >18 frames without triggering, or armed for >3.5s, disarm cleanly
        if (_zeroGreenFrames > 18 || (DateTime.UtcNow - _armedSince).TotalSeconds > 3.5)
        {
            Reset();
            return false;
        }

        return false;
    }

    /// <summary>
    /// Walks clockwise from greenEndDeg to find the exact noise-free leading edge of the white progress bar.
    /// Requiring >= 4 white pixels in a 1° bin rejects all single-pixel noise and anti-aliasing artifacts.
    /// </summary>
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

    private bool TryPredictArrivalTimeMs(out double zeroTimeMs, out double speedDegPerMs)
    {
        zeroTimeMs = 0;
        speedDegPerMs = 0;

        int total = _checkpoints.Count;
        if (total < 4)
            return false;

        int startIdx = Math.Max(0, total - 12);
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
        for (int i = startIdx; i < total; i++)
        {
            double dx = (_checkpoints[i].TimeMs - t0) - meanX;
            double dy = _checkpoints[i].RemainingDeg - meanY;
            num += dx * dy;
            den += dx * dx;
        }

        if (den <= 1e-6)
            return false;

        double slope = num / den; // deg / ms (negative)
        if (slope >= -0.08 || slope <= -2.4)
            return false;

        speedDegPerMs = -slope;
        double xZero = meanX + (meanY / speedDegPerMs);
        zeroTimeMs = t0 + xZero;
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

    private static bool TryComputeGreenEndDeg(double sinSum, double cosSum, ReadOnlySpan<int> bins, out double endDeg)
    {
        double meanDeg = Math.Atan2(sinSum, cosSum) * (180.0 / Math.PI);
        if (meanDeg < 0) meanDeg += 360.0;

        int centerBin = ((int)Math.Round(meanDeg)) % 360;
        int minOffset = 0;
        bool found = false;

        for (int minBinCount = 2; minBinCount >= 1 && !found; minBinCount--)
        {
            for (int offset = -100; offset <= 20; offset++)
            {
                int bin = (centerBin + offset + 360) % 360;
                if (bins[bin] >= minBinCount)
                {
                    minOffset = offset;
                    found = true;
                    break;
                }
            }
        }

        if (!found)
        {
            endDeg = meanDeg;
            return false;
        }

        endDeg = (centerBin + minOffset + 360.0) % 360.0;
        return true;
    }

    public Bitmap? CreateDiagnosticBitmap()
    {
        if ((State == DetectorState.AwaitFeedback || State == DetectorState.Cooldown) && _flashSnapshot != null)
        {
            return (Bitmap)_flashSnapshot.Clone();
        }

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
            int minR = (int)(_size * 0.12);
            int maxR = (int)(_size * 0.47);

            using var ringPen = new Pen(Color.FromArgb(120, 255, 255, 0), 1);
            g.DrawEllipse(ringPen, half - minR, half - minR, minR * 2, minR * 2);
            g.DrawEllipse(ringPen, half - maxR, half - maxR, maxR * 2, maxR * 2);

            if (LastBlueCount > 0 || State == DetectorState.Armed)
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
        return b >= 120 && (b - r) >= 35 && b >= (g - 25);
    }

    private static bool IsTachoWhite(byte r, byte g, byte b)
    {
        return r >= 205 && g >= 205 && b >= 205;
    }

    private void EnsureBitmap(int size)
    {
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
        _flashSnapshot?.Dispose();
        _graphics?.Dispose();
        _bitmap?.Dispose();
    }
}
