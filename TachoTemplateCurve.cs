using System.Text.Json.Serialization;

namespace TachoClicker;

/// <summary>
/// Empirische Master-Kurve fuer einen (Spieler, Tacho)-Slot.
/// Speichert die exakte relative Zeit vor dem Zielwinkel 0° (TimeBeforeZeroMs) an 16 festen Winkel-Stuetzstellen.
/// Ermoeglicht im Live-Betrieb einen 1-Parameter-Phase-Lock (nur ein Zeitversatz Δt ueber alle ~15-20 Checkpoints)
/// statt einer verrauschten 3-Parameter-Quadratik -> reduziert Klick-Jitter auf < ±0,5 ms und eliminiert RemAtTrig-Bias!
/// </summary>
internal sealed class TachoTemplateCurve
{
    public static readonly double[] GridAnglesDeg =
    {
        240.0, 225.0, 210.0, 195.0, 180.0, 165.0, 150.0, 135.0,
        120.0, 105.0, 90.0, 75.0, 65.0, 55.0, 45.0, 35.0
    };

    public const int MaxRefinementSamples = 10;

    public double[] TimeBeforeZeroMs { get; set; } = new double[GridAnglesDeg.Length];
    public int SampleCount { get; set; }

    [JsonIgnore]
    public bool IsReady => SampleCount >= 2 && PopulatedPointCount >= 6;

    [JsonIgnore]
    public int PopulatedPointCount
    {
        get
        {
            if (TimeBeforeZeroMs == null || TimeBeforeZeroMs.Length != GridAnglesDeg.Length)
                return 0;
            int cnt = 0;
            for (int i = 0; i < TimeBeforeZeroMs.Length; i++)
                if (TimeBeforeZeroMs[i] > 1.0) cnt++;
            return cnt;
        }
    }

    public void EnsureArray()
    {
        if (TimeBeforeZeroMs == null || TimeBeforeZeroMs.Length != GridAnglesDeg.Length)
            TimeBeforeZeroMs = new double[GridAnglesDeg.Length];
    }

    /// <summary>
    /// Lernt / verfeinert die Master-Kurve aus einem sauberen Durchlauf (R² >= 0.994, >= 10 Checkpoints).
    /// Nach MaxRefinementSamples (10) wird die Kurve eingefroren, damit der Nullpunkt niemals driftet.
    /// </summary>
    public bool UpdateFromSpin(
        IReadOnlyList<(double TimeMs, double RemainingDeg)> checkpoints,
        double referenceZeroTimeMs,
        double r2)
    {
        EnsureArray();
        if (SampleCount >= MaxRefinementSamples)
            return false;
        if (checkpoints == null || checkpoints.Count < 10 || r2 < 0.994)
            return false;

        double[] obsTimeAtGrid = new double[GridAnglesDeg.Length];
        bool[] hasObs = new bool[GridAnglesDeg.Length];
        int obsCount = 0;

        for (int g = 0; g < GridAnglesDeg.Length; g++)
        {
            if (TryInterpolateTimeAtAngle(checkpoints, GridAnglesDeg[g], out double tAtAngle))
            {
                obsTimeAtGrid[g] = tAtAngle;
                hasObs[g] = true;
                obsCount++;
            }
        }

        if (obsCount < 6)
            return false;

        if (SampleCount == 0)
        {
            for (int g = 0; g < GridAnglesDeg.Length; g++)
            {
                if (hasObs[g])
                {
                    double tbz = referenceZeroTimeMs - obsTimeAtGrid[g];
                    if (tbz > 5.0 && tbz < 2500.0)
                        TimeBeforeZeroMs[g] = Math.Round(tbz, 2);
                }
            }
            if (PopulatedPointCount >= 6)
            {
                SampleCount = 1;
                return true;
            }
            return false;
        }

        // Bestehende Vorlage zuerst per 1-Parameter-Phase-Lock (Interquartil-Mittel) ausrichten,
        // damit der Nullpunkt der Master-Kurve 100% stabil bleibt!
        var estimates = new List<double>(GridAnglesDeg.Length);
        for (int g = 0; g < GridAnglesDeg.Length; g++)
        {
            if (hasObs[g] && TimeBeforeZeroMs[g] > 1.0)
                estimates.Add(obsTimeAtGrid[g] + TimeBeforeZeroMs[g]);
        }

        if (estimates.Count < 6)
            return false;

        double alignedZeroMs = ComputeInterquartileMean(estimates);
        double madMs = ComputeMad(estimates, alignedZeroMs);
        if (madMs > 15.0)
            return false; // Ausreisser / unpassender Spin -> nicht einmischen!

        double alpha = SampleCount < 4 ? 0.25 : 0.12;
        for (int g = 0; g < GridAnglesDeg.Length; g++)
        {
            if (!hasObs[g]) continue;
            double tbz = alignedZeroMs - obsTimeAtGrid[g];
            if (tbz <= 5.0 || tbz >= 2500.0) continue;

            if (TimeBeforeZeroMs[g] <= 1.0)
                TimeBeforeZeroMs[g] = Math.Round(tbz, 2);
            else
                TimeBeforeZeroMs[g] = Math.Round(TimeBeforeZeroMs[g] * (1.0 - alpha) + tbz * alpha, 2);
        }

        SampleCount++;
        return true;
    }

    /// <summary>
    /// 1-Parameter-Phase-Lock: berechnet die exakte Zielzeit t_zero als robustes Interquartil-Mittel
    /// ueber alle aktuellen Checkpoints im Vergleich zur gelernten Master-Kurve.
    /// </summary>
    public bool TryPredictZeroTimeMs(
        IReadOnlyList<(double TimeMs, double RemainingDeg)> checkpoints,
        out double zeroTimeMs,
        out int matchedPoints,
        out double madMs)
    {
        zeroTimeMs = 0;
        matchedPoints = 0;
        madMs = double.MaxValue;

        EnsureArray();
        if (!IsReady || checkpoints == null || checkpoints.Count < 6)
            return false;

        var estimates = new List<double>(checkpoints.Count);
        for (int i = 0; i < checkpoints.Count; i++)
        {
            var (tMs, remDeg) = checkpoints[i];
            if (TryInterpolateTimeBeforeZeroMs(remDeg, out double tbz))
            {
                estimates.Add(tMs + tbz);
            }
        }

        matchedPoints = estimates.Count;
        if (matchedPoints < 6)
            return false;

        zeroTimeMs = ComputeInterquartileMean(estimates);
        madMs = ComputeMad(estimates, zeroTimeMs);
        return madMs <= 12.0;
    }

    private bool TryInterpolateTimeBeforeZeroMs(double remDeg, out double tbzMs)
    {
        tbzMs = 0;
        // GridAnglesDeg ist streng absteigend (240 -> 35)
        int prevIdx = -1;
        for (int i = 0; i < GridAnglesDeg.Length; i++)
        {
            if (TimeBeforeZeroMs[i] <= 1.0) continue;

            double gDeg = GridAnglesDeg[i];
            if (Math.Abs(remDeg - gDeg) < 0.01)
            {
                tbzMs = TimeBeforeZeroMs[i];
                return true;
            }

            if (prevIdx >= 0 && remDeg <= GridAnglesDeg[prevIdx] && remDeg >= gDeg)
            {
                double hiDeg = GridAnglesDeg[prevIdx];
                double loDeg = gDeg;
                double frac = (hiDeg - remDeg) / (hiDeg - loDeg);
                tbzMs = TimeBeforeZeroMs[prevIdx] + frac * (TimeBeforeZeroMs[i] - TimeBeforeZeroMs[prevIdx]);
                return true;
            }

            prevIdx = i;
        }
        return false;
    }

    private static bool TryInterpolateTimeAtAngle(
        IReadOnlyList<(double TimeMs, double RemainingDeg)> cps,
        double targetDeg,
        out double timeMs)
    {
        timeMs = 0;
        for (int i = 1; i < cps.Count; i++)
        {
            var prev = cps[i - 1];
            var cur = cps[i];
            if (prev.RemainingDeg >= targetDeg && cur.RemainingDeg <= targetDeg)
            {
                double dd = prev.RemainingDeg - cur.RemainingDeg;
                if (dd > 0.01)
                {
                    double frac = (prev.RemainingDeg - targetDeg) / dd;
                    timeMs = prev.TimeMs + frac * (cur.TimeMs - prev.TimeMs);
                    return true;
                }
            }
        }
        return false;
    }

    private static double ComputeInterquartileMean(List<double> values)
    {
        values.Sort();
        int n = values.Count;
        if (n <= 2) return values.Average();
        int lo = n / 4;
        int hi = n - lo;
        double sum = 0;
        for (int i = lo; i < hi; i++)
            sum += values[i];
        return sum / (hi - lo);
    }

    private static double ComputeMad(List<double> sortedValues, double center)
    {
        var devs = new List<double>(sortedValues.Count);
        for (int i = 0; i < sortedValues.Count; i++)
            devs.Add(Math.Abs(sortedValues[i] - center));
        devs.Sort();
        return devs[devs.Count / 2];
    }
}
