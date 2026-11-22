using System.Text.Json;

namespace TachoClicker;

internal readonly struct TachoFingerprint
{
    public double GreenEndDeg { get; init; }
    public double GreenWidthDeg { get; init; }
    public double BlueWidthDeg { get; init; }
    public double EndSpeedDegPerSec { get; init; }
    public double AccelDegPerSec2 { get; init; }
    public double SpinTimeTo55Ms { get; init; }
}

internal sealed class OffsetPerformance
{
    public double OffsetMs { get; set; }
    public int BlueHits { get; set; }
    public int GreenHits { get; set; }
    public int RedHits { get; set; }
    public int WhiteHits { get; set; }

    public int Total => BlueHits + GreenHits + RedHits + WhiteHits;
    public double BlueRate => Total > 0 ? (double)BlueHits / Total : 0.0;
    public double SafeRate => Total > 0 ? (double)(BlueHits + GreenHits) / Total : 0.0;
    public double RedRate => Total > 0 ? (double)RedHits / Total : 0.0;

    /// <summary>
    /// Composite quality score favoring high Blue hit rate while penalizing Red (0 points) & White.
    /// </summary>
    public double QualityScore => Total > 0
        ? (BlueHits * 1.0 + GreenHits * 0.35 - RedHits * 0.85 - WhiteHits * 0.95) / Total
        : -999.0;

    /// <summary>
    /// Bayesian-smoothed score (prior: 2 neutral clicks) so 39/41 Blue cleanly beats 1/1 Blue.
    /// </summary>
    public double SmoothedScore => Total > 0
        ? (BlueHits * 1.0 + GreenHits * 0.35 - RedHits * 0.85 - WhiteHits * 0.95 + 0.60) / (Total + 2.0)
        : -999.0;

    /// <summary>
    /// True if this (Player, Tacho, Preset) triplet has consistently performed poorly (Red/White, never successful).
    /// </summary>
    public bool IsKnownBad =>
        (Total >= 1 && BlueHits == 0 && GreenHits == 0 && (RedHits + WhiteHits) >= 1) ||
        (Total >= 3 && BlueHits == 0 && RedRate >= 0.50) ||
        (Total >= 4 && BlueRate < 0.15 && RedRate >= 0.40);
}

internal sealed class TachoSetupSlot
{
    public bool HasSignature { get; set; }

    // Feste Bildschirm-Position dieses Tachos fuer diesen Spieler
    public int X { get; set; }
    public int Y { get; set; }

    public double GreenEndDeg { get; set; }
    public double GreenWidthDeg { get; set; }
    public double BlueWidthDeg { get; set; }
    public double EndSpeedDegPerSec { get; set; }
    public double AccelDegPerSec2 { get; set; }
    public double SpinTimeTo55Ms { get; set; }

    public double ClickOffsetMs { get; set; } = 41.0;
    public double DefaultClickOffsetMs { get; set; } = 41.0;

    public int Samples { get; set; }
    public int BlueHits { get; set; }
    public int GreenHits { get; set; }
    public int RedHits { get; set; }
    public int WhiteHits { get; set; }
    public int TotalClicks { get; set; }

    // Pro Offset gemerkte Treffer-Quoten fuer Rueckkehr zu bewaehrten Parametern
    public List<OffsetPerformance> OffsetHistory { get; set; } = new();

    // Laufzeit-Zaehler (nicht zwingend persistent), um bei Fehlversuchen auf bewaehrten Offset zurueckzukehren
    public int ConsecutiveNonBlue { get; set; }
    public int ConsecutiveRed { get; set; }

    public bool HasPosition => X > 0 && Y > 0;
    public double BlueRatePercent => TotalClicks > 0 ? 100.0 * BlueHits / TotalClicks : 0.0;

    public void InitFrom(TachoFingerprint fp, double initialOffsetMs, int x, int y)
    {
        HasSignature = true;
        X = x;
        Y = y;
        GreenEndDeg = Math.Round(fp.GreenEndDeg, 1);
        GreenWidthDeg = Math.Round(fp.GreenWidthDeg, 1);
        BlueWidthDeg = Math.Round(fp.BlueWidthDeg, 1);
        EndSpeedDegPerSec = Math.Round(fp.EndSpeedDegPerSec, 1);
        AccelDegPerSec2 = Math.Round(fp.AccelDegPerSec2, 1);
        SpinTimeTo55Ms = Math.Round(fp.SpinTimeTo55Ms, 1);
        ClickOffsetMs = Math.Round(initialOffsetMs, 2);
        DefaultClickOffsetMs = Math.Round(initialOffsetMs, 2);
        Samples = 1;
    }

    public void UpdateSignatureEma(TachoFingerprint fp, bool isFixed = false)
    {
        if (!HasSignature)
        {
            InitFrom(fp, ClickOffsetMs, X, Y);
            return;
        }

        // Feste Spieler (Ammonit, Mammut, Oviraptor, ...) frieren ihre kalibrierte Tacho-Signatur ein,
        // damit sich aehnliche Tachos niemals gegenseitig per EMA verschmieren koennen!
        if (isFixed)
        {
            if (SpinTimeTo55Ms <= 1.0 && fp.SpinTimeTo55Ms > 100.0)
                SpinTimeTo55Ms = Math.Round(fp.SpinTimeTo55Ms, 1);
            Samples++;
            return;
        }

        double alpha = Samples < 5 ? 0.25 : 0.10;
        GreenEndDeg = Math.Round(BlendAngleDeg(GreenEndDeg, fp.GreenEndDeg, alpha), 1);
        GreenWidthDeg = Math.Round(GreenWidthDeg * (1 - alpha) + fp.GreenWidthDeg * alpha, 1);
        BlueWidthDeg = Math.Round(BlueWidthDeg * (1 - alpha) + fp.BlueWidthDeg * alpha, 1);
        EndSpeedDegPerSec = Math.Round(EndSpeedDegPerSec * (1 - alpha) + fp.EndSpeedDegPerSec * alpha, 1);
        AccelDegPerSec2 = Math.Round(AccelDegPerSec2 * (1 - alpha) + fp.AccelDegPerSec2 * alpha, 1);
        if (fp.SpinTimeTo55Ms > 100.0)
        {
            SpinTimeTo55Ms = SpinTimeTo55Ms <= 1.0
                ? Math.Round(fp.SpinTimeTo55Ms, 1)
                : Math.Round(SpinTimeTo55Ms * (1 - alpha) + fp.SpinTimeTo55Ms * alpha, 1);
        }
        Samples++;
    }

    public static double SnapToQuarterMs(double offsetMs) =>
        Math.Clamp(Math.Round(offsetMs * 4.0) / 4.0, 0.0, 120.0);

    public OffsetPerformance? FindPresetStats(double offsetMs)
    {
        double bucket = SnapToQuarterMs(offsetMs);
        return OffsetHistory.FirstOrDefault(h => Math.Abs(h.OffsetMs - bucket) < 0.05);
    }

    public bool IsPresetKnownBad(double offsetMs)
    {
        var st = FindPresetStats(offsetMs);
        return st != null && st.IsKnownBad;
    }

    public int CountKnownBadPresets() => OffsetHistory.Count(h => h.IsKnownBad);

    public OffsetPerformance? GetBestProvenOffset(double? excludeOffsetMs = null)
    {
        bool IsExcluded(OffsetPerformance h) =>
            excludeOffsetMs.HasValue && Math.Abs(h.OffsetMs - SnapToQuarterMs(excludeOffsetMs.Value)) < 0.05;

        // 1. Bewaehrte Blau-Presets (>= 3 Klicks, >= 2 Blau, >= 35% Blau, nicht als schlecht markiert)
        var proven = OffsetHistory
            .Where(h => !h.IsKnownBad && !IsExcluded(h) && h.Total >= 3 && h.BlueHits >= 2 && h.BlueRate >= 0.35)
            .OrderByDescending(h => h.SmoothedScore)
            .ThenByDescending(h => h.BlueHits)
            .FirstOrDefault();

        if (proven != null)
            return proven;

        // 2. Fallback: mindestens 1 sauberer Blau-Treffer ohne Rot/Weiss
        return OffsetHistory
            .Where(h => !h.IsKnownBad && !IsExcluded(h) && h.BlueHits >= 1 && h.RedHits == 0 && h.WhiteHits == 0)
            .OrderByDescending(h => h.SmoothedScore)
            .ThenByDescending(h => h.BlueHits)
            .FirstOrDefault();
    }

    /// <summary>
    /// Waehlt das beste bekannte Preset fuer dieses (Spieler, Tacho)-Paar beim Erkennen des Spielers:
    /// 1. Bestes Blau-Preset (nach Bayesian-geglatteter Erfolgsquote).
    /// 2. Falls noch kein Blau-Preset existiert: knappstes sicheres Gruen-Preset (0 Rot), niemals ein bekannt schlechtes Preset.
    /// </summary>
    public OffsetPerformance? GetBestStartupPreset(double? excludeOffsetMs = null)
    {
        var bestBlue = GetBestProvenOffset(excludeOffsetMs);
        if (bestBlue != null)
            return bestBlue;

        bool IsExcluded(OffsetPerformance h) =>
            excludeOffsetMs.HasValue && Math.Abs(h.OffsetMs - SnapToQuarterMs(excludeOffsetMs.Value)) < 0.05;

        return OffsetHistory
            .Where(h => !h.IsKnownBad && !IsExcluded(h) && h.GreenHits >= 1 && h.RedHits == 0 && h.WhiteHits == 0)
            .OrderBy(h => h.OffsetMs) // Knappstes sicheres Gruen nahe der Blau-Grenze
            .FirstOrDefault();
    }

    /// <summary>
    /// Laedt beim Erkennen eines Spielers sofort das Preset mit der bislang besten Erfolgsquote
    /// und stellt sicher, dass niemals auf einem nachweislich schlechten Preset gestartet wird.
    /// </summary>
    public (bool Changed, double OldOffsetMs, double NewOffsetMs, OffsetPerformance? ChosenStats) ApplyBestKnownPreset()
    {
        double oldOffset = ClickOffsetMs;
        var best = GetBestStartupPreset();
        if (best != null)
        {
            ClickOffsetMs = best.OffsetMs;
            ConsecutiveNonBlue = 0;
            ConsecutiveRed = 0;
            return (Math.Abs(ClickOffsetMs - oldOffset) >= 0.05, oldOffset, ClickOffsetMs, best);
        }

        // Selbst wenn noch kein gutes Preset bekannt ist: niemals auf einem bekannt schlechten Preset starten!
        if (IsPresetKnownBad(ClickOffsetMs))
        {
            var (safeOffset, _) = FindNextNonBadOffsetUp(ClickOffsetMs + 0.75);
            ClickOffsetMs = safeOffset;
            ConsecutiveNonBlue = 0;
            ConsecutiveRed = 0;
            return (Math.Abs(ClickOffsetMs - oldOffset) >= 0.05, oldOffset, ClickOffsetMs, null);
        }

        return (false, oldOffset, ClickOffsetMs, null);
    }

    private (double ResolvedOffsetMs, int SkippedBadCount) FindNextNonBadOffsetUp(double startOffsetMs)
    {
        double candidate = SnapToQuarterMs(startOffsetMs);
        int skipped = 0;
        const int maxSkips = 48; // Max +12 ms Suchfenster

        while (skipped < maxSkips && candidate <= 95.0 && IsPresetKnownBad(candidate))
        {
            candidate = SnapToQuarterMs(candidate + 0.25);
            skipped++;
        }

        return (Math.Clamp(candidate, 0.0, 120.0), skipped);
    }

    public (double NewOffsetMs, double AppliedDeltaMs, bool RevertedToProven, OffsetPerformance? RevertedStats, int SkippedBadCount, bool BlockedBadCliff)
        RecordOutcomeAndTune(double usedOffsetMs, TachoFeedback fb, bool isFixedPlayer)
    {
        if (fb == TachoFeedback.None)
            return (ClickOffsetMs, 0.0, false, null, 0, false);

        TotalClicks++;
        double bucketOffset = SnapToQuarterMs(usedOffsetMs);

        var stat = OffsetHistory.FirstOrDefault(h => Math.Abs(h.OffsetMs - bucketOffset) < 0.05);
        if (stat == null)
        {
            stat = new OffsetPerformance { OffsetMs = bucketOffset };
            OffsetHistory.Add(stat);
        }

        switch (fb)
        {
            case TachoFeedback.OptimalBlue:
                BlueHits++;
                stat.BlueHits++;
                ConsecutiveNonBlue = 0;
                ConsecutiveRed = 0;
                return (ClickOffsetMs, 0.0, false, null, 0, false);

            case TachoFeedback.TooEarlyGreen:
                GreenHits++;
                stat.GreenHits++;
                ConsecutiveNonBlue++;
                ConsecutiveRed = 0;
                break;

            case TachoFeedback.TooLateRed:
                RedHits++;
                stat.RedHits++;
                ConsecutiveNonBlue++;
                ConsecutiveRed++;
                break;

            case TachoFeedback.WayTooEarlyWhite:
                WhiteHits++;
                stat.WhiteHits++;
                ConsecutiveNonBlue++;
                ConsecutiveRed = 0;
                break;
        }

        var bestProven = GetBestProvenOffset();

        // 1. Bei WEISS (viel zu frueh): sofort auf das beste bekannte Preset zurueckkehren
        if (fb == TachoFeedback.WayTooEarlyWhite)
        {
            var target = bestProven ?? GetBestStartupPreset();
            if (target != null)
            {
                double delta = Math.Round(target.OffsetMs - ClickOffsetMs, 2);
                ClickOffsetMs = target.OffsetMs;
                ConsecutiveNonBlue = 0;
                ConsecutiveRed = 0;
                return (ClickOffsetMs, delta, true, target, 0, false);
            }

            double def = DefaultClickOffsetMs > 0 ? DefaultClickOffsetMs : 41.0;
            var (safeDef, sk) = FindNextNonBadOffsetUp(def);
            double defDelta = Math.Round(safeDef - ClickOffsetMs, 2);
            ClickOffsetMs = safeDef;
            ConsecutiveNonBlue = 0;
            ConsecutiveRed = 0;
            return (ClickOffsetMs, defDelta, false, null, sk, false);
        }

        // 2. Sofort-Schutz gegen Rot-Serien & Abdriften in schlechte Presets:
        if (fb == TachoFeedback.TooLateRed)
        {
            // 2a. Aktuelles Preset ist als schlecht markiert oder wir hatten >= 2x Rot:
            //     Sofort auf das beste bewaehrte Preset (bei >= 2x Rot auf diesem Bucket: bestes *anderes* bewaehrtes Preset!) springen!
            if (ConsecutiveRed >= 2)
            {
                var bestAlt = GetBestProvenOffset(excludeOffsetMs: bucketOffset) ?? bestProven;
                if (bestAlt != null && Math.Abs(bestAlt.OffsetMs - ClickOffsetMs) >= 0.20)
                {
                    double delta = Math.Round(bestAlt.OffsetMs - ClickOffsetMs, 2);
                    ClickOffsetMs = bestAlt.OffsetMs;
                    ConsecutiveNonBlue = 0;
                    ConsecutiveRed = 0;
                    return (ClickOffsetMs, delta, true, bestAlt, 0, false);
                }
            }
            else if (stat.IsKnownBad && bestProven != null && Math.Abs(bestProven.OffsetMs - ClickOffsetMs) >= 0.25)
            {
                // Schon nach 1x Rot auf einem bisher erfolglosen Preset sofort zum bewaehrten Champion zurueck!
                double delta = Math.Round(bestProven.OffsetMs - ClickOffsetMs, 2);
                ClickOffsetMs = bestProven.OffsetMs;
                ConsecutiveNonBlue = 0;
                ConsecutiveRed = 0;
                return (ClickOffsetMs, delta, true, bestProven, 0, false);
            }
        }
        else if (fb == TachoFeedback.TooEarlyGreen && bestProven != null && Math.Abs(ClickOffsetMs - bestProven.OffsetMs) >= 1.25)
        {
            bool driftedAndFailing =
                ConsecutiveNonBlue >= 3 && stat.Total >= 2 && stat.SmoothedScore < bestProven.SmoothedScore - 0.15;

            if (driftedAndFailing)
            {
                double delta = Math.Round(bestProven.OffsetMs - ClickOffsetMs, 2);
                ClickOffsetMs = bestProven.OffsetMs;
                ConsecutiveNonBlue = 0;
                ConsecutiveRed = 0;
                return (ClickOffsetMs, delta, true, bestProven, 0, false);
            }
        }

        // 3. Regulare Anpassung mit Sperre fuer bekannte schlechte Presets:
        if (fb == TachoFeedback.TooLateRed)
        {
            double redStep;
            if (!isFixedPlayer)
                redStep = +2.5;
            else if (bestProven == null && ConsecutiveRed >= 3)
                redStep = +3.0;
            else if (bestProven == null && EndSpeedDegPerSec >= 440.0)
                redStep = +1.5;
            else if (ConsecutiveRed >= 2)
                redStep = +1.5; // Auch bei bekanntem Spieler nach 2x Rot schneller aus dem Roten heraus
            else
                redStep = +0.75;

            double rawTarget = Math.Clamp(Math.Round(ClickOffsetMs + redStep, 2), 0.0, 120.0);
            var (safeTarget, skippedBad) = FindNextNonBadOffsetUp(rawTarget);
            double applied = Math.Round(safeTarget - ClickOffsetMs, 2);
            ClickOffsetMs = safeTarget;
            return (ClickOffsetMs, applied, false, null, skippedBad, false);
        }

        if (fb == TachoFeedback.TooEarlyGreen)
        {
            double greenStep = isFixedPlayer ? -0.25 : -0.5;
            double rawTarget = Math.Clamp(Math.Round(ClickOffsetMs + greenStep, 2), 0.0, 120.0);

            // Wichtig: Beim Schritt nach UNTEN (spaeter klicken) niemals auf ein bekanntes Rot-Preset treten!
            if (IsPresetKnownBad(rawTarget))
            {
                if (bestProven != null && Math.Abs(bestProven.OffsetMs - ClickOffsetMs) >= 0.25)
                {
                    double delta = Math.Round(bestProven.OffsetMs - ClickOffsetMs, 2);
                    ClickOffsetMs = bestProven.OffsetMs;
                    ConsecutiveNonBlue = 0;
                    return (ClickOffsetMs, delta, true, bestProven, 1, true);
                }

                // Lieber auf dem sicheren Gruen-Preset bleiben statt in ein bekannt schlechtes Rot-Preset zu fallen!
                return (ClickOffsetMs, 0.0, false, null, 1, true);
            }

            ClickOffsetMs = rawTarget;
            return (ClickOffsetMs, greenStep, false, null, 0, false);
        }

        return (ClickOffsetMs, 0.0, false, null, 0, false);
    }

    public double DistanceTo(TachoFingerprint fp)
    {
        if (!HasSignature)
            return double.MaxValue;

        double dEnd = AngleDiffDeg(GreenEndDeg, fp.GreenEndDeg) / 6.0;
        double dGreenW = Math.Abs(GreenWidthDeg - fp.GreenWidthDeg) / 4.5;
        double dBlueW = Math.Abs(BlueWidthDeg - fp.BlueWidthDeg) / 4.5;

        // Schaerfere Toleranzen fuer Speed, Accel & Spin-Laufzeit trennen Ammonit (358.6°/s, 258°/s², 1180ms)
        // und Mammut (372.6°/s, 298°/s², 1157ms) schon bei Tacho 1 zuverlaessig!
        double speedTol = Math.Max(14.0, EndSpeedDegPerSec * 0.042);
        double dSpeed = Math.Abs(EndSpeedDegPerSec - fp.EndSpeedDegPerSec) / speedTol;

        double accelTol = Math.Max(35.0, Math.Abs(AccelDegPerSec2) * 0.16);
        double dAccel = Math.Abs(AccelDegPerSec2 - fp.AccelDegPerSec2) / accelTol;

        bool hasTime = SpinTimeTo55Ms > 100.0 && fp.SpinTimeTo55Ms > 100.0;
        double dTime = 0.0;
        if (hasTime)
        {
            double timeTol = Math.Max(22.0, SpinTimeTo55Ms * 0.025);
            dTime = Math.Abs(SpinTimeTo55Ms - fp.SpinTimeTo55Ms) / timeTol;
            if (dTime > 1.65)
                return double.MaxValue;
        }

        if (dEnd > 1.5 || dGreenW > 1.4 || dBlueW > 1.5 || dSpeed > 1.55 || dAccel > 1.70)
            return double.MaxValue;

        double weightedSq =
            dEnd * dEnd * 1.0 +
            dGreenW * dGreenW * 1.0 +
            dBlueW * dBlueW * 0.4 +
            dSpeed * dSpeed * 2.0 +
            dAccel * dAccel * 1.6 +
            (hasTime ? dTime * dTime * 1.8 : 0.0);
        double totalWeight = hasTime ? 7.8 : 6.0;

        return Math.Sqrt(weightedSq / totalWeight);
    }

    public bool MatchesGeometryAndSpinTime(TachoFingerprint fp)
    {
        if (!HasSignature)
            return false;

        double dEnd = AngleDiffDeg(GreenEndDeg, fp.GreenEndDeg);
        double dGreenW = Math.Abs(GreenWidthDeg - fp.GreenWidthDeg);
        if (dEnd > 6.0 || dGreenW > 4.5)
            return false;

        if (SpinTimeTo55Ms > 100.0 && fp.SpinTimeTo55Ms > 100.0)
        {
            if (Math.Abs(SpinTimeTo55Ms - fp.SpinTimeTo55Ms) > 45.0)
                return false;
        }

        return true;
    }

    private static double AngleDiffDeg(double a, double b)
    {
        return Math.Abs((a - b + 540.0) % 360.0 - 180.0);
    }

    private static double BlendAngleDeg(double a, double b, double alpha)
    {
        double diff = ((b - a + 540.0) % 360.0) - 180.0;
        return (a + diff * alpha + 360.0) % 360.0;
    }
}

internal sealed class EnemySetup
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsFixed { get; set; }
    public int ProfileSize { get; set; } = 2;
    public DinoVisualFingerprint? Visual { get; set; }
    public List<TachoSetupSlot> Tachos { get; set; } = new();
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    public bool HasVisual => Visual != null && Visual.HasVisual;

    public void EnsureSlots()
    {
        int expected = ProfileSize == 3 ? 3 : 2;
        while (Tachos.Count < expected)
            Tachos.Add(new TachoSetupSlot());
    }

    public int TotalBlueHits => Tachos.Sum(t => t.BlueHits);
    public int TotalClicks => Tachos.Sum(t => t.TotalClicks);
}

internal sealed class EnemySetupStore
{
    public int NextId { get; set; } = 1;
    public List<EnemySetup> Setups { get; set; } = new();

    private static readonly string StorePath =
        Path.Combine(AppContext.BaseDirectory, "enemy_setups.json");

    public static EnemySetupStore Load()
    {
        if (!File.Exists(StorePath))
            return new EnemySetupStore();

        try
        {
            string json = File.ReadAllText(StorePath);
            var store = JsonSerializer.Deserialize<EnemySetupStore>(json) ?? new EnemySetupStore();
            foreach (var s in store.Setups)
                s.EnsureSlots();
            if (store.NextId <= store.Setups.Count)
                store.NextId = (store.Setups.Count > 0 ? store.Setups.Max(s => s.Id) : 0) + 1;
            return store;
        }
        catch
        {
            return new EnemySetupStore();
        }
    }

    public void Save()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(this, options);
        File.WriteAllText(StorePath, json);

        try
        {
            string projectRootPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "enemy_setups.json"));
            string projectRootDir = Path.GetDirectoryName(projectRootPath) ?? "";
            if (Directory.Exists(projectRootDir) && File.Exists(Path.Combine(projectRootDir, "TachoClicker.csproj")))
            {
                File.WriteAllText(projectRootPath, json);
            }
        }
        catch
        {
            // Ignore when running standalone outside project directory
        }
    }

    public (EnemySetup Setup, bool IsNew, double MatchDistance, double VisualDistance, bool JustLearnedVisual) MatchOrCreate(
        int profileSize,
        int tachoIndex,
        TachoFingerprint currentFp,
        DinoVisualFingerprint? currentVisual,
        EnemySetup? currentRoundSetup,
        EnemySetup? preferredStickySetup,
        IReadOnlyDictionary<int, TachoFingerprint> roundFingerprints,
        IReadOnlyList<TachoPoint> activePositions,
        double fallbackOffsetMs)
    {
        int curX = tachoIndex < activePositions.Count ? activePositions[tachoIndex].X : 0;
        int curY = tachoIndex < activePositions.Count ? activePositions[tachoIndex].Y : 0;
        bool hasCurVis = currentVisual != null && currentVisual.HasVisual;

        // Case 1: Mid-round (Tacho 2 or Tacho 3) with an already active setup from Tacho 1
        if (tachoIndex > 0 && currentRoundSetup != null && currentRoundSetup.ProfileSize == profileSize)
        {
            currentRoundSetup.EnsureSlots();
            var slot = currentRoundSetup.Tachos[tachoIndex];
            double curVisDist = (hasCurVis && currentRoundSetup.HasVisual)
                ? DinoVisualMatcher.Distance(currentRoundSetup.Visual!, currentVisual!)
                : -1.0;

            if (!slot.HasSignature)
            {
                slot.InitFrom(currentFp, fallbackOffsetMs, curX, curY);
                currentRoundSetup.LastSeenUtc = DateTime.UtcNow;
                Save();
                return (currentRoundSetup, false, 0.0, curVisDist, false);
            }

            double dCurrent = slot.DistanceTo(currentFp);
            if (dCurrent <= 1.0)
            {
                if (!slot.HasPosition && curX > 0 && curY > 0) { slot.X = curX; slot.Y = curY; }
                slot.UpdateSignatureEma(currentFp, currentRoundSetup.IsFixed);
                currentRoundSetup.LastSeenUtc = DateTime.UtcNow;
                Save();
                return (currentRoundSetup, false, dCurrent, curVisDist, false);
            }

            EnemySetup? bestJoint = null;
            double bestJointDist = double.MaxValue;
            double bestJointVisDist = -1.0;
            foreach (var candidate in Setups)
            {
                if (candidate.ProfileSize != profileSize) continue;
                candidate.EnsureSlots();

                // Visueller Ausschluss: niemals auf einen sichtbar anderen Dino umschalten!
                double candVisDist = -1.0;
                if (hasCurVis && candidate.HasVisual)
                {
                    candVisDist = DinoVisualMatcher.Distance(candidate.Visual!, currentVisual!);
                    if (candVisDist > DinoVisualMatcher.VisualMatchThreshold)
                        continue;
                }

                double dThis = candidate.Tachos[tachoIndex].DistanceTo(currentFp);
                if (dThis > 1.0) continue;

                double sumDist = dThis;
                int count = 1;
                bool allOk = true;

                foreach (var kv in roundFingerprints)
                {
                    if (kv.Key == tachoIndex || kv.Key >= candidate.Tachos.Count) continue;
                    double dPrev = candidate.Tachos[kv.Key].DistanceTo(kv.Value);
                    if (dPrev > 1.55)
                    {
                        allOk = false;
                        break;
                    }
                    sumDist += dPrev;
                    count++;
                }

                if (allOk)
                {
                    double avg = sumDist / count;
                    if (candVisDist >= 0)
                        avg = avg * 0.65 + (candVisDist / DinoVisualMatcher.VisualMatchThreshold) * 0.35;
                    if (avg < bestJointDist)
                    {
                        bestJointDist = avg;
                        bestJointVisDist = candVisDist;
                        bestJoint = candidate;
                    }
                }
            }

            if (bestJoint != null)
            {
                var matchedSlot = bestJoint.Tachos[tachoIndex];
                if (!matchedSlot.HasPosition && curX > 0 && curY > 0) { matchedSlot.X = curX; matchedSlot.Y = curY; }
                matchedSlot.UpdateSignatureEma(currentFp, bestJoint.IsFixed);
                bool learned = false;
                if (hasCurVis)
                {
                    if (!bestJoint.HasVisual)
                    {
                        bestJoint.Visual = new DinoVisualFingerprint();
                        DinoVisualMatcher.BlendInto(bestJoint.Visual, currentVisual!);
                        learned = true;
                    }
                    else
                    {
                        DinoVisualMatcher.BlendInto(bestJoint.Visual!, currentVisual!);
                    }
                }
                bestJoint.LastSeenUtc = DateTime.UtcNow;
                Save();
                return (bestJoint, false, bestJointDist, bestJointVisDist, learned);
            }

            // Schutz gegen einzelne Frame-Haker bei festen Spielern:
            if (currentRoundSetup.IsFixed && slot.MatchesGeometryAndSpinTime(currentFp))
            {
                currentRoundSetup.LastSeenUtc = DateTime.UtcNow;
                Save();
                return (currentRoundSetup, false, 0.99, curVisDist, false);
            }

            var splitSetup = CreateNewSetup(profileSize, tachoIndex, currentFp, currentVisual, roundFingerprints, activePositions, fallbackOffsetMs, currentRoundSetup);
            return (splitSetup, true, 0.0, 0.0, hasCurVis);
        }

        // Case 2: Tacho 1 (start of a round)
        // Kombination aus Tacho-Signatur UND visueller Dino-Erkennung:
        // - Wenn beide eine visuelle Signatur haben und visDist > 0.26 -> harter Ausschluss (trennt Stegosaurus & Oviraptor trotz identischem Tacho!).
        // - Ein visuell bestaetigter Kandidat (Tacho + Bild passen) hat IMMER Vorrang vor einem alten NO-VIS-Kandidaten.
        EnemySetup? bestVisMatch = null;
        double bestVisEffective = double.MaxValue;
        double bestVisRawTacho = double.MaxValue;
        double bestVisDist = -1.0;

        EnemySetup? bestLegacyMatch = null;
        double bestLegacyEffective = double.MaxValue;
        double bestLegacyRawTacho = double.MaxValue;

        foreach (var candidate in Setups)
        {
            if (candidate.ProfileSize != profileSize) continue;
            candidate.EnsureSlots();

            var candSlot = candidate.Tachos[tachoIndex];
            double tachoDist = candSlot.DistanceTo(currentFp);

            if (hasCurVis && candidate.HasVisual)
            {
                double visDist = DinoVisualMatcher.Distance(candidate.Visual!, currentVisual!);
                if (visDist > DinoVisualMatcher.VisualMatchThreshold)
                    continue; // Sichtbar ein anderer Dino -> harter Ausschluss!

                // Bei sehr starkem visuellen Match (z.B. VisDist=0.06 bei Ankylosaurus) und passender Grundgeometrie
                // (GreenEnd, GreenWidth, T55) darf ein einzelner 1-Frame-Speed-Haker niemals ein doppeltes Setup anlegen!
                if (tachoDist > 1.0)
                {
                    if (visDist <= 0.22 && candSlot.MatchesGeometryAndSpinTime(currentFp))
                        tachoDist = 1.0;
                    else
                        continue;
                }

                double visNorm = visDist / DinoVisualMatcher.VisualMatchThreshold;
                double combinedDist = tachoDist * 0.45 + visNorm * 0.55;
                double effectiveDist = (preferredStickySetup != null && candidate.Id == preferredStickySetup.Id)
                    ? combinedDist * 0.82
                    : combinedDist;

                if (effectiveDist < bestVisEffective)
                {
                    bestVisEffective = effectiveDist;
                    bestVisRawTacho = tachoDist;
                    bestVisDist = visDist;
                    bestVisMatch = candidate;
                }
            }
            else
            {
                if (tachoDist > 1.0) continue;

                double effectiveDist = (preferredStickySetup != null && candidate.Id == preferredStickySetup.Id)
                    ? tachoDist * 0.82
                    : tachoDist;

                if (effectiveDist < bestLegacyEffective)
                {
                    bestLegacyEffective = effectiveDist;
                    bestLegacyRawTacho = tachoDist;
                    bestLegacyMatch = candidate;
                }
            }
        }

        EnemySetup? bestMatch = bestVisMatch ?? bestLegacyMatch;
        double bestRawDist = bestVisMatch != null ? bestVisRawTacho : bestLegacyRawTacho;
        if (bestVisMatch == null) bestVisDist = -1.0;

        if (bestMatch != null)
        {
            var matchedSlot = bestMatch.Tachos[tachoIndex];
            if (!matchedSlot.HasPosition && curX > 0 && curY > 0) { matchedSlot.X = curX; matchedSlot.Y = curY; }
            matchedSlot.UpdateSignatureEma(currentFp, bestMatch.IsFixed);

            bool learnedVisual = false;
            if (hasCurVis)
            {
                if (!bestMatch.HasVisual)
                {
                    bestMatch.Visual = new DinoVisualFingerprint();
                    DinoVisualMatcher.BlendInto(bestMatch.Visual, currentVisual!);
                    learnedVisual = true;
                    bestVisDist = 0.0;
                }
                else
                {
                    DinoVisualMatcher.BlendInto(bestMatch.Visual!, currentVisual!);
                }
            }

            bestMatch.LastSeenUtc = DateTime.UtcNow;
            Save();
            return (bestMatch, false, bestRawDist, bestVisDist, learnedVisual);
        }

        var created = CreateNewSetup(profileSize, tachoIndex, currentFp, currentVisual, roundFingerprints, activePositions, fallbackOffsetMs, null);
        return (created, true, 0.0, 0.0, hasCurVis);
    }

    private EnemySetup CreateNewSetup(
        int profileSize,
        int currentTachoIndex,
        TachoFingerprint currentFp,
        DinoVisualFingerprint? currentVisual,
        IReadOnlyDictionary<int, TachoFingerprint> roundFingerprints,
        IReadOnlyList<TachoPoint> activePositions,
        double fallbackOffsetMs,
        EnemySetup? copiedFrom)
    {
        int id = NextId++;
        DinoVisualFingerprint? visualCopy = null;
        if (currentVisual != null && currentVisual.HasVisual)
        {
            visualCopy = new DinoVisualFingerprint();
            DinoVisualMatcher.BlendInto(visualCopy, currentVisual);
        }

        var setup = new EnemySetup
        {
            Id = id,
            Name = $"Spieler-Setup #{id}",
            IsFixed = false,
            ProfileSize = profileSize,
            Visual = visualCopy,
            LastSeenUtc = DateTime.UtcNow
        };
        setup.EnsureSlots();

        foreach (var kv in roundFingerprints)
        {
            if (kv.Key < setup.Tachos.Count)
            {
                double inheritedOffset = (copiedFrom != null && kv.Key < copiedFrom.Tachos.Count)
                    ? copiedFrom.Tachos[kv.Key].ClickOffsetMs
                    : fallbackOffsetMs;
                int x = kv.Key < activePositions.Count ? activePositions[kv.Key].X : 0;
                int y = kv.Key < activePositions.Count ? activePositions[kv.Key].Y : 0;
                setup.Tachos[kv.Key].InitFrom(kv.Value, inheritedOffset, x, y);
            }
        }

        int cx = currentTachoIndex < activePositions.Count ? activePositions[currentTachoIndex].X : 0;
        int cy = currentTachoIndex < activePositions.Count ? activePositions[currentTachoIndex].Y : 0;
        setup.Tachos[currentTachoIndex].InitFrom(currentFp, fallbackOffsetMs, cx, cy);
        Setups.Add(setup);
        Save();
        return setup;
    }
}
