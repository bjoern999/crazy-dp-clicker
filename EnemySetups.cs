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

internal sealed class TachoSetupSlot
{
    public bool HasSignature { get; set; }
    public double GreenEndDeg { get; set; }
    public double GreenWidthDeg { get; set; }
    public double BlueWidthDeg { get; set; }
    public double EndSpeedDegPerSec { get; set; }
    public double AccelDegPerSec2 { get; set; }

    public double ClickOffsetMs { get; set; } = 41.0;
    public double DefaultClickOffsetMs { get; set; } = 41.0;

    public int Samples { get; set; }
    public int BlueHits { get; set; }
    public int TotalClicks { get; set; }

    public void InitFrom(TachoFingerprint fp, double initialOffsetMs)
    {
        HasSignature = true;
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
            InitFrom(fp, ClickOffsetMs);
            return;
        }

        // Gentle exponential moving average so the enemy's signature stays stable & accurate
        double alpha = Samples < 5 ? 0.25 : 0.10;
        GreenEndDeg = Math.Round(BlendAngleDeg(GreenEndDeg, fp.GreenEndDeg, alpha), 1);
        GreenWidthDeg = Math.Round(GreenWidthDeg * (1 - alpha) + fp.GreenWidthDeg * alpha, 1);
        BlueWidthDeg = Math.Round(BlueWidthDeg * (1 - alpha) + fp.BlueWidthDeg * alpha, 1);
        EndSpeedDegPerSec = Math.Round(EndSpeedDegPerSec * (1 - alpha) + fp.EndSpeedDegPerSec * alpha, 1);
        AccelDegPerSec2 = Math.Round(AccelDegPerSec2 * (1 - alpha) + fp.AccelDegPerSec2 * alpha, 1);
        Samples++;
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

        // Hard rejection if any primary feature is clearly from a different enemy
        if (dEnd > 1.5 || dGreenW > 1.4 || dBlueW > 1.5 || dSpeed > 1.4 || dAccel > 1.6)
            return double.MaxValue;

        return Math.Sqrt((dEnd * dEnd + dGreenW * dGreenW + dBlueW * dBlueW + dSpeed * dSpeed * 1.4 + dAccel * dAccel * 0.8) / 5.2);
    }

    private static double AngleDiffDeg(double a, double b)
    {
        double d = Math.Abs((a - b + 540.0) % 360.0 - 180.0);
        return d;
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

    /// <summary>
    /// Identifies the enemy setup from the current Tacho's fingerprint (and previous Tacho fingerprints in this round).
    /// Returns the matched or newly created EnemySetup along with whether it was newly created.
    /// </summary>
    public (EnemySetup Setup, bool IsNew, double MatchDistance) MatchOrCreate(
        int profileSize,
        int tachoIndex,
        TachoFingerprint currentFp,
        EnemySetup? currentRoundSetup,
        IReadOnlyDictionary<int, TachoFingerprint> roundFingerprints,
        double fallbackOffsetMs)
    {
        // Case 1: Mid-round (Tacho 2 or Tacho 3) and we already have an active setup from Tacho 1
        if (tachoIndex > 0 && currentRoundSetup != null && currentRoundSetup.ProfileSize == profileSize)
        {
            currentRoundSetup.EnsureSlots();
            var slot = currentRoundSetup.Tachos[tachoIndex];

            if (!slot.HasSignature)
            {
                // First time seeing Tacho 2/3 for this enemy -> attach signature to this setup
                slot.InitFrom(currentFp, fallbackOffsetMs);
                currentRoundSetup.LastSeenUtc = DateTime.UtcNow;
                Save();
                return (currentRoundSetup, false, 0.0);
            }

            double dCurrent = slot.DistanceTo(currentFp);
            if (dCurrent <= 1.0)
            {
                slot.UpdateSignatureEma(currentFp);
                currentRoundSetup.LastSeenUtc = DateTime.UtcNow;
                Save();
                return (currentRoundSetup, false, dCurrent);
            }

            // Tacho 1 was similar to another enemy, but Tacho 2/3 differs! Search all setups matching ALL seen tachos of this round.
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
                bestJoint.Tachos[tachoIndex].UpdateSignatureEma(currentFp);
                bestJoint.LastSeenUtc = DateTime.UtcNow;
                Save();
                return (bestJoint, false, bestJointDist);
            }

            // Brand-new enemy that shared Tacho 1 with an earlier enemy -> create a distinct EnemySetup!
            var splitSetup = CreateNewSetup(profileSize, tachoIndex, currentFp, roundFingerprints, fallbackOffsetMs, currentRoundSetup);
            return (splitSetup, true, 0.0);
        }

        // Case 2: Tacho 1 (start of a round) -> match against all saved EnemySetups for this profile size
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
            bestMatch.Tachos[tachoIndex].UpdateSignatureEma(currentFp);
            bestMatch.LastSeenUtc = DateTime.UtcNow;
            Save();
            return (bestMatch, false, bestDist);
        }

        var created = CreateNewSetup(profileSize, tachoIndex, currentFp, roundFingerprints, fallbackOffsetMs, null);
        return (created, true, 0.0);
    }

    private EnemySetup CreateNewSetup(
        int profileSize,
        int currentTachoIndex,
        TachoFingerprint currentFp,
        IReadOnlyDictionary<int, TachoFingerprint> roundFingerprints,
        double fallbackOffsetMs,
        EnemySetup? copiedFrom)
    {
        int id = NextId++;
        var setup = new EnemySetup
        {
            Id = id,
            Name = $"Gegner #{id}",
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
                setup.Tachos[kv.Key].InitFrom(kv.Value, inheritedOffset);
            }
        }

        setup.Tachos[currentTachoIndex].InitFrom(currentFp, fallbackOffsetMs);
        Setups.Add(setup);
        Save();
        return setup;
    }
}
