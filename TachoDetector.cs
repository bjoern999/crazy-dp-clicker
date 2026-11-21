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
    public double LastWhiteFrontDeg { get; private set; }
    public bool JustArmed { get; private set; }

    public TachoFeedback LastFeedback { get; private set; } = TachoFeedback.None;
    public int LastPeakRed { get; private set; }
    public int LastPeakGreen { get; private set; }
    public int LastPeakBlue { get; private set; }
    public int LastPeakWhite { get; private set; }
    public double LastCorrectionDelta { get; set; }
    public double LastSpeedDegPerSec => _smoothedSpeedDegPerMs * 1000.0;
    public double LastEffectiveLeadDeg { get; private set; }

    private const double ReferenceSpeedDegPerMs = 0.45; // 450 deg/sec nominal reference speed
    private double _smoothedSpeedDegPerMs;
    private double _speedSampleRemDeg = 360.0;
    private DateTime _speedSampleTime = DateTime.MinValue;

    private double _greenEndDeg;
    private double _prevRemainingDeg = 360.0;
    private int _zeroGreenFrames;
    private DateTime _armedSince = DateTime.MinValue;
    private DateTime _cooldownUntil = DateTime.MinValue;

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
        _smoothedSpeedDegPerMs = 0;
        _speedSampleRemDeg = 360.0;
        _speedSampleTime = DateTime.MinValue;
    }

    public bool Update(int centerX, int centerY, double leadAngleDeg, AppConfig cfg)
    {
        JustArmed = false;

        if (State == DetectorState.Cooldown)
        {
            if (DateTime.UtcNow < _cooldownUntil)
                return false;

            Reset();
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

        var rect = new Rectangle(0, 0, _size, _size);
        BitmapData data = _bitmap!.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        int greenCount = 0;
        int whiteCount = 0;
        int bottomWhiteCount = 0;

        double greenSinSum = 0;
        double greenCosSum = 0;
        double minRemainingDeg = 360.0;
        double bestWhiteDeg = 0.0;

        Span<int> greenAngleBins = stackalloc int[360];
        greenAngleBins.Clear();

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

                            if (State == DetectorState.Armed)
                            {
                                double whiteDeg = Math.Atan2(dy, dx) * (180.0 / Math.PI);
                                if (whiteDeg < 0) whiteDeg += 360.0;

                                // Counter-clockwise in screen coords = decreasing angle toward _greenEndDeg
                                double rem = (whiteDeg - _greenEndDeg + 360.0) % 360.0;
                                // Only treat 342..360 as negative overshoot if the white front was already close (< 55°)
                                // so the 11 o'clock starting tail never triggers at the beginning of the spin!
                                if (rem >= 342.0 && _prevRemainingDeg < 55.0)
                                    rem -= 360.0;

                                if (rem < minRemainingDeg)
                                {
                                    minRemainingDeg = rem;
                                    bestWhiteDeg = whiteDeg;
                                }
                            }
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
        LastWhiteFrontDeg = bestWhiteDeg;

        int minGreen = Math.Max(8, cfg.MinBluePixels);

        if (State == DetectorState.WaitingForBlueZone)
        {
            if (greenCount >= minGreen && TryComputeGreenEndDeg(greenSinSum, greenCosSum, greenAngleBins, out double endDeg))
            {
                _greenEndDeg = endDeg;
                _zeroGreenFrames = 0;
                _prevRemainingDeg = 360.0;
                _armedSince = DateTime.UtcNow;
                _smoothedSpeedDegPerMs = 0;
                _speedSampleRemDeg = 360.0;
                _speedSampleTime = DateTime.MinValue;
                LastEffectiveLeadDeg = leadAngleDeg;
                _flashSnapshot?.Dispose();
                _flashSnapshot = null;
                State = DetectorState.Armed;
                JustArmed = true;
            }
            return false;
        }

        // State == Armed: update _greenEndDeg while the counter-clockwise end of the green zone is still visible
        if (greenCount >= minGreen && TryComputeGreenEndDeg(greenSinSum, greenCosSum, greenAngleBins, out double updatedEndDeg))
        {
            _greenEndDeg = updatedEndDeg;
            _zeroGreenFrames = 0;
        }
        else
        {
            _zeroGreenFrames++;
        }

        LastRemainingDeg = minRemainingDeg;

        // Measure live angular velocity (deg/ms) of the white bar during the current spin
        if (whiteCount >= 12 && minRemainingDeg < 275.0 && minRemainingDeg > 5.0)
        {
            var now = DateTime.UtcNow;
            if (_speedSampleTime == DateTime.MinValue || minRemainingDeg > _speedSampleRemDeg + 5.0)
            {
                _speedSampleRemDeg = minRemainingDeg;
                _speedSampleTime = now;
            }
            else
            {
                double dtMs = (now - _speedSampleTime).TotalMilliseconds;
                double dDeg = _speedSampleRemDeg - minRemainingDeg;
                if (dtMs >= 10.0 && dDeg >= 3.5)
                {
                    if (dtMs <= 160.0 && dDeg <= 80.0)
                    {
                        double instSpeed = Math.Clamp(dDeg / dtMs, 0.15, 1.60);
                        _smoothedSpeedDegPerMs = _smoothedSpeedDegPerMs <= 0.01
                            ? instSpeed
                            : (0.60 * _smoothedSpeedDegPerMs + 0.40 * instSpeed);
                    }
                    _speedSampleRemDeg = minRemainingDeg;
                    _speedSampleTime = now;
                }
            }
        }

        // Scale the lead angle dynamically with the live measured speed of this enemy's tacho
        double speedFactor = _smoothedSpeedDegPerMs > 0.05
            ? Math.Clamp(_smoothedSpeedDegPerMs / ReferenceSpeedDegPerMs, 0.60, 2.30)
            : 1.0;
        double effectiveLeadDeg = Math.Clamp(leadAngleDeg * speedFactor, -15.0, 65.0);
        LastEffectiveLeadDeg = effectiveLeadDeg;
        _prevRemainingDeg = minRemainingDeg;

        // Trigger as soon as the moving white front has passed the bottom of the ring and reaches effectiveLeadDeg before the end of the green zone
        if (whiteCount >= 20 && bottomWhiteCount >= 6 && minRemainingDeg <= effectiveLeadDeg)
        {
            State = DetectorState.Cooldown;
            _cooldownUntil = DateTime.UtcNow.AddMilliseconds(cfg.CooldownMs);
            return true;
        }

        // Safety reset: if green disappeared for >15 frames without triggering, or armed for >3.5s, disarm cleanly
        if (_zeroGreenFrames > 15 || (DateTime.UtcNow - _armedSince).TotalSeconds > 3.5)
        {
            Reset();
            return false;
        }

        return false;
    }

    /// <summary>
    /// Called ONLY immediately after the button click for this specific Tacho.
    /// Measures the brief full-tacho color flash (Blue = optimal, Green = slightly early, Red = too late)
    /// over ~20..220 ms right after the click and freezes the peak flash image for the Live-Inspektor.
    /// </summary>
    public TachoFeedback MeasureFeedbackRightAfterClick(int centerX, int centerY, AppConfig cfg)
    {
        EnsureBitmap(cfg.TachoBoxSize);

        int half = _size / 2;
        int left = Math.Max(0, centerX - half);
        int top = Math.Max(0, centerY - half);

        double minR = _size * 0.12;
        double maxR = _size * 0.47;
        double minRSq = minR * minR;
        double maxRSq = maxR * maxR;

        int peakRed = 0;
        int peakGreen = 0;
        int peakBlue = 0;
        int peakFullWhite = 0;
        int bestOverallPeak = 0;

        DateTime start = DateTime.UtcNow;

        // Short pause after ClickAt so the game renders the post-click color flash on the tacho
        Thread.Sleep(20);

        while ((DateTime.UtcNow - start).TotalMilliseconds <= 230.0)
        {
            _graphics!.CopyFromScreen(left, top, 0, 0, new Size(_size, _size), CopyPixelOperation.SourceCopy);

            var rect = new Rectangle(0, 0, _size, _size);
            BitmapData data = _bitmap!.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            int redCount = 0;
            int greenCount = 0;
            int blueCount = 0;
            int leftGreenCount = 0;
            int whiteCount = 0;
            int rightWhiteCount = 0;

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
                                whiteCount++;
                                if (dx > 6) rightWhiteCount++;
                                continue;
                            }

                            if (IsFeedbackRed(r, g, b))
                            {
                                redCount++;
                            }
                            else if (IsFeedbackGreen(r, g, b))
                            {
                                greenCount++;
                                if (dx < -4) leftGreenCount++;
                            }
                            else if (IsFeedbackBlue(r, g, b))
                            {
                                blueCount++;
                            }
                        }
                    }
                }
            }
            finally
            {
                _bitmap.UnlockBits(data);
            }

            // For Green, require that the whole tacho flashed green (left half also has green, or green > white)
            // so we never count the small pre-click green target arc on the right!
            int validGreen = (leftGreenCount >= 25 || greenCount > whiteCount) ? greenCount : 0;

            // For White (way too early), the whole tacho jumps to white (including the right half where green was, so green disappears)
            int validFullWhite = (greenCount < 15 && rightWhiteCount >= 40 && whiteCount >= 120) ? whiteCount : 0;

            if (redCount > peakRed) peakRed = redCount;
            if (validGreen > peakGreen) peakGreen = validGreen;
            if (blueCount > peakBlue) peakBlue = blueCount;
            if (validFullWhite > peakFullWhite) peakFullWhite = validFullWhite;

            int currentColorMax = Math.Max(redCount, Math.Max(validGreen, blueCount));
            int currentMax = Math.Max(currentColorMax, validFullWhite);
            if (currentMax > bestOverallPeak && currentMax >= 60)
            {
                bestOverallPeak = currentMax;
                _flashSnapshot?.Dispose();
                _flashSnapshot = (Bitmap)_bitmap.Clone();
            }

            // Once we have captured a strong color flash peak (>= 120 px) and it starts fading, finish immediately
            if (currentColorMax >= 120 && currentColorMax < bestOverallPeak / 2)
                break;

            Thread.Sleep(10);
        }

        LastPeakRed = peakRed;
        LastPeakGreen = peakGreen;
        LastPeakBlue = peakBlue;
        LastPeakWhite = peakFullWhite;

        int maxColorPeak = Math.Max(peakRed, Math.Max(peakGreen, peakBlue));
        if (maxColorPeak >= 60)
        {
            if (peakRed == maxColorPeak)
                LastFeedback = TachoFeedback.TooLateRed;
            else if (peakBlue == maxColorPeak)
                LastFeedback = TachoFeedback.OptimalBlue;
            else
                LastFeedback = TachoFeedback.TooEarlyGreen;

            return LastFeedback;
        }

        if (peakFullWhite >= 120)
        {
            LastFeedback = TachoFeedback.WayTooEarlyWhite;
            return LastFeedback;
        }

        return TachoFeedback.None;
    }

    private static bool TryComputeGreenEndDeg(double sinSum, double cosSum, ReadOnlySpan<int> bins, out double endDeg)
    {
        double meanDeg = Math.Atan2(sinSum, cosSum) * (180.0 / Math.PI);
        if (meanDeg < 0) meanDeg += 360.0;

        int centerBin = ((int)Math.Round(meanDeg)) % 360;
        int minOffset = 0;
        bool found = false;

        for (int offset = -100; offset <= 20; offset++)
        {
            int bin = (centerBin + offset + 360) % 360;
            if (bins[bin] >= 2)
            {
                minOffset = offset;
                found = true;
                break;
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

    public Bitmap? CreateDiagnosticBitmap(double leadAngleDeg)
    {
        // While in Cooldown right after a click, show the frozen post-click flash image if captured!
        if (State == DetectorState.Cooldown && _flashSnapshot != null)
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
                double triggerRad = (_greenEndDeg + leadAngleDeg) * (Math.PI / 180.0);

                using var endPen = new Pen(Color.Lime, 2);
                using var trigPen = new Pen(Color.Red, 2);

                g.DrawLine(endPen, half, half,
                    half + (int)(Math.Cos(endRad) * maxR),
                    half + (int)(Math.Sin(endRad) * maxR));

                g.DrawLine(trigPen, half, half,
                    half + (int)(Math.Cos(triggerRad) * maxR),
                    half + (int)(Math.Sin(triggerRad) * maxR));
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
