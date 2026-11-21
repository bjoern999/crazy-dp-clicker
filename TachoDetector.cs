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
    TooLateRed
}

internal sealed class TachoDetector : IDisposable
{
    private Bitmap? _bitmap;
    private Graphics? _graphics;
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

    private TachoFeedback _pendingFeedback = TachoFeedback.None;
    private double _greenEndDeg;
    private int _zeroGreenFrames;
    private DateTime _armedSince = DateTime.MinValue;
    private DateTime _clickTime = DateTime.MinValue;
    private DateTime _cooldownUntil = DateTime.MinValue;

    private int _voteRed;
    private int _voteGreen;
    private int _voteBlue;

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
        _pendingFeedback = TachoFeedback.None;
        _voteRed = 0;
        _voteGreen = 0;
        _voteBlue = 0;
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

        if (State == DetectorState.AwaitFeedback)
        {
            UpdateFeedbackAfterClick(left, top, half, minRSq, maxRSq);
            return false;
        }

        _graphics!.CopyFromScreen(left, top, 0, 0, new Size(_size, _size), CopyPixelOperation.SourceCopy);

        var rect = new Rectangle(0, 0, _size, _size);
        BitmapData data = _bitmap!.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        int greenCount = 0;
        int whiteCount = 0;

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
                            if (State == DetectorState.Armed)
                            {
                                double whiteDeg = Math.Atan2(dy, dx) * (180.0 / Math.PI);
                                if (whiteDeg < 0) whiteDeg += 360.0;

                                // Counter-clockwise in screen coords = decreasing angle toward _greenEndDeg
                                double rem = (whiteDeg - _greenEndDeg + 360.0) % 360.0;
                                // Treat up to 18 degrees past _greenEndDeg as negative (-18..0) so overshoots still trigger
                                if (rem >= 342.0) rem -= 360.0;

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
                _armedSince = DateTime.UtcNow;
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

        // Trigger as soon as the moving white front reaches leadAngleDeg before (or up to 18° past) the end of the green zone
        if (whiteCount >= 8 && minRemainingDeg <= leadAngleDeg)
        {
            State = DetectorState.AwaitFeedback;
            _clickTime = DateTime.UtcNow;
            _cooldownUntil = _clickTime.AddMilliseconds(cfg.CooldownMs);
            _voteRed = 0;
            _voteGreen = 0;
            _voteBlue = 0;
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

    private void UpdateFeedbackAfterClick(int left, int top, int half, double minRSq, double maxRSq)
    {
        double elapsedMs = (DateTime.UtcNow - _clickTime).TotalMilliseconds;

        // Wait 40 ms so ClickHoldMs (15 ms) has completed and the game rendered the feedback flash
        if (elapsedMs < 40.0)
            return;

        if (elapsedMs > 360.0)
        {
            FinalizeFeedbackVote();
            State = DetectorState.Cooldown;
            return;
        }

        _graphics!.CopyFromScreen(left, top, 0, 0, new Size(_size, _size), CopyPixelOperation.SourceCopy);

        var rect = new Rectangle(0, 0, _size, _size);
        BitmapData data = _bitmap!.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        int redCount = 0;
        int greenCount = 0;
        int blueCount = 0;
        int whiteCount = 0;

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

                        if (IsTachoWhite(r, g, b)) whiteCount++;
                        else if (IsFeedbackRed(r, g, b)) redCount++;
                        else if (IsFeedbackBlue(r, g, b)) blueCount++;
                        else if (IsTachoGreen(r, g, b)) greenCount++;
                    }
                }
            }
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }

        // The post-click flash colors the whole tacho ring, so the feedback color dominates over white
        int maxColor = Math.Max(redCount, Math.Max(greenCount, blueCount));
        if (maxColor >= 45 && maxColor > whiteCount)
        {
            if (redCount == maxColor) _voteRed++;
            else if (blueCount == maxColor) _voteBlue++;
            else if (greenCount == maxColor && greenCount >= 65) _voteGreen++;
        }

        if (_voteRed >= 2 || _voteBlue >= 2 || _voteGreen >= 2)
        {
            FinalizeFeedbackVote();
            State = DetectorState.Cooldown;
        }
    }

    private void FinalizeFeedbackVote()
    {
        int best = Math.Max(_voteRed, Math.Max(_voteGreen, _voteBlue));
        if (best <= 0)
            return;

        if (_voteRed == best)
            _pendingFeedback = TachoFeedback.TooLateRed;
        else if (_voteBlue == best)
            _pendingFeedback = TachoFeedback.OptimalBlue;
        else if (_voteGreen == best)
            _pendingFeedback = TachoFeedback.TooEarlyGreen;

        if (_pendingFeedback != TachoFeedback.None)
            LastFeedback = _pendingFeedback;
    }

    /// <summary>
    /// Finds the counter-clockwise end angle (minimum angle in screen coordinates) of the green arc.
    /// </summary>
    private static bool TryComputeGreenEndDeg(double sinSum, double cosSum, ReadOnlySpan<int> bins, out double endDeg)
    {
        double meanDeg = Math.Atan2(sinSum, cosSum) * (180.0 / Math.PI);
        if (meanDeg < 0) meanDeg += 360.0;

        int centerBin = ((int)Math.Round(meanDeg)) % 360;
        int minOffset = 0;
        bool found = false;

        // Search counter-clockwise (negative offset in screen coords) up to 100° from the green centroid
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
                            // Highlight detected green zone pixels in bright Magenta
                            row[x * 4 + 0] = 255;
                            row[x * 4 + 1] = 0;
                            row[x * 4 + 2] = 255;
                        }
                        else if (_lastWhiteNow[idx])
                        {
                            // Highlight detected white progress pixels in bright Cyan
                            row[x * 4 + 0] = 255;
                            row[x * 4 + 1] = 255;
                            row[x * 4 + 2] = 40;
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
                // Draw green end angle ray (Lime) and trigger lead angle ray (Red)
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
        return r >= 140 && g <= 90 && b <= 90 && (r - g) >= 55 && (r - b) >= 55;
    }

    private static bool IsFeedbackBlue(byte r, byte g, byte b)
    {
        return b >= 135 && r <= 125 && (b - r) >= 45;
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
        _graphics?.Dispose();
        _bitmap?.Dispose();
    }
}
