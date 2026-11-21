using System.Text.Json;

namespace TachoClicker;

internal readonly struct TachoFingerprint
{
    public double GreenEndDeg { get; init; }
    public double GreenWidthDeg { get; init; }
    public double BlueWidthDeg { get; init; }
    public double EndSpeedDegPerSec { get; init; }
    public double AccelDegPerSec2 { get; init; }
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

    /// <summary>
    /// Composite quality score favoring high Blue hit rate while penalizing Red (0 points) & White.
    /// </summary>
    public double QualityScore => Total > 0
        ? (BlueHits * 1.0 + GreenHits * 0.35 - RedHits * 0.7 - WhiteHits * 0.9) / Total
        : -999.0;
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
        ClickOffsetMs = Math.Round(initialOffsetMs, 2);
        DefaultClickOffsetMs = Math.Round(initialOffsetMs, 2);
        Samples = 1;
    }

    public void UpdateSignatureEma(TachoFingerprint fp)
    {
        if (!HasSignature)
        {
            InitFrom(fp, ClickOffsetMs, X, Y);
            return;
        }

        double alpha = Samples < 5 ? 0.25 : 0.10;
        GreenEndDeg = Math.Round(BlendAngleDeg(GreenEndDeg, fp.GreenEndDeg, alpha), 1);
        GreenWidthDeg = Math.Round(GreenWidthDeg * (1 - alpha) + fp.GreenWidthDeg * alpha, 1);
        BlueWidthDeg = Math.Round(BlueWidthDeg * (1 - alpha) + fp.BlueWidthDeg * alpha, 1);
        EndSpeedDegPerSec = Math.Round(EndSpeedDegPerSec * (1 - alpha) + fp.EndSpeedDegPerSec * alpha, 1);
        AccelDegPerSec2 = Math.Round(AccelDegPerSec2 * (1 - alpha) + fp.AccelDegPerSec2 * alpha, 1);
        Samples++;
    }

    public OffsetPerformance? GetBestProvenOffset()
    {
        // Ein Offset gilt als "bewaehrt", wenn er mindestens 3 Klicks und mindestens 2 Blau-Treffer (>= 40% Blau-Quote) hat
        return OffsetHistory
            .Where(h => h.Total >= 3 && h.BlueHits >= 2 && h.BlueRate >= 0.40)
            .OrderByDescending(h => h.QualityScore)
            .ThenByDescending(h => h.BlueHits)
            .FirstOrDefault();
    }

    public (double NewOffsetMs, double AppliedDeltaMs, bool RevertedToProven, OffsetPerformance? RevertedStats)
        RecordOutcomeAndTune(double usedOffsetMs, TachoFeedback fb, bool isFixedPlayer)
    {
        if (fb == TachoFeedback.None)
            return (ClickOffsetMs, 0.0, false, null);

        TotalClicks++;
        double bucketOffset = Math.Round(usedOffsetMs * 4.0) / 4.0; // 0.25 ms Raster

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
                return (ClickOffsetMs, 0.0, false, null);

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

        // 1. Bei WEISS (viel zu frueh): sofort auf den besten bewaehrten Offset (oder Default) zurueckkehren
        if (fb == TachoFeedback.WayTooEarlyWhite)
        {
            if (bestProven != null)
            {
                double delta = Math.Round(bestProven.OffsetMs - ClickOffsetMs, 2);
                ClickOffsetMs = bestProven.OffsetMs;
                ConsecutiveNonBlue = 0;
                ConsecutiveRed = 0;
                return (ClickOffsetMs, delta, true, bestProven);
            }

            double def = DefaultClickOffsetMs > 0 ? DefaultClickOffsetMs : 41.0;
            double defDelta = Math.Round(def - ClickOffsetMs, 2);
            ClickOffsetMs = def;
            ConsecutiveNonBlue = 0;
            ConsecutiveRed = 0;
            return (ClickOffsetMs, defDelta, false, null);
        }

        // 2. Pruefen, ob wir uns von einem bewaehrten Offset wegbewegt haben und jetzt schlechter treffen -> Rueckkehr!
        if (bestProven != null && Math.Abs(ClickOffsetMs - bestProven.OffsetMs) >= 1.25)
        {
            bool driftedAndFailing =
                ConsecutiveRed >= 2 ||
                (ConsecutiveNonBlue >= 3 && stat.Total >= 2 && stat.QualityScore < bestProven.QualityScore - 0.15);

            if (driftedAndFailing)
            {
                double delta = Math.Round(bestProven.OffsetMs - ClickOffsetMs, 2);
                ClickOffsetMs = bestProven.OffsetMs;
                ConsecutiveNonBlue = 0;
                ConsecutiveRed = 0;
                return (ClickOffsetMs, delta, true, bestProven);
            }
        }

        // 3. Regulare Anpassung: fuer "feste" Spieler (z.B. Ammonit / Mammut) deutlich kleinere, feinere Schritte!
        double step = fb switch
        {
            TachoFeedback.TooLateRed => isFixedPlayer ? +0.75 : +2.5,
            TachoFeedback.TooEarlyGreen => isFixedPlayer ? -0.25 : -0.5,
            _ => 0.0
        };

        ClickOffsetMs = Math.Clamp(Math.Round(ClickOffsetMs + step, 2), 0.0, 120.0);
        return (ClickOffsetMs, step, false, null);
    }

    public double DistanceTo(TachoFingerprint fp)
    {
        if (!HasSignature)
            return double.MaxValue;

        double dEnd = AngleDiffDeg(GreenEndDeg, fp.GreenEndDeg) / 6.0;
        double dGreenW = Math.Abs(GreenWidthDeg - fp.GreenWidthDeg) / 4.5;
        double dBlueW = Math.Abs(BlueWidthDeg - fp.BlueWidthDeg) / 4.5;

        double speedTol = Math.Max(20.0, EndSpeedDegPerSec * 0.065);
        double dSpeed = Math.Abs(EndSpeedDegPerSec - fp.EndSpeedDegPerSec) / speedTol;

        double accelTol = Math.Max(55.0, Math.Abs(AccelDegPerSec2) * 0.25);
        double dAccel = Math.Abs(AccelDegPerSec2 - fp.AccelDegPerSec2) / accelTol;

        if (dEnd > 1.5 || dGreenW > 1.4 || dBlueW > 1.5 || dSpeed > 1.4 || dAccel > 1.6)
            return double.MaxValue;

        return Math.Sqrt((dEnd * dEnd + dGreenW * dGreenW + dBlueW * dBlueW + dSpeed * dSpeed * 1.4 + dAccel * dAccel * 0.8) / 5.2);
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
    public List<TachoSetupSlot> Tachos { get; set; } = new();
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

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

    public (EnemySetup Setup, bool IsNew, double MatchDistance) MatchOrCreate(
        int profileSize,
        int tachoIndex,
        TachoFingerprint currentFp,
        EnemySetup? currentRoundSetup,
        IReadOnlyDictionary<int, TachoFingerprint> roundFingerprints,
        IReadOnlyList<TachoPoint> activePositions,
        double fallbackOffsetMs)
    {
        int curX = tachoIndex < activePositions.Count ? activePositions[tachoIndex].X : 0;
        int curY = tachoIndex < activePositions.Count ? activePositions[tachoIndex].Y : 0;

        // Case 1: Mid-round (Tacho 2 or Tacho 3) with an already active setup from Tacho 1
        if (tachoIndex > 0 && currentRoundSetup != null && currentRoundSetup.ProfileSize == profileSize)
        {
            currentRoundSetup.EnsureSlots();
            var slot = currentRoundSetup.Tachos[tachoIndex];

            if (!slot.HasSignature)
            {
                slot.InitFrom(currentFp, fallbackOffsetMs, curX, curY);
                currentRoundSetup.LastSeenUtc = DateTime.UtcNow;
                Save();
                return (currentRoundSetup, false, 0.0);
            }

            double dCurrent = slot.DistanceTo(currentFp);
            if (dCurrent <= 1.0)
            {
                if (!slot.HasPosition && curX > 0 && curY > 0) { slot.X = curX; slot.Y = curY; }
                slot.UpdateSignatureEma(currentFp);
                currentRoundSetup.LastSeenUtc = DateTime.UtcNow;
                Save();
                return (currentRoundSetup, false, dCurrent);
            }

            EnemySetup? bestJoint = null;
            double bestJointDist = double.MaxValue;
            foreach (var candidate in Setups)
            {
                if (candidate.ProfileSize != profileSize) continue;
                candidate.EnsureSlots();

                double dThis = candidate.Tachos[tachoIndex].DistanceTo(currentFp);
                if (dThis > 1.0) continue;

                double sumDist = dThis;
                int count = 1;
                bool allOk = true;

                foreach (var kv in roundFingerprints)
                {
                    if (kv.Key == tachoIndex || kv.Key >= candidate.Tachos.Count) continue;
                    double dPrev = candidate.Tachos[kv.Key].DistanceTo(kv.Value);
                    if (dPrev > 1.15)
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
                    if (avg < bestJointDist)
                    {
                        bestJointDist = avg;
                        bestJoint = candidate;
                    }
                }
            }

            if (bestJoint != null)
            {
                var matchedSlot = bestJoint.Tachos[tachoIndex];
                if (!matchedSlot.HasPosition && curX > 0 && curY > 0) { matchedSlot.X = curX; matchedSlot.Y = curY; }
                matchedSlot.UpdateSignatureEma(currentFp);
                bestJoint.LastSeenUtc = DateTime.UtcNow;
                Save();
                return (bestJoint, false, bestJointDist);
            }

            var splitSetup = CreateNewSetup(profileSize, tachoIndex, currentFp, roundFingerprints, activePositions, fallbackOffsetMs, currentRoundSetup);
            return (splitSetup, true, 0.0);
        }

        // Case 2: Tacho 1 (start of a round)
        EnemySetup? bestMatch = null;
        double bestDist = double.MaxValue;

        foreach (var candidate in Setups)
        {
            if (candidate.ProfileSize != profileSize) continue;
            candidate.EnsureSlots();

            double dist = candidate.Tachos[tachoIndex].DistanceTo(currentFp);
            if (dist <= 1.0 && dist < bestDist)
            {
                bestDist = dist;
                bestMatch = candidate;
            }
        }

        if (bestMatch != null)
        {
            var matchedSlot = bestMatch.Tachos[tachoIndex];
            if (!matchedSlot.HasPosition && curX > 0 && curY > 0) { matchedSlot.X = curX; matchedSlot.Y = curY; }
            matchedSlot.UpdateSignatureEma(currentFp);
            bestMatch.LastSeenUtc = DateTime.UtcNow;
            Save();
            return (bestMatch, false, bestDist);
        }

        var created = CreateNewSetup(profileSize, tachoIndex, currentFp, roundFingerprints, activePositions, fallbackOffsetMs, null);
        return (created, true, 0.0);
    }

    private EnemySetup CreateNewSetup(
        int profileSize,
        int currentTachoIndex,
        TachoFingerprint currentFp,
        IReadOnlyDictionary<int, TachoFingerprint> roundFingerprints,
        IReadOnlyList<TachoPoint> activePositions,
        double fallbackOffsetMs,
        EnemySetup? copiedFrom)
    {
        int id = NextId++;
        var setup = new EnemySetup
        {
            Id = id,
            Name = $"Spieler-Setup #{id}",
            IsFixed = false,
            ProfileSize = profileSize,
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
