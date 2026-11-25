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
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    private readonly DxgiScreenCapture _capture;
    private readonly bool _ownsCapture;

    private byte[] _pixelBuffer = Array.Empty<byte>();
    private readonly byte[] _patchBuffer = new byte[9 * 9 * 4];
    private int _size;
    private bool[] _lastGreenNow = Array.Empty<bool>();
    private bool[] _lastWhiteNow = Array.Empty<bool>();

    // Vorberechnete Polar-Tabellen fuer Zero-Trig Pixel-Scans (< 0,08 ms pro 180x180 Frame!)
    private byte[] _pixelZone = Array.Empty<byte>(); // 0 = outer, 1 = center, 2 = ring
    private int[] _pixelAngleBin = Array.Empty<int>();
    private float[] _pixelSin = Array.Empty<float>();
    private float[] _pixelCos = Array.Empty<float>();
    private bool[] _pixelInBottomSector = Array.Empty<bool>();

    private bool _hasInitialStaticFrame;
    private int _lastCapLeft = -1;
    private int _lastCapTop = -1;

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
    public double LastPredictedZeroTimeMs { get; private set; }
    public double LastPhaseLockMadMs { get; private set; } = -1.0;
    public string LastCaptureSource { get; private set; } = "-";
    public double LastAvgCaptureMs { get; private set; }
    public double LastClickVSyncPhaseMs { get; set; } = -1.0;
    public double LastRefreshPeriodMs { get; set; }
    public long LastDwmFramesPending { get; set; }
    public long LastDwmFramesDropped { get; set; }
    public TachoFingerprint LastFingerprint { get; private set; }
    public bool JustAbortedWithoutClick { get; private set; }
    public string LastAbortReason { get; private set; } = "";
    public string LastAbortCheckpointSummary { get; private set; } = "";
    public double LastAbortMinRemDeg { get; private set; }
    public int LastAbortRedOnBottom { get; private set; }

    public TachoDetector(DxgiScreenCapture? sharedCapture = null)
    {
        if (sharedCapture != null)
        {
            _capture = sharedCapture;
            _ownsCapture = false;
        }
        else
        {
            _capture = new DxgiScreenCapture();
            _ownsCapture = true;
        }
    }

    public string FormatCheckpointsSummary()
    {
        if (_checkpoints.Count == 0) return "[]";
        double t0 = _checkpoints[0].TimeMs;
        if (_checkpoints.Count <= 28)
        {
            return "[" + string.Join(", ", _checkpoints.Select(c => $"{c.TimeMs - t0:F1}ms:{c.RemainingDeg:F1}°")) + "]";
        }

        // Bei 40-70 DXGI-Frames kompakt aber mit voller Sub-Grad-Aufloesung am Start & Ende ausgeben
        var parts = new List<string>(28);
        int lastIdx = _checkpoints.Count - 1;
        for (int i = 0; i < _checkpoints.Count; i++)
        {
            if (i < 4 || i >= lastIdx - 7 || (i % 3 == 0))
            {
                var c = _checkpoints[i];
                parts.Add($"{c.TimeMs - t0:F1}ms:{c.RemainingDeg:F1}°");
            }
        }
        return $"[n={_checkpoints.Count}: " + string.Join(", ", parts) + "]";
    }

    public List<(double TimeMs, double RemainingDeg)> GetLastCheckpointsCopy()
    {
        var list = new List<(double TimeMs, double RemainingDeg)>(_checkpoints.Count);
        for (int i = 0; i < _checkpoints.Count; i++)
            list.Add((_checkpoints[i].TimeMs, _checkpoints[i].RemainingDeg));
        return list;
    }

    // Exact measured bottom-of-ring pixel position (6 o'clock) in local box coordinates
    public int BottomSpotLocalX { get; private set; }
    public int BottomSpotLocalY { get; private set; }

    private readonly List<AngleCheckpoint> _checkpoints = new(80);
    private double _greenEndDeg;
    private int _greenStableFrames;
    private bool _greenEndLocked;
    private bool _setupResolvedForSpin;
    private double _activeSpinClickOffsetMs;
    private TachoTemplateCurve? _activeSpinTemplateCurve;
    private double _prevRemainingDeg = 360.0;
    private int _zeroGreenFrames;
    private DateTime _armedSince = DateTime.MinValue;
    private DateTime _cooldownUntil = DateTime.MinValue;

    // Zwischengespeicherte Vorhersage fuer Drop-Frame-Immunitaet bei DXGI WAIT_TIMEOUT
    private bool _hasActivePrediction;
    private double _cachedEffectiveZeroTimeMs;
    private double _cachedPredictedZeroTimeMs;
    private double _cachedUsedOffsetMs;
    private bool _cachedUsedPhaseLock;
    private int _cachedPhaseLockPts;
    private double _cachedPhaseLockMad;
    private bool _cachedUsedQuadratic;
    private int _lastBottomWhiteCount;
    private double _spinCaptureMsSum;
    private int _spinCaptureCount;

    private static double NowMs() => Stopwatch.GetTimestamp() * TicksToMs;

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
        _activeSpinTemplateCurve = null;
        LastPhaseLockMadMs = -1.0;
        _hasInitialStaticFrame = false;
        _hasActivePrediction = false;
        _cachedEffectiveZeroTimeMs = 0;
        _cachedPredictedZeroTimeMs = 0;
        _cachedUsedOffsetMs = 0;
        _lastBottomWhiteCount = 0;
        _spinCaptureMsSum = 0;
        _spinCaptureCount = 0;
        _checkpoints.Clear();
    }

    public bool Update(
        int centerX,
        int centerY,
        double defaultClickOffsetMs,
        AppConfig cfg,
        Func<TachoFingerprint, (double OffsetMs, TachoTemplateCurve? TemplateCurve)>? onFingerprintReady = null)
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

        EnsureBuffers(cfg.TachoBoxSize);

        int half = _size / 2;
        int left = Math.Max(0, centerX - half);
        int top = Math.Max(0, centerY - half);

        bool movedTarget = left != _lastCapLeft || top != _lastCapTop;
        int timeoutMs = State == DetectorState.Armed ? 3 : 0;
        bool allowGdiIfNoNewFrame = State != DetectorState.Armed && (!_hasInitialStaticFrame || movedTarget);

        var cap = _capture.CaptureRegion(left, top, _size, _size, _pixelBuffer, timeoutMs, allowGdiIfNoNewFrame);

        if (!cap.HasPixels)
        {
            // Kein neues GPU-Frame in diesem 3ms-Fenster:
            // Falls wir im Armed-Spin bereits einen sauberen Lock/Vorhersage haben, pruefen wir dennoch,
            // ob das Spin-Wait-Fenster (<= 36 ms) erreicht ist (100% immun gegen Browser-Frame-Drops!).
            if (State == DetectorState.Armed)
            {
                if (_hasActivePrediction && _cachedEffectiveZeroTimeMs > 0 && _lastBottomWhiteCount >= 6 && _prevRemainingDeg <= 105.0)
                {
                    double targetMs = _cachedEffectiveZeroTimeMs - _cachedUsedOffsetMs;
                    double waitDrop = targetMs - NowMs();
                    if (waitDrop <= 36.0)
                    {
                        LastUsedClickOffsetMs = _cachedUsedOffsetMs;
                        LastPredictedZeroTimeMs = _cachedPredictedZeroTimeMs;
                        LastPhaseLockMadMs = _cachedUsedPhaseLock ? _cachedPhaseLockMad : -1.0;
                        LastTriggerReason = _cachedUsedPhaseLock
                            ? $"LOCK(n={_cachedPhaseLockPts},mad={_cachedPhaseLockMad:F2})"
                            : (_cachedUsedQuadratic ? "QUAD" : "LINEAR");
                        LastRemDegAtTrigger = _prevRemainingDeg;
                        LastWaitMsAtTrigger = waitDrop;

                        if (waitDrop > 0.5)
                            PreciseWaitUntil(targetMs);

                        State = DetectorState.Cooldown;
                        _cooldownUntil = DateTime.UtcNow.AddMilliseconds(cfg.CooldownMs);
                        return true;
                    }
                }

                if ((DateTime.UtcNow - _armedSince).TotalSeconds > 3.5)
                {
                    JustAbortedWithoutClick = true;
                    LastAbortReason = "MAX_TIME_3.5S";
                    LastAbortCheckpointSummary = FormatCheckpointsSummary();
                    LastAbortMinRemDeg = _prevRemainingDeg;
                    LastAbortRedOnBottom = 0;
                    Reset();
                }
            }
            return false;
        }

        _hasInitialStaticFrame = true;
        _lastCapLeft = left;
        _lastCapTop = top;
        LastCaptureSource = cap.Source;
        if (State == DetectorState.Armed)
        {
            _spinCaptureMsSum += cap.CaptureDurationMs;
            _spinCaptureCount++;
            LastAvgCaptureMs = _spinCaptureMsSum / _spinCaptureCount;
        }
        else
        {
            LastAvgCaptureMs = cap.CaptureDurationMs;
        }

        double nowAfterCap = NowMs();
        double captureTimeMs = (cap.IsNewFrame && cap.FrameQpcTimestamp > 0)
            ? cap.FrameQpcTimestamp * TicksToMs
            : nowAfterCap;
        if (Math.Abs(nowAfterCap - captureTimeMs) > 200.0)
            captureTimeMs = nowAfterCap;

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
        Span<double> whiteWeightBins = stackalloc double[360];
        greenAngleBins.Clear();
        blueAngleBins.Clear();
        whiteAngleBins.Clear();
        whiteWeightBins.Clear();

        unsafe
        {
            fixed (byte* buf = _pixelBuffer)
            {
                int totalPixels = _size * _size;
                for (int idx = 0; idx < totalPixels; idx++)
                {
                    byte zone = _pixelZone[idx];
                    if (zone == 0)
                    {
                        _lastGreenNow[idx] = false;
                        _lastWhiteNow[idx] = false;
                        continue;
                    }

                    int p = idx * 4;
                    byte b = buf[p];
                    byte g = buf[p + 1];
                    byte r = buf[p + 2];

                    if (zone == 1)
                    {
                        _lastGreenNow[idx] = false;
                        _lastWhiteNow[idx] = false;
                        centerPixels++;
                        if (IsTachoWhite(r, g, b)) centerWhitePixels++;
                        continue;
                    }

                    // zone == 2: Ring-Bereich (minR..maxR)
                    bool isGreen = IsTachoGreen(r, g, b);
                    bool isWhite = IsTachoWhite(r, g, b);

                    _lastGreenNow[idx] = isGreen;
                    _lastWhiteNow[idx] = isWhite;

                    int bin = _pixelAngleBin[idx];

                    if (isGreen)
                    {
                        greenCount++;
                        greenSinSum += _pixelSin[idx];
                        greenCosSum += _pixelCos[idx];
                        greenAngleBins[bin]++;
                    }
                    else if (IsFeedbackBlue(r, g, b))
                    {
                        blueSliverCount++;
                        blueAngleBins[bin]++;
                    }
                    else if (_pixelInBottomSector[idx] && IsFeedbackRed(r, g, b))
                    {
                        bottomRedCount++;
                    }

                    if (isWhite)
                    {
                        whiteCount++;
                        if (_pixelInBottomSector[idx])
                        {
                            bottomWhiteCount++;
                            bottomWhiteSumX += idx % _size;
                            bottomWhiteSumY += idx / _size;
                        }
                        whiteAngleBins[bin]++;
                        whiteWeightBins[bin] += 1.0;
                    }
                    else if (TryGetAntiAliasedWhiteWeight(r, g, b, out double aaWeight))
                    {
                        whiteWeightBins[bin] += aaWeight;
                    }
                }
            }
        }

        LastGreenCount = greenCount;
        LastWhiteCount = whiteCount;
        _lastBottomWhiteCount = bottomWhiteCount;

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

        // Even a narrow 18° green zone has ~380 green pixels on 4K; requiring >= 35 px and Width 6°..85°
        // plus upper-right/right sector (245°..360° or 0°..55°) completely prevents green character sprites from arming!
        int minGreen = Math.Max(35, cfg.MinGreenPixels);

        if (State == DetectorState.WaitingForBlueZone)
        {
            if (greenCount >= minGreen && greenCount <= 2800 &&
                TryComputeGreenArc(greenSinSum, greenCosSum, greenAngleBins, out double endDeg, out double widthDeg) &&
                widthDeg >= 6.0 && widthDeg <= 85.0 &&
                IsValidGreenEndAngle(endDeg))
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
                _activeSpinTemplateCurve = null;
                LastPhaseLockMadMs = -1.0;
                _zeroGreenFrames = 0;
                _prevRemainingDeg = 360.0;
                _armedSince = DateTime.UtcNow;
                _hasActivePrediction = false;
                _cachedEffectiveZeroTimeMs = 0;
                _cachedPredictedZeroTimeMs = 0;
                _spinCaptureMsSum = cap.CaptureDurationMs;
                _spinCaptureCount = 1;
                _checkpoints.Clear();
                LastSpeedDegPerSec = 0;
                LastAccelDegPerSec2 = 0;
                LastRegressionR2 = 0;
                State = DetectorState.Armed;
                JustArmed = true;
            }
            return false;
        }

        // State == Armed: refine _greenEndDeg for the first 3 unique frames before locking it
        if (greenCount >= minGreen && greenCount <= 2800)
        {
            _zeroGreenFrames = 0;
            if (!_greenEndLocked && cap.IsNewFrame &&
                TryComputeGreenArc(greenSinSum, greenCosSum, greenAngleBins, out double updatedEndDeg, out double updatedWidthDeg) &&
                updatedWidthDeg >= 6.0 && updatedWidthDeg <= 85.0 &&
                IsValidGreenEndAngle(updatedEndDeg))
            {
                // Sanfte Mittelung ueber die ersten 3 Frames fuer ±0,05°-stabile gruene Endkante
                _greenEndDeg = _greenStableFrames == 0
                    ? updatedEndDeg
                    : BlendAngleDeg(_greenEndDeg, updatedEndDeg, 0.5);
                LastGreenWidthDeg = updatedWidthDeg;
                double bw = ComputeBlueSliverWidthDeg(_greenEndDeg, blueAngleBins);
                if (bw > LastBlueWidthDeg) LastBlueWidthDeg = bw;
                if (greenCount > LastInitialGreenCount) LastInitialGreenCount = greenCount;
                if (blueSliverCount > LastBlueSliverCount) LastBlueSliverCount = blueSliverCount;
                _greenStableFrames++;
                if (_greenStableFrames >= 3)
                    _greenEndLocked = true;
            }
        }
        else if (cap.IsNewFrame)
        {
            _zeroGreenFrames++;
        }

        double minRemainingDeg = FindWhiteFrontRemainingDeg(_greenEndDeg, whiteAngleBins, whiteWeightBins, _prevRemainingDeg);
        LastRemainingDeg = minRemainingDeg;

        // 1. JEDES echte, Hardware-gestempelte 60-Hz-Frame als Sub-Grad-Checkpoint erfassen (~40-70 Punkte/Spin!)
        if (cap.IsNewFrame && _greenEndLocked && whiteCount >= 15 && minRemainingDeg < 280.0 && minRemainingDeg > 2.0)
        {
            if (_checkpoints.Count == 0)
            {
                _checkpoints.Add(new AngleCheckpoint { TimeMs = captureTimeMs, RemainingDeg = minRemainingDeg });
            }
            else
            {
                var last = _checkpoints[_checkpoints.Count - 1];
                double dt = captureTimeMs - last.TimeMs;
                double dDeg = last.RemainingDeg - minRemainingDeg;
                if (dt >= 8.0 && dDeg >= 1.2)
                {
                    double instSpeed = dDeg / dt;
                    if (instSpeed >= 0.06 && instSpeed <= 2.5)
                    {
                        _checkpoints.Add(new AngleCheckpoint { TimeMs = captureTimeMs, RemainingDeg = minRemainingDeg });
                    }
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

            // Identify character setup ONCE at rem <= 55° (fully converged speed/accel + exact T55 spin time, still ~65-175 ms BEFORE click!)
            if (!_setupResolvedForSpin && onFingerprintReady != null && _checkpoints.Count >= 8 && minRemainingDeg <= 55.0)
            {
                _setupResolvedForSpin = true;
                double spinTime55 = ComputeSpinTimeToDegMs(55.0, captureTimeMs, minRemainingDeg);
                var fp = new TachoFingerprint
                {
                    GreenEndDeg = _greenEndDeg,
                    GreenWidthDeg = LastGreenWidthDeg,
                    BlueWidthDeg = LastBlueWidthDeg,
                    EndSpeedDegPerSec = endSpeedDegPerSec,
                    AccelDegPerSec2 = accelDegPerSec2,
                    SpinTimeTo55Ms = spinTime55
                };
                LastFingerprint = fp;
                var resolved = onFingerprintReady(fp);
                _activeSpinClickOffsetMs = resolved.OffsetMs;
                _activeSpinTemplateCurve = resolved.TemplateCurve;
            }

            // 2b. 1-Parameter-Phase-Lock ueber ALLE ~40-70 VBlank-gestempelten Sub-Grad-Checkpoints!
            bool usedPhaseLock = false;
            int phaseLockPts = 0;
            double phaseLockMad = -1.0;
            double effectiveZeroTimeMs = predictedZeroTimeMs;

            if (_activeSpinTemplateCurve != null && _activeSpinTemplateCurve.IsReady)
            {
                var cpList = GetLastCheckpointsCopy();
                if (_activeSpinTemplateCurve.TryPredictZeroTimeMs(cpList, out double lockZeroMs, out phaseLockPts, out phaseLockMad))
                {
                    effectiveZeroTimeMs = lockZeroMs;
                    usedPhaseLock = true;
                }
            }

            double usedOffset = _setupResolvedForSpin ? _activeSpinClickOffsetMs : defaultClickOffsetMs;
            double now = NowMs();
            double clickTargetTimeMs = effectiveZeroTimeMs - usedOffset;
            double waitMs = clickTargetTimeMs - now;

            // Fuer Drop-Frame-Immunitaet zwischenspeichern
            _hasActivePrediction = true;
            _cachedEffectiveZeroTimeMs = effectiveZeroTimeMs;
            _cachedPredictedZeroTimeMs = predictedZeroTimeMs;
            _cachedUsedOffsetMs = usedOffset;
            _cachedUsedPhaseLock = usedPhaseLock;
            _cachedPhaseLockPts = phaseLockPts;
            _cachedPhaseLockMad = phaseLockMad;
            _cachedUsedQuadratic = usedQuadratic;

            if (minRemainingDeg <= 105.0 && waitMs <= 36.0)
            {
                double finalSpinTime55 = ComputeSpinTimeToDegMs(55.0, captureTimeMs, minRemainingDeg);
                var finalFp = new TachoFingerprint
                {
                    GreenEndDeg = _greenEndDeg,
                    GreenWidthDeg = LastGreenWidthDeg,
                    BlueWidthDeg = LastBlueWidthDeg,
                    EndSpeedDegPerSec = endSpeedDegPerSec,
                    AccelDegPerSec2 = accelDegPerSec2,
                    SpinTimeTo55Ms = finalSpinTime55
                };
                LastFingerprint = finalFp;

                // If not resolved yet (e.g. fast tacho), resolve right before clicking
                if (!_setupResolvedForSpin && onFingerprintReady != null)
                {
                    _setupResolvedForSpin = true;
                    var resolved = onFingerprintReady(finalFp);
                    usedOffset = resolved.OffsetMs;
                    _activeSpinClickOffsetMs = usedOffset;
                    _activeSpinTemplateCurve = resolved.TemplateCurve;

                    if (_activeSpinTemplateCurve != null && _activeSpinTemplateCurve.IsReady)
                    {
                        var cpList = GetLastCheckpointsCopy();
                        if (_activeSpinTemplateCurve.TryPredictZeroTimeMs(cpList, out double lockZeroMs, out phaseLockPts, out phaseLockMad))
                        {
                            effectiveZeroTimeMs = lockZeroMs;
                            usedPhaseLock = true;
                        }
                    }

                    clickTargetTimeMs = effectiveZeroTimeMs - usedOffset;
                    waitMs = clickTargetTimeMs - NowMs();
                }

                LastUsedClickOffsetMs = usedOffset;
                LastPredictedZeroTimeMs = predictedZeroTimeMs;
                LastPhaseLockMadMs = usedPhaseLock ? phaseLockMad : -1.0;
                LastTriggerReason = usedPhaseLock
                    ? $"LOCK(n={phaseLockPts},mad={phaseLockMad:F2})"
                    : (usedQuadratic ? "QUAD" : "LINEAR");
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

    public Bitmap? CaptureClickMomentSnapshot(int centerX, int centerY, AppConfig cfg)
    {
        EnsureBuffers(cfg.TachoBoxSize);
        int half = _size / 2;
        int left = Math.Max(0, centerX - half);
        int top = Math.Max(0, centerY - half);

        var snap = new Bitmap(_size, _size, PixelFormat.Format32bppArgb);
        try
        {
            byte[] snapBuf = new byte[_size * _size * 4];
            var cap = _capture.CaptureRegion(left, top, _size, _size, snapBuf, timeoutMs: 2, allowGdiIfNoNewFrame: true);
            if (cap.HasPixels)
            {
                var rect = new Rectangle(0, 0, _size, _size);
                BitmapData data = snap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    unsafe
                    {
                        byte* dstPtr = (byte*)data.Scan0;
                        int rowBytes = _size * 4;
                        fixed (byte* srcPtr = snapBuf)
                        {
                            for (int y = 0; y < _size; y++)
                                Buffer.MemoryCopy(srcPtr + (y * rowBytes), dstPtr + (y * data.Stride), rowBytes, rowBytes);
                        }
                    }
                }
                finally
                {
                    snap.UnlockBits(data);
                }
            }
            else
            {
                using var gFallback = Graphics.FromImage(snap);
                gFallback.CopyFromScreen(left, top, 0, 0, new Size(_size, _size), CopyPixelOperation.SourceCopy);
            }

            using var g = Graphics.FromImage(snap);
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
        }
        catch
        {
            // Ignore transient screenshot errors
        }

        return snap;
    }

    /// <summary>
    /// Identical post-click color check for EVERY Tacho:
    /// Reads ONLY a tiny 9x9 pixel patch on the bottom (6 o'clock) of the Tacho ring (~15..135 ms after click)
    /// via DXGI (< 0,3 ms pro Frame) und gibt sofort Blau, Gruen, Rot oder Weiss zurueck.
    /// </summary>
    public TachoFeedback MeasureBottomColorAfterClick(int tachoNumber, int centerX, int centerY, AppConfig cfg)
    {
        EnsureBuffers(cfg.TachoBoxSize);

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
            var cap = _capture.CaptureRegion(
                screenPatchLeft, screenPatchTop, patchSize, patchSize,
                _patchBuffer, timeoutMs: 12, allowGdiIfNoNewFrame: true);

            if (!cap.HasPixels)
            {
                Thread.Sleep(3);
                continue;
            }

            int rCount = 0;
            int gCount = 0;
            int bCount = 0;
            int wCount = 0;
            long sumR = 0, sumG = 0, sumB = 0;

            for (int i = 0; i < patchSize * patchSize; i++)
            {
                int p = i * 4;
                byte b = _patchBuffer[p];
                byte g = _patchBuffer[p + 1];
                byte r = _patchBuffer[p + 2];

                sumR += r;
                sumG += g;
                sumB += b;

                if (IsTachoWhite(r, g, b)) wCount++;
                else if (IsFeedbackRed(r, g, b)) rCount++;
                else if (IsFeedbackGreen(r, g, b)) gCount++;
                else if (IsFeedbackBlue(r, g, b)) bCount++;
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

            if (!_capture.IsDxgiActive)
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

        if (whiteFrames >= 3 || lastWhiteCount >= 40)
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

    /// <summary>
    /// Zentriert eine grob per F9 gesetzte Tacho-Position automatisch auf das geometrische Kreiszentrum
    /// des Tacho-Rings (oder der dunklen Tacho-Scheibe) fuer maximale Winkel-/Geschwindigkeitsgenauigkeit.
    /// </summary>
    /// <summary>
    /// Prueft ueber eine 12-Sektoren-Kontrastmessung (Innenloch vs. Ring vs. Aussenrand),
    /// ob an der angegebenen Position ein halbtransparenter Tacho-Schattenring sichtbar ist.
    /// Nutzt GDI CopyFromScreen, um keinen DXGI-Frame des aktiven Tachos zu verbrauchen.
    /// </summary>
    public static (bool HasShadowRing, int DarkSectors, double AvgInDiff, double AvgOutDiff) DetectShadowRing(
        int centerX,
        int centerY,
        int boxSize)
    {
        if (centerX <= 0 || centerY <= 0)
            return (false, 0, 0.0, 0.0);

        try
        {
            int size = Math.Clamp(boxSize, 120, 260);
            int half = size / 2;
            int left = Math.Max(0, centerX - half);
            int top = Math.Max(0, centerY - half);

            using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(left, top, 0, 0, new Size(size, size), CopyPixelOperation.SourceCopy);

            double rInMin = size * 0.06;
            double rInMax = size * 0.20;
            double rRingMin = size * 0.24;
            double rRingMax = size * 0.38;
            double rOutMin = size * 0.42;
            double rOutMax = size * 0.49;

            double rInMinSq = rInMin * rInMin;
            double rInMaxSq = rInMax * rInMax;
            double rRingMinSq = rRingMin * rRingMin;
            double rRingMaxSq = rRingMax * rRingMax;
            double rOutMinSq = rOutMin * rOutMin;
            double rOutMaxSq = rOutMax * rOutMax;

            Span<double> inSum = stackalloc double[12];
            Span<int> inCnt = stackalloc int[12];
            Span<double> ringSum = stackalloc double[12];
            Span<int> ringCnt = stackalloc int[12];
            Span<double> outSum = stackalloc double[12];
            Span<int> outCnt = stackalloc int[12];

            double totalCenterLum = 0.0;
            int totalCenterCnt = 0;

            var rect = new Rectangle(0, 0, size, size);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    byte* basePtr = (byte*)data.Scan0;
                    int stride = data.Stride;

                    for (int y = 0; y < size; y += 2)
                    {
                        double dy = y - half;
                        double dySq = dy * dy;
                        byte* row = basePtr + y * stride;

                        for (int x = 0; x < size; x += 2)
                        {
                            double dx = x - half;
                            double dSq = dx * dx + dySq;
                            if (dSq < rInMinSq || dSq > rOutMaxSq)
                                continue;

                            int zone;
                            if (dSq <= rInMaxSq) zone = 1;
                            else if (dSq >= rRingMinSq && dSq <= rRingMaxSq) zone = 2;
                            else if (dSq >= rOutMinSq) zone = 3;
                            else continue;

                            int p = x * 4;
                            double lum = 0.299 * row[p + 2] + 0.587 * row[p + 1] + 0.114 * row[p];

                            double deg = Math.Atan2(dy, dx) * (180.0 / Math.PI);
                            if (deg < 0.0) deg += 360.0;
                            int sector = ((int)(deg / 30.0)) % 12;

                            if (zone == 1)
                            {
                                inSum[sector] += lum;
                                inCnt[sector]++;
                                totalCenterLum += lum;
                                totalCenterCnt++;
                            }
                            else if (zone == 2)
                            {
                                ringSum[sector] += lum;
                                ringCnt[sector]++;
                            }
                            else
                            {
                                outSum[sector] += lum;
                                outCnt[sector]++;
                            }
                        }
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            if (totalCenterCnt == 0)
                return (false, 0, 0.0, 0.0);

            double avgCenterLum = totalCenterLum / totalCenterCnt;
            int darkBothSectors = 0;
            double diffInTotal = 0.0;
            double diffOutTotal = 0.0;
            int validSectors = 0;

            for (int s = 0; s < 12; s++)
            {
                if (inCnt[s] == 0 || ringCnt[s] == 0 || outCnt[s] == 0)
                    continue;

                double avgIn = inSum[s] / inCnt[s];
                double avgRing = ringSum[s] / ringCnt[s];
                double avgOut = outSum[s] / outCnt[s];

                double dIn = avgIn - avgRing;
                double dOut = avgOut - avgRing;
                diffInTotal += dIn;
                diffOutTotal += dOut;
                validSectors++;

                if (dIn >= 5.5 && dOut >= 5.5)
                    darkBothSectors++;
            }

            if (validSectors < 10)
                return (false, darkBothSectors, 0.0, 0.0);

            double avgInDiff = diffInTotal / validSectors;
            double avgOutDiff = diffOutTotal / validSectors;

            // Ein echter Tacho-Schattenring ist rundherum (typisch 11-12/12 Sektoren, ~17-22 Lum)
            // deutlich dunkler als sein Innenloch UND sein Aussenrand; leerer Hintergrund liegt bei 0-1/12.
            bool hasShadow = avgCenterLum < 220.0 &&
                             darkBothSectors >= 7 &&
                             avgInDiff >= 7.0 &&
                             avgOutDiff >= 7.0;

            return (hasShadow, darkBothSectors, avgInDiff, avgOutDiff);
        }
        catch
        {
            return (false, 0, 0.0, 0.0);
        }
    }

    public static (int X, int Y, int Dx, int Dy, bool Centered) TryAutoCenterOnRing(int approxX, int approxY, int boxSize)
    {
        try
        {
            int size = Math.Clamp(boxSize + 24, 140, 260);
            int half = size / 2;
            int left = Math.Max(0, approxX - half);
            int top = Math.Max(0, approxY - half);

            using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(left, top, 0, 0, new Size(size, size), CopyPixelOperation.SourceCopy);

            // 1. Hintergrund-Helligkeit aus den 4 Ecken bestimmen
            double bgSum = 0;
            int bgN = 0;
            int[] corners = { 2, 5, size - 6, size - 3 };
            foreach (int cy in corners)
            {
                foreach (int cx in corners)
                {
                    if ((cx < 10 || cx > size - 10) && (cy < 10 || cy > size - 10))
                    {
                        Color c = bmp.GetPixel(cx, cy);
                        bgSum += (c.R + c.G + c.B) / 3.0;
                        bgN++;
                    }
                }
            }
            double bgLuma = bgN > 0 ? bgSum / bgN : 80.0;

            // 2. Zuerst nach dunklem Tacho-Kreis (oder schwarzem Ring-Rand) im Mittelstreifen suchen
            int minX = size, maxX = -1, minY = size, maxY = -1;
            int bandLo = half - 18, bandHi = half + 18;

            for (int y = bandLo; y <= bandHi; y++)
            {
                for (int x = 2; x < size - 2; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    double luma = (c.R + c.G + c.B) / 3.0;
                    if (luma < bgLuma - 12.0)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                    }
                }
            }

            for (int x = bandLo; x <= bandHi; x++)
            {
                for (int y = 2; y < size - 2; y++)
                {
                    Color c = bmp.GetPixel(x, y);
                    double luma = (c.R + c.G + c.B) / 3.0;
                    if (luma < bgLuma - 12.0)
                    {
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            if (maxX > minX + 80 && maxY > minY + 80)
            {
                double cx = (minX + maxX) / 2.0;
                double cy = (minY + maxY) / 2.0;
                int dx = (int)Math.Round(cx - half);
                int dy = (int)Math.Round(cy - half);
                if (Math.Abs(dx) <= 18 && Math.Abs(dy) <= 18)
                {
                    return (approxX + dx, approxY + dy, dx, dy, dx != 0 || dy != 0);
                }
            }
        }
        catch { }

        return (approxX, approxY, 0, 0, false);
    }

    /// <summary>
    /// Sub-Grad-Kantenerkennung (±0,1°) fuer die fuehrende Kante der weissen Nadel.
    /// Nutzt das stetige Kanten-Integral ueber die Anti-Aliasing-Gewichte [k0-1..k0+2] normiert an der
    /// lokalen Balkendicke F (getrimmtes Mittel aus [k0+3..k0+8]) -> null Sprungstelle an 1°-Grenzen!
    /// </summary>
    private static double FindWhiteFrontRemainingDeg(
        double greenEndDeg,
        ReadOnlySpan<int> whiteBins,
        ReadOnlySpan<double> whiteWeights,
        double prevRemainingDeg)
    {
        double floorEnd = Math.Floor(greenEndDeg);
        int endBin = (((int)floorEnd) % 360 + 360) % 360;
        double endFrac = greenEndDeg - floorEnd;
        int startRem = prevRemainingDeg < 45.0 ? -15 : 0;

        int k0 = int.MinValue;
        for (int minPx = 4; minPx >= 2 && k0 == int.MinValue; minPx -= 2)
        {
            for (int rem = startRem; rem <= 295; rem++)
            {
                int bin = (endBin + rem + 360) % 360;
                if (whiteBins[bin] >= minPx)
                {
                    k0 = rem;
                    break;
                }
            }
        }

        if (k0 == int.MinValue)
            return 360.0;

        // Referenz-Balkendicke F pro 1°-Bin aus den 6 vollen Bins direkt hinter der Kante [k0+3..k0+8]
        Span<double> refBins = stackalloc double[6];
        for (int i = 0; i < 6; i++)
        {
            int bin = (endBin + k0 + 3 + i + 360) % 360;
            refBins[i] = whiteWeights[bin];
        }
        refBins.Sort();
        double fullDensity = (refBins[1] + refBins[2] + refBins[3] + refBins[4]) * 0.25;

        if (fullDensity >= 6.0)
        {
            double wWindow =
                whiteWeights[(endBin + k0 - 1 + 360) % 360] +
                whiteWeights[(endBin + k0 + 360) % 360] +
                whiteWeights[(endBin + k0 + 1 + 360) % 360] +
                whiteWeights[(endBin + k0 + 2 + 360) % 360];

            double edgeInEndBinFrame = (k0 + 3.0) - (wWindow / fullDensity);
            if (edgeInEndBinFrame >= k0 - 1.2 && edgeInEndBinFrame <= k0 + 2.2)
            {
                return edgeInEndBinFrame - endFrac;
            }
        }

        return k0 - endFrac;
    }

    private double ComputeSpinTimeToDegMs(double targetDeg, double currentTimeMs, double currentRemDeg)
    {
        if (_checkpoints.Count < 2)
            return 0.0;

        double t0 = _checkpoints[0].TimeMs;

        for (int i = 1; i < _checkpoints.Count; i++)
        {
            var prev = _checkpoints[i - 1];
            var cur = _checkpoints[i];
            if (prev.RemainingDeg >= targetDeg && cur.RemainingDeg <= targetDeg)
            {
                double dd = prev.RemainingDeg - cur.RemainingDeg;
                if (dd > 0.01)
                {
                    double tCross = prev.TimeMs + ((prev.RemainingDeg - targetDeg) / dd) * (cur.TimeMs - prev.TimeMs);
                    return Math.Max(0.0, tCross - t0);
                }
            }
        }

        // Fallback: interpolate between last checkpoint and current frame
        var last = _checkpoints[_checkpoints.Count - 1];
        if (last.RemainingDeg >= targetDeg && currentRemDeg <= targetDeg)
        {
            double dd = last.RemainingDeg - currentRemDeg;
            if (dd > 0.01)
            {
                double tCross = last.TimeMs + ((last.RemainingDeg - targetDeg) / dd) * (currentTimeMs - last.TimeMs);
                return Math.Max(0.0, tCross - t0);
            }
        }

        return 0.0;
    }

    /// <summary>
    /// Fits a quadratic ease-in curve r(t) = a*t^2 + b*t + c over the recent ~155° angular window
    /// (works identically for 14 DWM checkpoints or 45 DXGI 60Hz frames!) and solves for r(t_zero) = 0°.
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

        // 2. Fallback: Linear Regression over the most recent ~75° arc (min 4, max 24 points)
        int startIdx = FindAngularWindowStartIndex(total, 75.0, minPoints: 4, maxPoints: 24);
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
        if (slope >= -0.06 || slope <= -2.5)
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

    private int FindAngularWindowStartIndex(int total, double maxArcDeg, int minPoints, int maxPoints)
    {
        double lastRem = _checkpoints[total - 1].RemainingDeg;
        int earliestAllowed = Math.Max(0, total - maxPoints);
        int latestForMin = Math.Max(0, total - minPoints);

        int idx = latestForMin;
        while (idx > earliestAllowed)
        {
            if ((_checkpoints[idx - 1].RemainingDeg - lastRem) > maxArcDeg)
                break;
            idx--;
        }
        return idx;
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

        int startIdx = FindAngularWindowStartIndex(total, 155.0, minPoints: 8, maxPoints: 50);
        int n = total - startIdx;
        if (n < 6)
            return false;

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
            if (b >= -60.0) return false;
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
        if (endSpeed < 60.0 || endSpeed > 2500.0)
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

            if (rem > 3.5)
                Thread.Sleep(1);
            else
                Thread.SpinWait(50);
        }
    }

    private static bool IsValidGreenEndAngle(double deg)
    {
        // Real Tacho green zone ends on the right / upper-right sector (245°..360° or 0°..55°), never at 8 o'clock (142°)
        return deg >= 245.0 || deg <= 55.0;
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

        int centerBin = ((int)Math.Floor(meanDeg)) % 360;
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

        // Sub-Grad-Kanteninterpolation fuer die gruene Endkante (verhindert ±1°-Sprung zwischen Durchlaeufen!)
        double subMinOffset = minOffset;
        int k0Bin = (centerBin + minOffset + 360) % 360;
        Span<double> refGreen = stackalloc double[5];
        for (int i = 0; i < 5; i++)
            refGreen[i] = bins[(k0Bin + 3 + i) % 360];
        refGreen.Sort();
        double fullGreen = (refGreen[1] + refGreen[2] + refGreen[3]) / 3.0;
        if (fullGreen >= 6.0)
        {
            double gWindow =
                bins[(k0Bin - 1 + 360) % 360] +
                bins[k0Bin] +
                bins[(k0Bin + 1) % 360] +
                bins[(k0Bin + 2) % 360];
            double edgeRel = 3.0 - (gWindow / fullGreen);
            if (edgeRel >= -1.2 && edgeRel <= 2.2)
                subMinOffset = minOffset + edgeRel;
        }

        endDeg = (centerBin + subMinOffset + 360.0) % 360.0;
        widthDeg = foundMax ? Math.Max(1.0, maxOffset - minOffset) : 0;
        return true;
    }

    private static double BlendAngleDeg(double aDeg, double bDeg, double alpha)
    {
        double diff = ((bDeg - aDeg + 540.0) % 360.0) - 180.0;
        return (aDeg + diff * alpha + 360.0) % 360.0;
    }

    public Bitmap? CreateDiagnosticBitmap()
    {
        if (_pixelBuffer.Length == 0 || _size <= 0)
            return null;

        var copy = new Bitmap(_size, _size, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, _size, _size);
        BitmapData data = copy.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* dstPtr = (byte*)data.Scan0;
                int stride = data.Stride;
                int rowBytes = _size * 4;

                fixed (byte* srcPtr = _pixelBuffer)
                {
                    for (int y = 0; y < _size; y++)
                    {
                        byte* dstRow = dstPtr + (y * stride);
                        byte* srcRow = srcPtr + (y * rowBytes);
                        Buffer.MemoryCopy(srcRow, dstRow, rowBytes, rowBytes);

                        int rowOffset = y * _size;
                        for (int x = 0; x < _size; x++)
                        {
                            int idx = rowOffset + x;
                            dstRow[x * 4 + 3] = 255;
                            if (_lastGreenNow[idx])
                            {
                                dstRow[x * 4 + 0] = 255;
                                dstRow[x * 4 + 1] = 0;
                                dstRow[x * 4 + 2] = 255;
                            }
                            else if (_lastWhiteNow[idx])
                            {
                                dstRow[x * 4 + 0] = 40;
                                dstRow[x * 4 + 1] = 255;
                                dstRow[x * 4 + 2] = 255;
                            }
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

    private static bool TryGetAntiAliasedWhiteWeight(byte r, byte g, byte b, out double weight)
    {
        int minC = r < g ? (r < b ? r : b) : (g < b ? g : b);
        if (minC < 140)
        {
            weight = 0;
            return false;
        }
        int maxC = r > g ? (r > b ? r : b) : (g > b ? g : b);
        if ((maxC - minC) > 25)
        {
            weight = 0;
            return false;
        }
        weight = Math.Clamp((minC - 130.0) / 85.0, 0.0, 0.99);
        return weight > 0.05;
    }

    private void EnsureBuffers(int size)
    {
        if (_size == size && _pixelBuffer.Length == size * size * 4)
            return;

        _size = size;
        int total = size * size;
        _pixelBuffer = new byte[total * 4];
        _lastGreenNow = new bool[total];
        _lastWhiteNow = new bool[total];
        _pixelZone = new byte[total];
        _pixelAngleBin = new int[total];
        _pixelSin = new float[total];
        _pixelCos = new float[total];
        _pixelInBottomSector = new bool[total];

        int half = size / 2;
        double minR = size * 0.23;
        double maxR = size * 0.395;
        double minRSq = minR * minR;
        double maxRSq = maxR * maxR;

        for (int y = 0; y < size; y++)
        {
            double dy = y - half;
            double dySq = dy * dy;
            int rowOffset = y * size;
            for (int x = 0; x < size; x++)
            {
                int idx = rowOffset + x;
                double dx = x - half;
                double distSq = dx * dx + dySq;

                if (distSq < minRSq)
                {
                    _pixelZone[idx] = 1;
                }
                else if (distSq <= maxRSq)
                {
                    _pixelZone[idx] = 2;
                    double rad = Math.Atan2(dy, dx);
                    double deg = rad * (180.0 / Math.PI);
                    if (deg < 0) deg += 360.0;
                    _pixelAngleBin[idx] = ((int)Math.Floor(deg)) % 360;
                    _pixelSin[idx] = (float)Math.Sin(rad);
                    _pixelCos[idx] = (float)Math.Cos(rad);
                    _pixelInBottomSector[idx] = dy > 15 && Math.Abs(dx) <= 12;
                }
                else
                {
                    _pixelZone[idx] = 0;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_ownsCapture)
            _capture.Dispose();
    }
}
