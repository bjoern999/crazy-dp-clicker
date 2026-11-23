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
    public const int MatureMinSamples = 30;

    public double OffsetMs { get; set; }
    public int BlueHits { get; set; }
    public int GreenHits { get; set; }
    public int RedHits { get; set; }
    public int WhiteHits { get; set; }

    public int Total => BlueHits + GreenHits + RedHits + WhiteHits;
    public bool IsMature => Total >= MatureMinSamples;
    public int TopRank { get; set; } // 1 = Top-1, 2 = Top-2, 3 = Top-3, 0 = nicht in Top-3
    public bool ManualTop1 { get; set; } // Manuell gesetztes Top-1 (bleibt erhalten, auch wenn N < 30)
    public bool ExcludeFromTop { get; set; } // Manuell von automatischen Top-Markierungen ausgenommen
    public double BlueRate => Total > 0 ? (double)BlueHits / Total : 0.0;
    public double SafeRate => Total > 0 ? (double)(BlueHits + GreenHits) / Total : 0.0;
    public double RedRate => Total > 0 ? (double)RedHits / Total : 0.0;
    public double BadRate => Total > 0 ? (double)(RedHits + WhiteHits) / Total : 0.0;
    public bool IsPureGreenOnly => GreenHits > 0 && BlueHits == 0 && RedHits == 0 && WhiteHits == 0;
    public bool IsSafeGreen => GreenHits >= 10 && RedHits == 0;

    /// <summary>
    /// Composite quality score favoring high Blue hit rate and safe Green hits while heavily penalizing Red (round lost!).
    /// </summary>
    public double QualityScore => Total > 0
        ? (BlueHits * 1.0 + GreenHits * 0.45 - RedHits * 3.00 - WhiteHits * 1.50) / Total
        : -999.0;

    /// <summary>
    /// Konservativer Wilson-Risiko-Score: belohnt Blau (+1,00) & sicheres Gruen (+0,45), bestraft Rot (-3,00)
    /// und Weiss (-1,50) massiv und zieht bei kleinem N einen Wilson-Unsicherheitsabschlag (z=1,28) ab.
    /// </summary>
    public double SmoothedScore
    {
        get
        {
            if (Total <= 0) return -999.0;
            double baseScore = (BlueHits * 1.0 + GreenHits * 0.45 - RedHits * 3.00 - WhiteHits * 1.50 + 0.60) / (Total + 2.0);
            double pBad = BadRate;
            const double z = 1.28;
            const double z2 = z * z;
            double denom = 1.0 + z2 / Total;
            double wilsonUpper = (pBad + z2 / (2.0 * Total) + z * Math.Sqrt((pBad * (1.0 - pBad) + z2 / (4.0 * Total)) / Total)) / denom;
            double riskPenalty = 0.35 * Math.Max(0.0, wilsonUpper - pBad);
            return baseScore - riskPenalty;
        }
    }
}

internal sealed class DeploymentOutcomeRecord
{
    public int DeploymentId { get; set; } // 0 = historische Daten vor Tagging, 1, 2, 3... = Spieler-Einsatz-Nummer
    public double OffsetMs { get; set; }
    public int BlueHits { get; set; }
    public int GreenHits { get; set; }
    public int RedHits { get; set; }
    public int WhiteHits { get; set; }
}

internal sealed class TachoSetupSlot
{
    public const int CurrentTimingVersion = 3; // v3 = DXGI/DWM Hardware-VBlank-Zeitstempel + Sub-Grad-Kante + Spin-Wait ClickAt

    public bool HasSignature { get; set; }
    public bool IsFixed { get; set; }
    public int TimingVersion { get; set; } = 1; // Fehlt im Alt-JSON -> migriert einmalig sauber auf v2!

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

    // Empirische Master-Kurve fuer 1-Parameter-Phase-Lock (< ±0,5 ms Jitter, 0 ms RemAtTrig-Bias)
    public TachoTemplateCurve TemplateCurve { get; set; } = new();

    public int Samples { get; set; }
    public int BlueHits { get; set; }
    public int GreenHits { get; set; }
    public int RedHits { get; set; }
    public int WhiteHits { get; set; }
    public int TotalClicks { get; set; }

    // Pro Offset gemerkte Treffer-Quoten fuer Rueckkehr zu bewaehrten Parametern (aktuelle TimingVersion)
    public List<OffsetPerformance> OffsetHistory { get; set; } = new();

    // Archivierte Vor-Zentrierungs-Quoten (TimingVersion < 2), damit Altlasten die neue Engine nicht verzerren
    public List<OffsetPerformance> LegacyOffsetHistory { get; set; } = new();

    // Pro Spieler-Einsatz (DeploymentId: 0 = Altbestand, 1, 2, 3... = fortlaufende Einsatz-Nr.) getaggte Statistik
    // fuer sauberes Umhaengen bei nachtraeglicher Spieler-Korrektur
    public List<DeploymentOutcomeRecord> DeploymentHistory { get; set; } = new();

    // True sobald 3 gereifte (mature, >=30 Klicks) Top-Presets fuer diesen Tacho feststehen
    public bool IsMeisterTacho { get; set; }

    // True sobald 3 gereifte Top-Presets existieren und noch 0,05ms-Zwischenwerte zwischen Top-1..3 verfeinert werden
    public bool IsRefinementActive { get; set; }

    // Manuell aktivierte Verfeinerungsphase (auch schon ab Top-1 im +-0,50ms-Fenster in 0,05ms-Schritten)
    public bool ManualRefinement { get; set; }

    // True sobald alle 0,05ms-Zwischenwerte zwischen den Top-3 gereift (oder gesperrt) sind -> reine Top-1/2/3-Rotation!
    public bool IsRefinementComplete { get; set; }

    // Laufzeit-Zaehler & fluechtiger Session-Drift gegen 60Hz-/DWM-Phasenverschiebung (nicht persistent)
    public int ConsecutiveNonBlue { get; set; }
    public int ConsecutiveRed { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public double SessionDriftMs { get; set; } = 0.0;

    [System.Text.Json.Serialization.JsonIgnore]
    public double EffectiveClickOffsetMs =>
        Math.Clamp(Math.Round(ClickOffsetMs + SessionDriftMs, 2), 0.0, 120.0);

    public bool HasPosition => X > 0 && Y > 0;
    public double BlueRatePercent => TotalClicks > 0 ? 100.0 * BlueHits / TotalClicks : 0.0;

    public bool MigrateToCurrentTimingVersion()
    {
        TemplateCurve ??= new TachoTemplateCurve();
        TemplateCurve.EnsureArray();

        if (TimingVersion >= CurrentTimingVersion)
            return false;

        // Bewahre den besten bisherigen Offset als sicheren Startwert fuer v3 (Hardware-VBlank-Epoche):
        // Hardware-VBlank liegt im Mittel ~3,2 ms frueher als das alte Post-GDI NowMs() -> -2,0 ms Shift startet
        // konservativ ~1,2 ms auf der sicheren GRUEN-Seite (kein Rot-Risiko) und zoomt per Bisection direkt auf Blau!
        var bestOld = GetTopPreset(1) ?? GetBestStartupPreset();
        double baseOff = (bestOld != null && bestOld.OffsetMs >= 20.0 && bestOld.OffsetMs <= 95.0)
            ? bestOld.OffsetMs
            : ClickOffsetMs;
        if (baseOff >= 15.0 && baseOff <= 105.0)
        {
            double v3Start = SnapToQuarterMs(Math.Max(10.0, baseOff - 2.0));
            ClickOffsetMs = v3Start;
            DefaultClickOffsetMs = v3Start;
        }

        // Alte GDI-TemplateCurve zuruecksetzen, damit v3 in 2 Spins eine 0,1°-scharfe VBlank-Master-Kurve lernt!
        TemplateCurve = new TachoTemplateCurve();

        if (OffsetHistory.Count > 0)
        {
            LegacyOffsetHistory = new List<OffsetPerformance>(OffsetHistory);
            OffsetHistory.Clear();
        }
        DeploymentHistory.Clear();
        BlueHits = 0;
        GreenHits = 0;
        RedHits = 0;
        WhiteHits = 0;
        TotalClicks = 0;
        IsMeisterTacho = false;
        IsRefinementActive = false;
        IsRefinementComplete = false;
        ConsecutiveNonBlue = 0;
        ConsecutiveRed = 0;
        SessionDriftMs = 0.0;
        TimingVersion = CurrentTimingVersion;
        return true;
    }

    public void InitFrom(TachoFingerprint fp, double initialOffsetMs, int x, int y)
    {
        HasSignature = true;
        TimingVersion = CurrentTimingVersion;
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

    public void UpdateSignatureEma(TachoFingerprint fp)
    {
        if (!HasSignature)
        {
            InitFrom(fp, ClickOffsetMs, X, Y);
            return;
        }

        // Feste Tachos frieren ihre kalibrierte Tacho-Signatur ein,
        // damit sich aehnliche Tachos niemals gegenseitig per EMA verschmieren koennen!
        if (IsFixed)
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

    public static double SnapToFineMs(double offsetMs) =>
        Math.Clamp(Math.Round(offsetMs * 20.0) / 20.0, 0.0, 120.0);

    public OffsetPerformance? FindPresetStats(double offsetMs)
    {
        double bucket = SnapToFineMs(offsetMs);
        return OffsetHistory.FirstOrDefault(h => Math.Abs(h.OffsetMs - bucket) < 0.02);
    }

    public OffsetPerformance? GetBestProvenOffset(double? excludeOffsetMs = null)
    {
        bool IsExcluded(OffsetPerformance h) =>
            excludeOffsetMs.HasValue && Math.Abs(h.OffsetMs - SnapToFineMs(excludeOffsetMs.Value)) < 0.02;

        // 1. Bewaehrte Blau-Presets (>= 3 Klicks, >= 2 Blau, >= 35% Blau)
        var proven = OffsetHistory
            .Where(h => !IsExcluded(h) && h.Total >= 3 && h.BlueHits >= 2 && h.BlueRate >= 0.35)
            .OrderByDescending(h => h.SmoothedScore)
            .ThenByDescending(h => h.BlueHits)
            .FirstOrDefault();

        if (proven != null)
            return proven;

        // 2. Fallback: mindestens 1 sauberer Blau-Treffer ohne Rot/Weiss
        return OffsetHistory
            .Where(h => !IsExcluded(h) && h.BlueHits >= 1 && h.RedHits == 0 && h.WhiteHits == 0)
            .OrderByDescending(h => h.SmoothedScore)
            .ThenByDescending(h => h.BlueHits)
            .FirstOrDefault();
    }

    /// <summary>
    /// Waehlt das beste bekannte Preset fuer dieses (Spieler, Tacho)-Paar beim Erkennen des Spielers:
    /// 1. Bestes Blau-Preset.
    /// 2. Falls noch kein Blau-Preset existiert: das spaeteste (riskanteste) ueberwiegend gruene Preset (SafeRate >= 50%).
    /// </summary>
    public OffsetPerformance? GetBestStartupPreset(double? excludeOffsetMs = null)
    {
        var bestBlue = GetBestProvenOffset(excludeOffsetMs);
        if (bestBlue != null)
            return bestBlue;

        bool IsExcluded(OffsetPerformance h) =>
            excludeOffsetMs.HasValue && Math.Abs(h.OffsetMs - SnapToFineMs(excludeOffsetMs.Value)) < 0.02;

        return OffsetHistory
            .Where(h => !IsExcluded(h) &&
                        h.GreenHits >= 1 && h.SafeRate >= 0.50 && h.WhiteHits == 0)
            .OrderBy(h => h.OffsetMs) // Spaetestes / riskantestes noch ueberwiegend gruenes Preset!
            .FirstOrDefault();
    }

    /// <summary>
    /// Aktualisiert die Markierungen "Top-1", "Top-2", "Top-3" fuer gereifte Presets.
    /// Wenn eine Top-Markierung auf einem ausschliesslich gruenen Preset (0 Blau, 0 Rot) liegt, wird sie aktiv
    /// weiter in Richtung "riskant" (spaeterer Klick / kleinerer Offset) verschoben — auf ein blaues oder noch
    /// ueberwiegend gruenes Preset mit spaeterem Klickzeitpunkt!
    /// </summary>
    public void RefreshTopPresets()
    {
        foreach (var h in OffsetHistory)
            h.TopRank = 0;

        var topCandidates = OffsetHistory
            .Where(h => h.IsMature && !h.ExcludeFromTop)
            .OrderByDescending(h => h.BlueHits > 0 ? 1 : 0) // Blau immer vor reinem Gruen
            .ThenByDescending(h => h.SmoothedScore)
            .ThenByDescending(h => h.BlueRate)
            .ThenBy(h => h.OffsetMs) // Bei Gleichstand: spaeterer (riskanterer) Klick gewinnt!
            .Take(3)
            .ToList();

        var assigned = new List<OffsetPerformance>(3);

        // Manuell gesetztes Top-1 immer zuerst beruecksichtigen (solange nicht ausgenommen)
        var manualTop1 = OffsetHistory
            .FirstOrDefault(h => h.ManualTop1 && !h.ExcludeFromTop);
        if (manualTop1 != null)
            assigned.Add(manualTop1);

        for (int i = 0; i < topCandidates.Count && assigned.Count < 3; i++)
        {
            var chosen = topCandidates[i];
            if (assigned.Contains(chosen))
                continue;

            // Reines Gruen-Top-Preset? -> Staerker in Richtung "riskant" (spaeter klicken) verschieben!
            if (chosen.IsPureGreenOnly)
            {
                // 1. Bevorzugt: spaeter klickendes Preset mit mindestens 1 Blau-Treffer (und SafeRate >= 50%)
                var riskierBlue = OffsetHistory
                    .Where(h => !assigned.Contains(h) && !h.ExcludeFromTop &&
                                h.OffsetMs < chosen.OffsetMs - 0.02 &&
                                h.BlueHits > 0 && h.SafeRate >= 0.50)
                    .OrderByDescending(h => h.BlueRate)
                    .ThenBy(h => h.OffsetMs)
                    .FirstOrDefault();

                if (riskierBlue != null)
                {
                    chosen = riskierBlue;
                }
                else
                {
                    // 2. Sonst: spaetestes Preset unterhalb davon, das noch ueberwiegend gruen (SafeRate >= 50%) ist
                    var riskierGreen = OffsetHistory
                        .Where(h => !assigned.Contains(h) && !h.ExcludeFromTop &&
                                    h.OffsetMs < chosen.OffsetMs - 0.02 &&
                                    h.GreenHits > 0 && h.SafeRate >= 0.50)
                        .OrderBy(h => h.OffsetMs) // Kleinster Offset = spaetester / riskantester Klick!
                        .FirstOrDefault();

                    if (riskierGreen != null)
                        chosen = riskierGreen;
                }
            }

            if (!assigned.Contains(chosen))
                assigned.Add(chosen);
        }

        for (int i = 0; i < assigned.Count; i++)
            assigned[i].TopRank = i + 1;

        // Meister-Tacho nur, wenn 3 echte gereifte Top-Presets existieren und Top-1 kein reines Gruen ist
        IsMeisterTacho = assigned.Count >= 3 && assigned.All(h => h.IsMature) && assigned[0].BlueHits > 0;
        if (IsMeisterTacho || (ManualRefinement && assigned.Count >= 1 && assigned[0].BlueHits > 0))
        {
            var (_, _, openCount) = GetRefinementInfo();
            IsRefinementActive = openCount > 0;
            IsRefinementComplete = openCount == 0;
        }
        else
        {
            IsRefinementActive = false;
            IsRefinementComplete = false;
        }
    }

    public OffsetPerformance? GetTopPreset(int rank)
    {
        return OffsetHistory.FirstOrDefault(h => h.TopRank == rank);
    }

    public (double MinMs, double MaxMs, int OpenCandidates) GetRefinementInfo()
    {
        var t1 = GetTopPreset(1);
        var t2 = GetTopPreset(2);
        var t3 = GetTopPreset(3);
        double minMs, maxMs;
        if (t1 != null && t2 != null && t3 != null)
        {
            minMs = SnapToFineMs(Math.Min(t1.OffsetMs, Math.Min(t2.OffsetMs, t3.OffsetMs)));
            maxMs = SnapToFineMs(Math.Max(t1.OffsetMs, Math.Max(t2.OffsetMs, t3.OffsetMs)));
        }
        else if (ManualRefinement && t1 != null)
        {
            // Manuelle Verfeinerung rund um Top-1: feines +-0,50ms-Fenster in 0,05ms-Schritten
            minMs = SnapToFineMs(Math.Max(0.0, t1.OffsetMs - 0.50));
            maxMs = SnapToFineMs(Math.Min(120.0, t1.OffsetMs + 0.50));
        }
        else
        {
            return (0.0, 0.0, 0);
        }
        int open = 0;

        for (double v = SnapToFineMs(minMs + 0.05); v < maxMs - 0.02; v = SnapToFineMs(v + 0.05))
        {
            var st = FindPresetStats(v);
            if (st == null || !st.IsMature)
                open++;
        }

        return (minMs, maxMs, open);
    }

    private double? FindNextRefinementOffset(double currentMs, int direction, double minMs, double maxMs)
    {
        double cur = SnapToFineMs(currentMs);
        double step = direction >= 0 ? 0.05 : -0.05;

        // 1. Zuerst in gewuenschter Korrekturrichtung (+0,05 ms bei Rot, -0,05 ms bei Gruen/Weiss)
        //    den naechsten 0,05ms-Wert im Bereich [minMs, maxMs] suchen.
        for (double v = SnapToFineMs(cur + step);
             v >= minMs - 0.001 && v <= maxMs + 0.001;
             v = SnapToFineMs(v + step))
        {
            if (Math.Abs(v - cur) < 0.02) continue;
            return v;
        }

        // 2. Falls am Rand des Top-3-Intervalls: naechstgelegenen offenen (noch nicht mature) 0,05ms-Kandidaten waehlen
        double? nearestOpen = null;
        double bestDist = double.MaxValue;
        for (double v = SnapToFineMs(minMs + 0.05); v < maxMs - 0.02; v = SnapToFineMs(v + 0.05))
        {
            if (Math.Abs(v - cur) < 0.02) continue;
            var st = FindPresetStats(v);
            if (st == null || !st.IsMature)
            {
                double d = Math.Abs(v - cur);
                if (d < bestDist)
                {
                    bestDist = d;
                    nearestOpen = v;
                }
            }
        }

        return nearestOpen;
    }

    /// <summary>
    /// Laedt beim Erkennen eines Spielers sofort das Top-1-Preset (falls gereift vorhanden)
    /// bzw. sonst das Preset mit der bislang besten Erfolgsquote und setzt den Session-Drift zurueck.
    /// </summary>
    public (bool Changed, double OldOffsetMs, double NewOffsetMs, OffsetPerformance? ChosenStats) ApplyBestKnownPreset()
    {
        RefreshTopPresets();
        SessionDriftMs = 0.0;
        double oldOffset = ClickOffsetMs;
        var best = GetTopPreset(1) ?? GetBestStartupPreset();
        if (best != null)
        {
            // Niemals von einem bereits spaeteren (riskanteren) Preset zurueck auf ein frueheres reines Gruen-Preset ziehen!
            if (best.IsPureGreenOnly && ClickOffsetMs < best.OffsetMs - 0.02)
            {
                ConsecutiveNonBlue = 0;
                ConsecutiveRed = 0;
                return (false, oldOffset, ClickOffsetMs, null);
            }

            ClickOffsetMs = best.OffsetMs;
            ConsecutiveNonBlue = 0;
            ConsecutiveRed = 0;
            return (Math.Abs(ClickOffsetMs - oldOffset) >= 0.02, oldOffset, ClickOffsetMs, best);
        }

        return (false, oldOffset, ClickOffsetMs, null);
    }

    /// <summary>
    /// Lokales adaptives Bracketing & Intervall-Halbierung (Bisection) direkt unterhalb des AKTUELLEN Gruen-Offsets:
    /// Springt niemals weit nach unten in veraltete Zonen, sondern halbiert nur lokal (Span 0,09..2,50 ms) zwischen
    /// aktuellem Gruen (ClickOffsetMs) und dem naechsten bekannten Rot/Vogel-Weiss direkt darunter!
    /// </summary>
    public bool TryGetBisectionTarget(bool isBird, out double midOffsetMs, out double greenLowMs, out double lateHighMs)
    {
        midOffsetMs = 0;
        greenLowMs = 0;
        lateHighMs = 0;

        // Nur aktiv, solange noch kein starkes Blau-Preset (>=40% Blau) gefunden wurde
        var bestBlue = GetBestProvenOffset();
        if (bestBlue != null && bestBlue.BlueRate >= 0.40)
            return false;

        double curGreen = SnapToFineMs(ClickOffsetMs);

        // Suche das hoechste Rot/Vogel-Weiss-Preset strikt UNTERHALB des aktuellen Gruen-Offsets (max 2,5 ms Abstand!)
        var lateHigh = OffsetHistory
            .Where(h => h.OffsetMs < curGreen - 0.04 &&
                        h.OffsetMs >= curGreen - 2.50 &&
                        (isBird ? (h.RedHits + h.WhiteHits) >= 1 : h.RedHits >= 1))
            .OrderByDescending(h => h.OffsetMs)
            .FirstOrDefault();

        if (lateHigh == null)
            return false;

        double span = curGreen - lateHigh.OffsetMs;
        if (span < 0.09 || span > 2.50)
            return false;

        double mid = SnapToFineMs((curGreen + lateHigh.OffsetMs) / 2.0);
        if (mid <= lateHigh.OffsetMs + 0.02 || mid >= curGreen - 0.02)
            return false;

        midOffsetMs = mid;
        greenLowMs = curGreen;
        lateHighMs = lateHigh.OffsetMs;
        return true;
    }

    private bool TryGetSafeGreenEscape(bool isBird, double currentMs, out double safeGreenMs)
    {
        safeGreenMs = 0;
        var greenAbove = OffsetHistory
            .Where(h => h.GreenHits >= 1 &&
                        (isBird ? h.WhiteHits == 0 : h.RedHits == 0) &&
                        h.OffsetMs >= currentMs + 0.04 && h.OffsetMs <= currentMs + 2.5)
            .OrderBy(h => h.OffsetMs)
            .FirstOrDefault();
        if (greenAbove == null)
            return false;
        // +0,25 ms zusaetzlicher Sicherheitsabstand weg von der Rot-Kante!
        safeGreenMs = Math.Clamp(SnapToFineMs(greenAbove.OffsetMs + 0.25), 0.0, 120.0);
        return true;
    }

    public void EnsureDeploymentBackfill()
    {
        if (DeploymentHistory.Count > 0 || OffsetHistory.Count == 0)
            return;

        foreach (var h in OffsetHistory)
        {
            if (h.Total <= 0) continue;
            DeploymentHistory.Add(new DeploymentOutcomeRecord
            {
                DeploymentId = 0,
                OffsetMs = h.OffsetMs,
                BlueHits = h.BlueHits,
                GreenHits = h.GreenHits,
                RedHits = h.RedHits,
                WhiteHits = h.WhiteHits
            });
        }
    }

    public (double NewOffsetMs, double AppliedDeltaMs, bool RevertedToProven, OffsetPerformance? RevertedStats, int SkippedBadCount, bool BlockedBadCliff, bool UsedBisection)
        RecordOutcomeAndTune(double usedOffsetMs, TachoFeedback fb, bool isBird = false, int deploymentId = 0, bool allowEmergencyJump = true)
    {
        if (fb == TachoFeedback.None)
            return (ClickOffsetMs, 0.0, false, null, 0, false, false);

        TotalClicks++;
        double bucketOffset = SnapToFineMs(usedOffsetMs);

        var stat = OffsetHistory.FirstOrDefault(h => Math.Abs(h.OffsetMs - bucketOffset) < 0.02);
        if (stat == null)
        {
            stat = new OffsetPerformance { OffsetMs = bucketOffset };
            OffsetHistory.Add(stat);
        }

        var depStat = DeploymentHistory.FirstOrDefault(d => d.DeploymentId == deploymentId && Math.Abs(d.OffsetMs - bucketOffset) < 0.02);
        if (depStat == null)
        {
            depStat = new DeploymentOutcomeRecord { DeploymentId = deploymentId, OffsetMs = bucketOffset };
            DeploymentHistory.Add(depStat);
        }

        // Bei Spielern vom Typ "Vogel" (z.B. Therizinosaurus) folgt NACH dem blauen Bereich noch ein zweiter
        // WEISSER Bereich (Runde nicht verloren, aber wenig Impact - gleicher Farbstatus "Weiss" wie vor Gruen!).
        bool isBirdPostBlueLate = false;
        if (isBird && fb == TachoFeedback.WayTooEarlyWhite)
        {
            var refGood = GetBestProvenOffset() ?? GetBestStartupPreset();
            if (refGood != null)
                isBirdPostBlueLate = usedOffsetMs <= refGood.OffsetMs + 8.0;
            else
                isBirdPostBlueLate = usedOffsetMs < 72.0;
        }

        switch (fb)
        {
            case TachoFeedback.OptimalBlue:
                BlueHits++;
                stat.BlueHits++;
                depStat.BlueHits++;
                ConsecutiveNonBlue = 0;
                ConsecutiveRed = 0;
                RefreshTopPresets();
                return (ClickOffsetMs, 0.0, false, null, 0, false, false);

            case TachoFeedback.TooEarlyGreen:
                GreenHits++;
                stat.GreenHits++;
                depStat.GreenHits++;
                ConsecutiveNonBlue++;
                ConsecutiveRed = 0;
                break;

            case TachoFeedback.TooLateRed:
                RedHits++;
                stat.RedHits++;
                depStat.RedHits++;
                ConsecutiveNonBlue++;
                ConsecutiveRed++;
                break;

            case TachoFeedback.WayTooEarlyWhite:
                WhiteHits++;
                stat.WhiteHits++;
                depStat.WhiteHits++;
                ConsecutiveNonBlue++;
                if (isBirdPostBlueLate)
                    ConsecutiveRed++;
                else
                    ConsecutiveRed = 0;
                break;
        }

        RefreshTopPresets();
        var top1 = GetTopPreset(1);
        var top2 = GetTopPreset(2);
        var top3 = GetTopPreset(3);
        bool isLateMiss = fb == TachoFeedback.TooLateRed || isBirdPostBlueLate;

        // 0. NOTBREMSE bei >= 2x echtem Rot (bzw. Vogel Nach-Blau-Weiss) in Folge:
        //    Grosse 1-Frame-Kompensation (+14,0 ms bzw. +16,0 ms ab 3x), damit selbst ein kompletter +16,7ms
        //    GPU-/Browser-Frame-Sprung in EINEM Schritt ueberwunden wird -> garantiert kein 3. Rot!
        //    Bei bereits bewaehrtem Blau-Preset geht der Sprung in den fluechtigen SessionDriftMs, damit die Basis-Statistik sauber bleibt!
        if (isLateMiss && allowEmergencyJump && ConsecutiveRed >= 2)
        {
            double emergencyJump = ConsecutiveRed >= 3 ? +16.0 : +14.0;
            var provenBase = top1 ?? GetBestProvenOffset();
            if (provenBase != null && provenBase.Total >= 5 && provenBase.BlueRate >= 0.45)
            {
                double oldEff = EffectiveClickOffsetMs;
                ClickOffsetMs = provenBase.OffsetMs;
                SessionDriftMs = Math.Clamp(Math.Round(SessionDriftMs + emergencyJump, 2), -4.0, 24.0);
                double applied = Math.Round(EffectiveClickOffsetMs - oldEff, 2);
                ConsecutiveNonBlue = 0;
                return (EffectiveClickOffsetMs, applied, false, null, 0, false, false);
            }

            double rawTarget = Math.Clamp(Math.Round(ClickOffsetMs + emergencyJump, 2), 0.0, 120.0);
            double appliedRaw = Math.Round(rawTarget - ClickOffsetMs, 2);
            ClickOffsetMs = rawTarget;
            ConsecutiveNonBlue = 0;
            // ConsecutiveRed bleibt aktiv, bis wieder Gruen oder Blau getroffen wird!
            return (ClickOffsetMs, appliedRaw, false, null, 0, false, false);
        }

        // 0a. REFINEMENT- & MASTER-PHASE bei ROT (bzw. Vogel Nach-Blau-Weiss):
        //     Immer das naechste Top-Preset waehlen, das FRUEHER klickt (hoeherer Offset).
        //     Zusaetzlich auf gereiftem Top-Preset den fluechtigen SessionDriftMs sanft +1,0 ms nachfuehren.
        //     Falls bereits auf (oder ueber) dem am fruehesten klickenden Top-Preset: trotzdem frueher klicken (+0,75 ms)!
        if (isLateMiss && (IsMeisterTacho || top1 != null))
        {
            if (stat.TopRank > 0)
                SessionDriftMs = Math.Clamp(Math.Round(SessionDriftMs + 1.0, 2), -4.0, 24.0);

            var earlierTop = new[] { top1, top2, top3 }
                .Where(t => t != null && t.OffsetMs > ClickOffsetMs + 0.02)
                .OrderBy(t => t!.OffsetMs)
                .FirstOrDefault();

            if (earlierTop != null)
            {
                double delta = Math.Round(earlierTop.OffsetMs - ClickOffsetMs, 2);
                ClickOffsetMs = earlierTop.OffsetMs;
                ConsecutiveNonBlue = 0;
                return (ClickOffsetMs, delta, true, earlierTop, 0, false, false);
            }

            if (IsMeisterTacho || ManualRefinement)
            {
                double rawTarget = Math.Clamp(Math.Round(ClickOffsetMs + 0.75, 2), 0.0, 120.0);
                double applied = Math.Round(rawTarget - ClickOffsetMs, 2);
                ClickOffsetMs = rawTarget;
                ConsecutiveNonBlue = 0;
                return (ClickOffsetMs, applied, false, null, 0, false, false);
            }
        }

        // 0b. MODUS "VERFEINERUNG" bei GRUEN / WEISS (zu frueh):
        //     In 0,05ms-Schritten nach unten zwischen Top-1..3 (bzw. im +-0,50ms-Fenster um Top-1) verfeinern!
        if (!isLateMiss && (IsMeisterTacho || ManualRefinement) && IsRefinementActive)
        {
            var (minMs, maxMs, _) = GetRefinementInfo();
            double? nextFine = FindNextRefinementOffset(ClickOffsetMs, -1, minMs, maxMs);
            if (nextFine.HasValue)
            {
                double delta = Math.Round(nextFine.Value - ClickOffsetMs, 2);
                ClickOffsetMs = nextFine.Value;
                ConsecutiveNonBlue = 0;
                ConsecutiveRed = 0;
                return (ClickOffsetMs, delta, false, null, 0, false, false);
            }
        }

        // 0c. Finaler Meister-Tacho (Verfeinerung abgeschlossen) bei GRUEN / WEISS:
        if (!isLateMiss && IsMeisterTacho && IsRefinementComplete)
        {
            if (fb == TachoFeedback.TooEarlyGreen)
            {
                if (stat.TopRank == 0 && top1 != null)
                {
                    double delta = Math.Round(top1.OffsetMs - ClickOffsetMs, 2);
                    ClickOffsetMs = top1.OffsetMs;
                    ConsecutiveNonBlue = 0;
                    ConsecutiveRed = 0;
                    return (ClickOffsetMs, delta, true, top1, 0, false, false);
                }

                // Bei 3x Gruen in Folge auf gereiftem Top-Preset: sanften Session-Drift -0,35 ms zulassen
                if (stat.TopRank > 0 && ConsecutiveNonBlue >= 3)
                {
                    SessionDriftMs = Math.Clamp(Math.Round(SessionDriftMs - 0.35, 2), -4.0, 24.0);
                    ConsecutiveNonBlue = 0;
                }
                return (ClickOffsetMs, 0.0, false, null, 0, false, false);
            }

            if (fb == TachoFeedback.WayTooEarlyWhite)
            {
                var nextTop = stat.TopRank switch { 1 => top2 ?? top1, 2 => top3 ?? top1, _ => top1 };
                if (nextTop != null)
                {
                    double delta = Math.Round(nextTop.OffsetMs - ClickOffsetMs, 2);
                    ClickOffsetMs = nextTop.OffsetMs;
                    ConsecutiveNonBlue = 0;
                    ConsecutiveRed = 0;
                    return (ClickOffsetMs, delta, true, nextTop, 0, false, false);
                }
            }
        }

        var bestProven = top1 ?? GetBestProvenOffset();

        // 1. Bei WEISS (Vierbeiner oder Vogel VOR Gruen):
        if (fb == TachoFeedback.WayTooEarlyWhite && !isBirdPostBlueLate)
        {
            var target = bestProven ?? GetBestStartupPreset();
            if (target != null)
            {
                double delta = Math.Round(target.OffsetMs - ClickOffsetMs, 2);
                ClickOffsetMs = target.OffsetMs;
                ConsecutiveNonBlue = 0;
                ConsecutiveRed = 0;
                return (ClickOffsetMs, delta, true, target, 0, false, false);
            }

            if (isBird)
            {
                double stepDown = -3.0;
                double rawTarget = Math.Clamp(Math.Round(ClickOffsetMs + stepDown, 2), 0.0, 120.0);
                double applied = Math.Round(rawTarget - ClickOffsetMs, 2);
                ClickOffsetMs = rawTarget;
                ConsecutiveNonBlue = 0;
                ConsecutiveRed = 0;
                return (ClickOffsetMs, applied, false, null, 0, false, false);
            }

            double def = Math.Clamp(DefaultClickOffsetMs > 0 ? DefaultClickOffsetMs : 41.0, 0.0, 120.0);
            double defDelta = Math.Round(def - ClickOffsetMs, 2);
            ClickOffsetMs = def;
            ConsecutiveNonBlue = 0;
            ConsecutiveRed = 0;
            return (ClickOffsetMs, defDelta, false, null, 0, false, false);
        }

        // 2. Sofort-Schutz gegen Rot-Serien & Bisection-Rueckzug auf sicheres Gruen:
        if (isLateMiss)
        {
            // Bisection-Schutz: Wenn wir bei der Blau-Suche 1x Rot treffen und knapp darueber ein sicheres Gruen kennen,
            // sofort sicher auf dieses Gruen zurueckziehen (verhindert 2x Rot, naechste Runde halbiert dann weiter)!
            if (bestProven == null && TryGetSafeGreenEscape(isBird, ClickOffsetMs, out double safeGreenMs))
            {
                double delta = Math.Round(safeGreenMs - ClickOffsetMs, 2);
                ClickOffsetMs = safeGreenMs;
                ConsecutiveNonBlue = 0;
                return (ClickOffsetMs, delta, false, null, 0, false, true);
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
                return (ClickOffsetMs, delta, true, bestProven, 0, false, false);
            }
        }

        // 3. Regulaere Anpassung bei Rot (bzw. Vogel Nach-Blau-Weiss):
        if (isLateMiss)
        {
            double redStep;
            if (!IsFixed)
                redStep = +3.0;
            else if (isBirdPostBlueLate && bestProven == null)
                redStep = +2.0;
            else if (bestProven == null && EndSpeedDegPerSec >= 440.0)
                redStep = +2.5;
            else
                redStep = +1.5; // 1. Rot: kraeftig genug (+1,5 ms), um aus der Rot-Kante sicher herauszukommen

            double rawTarget = Math.Clamp(Math.Round(ClickOffsetMs + redStep, 2), 0.0, 120.0);
            double applied = Math.Round(rawTarget - ClickOffsetMs, 2);
            ClickOffsetMs = rawTarget;
            return (ClickOffsetMs, applied, false, null, 0, false, false);
        }

        // 4. Regulaere Anpassung bei GRUEN:
        //    Niemals lange im gruenen Bereich verharren! Erst evtl. positiven SessionDrift abbauen,
        //    dann zuegig (beschleunigt) in Richtung Blau spaeter klicken!
        if (fb == TachoFeedback.TooEarlyGreen)
        {
            // Falls noch ein positiver Schutz-Drift aus einem frueheren Notbremse-Sprung aktiv ist:
            // bei Gruen sofort abbauen, damit T2/T3 nicht kuenstlich im gruenen Bereich haengen bleiben!
            if (SessionDriftMs > 0.05)
            {
                double oldEff = EffectiveClickOffsetMs;
                SessionDriftMs = SessionDriftMs <= 4.0 ? 0.0 : Math.Round(SessionDriftMs - 4.0, 2);
                double driftDelta = Math.Round(EffectiveClickOffsetMs - oldEff, 2);
                return (EffectiveClickOffsetMs, driftDelta, false, null, 0, false, false);
            }

            // Auf einem stark bewaehrten Best-Preset (>=8 Klicks, >=50% Blau, <=10% Rot) nicht nach nur 1x Gruen wegdriften!
            if (bestProven != null &&
                Math.Abs(ClickOffsetMs - bestProven.OffsetMs) < 0.02 &&
                bestProven.Total >= 8 &&
                bestProven.BlueRate >= 0.50 &&
                bestProven.RedRate <= 0.10 &&
                ConsecutiveNonBlue < 2)
            {
                return (ClickOffsetMs, 0.0, false, null, 0, false, false);
            }

            // BISECTION: Wenn zwischen aktuellem Gruen und bekanntem Rot direkt darunter (<= 2,5 ms)
            // noch Platz ist (>= 0,10 ms) und noch kein starkes Blau existiert -> exakt halbieren!
            if (TryGetBisectionTarget(isBird, out double bisectionMid, out _, out _))
            {
                double delta = Math.Round(bisectionMid - ClickOffsetMs, 2);
                ClickOffsetMs = bisectionMid;
                ConsecutiveNonBlue = 0;
                return (ClickOffsetMs, delta, false, null, 0, false, true);
            }

            // Naechstgelegenes bekanntes Rot strikt unterhalb des aktuellen Offsets finden
            double? nearestRedBelow = null;
            foreach (var h in OffsetHistory)
            {
                bool hasLate = isBird ? (h.RedHits + h.WhiteHits) >= 1 : h.RedHits >= 1;
                if (hasLate && h.OffsetMs < ClickOffsetMs - 0.02)
                {
                    if (!nearestRedBelow.HasValue || h.OffsetMs > nearestRedBelow.Value)
                        nearestRedBelow = h.OffsetMs;
                }
            }

            double distToRedBelow = nearestRedBelow.HasValue ? (ClickOffsetMs - nearestRedBelow.Value) : double.MaxValue;

            // Beschleunigte Annaeherung aus dem gruenen Bereich (schnell spaeter klicken bis nah an Blau/Rot!),
            // automatisches Abbremsen auf Feinschritte (-0,25 ms) in der Naehe von Blau oder Rot!
            double greenStep;
            if (distToRedBelow <= 1.50 || (bestProven != null && Math.Abs(ClickOffsetMs - bestProven.OffsetMs) <= 1.25))
            {
                greenStep = -0.25;
            }
            else
            {
                greenStep = ConsecutiveNonBlue switch
                {
                    <= 1 => -0.50,
                    2 => -1.00,
                    3 => -2.00,
                    _ => -3.00
                };
            }

            double rawTarget = Math.Clamp(Math.Round(ClickOffsetMs + greenStep, 2), 0.0, 120.0);

            // Bei schneller Beschleunigung nicht blind ueber ein bekanntes Rot-Preset hinwegspringen:
            if (nearestRedBelow.HasValue && rawTarget <= nearestRedBelow.Value + 0.02)
            {
                double gap = ClickOffsetMs - nearestRedBelow.Value;
                if (gap >= 0.10)
                    rawTarget = SnapToFineMs((ClickOffsetMs + nearestRedBelow.Value) / 2.0);
            }

            double appliedGreen = Math.Round(rawTarget - ClickOffsetMs, 2);
            ClickOffsetMs = rawTarget;
            return (ClickOffsetMs, appliedGreen, false, null, 0, false, false);
        }

        return (ClickOffsetMs, 0.0, false, null, 0, false, false);
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

        double accelTol = Math.Max(55.0, Math.Abs(AccelDegPerSec2) * 0.16);
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
    public string DinoType { get; set; } = "Vierbeiner"; // "Vierbeiner" oder "Vogel"
    public int ProfileSize { get; set; } = 2;
    public int TachoCount
    {
        get => ProfileSize == 3 ? 3 : 2;
        set => ProfileSize = value == 3 ? 3 : 2;
    }
    public DinoVisualFingerprint? Visual { get; set; }
    public List<TachoSetupSlot> Tachos { get; set; } = new();
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBird => string.Equals(DinoType, "Vogel", StringComparison.OrdinalIgnoreCase);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasAnyFixedTacho => Tachos.Any(t => t.IsFixed);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool AllTachosFixed => Tachos.Count > 0 && Tachos.All(t => t.IsFixed);

    public bool IsTachoFixed(int tachoIndex) =>
        tachoIndex >= 0 && tachoIndex < Tachos.Count && Tachos[tachoIndex].IsFixed;

    public bool HasVisual => Visual != null && Visual.IsCurrentVersion;

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
    public int LastDeploymentId { get; set; } = 0;
    public List<EnemySetup> Setups { get; set; } = new();

    public int AllocateNextDeploymentId()
    {
        LastDeploymentId++;
        Save();
        return LastDeploymentId;
    }

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
            int migratedSlots = 0;
            foreach (var s in store.Setups)
            {
                s.EnsureSlots();
                foreach (var sl in s.Tachos)
                {
                    if (sl.MigrateToCurrentTimingVersion())
                        migratedSlots++;
                    sl.EnsureDeploymentBackfill();
                    sl.RefreshTopPresets();
                }
            }
            if (migratedSlots > 0)
                Console.WriteLine($"[Timing v{TachoSetupSlot.CurrentTimingVersion}] {migratedSlots} Tacho-Slots sauber in TimingVersion={TachoSetupSlot.CurrentTimingVersion} (DXGI/VBlank + Sub-Grad) migriert (Alt-Quoten in LegacyOffsetHistory archiviert).");
            if (store.NextId <= store.Setups.Count)
                store.NextId = (store.Setups.Count > 0 ? store.Setups.Max(s => s.Id) : 0) + 1;

            store.SyncDeletedScreenshots();
            store.Save();

            return store;
        }
        catch
        {
            return new EnemySetupStore();
        }
    }

    private bool SyncDeletedScreenshots()
    {
        string checkDir = AppContext.BaseDirectory;
        string projectRootDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        bool hasProjectRoot = Directory.Exists(projectRootDir) && File.Exists(Path.Combine(projectRootDir, "TachoClicker.csproj"));
        if (hasProjectRoot)
            checkDir = projectRootDir;

        bool changed = false;
        foreach (var s in Setups)
        {
            string pattern = $"dino_{s.Id}_*.png";
            string[] files = Directory.GetFiles(checkDir, pattern);

            if (s.Visual != null && s.Visual.HasVisual)
            {
                if (files.Length == 0)
                {
                    s.Visual = null;
                    changed = true;
                    Console.WriteLine($"[Visual] Screenshot fuer #{s.Id} ({s.Name}) geloescht -> Visual-Signatur zurueckgesetzt, wird beim naechsten Kampf neu gelernt.");

                    if (hasProjectRoot)
                    {
                        foreach (string stale in Directory.GetFiles(AppContext.BaseDirectory, pattern))
                        {
                            try { File.Delete(stale); } catch { }
                        }
                    }
                }
            }
            else if (files.Length > 0)
            {
                var seeded = DinoVisualMatcher.ExtractFromPreviewPng(files[0]);
                if (seeded != null)
                {
                    s.Visual = seeded;
                    changed = true;
                    Console.WriteLine($"[Visual] Signatur fuer #{s.Id} ({s.Name}) automatisch aus {Path.GetFileName(files[0])} geladen (Fg={seeded.ForegroundPixels}px, AR={seeded.AspectRatio:F2}).");
                }
            }
        }
        return changed;
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
        bool hasCurVis = currentVisual != null && currentVisual.IsCurrentVersion;

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
            bool isLastSlot = tachoIndex >= currentRoundSetup.Tachos.Count - 1;

            if (dCurrent <= 1.0)
            {
                if (!slot.HasPosition && curX > 0 && curY > 0) { slot.X = curX; slot.Y = curY; }
                slot.UpdateSignatureEma(currentFp);

                // Neues Dino-Bild erst speichern, sobald der letzte Tacho (T2/T3) den Spieler der Runde bestaetigt hat!
                bool learnedOnConfirm = false;
                if (isLastSlot && hasCurVis)
                {
                    if (!currentRoundSetup.HasVisual)
                    {
                        currentRoundSetup.Visual = new DinoVisualFingerprint();
                        DinoVisualMatcher.BlendInto(currentRoundSetup.Visual, currentVisual!);
                        learnedOnConfirm = true;
                        curVisDist = 0.0;
                    }
                    else
                    {
                        DinoVisualMatcher.BlendInto(currentRoundSetup.Visual!, currentVisual!);
                    }
                }

                currentRoundSetup.LastSeenUtc = DateTime.UtcNow;
                Save();
                return (currentRoundSetup, false, dCurrent, curVisDist, learnedOnConfirm);
            }

            EnemySetup? bestJointVis = null;
            double bestJointVisScore = double.MaxValue;
            double bestJointVisDist = -1.0;

            EnemySetup? bestJointLegacy = null;
            double bestJointLegacyScore = double.MaxValue;

            foreach (var candidate in Setups)
            {
                if (candidate.ProfileSize != profileSize) continue;
                candidate.EnsureSlots();

                // Visueller Ausschluss: niemals auf einen sichtbar anderen Dino umschalten!
                double candVisDist = -1.0;
                bool isVisConfirmed = false;
                if (hasCurVis && candidate.HasVisual)
                {
                    candVisDist = DinoVisualMatcher.Distance(candidate.Visual!, currentVisual!);
                    if (candVisDist > DinoVisualMatcher.VisualMatchThreshold)
                        continue;
                    isVisConfirmed = true;
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
                    if (isVisConfirmed)
                    {
                        double combined = avg * 0.55 + (candVisDist / DinoVisualMatcher.VisualMatchThreshold) * 0.45;
                        if (combined < bestJointVisScore)
                        {
                            bestJointVisScore = combined;
                            bestJointVisDist = candVisDist;
                            bestJointVis = candidate;
                        }
                    }
                    else if (avg < bestJointLegacyScore)
                    {
                        bestJointLegacyScore = avg;
                        bestJointLegacy = candidate;
                    }
                }
            }

            EnemySetup? bestJoint = bestJointVis ?? bestJointLegacy;
            double bestJointDist = bestJointVis != null ? bestJointVisScore : bestJointLegacyScore;
            if (bestJointVis == null) bestJointVisDist = -1.0;

            if (bestJoint != null)
            {
                var matchedSlot = bestJoint.Tachos[tachoIndex];
                if (!matchedSlot.HasPosition && curX > 0 && curY > 0) { matchedSlot.X = curX; matchedSlot.Y = curY; }
                matchedSlot.UpdateSignatureEma(currentFp);
                bool learned = false;
                if (isLastSlot && hasCurVis)
                {
                    if (!bestJoint.HasVisual)
                    {
                        bestJoint.Visual = new DinoVisualFingerprint();
                        DinoVisualMatcher.BlendInto(bestJoint.Visual, currentVisual!);
                        learned = true;
                        bestJointVisDist = 0.0;
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

            // Schutz gegen einzelne Frame-Haker bzw. T2/T3-Messausreisser (unabhaengig von IsFixed!):
            // Wenn der Dino visuell eindeutig derselbe ist (VisDist <= 0.18) ODER Geometrie + Spinzeit passen,
            // darf niemals ein doppeltes Spieler-Setup abgespalten werden!
            bool strongVisualMatch = curVisDist >= 0 && curVisDist <= 0.18;
            bool geoAndSpinMatch = slot.MatchesGeometryAndSpinTime(currentFp) &&
                                   (curVisDist < 0 || curVisDist <= DinoVisualMatcher.VisualMatchThreshold);
            if (strongVisualMatch || geoAndSpinMatch)
            {
                bool learnedFallback = false;
                if (isLastSlot && hasCurVis)
                {
                    if (!currentRoundSetup.HasVisual)
                    {
                        currentRoundSetup.Visual = new DinoVisualFingerprint();
                        DinoVisualMatcher.BlendInto(currentRoundSetup.Visual, currentVisual!);
                        learnedFallback = true;
                        curVisDist = 0.0;
                    }
                    else
                    {
                        DinoVisualMatcher.BlendInto(currentRoundSetup.Visual!, currentVisual!);
                    }
                }
                if (slot.IsFixed && curVisDist >= 0 && curVisDist <= 0.14 && dCurrent > 1.0 && currentFp.EndSpeedDegPerSec > 50.0)
                {
                    // Eindeutiger visueller Match nach Tacho-Zentrierung -> Tacho-Signatur sauber nachziehen
                    slot.GreenEndDeg = Math.Round(currentFp.GreenEndDeg, 1);
                    slot.GreenWidthDeg = Math.Round(currentFp.GreenWidthDeg, 1);
                    slot.EndSpeedDegPerSec = Math.Round(currentFp.EndSpeedDegPerSec, 1);
                    slot.AccelDegPerSec2 = Math.Round(currentFp.AccelDegPerSec2, 1);
                    if (currentFp.SpinTimeTo55Ms > 100.0)
                        slot.SpinTimeTo55Ms = Math.Round(currentFp.SpinTimeTo55Ms, 1);
                }
                else if (!slot.IsFixed && geoAndSpinMatch)
                {
                    slot.UpdateSignatureEma(currentFp);
                }
                currentRoundSetup.LastSeenUtc = DateTime.UtcNow;
                Save();
                return (currentRoundSetup, false, 0.99, curVisDist, learnedFallback);
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
            // Bei Tacho 1: visuell bekannte Spieler duerfen auch profiluebergreifend (2er <-> 3er)
            // erkannt werden, damit TachoClicker automatisch auf die gemerkte Tacho-Anzahl umschalten kann!
            bool sameProfile = candidate.ProfileSize == profileSize;
            if (!sameProfile && !(hasCurVis && candidate.HasVisual))
                continue;
            candidate.EnsureSlots();

            var candSlot = candidate.Tachos[tachoIndex];
            double tachoDist = candSlot.DistanceTo(currentFp);

            if (hasCurVis && candidate.HasVisual)
            {
                double visDist = DinoVisualMatcher.Distance(candidate.Visual!, currentVisual!);
                if (visDist > DinoVisualMatcher.VisualMatchThreshold)
                    continue; // Sichtbar ein anderer Dino -> harter Ausschluss!

                // Bei sehr starkem visuellen Match (z.B. VisDist <= 0.18, oder VisDist <= 0.22 mit passender Grundgeometrie)
                // darf eine leicht verschobene Tacho-Signatur niemals ein doppeltes Setup anlegen!
                if (tachoDist > 1.0)
                {
                    if (visDist <= 0.18)
                        tachoDist = 0.95;
                    else if (visDist <= 0.22 && candSlot.MatchesGeometryAndSpinTime(currentFp))
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
            else if (sameProfile)
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
            if (matchedSlot.IsFixed && bestVisDist >= 0 && bestVisDist <= 0.14 && matchedSlot.DistanceTo(currentFp) > 1.0 && currentFp.EndSpeedDegPerSec > 50.0)
            {
                // Eindeutiger visueller Match nach Tacho-Zentrierung -> Tacho-Signatur automatisch auf zentrierte Werte aktualisieren
                matchedSlot.GreenEndDeg = Math.Round(currentFp.GreenEndDeg, 1);
                matchedSlot.GreenWidthDeg = Math.Round(currentFp.GreenWidthDeg, 1);
                matchedSlot.EndSpeedDegPerSec = Math.Round(currentFp.EndSpeedDegPerSec, 1);
                matchedSlot.AccelDegPerSec2 = Math.Round(currentFp.AccelDegPerSec2, 1);
                if (currentFp.SpinTimeTo55Ms > 100.0)
                    matchedSlot.SpinTimeTo55Ms = Math.Round(currentFp.SpinTimeTo55Ms, 1);
            }
            matchedSlot.UpdateSignatureEma(currentFp);

            // Bei T1 niemals ein brandneues Visual auf ein NO-VIS-Setup speichern (erst wenn T2 die Runde bestaetigt hat!),
            // damit ein vorlaeufiges T1-Match niemals ein fremdes Setup mit einem falschen Dino-Foto vergiftet!
            if (hasCurVis && bestMatch.HasVisual)
            {
                DinoVisualMatcher.BlendInto(bestMatch.Visual!, currentVisual!);
            }

            bestMatch.LastSeenUtc = DateTime.UtcNow;
            Save();
            return (bestMatch, false, bestRawDist, bestVisDist, false);
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
        if (currentVisual != null && currentVisual.IsCurrentVersion)
        {
            visualCopy = new DinoVisualFingerprint();
            DinoVisualMatcher.BlendInto(visualCopy, currentVisual);
        }

        var setup = new EnemySetup
        {
            Id = id,
            Name = $"Spieler-Setup #{id}",
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
