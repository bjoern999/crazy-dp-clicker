using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace TachoClicker;

internal static class Program
{
    // F6 / F9 / F10 voruebergehend deaktiviert, damit Tacho- und Button-Positionen nicht versehentlich verstellt werden koennen.
    private static readonly bool CalibrationHotkeysEnabled = false;

    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        NativeMethods.SetProcessDPIAware();

        if (args.Length > 0 && args[0].Equals("--benchmark-capture", StringComparison.OrdinalIgnoreCase))
        {
            RunCaptureBenchmark();
            return;
        }

        Console.Title = "TachoClicker (F1-F4=Taktik | F5=Dino | F6=Gruen | F7=Debug | F8=Start | F9=Tacho | F10=Blau)";

        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch { }
        try { Thread.CurrentThread.Priority = ThreadPriority.Highest; } catch { }

        NativeMethods.timeBeginPeriod(1);

        try
        {
            RunLoop();
        }
        finally
        {
            NativeMethods.timeEndPeriod(1);
        }
    }

    private static void RunCaptureBenchmark()
    {
        NativeMethods.timeBeginPeriod(1);
        try
        {
            const int size = 180;
            const int left = 1595;
            const int top = 1040;
            byte[] buf = new byte[size * size * 4];
            double ticksToMs = 1000.0 / Stopwatch.Frequency;

            using var cap = new DxgiScreenCapture(left + size / 2, top + size / 2);
            Console.WriteLine($"DXGI Active : {cap.IsDxgiActive} | Output={cap.OutputWidth}x{cap.OutputHeight} | Err='{cap.LastInitError}'");
            if (NativeMethods.TryGetDwmTiming(out long vb0, out long per0, out long pend0, out long drop0))
            {
                double perMs = per0 * ticksToMs;
                Console.WriteLine($"DWM Timing  : Refresh={1000.0 / perMs:F3} Hz ({perMs:F4} ms) | Pending={pend0} | Dropped={drop0}");
            }

            // 1. DXGI Readback Benchmark (forced redraws or active screen)
            var dxgiDurs = new List<double>();
            var presentDeltas = new List<double>();
            var presentToNow = new List<double>();
            var presentToDwm = new List<double>();
            long prevPresent = 0;
            int nonZeroSamples = 0;

            var sw = Stopwatch.StartNew();
            string seenSrc = "-";
            while (sw.ElapsedMilliseconds < 1500 && dxgiDurs.Count < 45)
            {
                var r = cap.CaptureRegion(left, top, size, size, buf, timeoutMs: 25, allowGdiIfNoNewFrame: false);
                if (r.HasPixels && r.IsNewFrame)
                {
                    seenSrc = r.Source;
                    long nowQpc = Stopwatch.GetTimestamp();
                    dxgiDurs.Add(r.CaptureDurationMs);
                    if (prevPresent > 0)
                        presentDeltas.Add((r.FrameQpcTimestamp - prevPresent) * ticksToMs);
                    prevPresent = r.FrameQpcTimestamp;
                    presentToNow.Add((nowQpc - r.FrameQpcTimestamp) * ticksToMs);

                    if (NativeMethods.TryGetDwmTiming(out long qpcVb, out long qpcPer, out _, out _))
                    {
                        long pastVb = qpcVb;
                        while (pastVb > nowQpc) pastVb -= qpcPer;
                        while (nowQpc - pastVb >= qpcPer) pastVb += qpcPer;
                        presentToDwm.Add((pastVb - r.FrameQpcTimestamp) * ticksToMs);
                    }

                    int nz = 0;
                    for (int i = 0; i < buf.Length; i += 64)
                        if (buf[i] != 0) nz++;
                    if (nz > 0) nonZeroSamples++;
                }
            }

            if (dxgiDurs.Count > 0)
            {
                Console.WriteLine($"{seenSrc} Frames : n={dxgiDurs.Count}, nonZero={nonZeroSamples}, Dur avg={dxgiDurs.Average():F3} ms (min={dxgiDurs.Min():F3}, max={dxgiDurs.Max():F3})");
                if (presentDeltas.Count > 0)
                    Console.WriteLine($"{seenSrc} dPres  : avg={presentDeltas.Average():F3} ms (min={presentDeltas.Min():F3}, max={presentDeltas.Max():F3})");
                Console.WriteLine($"Now-FrameTs : avg={presentToNow.Average():F3} ms (min={presentToNow.Min():F3}, max={presentToNow.Max():F3})");
                if (presentToDwm.Count > 0)
                    Console.WriteLine($"PastVb-Ts   : avg={presentToDwm.Average():F3} ms (min={presentToDwm.Min():F3}, max={presentToDwm.Max():F3})");
            }
            else
            {
                Console.WriteLine("DXGI Frames : 0 neue Frames in 1500 ms (Desktop war statisch)");
            }

            // 2. Compare GDI CopyFromScreen latency & epoch vs DWM qpcVBlank
            using var gdiBmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using var gdiG = Graphics.FromImage(gdiBmp);
            var gdiDurs = new List<double>();
            var gdiNowMinusVb = new List<double>();
            for (int i = 0; i < 12; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                gdiG.CopyFromScreen(left, top, 0, 0, new Size(size, size), CopyPixelOperation.SourceCopy);
                long t1 = Stopwatch.GetTimestamp();
                gdiDurs.Add((t1 - t0) * ticksToMs);
                if (NativeMethods.TryGetDwmTiming(out long vb, out _, out _, out _))
                    gdiNowMinusVb.Add((t1 - vb) * ticksToMs);
            }
            Console.WriteLine($"GDI 180x180 : n={gdiDurs.Count}, Dur avg={gdiDurs.Average():F2} ms (min={gdiDurs.Min():F2}, max={gdiDurs.Max():F2})");
            if (gdiNowMinusVb.Count > 0)
                Console.WriteLine($"GDI Now-Vb  : avg={gdiNowMinusVb.Average():F2} ms (min={gdiNowMinusVb.Min():F2}, max={gdiNowMinusVb.Max():F2})");
        }
        finally
        {
            NativeMethods.timeEndPeriod(1);
        }
    }

    private static void RunLoop()
    {
        AppConfig cfg = AppConfig.Load();
        EnemySetupStore enemyStore = EnemySetupStore.Load();

        var initTachos = cfg.GetActiveTachos();
        int hintX = initTachos.Count > 0 && initTachos[0].IsSet ? initTachos[0].X : 0;
        int hintY = initTachos.Count > 0 && initTachos[0].IsSet ? initTachos[0].Y : 0;
        using var screenCapture = new DxgiScreenCapture(hintX, hintY);
        var detectors = new[]
        {
            new TachoDetector(screenCapture),
            new TachoDetector(screenCapture),
            new TachoDetector(screenCapture)
        };

        OverlayForm? overlay = null;
        using var overlayReady = new ManualResetEventSlim(false);
        var overlayThread = new Thread(() =>
        {
            overlay = new OverlayForm();
            overlay.Show();
            overlayReady.Set();
            Application.Run(overlay);
        });
        overlayThread.SetApartmentState(ApartmentState.STA);
        overlayThread.IsBackground = true;
        overlayThread.Start();
        overlayReady.Wait();

        bool running = false;
        bool debugMode = false;
        int nextTachoIndex = 0;

        // Strenge Sequenz: immer nur 1 aktiver Tacho (T1 -> T2 -> (T3) -> T1)
        int expectedTachoIndex = 0;
        DateTime lastClickUtc = DateTime.MinValue;

        // Aktuell erkannter Spieler & Fingerabdruecke der laufenden Runde
        EnemySetup? activeEnemy = null;
        EnemySetup? lastConfirmedEnemy = null;
        bool activeEnemyIsNew = false;
        var roundFingerprints = new Dictionary<int, TachoFingerprint>();
        DinoVisualFingerprint? currentRoundVisual = null;
        var roundDinoSamples = new List<DinoVisualFingerprint>(3);
        int dinoCaptureStage = 0; // 0 -> rem<=210°, 1 -> rem<=155°, 2 -> rem<=100° (3-Frame-Mittelung ueber Gummimatten-Dehnung!)
        double lastRoundVisDist = -1.0;
        Bitmap? lastDinoPreview = null;
        string lastDinoVisualInfo = "";
        var sessionPlayedEnemyIds = new HashSet<int>();
        int currentDeploymentId = 0;
        var sessionDeployments = new List<(int DeploymentId, int SetupId, string Name)>();
        bool probeThirdTacho = false;
        DateTime probeThirdTachoUntilUtc = DateTime.MinValue;

        // Letztes Feedback fuer die Anzeige im Live-Inspektor
        int lastClickedTachoNumber = 0;
        TachoFeedback lastFeedback = TachoFeedback.None;
        int lastPatchR = 0, lastPatchG = 0, lastPatchB = 0, lastPatchW = 0;
        double lastDeltaMs = 0;
        double lastResultNewOffsetMs = 0;

        BattleTactic activeTactic = BattleTactic.LastOneAttack; // Standard beim Start (F2: letzter Tacho Angriff, davor Abwehr)

        bool f1Prev = false;
        bool f2Prev = false;
        bool f3Prev = false;
        bool f4Prev = false;
        bool f5Prev = false;
        bool f6Prev = false;
        bool f7Prev = false;
        bool f8Prev = false;
        bool f9Prev = false;
        bool f10Prev = false;
        bool plusPrev = false;
        bool minusPrev = false;

        PrintHeader(cfg, enemyStore, screenCapture, running, debugMode, activeTactic);

        DateTime lastDebugPrint = DateTime.MinValue;
        DateTime lastOverlayUpdate = DateTime.MinValue;

        try
        {
            while (true)
            {
                BattleTactic? requestedTactic = null;
                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_F1, ref f1Prev)) requestedTactic = BattleTactic.AllDefense;
                else if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_F2, ref f2Prev)) requestedTactic = BattleTactic.LastOneAttack;
                else if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_F3, ref f3Prev)) requestedTactic = BattleTactic.LastTwoAttack;
                else if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_F4, ref f4Prev)) requestedTactic = BattleTactic.AllAttack;

                if (requestedTactic.HasValue)
                {
                    activeTactic = requestedTactic.Value;
                    var curTachosForTactic = cfg.GetActiveTachos();
                    int totalT = curTachosForTactic.Count;
                    Console.WriteLine(
                        $"[Taktik] {BattleTacticHelper.GetLabel(activeTactic)} -> {BattleTacticHelper.FormatPlan(activeTactic, totalT)}");

                    if (running && cfg.IsCalibrated && expectedTachoIndex < totalT &&
                        detectors[expectedTachoIndex].State == DetectorState.Armed)
                    {
                        bool atkNow = BattleTacticHelper.IsAttack(activeTactic, expectedTachoIndex, totalT);
                        NativeMethods.SetCursorPos(
                            atkNow ? cfg.GreenButtonX : cfg.BlueButtonX,
                            atkNow ? cfg.GreenButtonY : cfg.BlueButtonY);
                    }

                    if (overlay != null)
                    {
                        lastOverlayUpdate = DateTime.UtcNow;
                        overlay.UpdateSnapshot(BuildOverlaySnapshot(
                            cfg, enemyStore, activeEnemy, activeEnemyIsNew,
                            detectors, expectedTachoIndex, running, activeTactic,
                            lastClickedTachoNumber, lastFeedback,
                            lastPatchR, lastPatchG, lastPatchB, lastPatchW,
                            lastDeltaMs, lastResultNewOffsetMs,
                            lastDinoPreview, lastDinoVisualInfo));
                    }
                }
                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_F9, ref f9Prev))
                {
                    if (!CalibrationHotkeysEnabled)
                    {
                        Console.WriteLine("[Sperre] F9 (Tacho-Position) ist voruebergehend deaktiviert.");
                    }
                    else if (NativeMethods.GetCursorPos(out var pt))
                    {
                        if (pt.X <= 0 || pt.Y <= 0)
                        {
                            Console.WriteLine($"[Kalibrierung] F9 ignoriert: Mauszeiger liegt bei ({pt.X}, {pt.Y}) ausserhalb des Hauptmonitors -> bestehende Tacho-Position bleibt geschuetzt.");
                        }
                        else
                        {
                            var tachos = cfg.GetActiveTachos();
                            if (nextTachoIndex >= tachos.Count)
                                nextTachoIndex = 0;

                            var (cx, cy, dx, dy, centered) = TachoDetector.TryAutoCenterOnRing(pt.X, pt.Y, cfg.TachoBoxSize);
                            tachos[nextTachoIndex].X = cx;
                            tachos[nextTachoIndex].Y = cy;
                            cfg.Save();
                            detectors[nextTachoIndex].Reset();

                            int justSetIndex = nextTachoIndex;
                            expectedTachoIndex = justSetIndex;
                            nextTachoIndex = (nextTachoIndex + 1) % tachos.Count;

                            string centerNote = centered ? $" (auto-zentriert {dx:+0;-0}/{dy:+0;-0} px)" : "";
                            Console.WriteLine(
                                $"[Kalibrierung {cfg.ActiveProfile}er-Modus] Tacho {justSetIndex + 1}/{tachos.Count} gesetzt auf: ({cx}, {cy}){centerNote} | Naechster mit F9: Tacho {nextTachoIndex + 1}/{tachos.Count}");
                        }
                    }
                }

                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_F10, ref f10Prev))
                {
                    if (!CalibrationHotkeysEnabled)
                    {
                        Console.WriteLine("[Sperre] F10 (Blauer Button) ist voruebergehend deaktiviert.");
                    }
                    else if (NativeMethods.GetCursorPos(out var pt))
                    {
                        cfg.BlueButtonX = pt.X;
                        cfg.BlueButtonY = pt.Y;
                        cfg.Save();
                        ResetDetectors(detectors);
                        expectedTachoIndex = 0;
                        Console.WriteLine($"[Kalibrierung] Blauer Schild-Button gesetzt auf: ({pt.X}, {pt.Y})");
                        TryAutoStart(cfg, ref running);
                    }
                }

                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_F5, ref f5Prev))
                {
                    if (NativeMethods.GetCursorPos(out var pt))
                    {
                        cfg.DinoBoxX = pt.X;
                        cfg.DinoBoxY = pt.Y;
                        cfg.Save();
                        var (visFp, visPreview) = DinoVisualMatcher.CaptureAndExtract(pt.X, pt.Y, cfg.DinoBoxWidth, cfg.DinoBoxHeight);
                        lastDinoPreview?.Dispose();
                        lastDinoPreview = visPreview;
                        lastDinoVisualInfo = visFp.HasVisual
                            ? $"Kalibriert: {visFp.ForegroundPixels}px AR={visFp.AspectRatio:F2}"
                            : "Kalibriert (kein Vordergrund)";
                        Console.WriteLine($"[Kalibrierung] Aktive Dino-Position gesetzt auf: ({pt.X}, {pt.Y}) | {lastDinoVisualInfo}");
                    }
                }

                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_F6, ref f6Prev))
                {
                    if (!CalibrationHotkeysEnabled)
                    {
                        Console.WriteLine("[Sperre] F6 (Gruener Button) ist voruebergehend deaktiviert.");
                    }
                    else if (NativeMethods.GetCursorPos(out var pt))
                    {
                        cfg.GreenButtonX = pt.X;
                        cfg.GreenButtonY = pt.Y;
                        cfg.Save();
                        ResetDetectors(detectors);
                        expectedTachoIndex = 0;
                        Console.WriteLine($"[Kalibrierung] Gruener Schwerter-Button gesetzt auf: ({pt.X}, {pt.Y})");
                        TryAutoStart(cfg, ref running);
                    }
                }

                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_F8, ref f8Prev))
                {
                    if (!cfg.IsCalibrated)
                    {
                        Console.WriteLine($"[Hinweis] Bitte zuerst alle Tachos im {cfg.ActiveProfile}er-Modus (F9) sowie blauen (F10) und gruenen (F6) Button festlegen!");
                    }
                    else
                    {
                        running = !running;
                        ResetDetectors(detectors);
                        expectedTachoIndex = 0;
                        roundFingerprints.Clear();
                        if (running)
                        {
                            activeEnemy = null; // Bei Neustart nach Pause immer sofort das Top-1 / Best-Preset laden
                            sessionPlayedEnemyIds.Clear();
                            sessionDeployments.Clear();
                            probeThirdTacho = false;
                        }
                        overlay?.EnsureVisibleOnTop();
                        Console.WriteLine(running
                            ? $"[Status] AKTIV ({cfg.ActiveProfile}er-Modus) - Starte sequenziell bei Tacho 1..."
                            : "[Status] PAUSIERT.");
                        if (!running)
                        {
                            PrintSessionPresetStats(enemyStore, sessionPlayedEnemyIds, sessionDeployments);
                        }
                    }
                }

                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_F7, ref f7Prev))
                {
                    debugMode = !debugMode;
                    overlay?.EnsureVisibleOnTop();
                    Console.WriteLine($"[Debug] Live-Werte: {(debugMode ? "AN" : "AUS")}");
                }

                if (NativeMethods.IsAnyKeyJustPressed(NativeMethods.VK_OEM_PLUS, NativeMethods.VK_ADD, ref plusPrev))
                {
                    double manualStep = (activeEnemy != null && activeEnemy.IsTachoFixed(expectedTachoIndex)) ? +0.5 : +2.0;
                    AdjustOffsetManually(expectedTachoIndex, manualStep, cfg, enemyStore, activeEnemy);
                }

                if (NativeMethods.IsAnyKeyJustPressed(NativeMethods.VK_OEM_MINUS, NativeMethods.VK_SUBTRACT, ref minusPrev))
                {
                    double manualStep = (activeEnemy != null && activeEnemy.IsTachoFixed(expectedTachoIndex)) ? -0.5 : -2.0;
                    AdjustOffsetManually(expectedTachoIndex, manualStep, cfg, enemyStore, activeEnemy);
                }

                var activeTachos = cfg.GetActiveTachos();
                if (expectedTachoIndex >= activeTachos.Count)
                    expectedTachoIndex = 0;

                bool isArmed = false;

                if (running && cfg.IsCalibrated)
                {
                    int cur = expectedTachoIndex;
                    (int curX, int curY) = GetEffectiveTachoPos(cur, cfg, activeEnemy);
                    double defaultOffsetMs = GetEffectiveOffsetMs(cur, cfg, activeEnemy);

                    bool triggered = detectors[cur].Update(
                        curX,
                        curY,
                        defaultOffsetMs,
                        cfg,
                        fp =>
                        {
                            if (cur == 0)
                                roundFingerprints.Clear();

                            roundFingerprints[cur] = fp;

                            var (matched, isNew, dist, visDist, learnedVisual) = enemyStore.MatchOrCreate(
                                cfg.ActiveProfile,
                                cur,
                                fp,
                                currentRoundVisual,
                                cur > 0 ? activeEnemy : null,
                                lastConfirmedEnemy,
                                roundFingerprints,
                                activeTachos,
                                cfg.GetClickOffsetMsForTacho(cur));

                            bool isNewFightAfterPause = cur == 0 && lastClickUtc != DateTime.MinValue &&
                                (DateTime.UtcNow - lastClickUtc).TotalSeconds >= 15.0;
                            bool changedEnemy = activeEnemy == null || activeEnemy.Id != matched.Id || isNew;
                            if (changedEnemy || isNewFightAfterPause)
                            {
                                currentDeploymentId = enemyStore.AllocateNextDeploymentId();
                                sessionDeployments.Add((currentDeploymentId, matched.Id, matched.Name));
                            }
                            activeEnemy = matched;
                            activeEnemyIsNew = isNew;
                            sessionPlayedEnemyIds.Add(matched.Id);

                            // Falls der erkannte Spieler eine andere gemerkte Tacho-Anzahl (2 vs. 3) hat:
                            // automatisch in den passenden Modus wechseln, sofern dieser bereits kalibriert ist!
                            if (cur == 0 && matched.TachoCount != cfg.ActiveProfile)
                            {
                                if (cfg.IsProfileCalibrated(matched.TachoCount))
                                {
                                    cfg.ActiveProfile = matched.TachoCount;
                                    cfg.Save();
                                    activeTachos = cfg.GetActiveTachos();
                                    Console.WriteLine($"[Auto-Modus] {matched.Name} hat {matched.TachoCount} Tachos -> wechsle automatisch in den {matched.TachoCount}er-Modus!");
                                }
                                else
                                {
                                    Console.WriteLine($"[Hinweis] {matched.Name} hat {matched.TachoCount} Tachos – bitte {matched.TachoCount}er-Modus (F{(matched.TachoCount == 3 ? 3 : 2)} + F9) kalibrieren!");
                                }
                            }
                            if (cur == activeTachos.Count - 1)
                                lastConfirmedEnemy = matched;

                            if ((learnedVisual || isNew) && lastDinoPreview != null)
                            {
                                DinoVisualMatcher.SaveReferenceThumbnail(matched.Id, matched.Name, lastDinoPreview);
                            }

                            if (cur == 0)
                            {
                                lastRoundVisDist = visDist;
                                lastDinoVisualInfo = visDist >= 0
                                    ? (learnedVisual ? $"Dino NEU gelernt (v2)!" : $"VisDist={visDist:F2} (OK)")
                                    : (currentRoundVisual?.IsCurrentVersion == true ? "Dino v2 gespeichert" : "Kein Dino-Bild");
                            }

                            var slot = matched.Tachos[cur];

                            // Sobald ein Spieler erkannt wird (oder nach einer Kampfpause neu startet):
                            // sofort fuer alle seine Tachos das bislang beste Preset laden!
                            // Und selbst im laufenden Betrieb: niemals ein nachweislich schlechtes Preset verwenden!
                            bool presetAutoSwitched = false;
                            if ((changedEnemy || isNewFightAfterPause) && !isNew)
                            {
                                bool anyPresetChanged = false;
                                for (int sIdx = 0; sIdx < matched.Tachos.Count; sIdx++)
                                {
                                    var (ch, oldOff, newOff, chosen) = matched.Tachos[sIdx].ApplyBestKnownPreset();
                                    if (ch)
                                    {
                                        anyPresetChanged = true;
                                        string rankTag = chosen != null && chosen.TopRank > 0 ? $" [TOP-{chosen.TopRank}]" : "";
                                        string statInfo = chosen != null
                                            ? $"{rankTag} (Blau: {chosen.BlueHits}/{chosen.Total} = {chosen.BlueRate * 100:F0}%, G={chosen.GreenHits}, R={chosen.RedHits})"
                                            : "(oberhalb Rot-Grenze gesetzt)";
                                        Console.WriteLine(
                                            $"[Best-Preset {matched.Name} T{sIdx + 1}] {oldOff:F2} ms -> {newOff:F2} ms {statInfo}");
                                    }
                                }
                                if (anyPresetChanged)
                                {
                                    presetAutoSwitched = true;
                                    enemyStore.Save();
                                }
                            }

                            double enemyOffset = slot.ClickOffsetMs;
                            double effectiveOffset = slot.EffectiveClickOffsetMs;

                            if (changedEnemy || cur == 0 || presetAutoSwitched)
                            {
                                string tag = isNew ? "NEU ANGELEGT" : (slot.IsFixed ? $"FEST (Dist={dist:F2})" : $"ERKANNT (Dist={dist:F2})");
                                string visStr = visDist >= 0 ? $", Vis={visDist:F2}{(learnedVisual ? " [NEU]" : "")}" : "";
                                string lockStr = slot.TemplateCurve.IsReady ? " [LOCK]" : $" [Kurve {slot.TemplateCurve.SampleCount}/2]";
                                string driftStr = Math.Abs(slot.SessionDriftMs) >= 0.05 ? $" (Drift {slot.SessionDriftMs:+0.00;-0.00}ms)" : "";
                                var curPresetStat = slot.FindPresetStats(enemyOffset);
                                string curStatStr = curPresetStat != null
                                    ? $" [Preset: {curPresetStat.BlueHits}B/{curPresetStat.GreenHits}G/{curPresetStat.RedHits}R]"
                                    : " [Preset: neu]";
                                var best = slot.GetBestProvenOffset();
                                string bestStr = best != null ? $", Best={best.OffsetMs:F2}ms ({best.BlueHits}/{best.Total} Blau)" : "";
                                Console.WriteLine(
                                    $"[Spieler-Setup] {matched.Name} ({matched.DinoType}) [Einsatz #{currentDeploymentId} | {tag}{visStr}{lockStr}] | T{cur + 1}: v={fp.EndSpeedDegPerSec:F1}°/s, a={fp.AccelDegPerSec2:F0}°/s², T55={fp.SpinTimeTo55Ms:F0}ms, G={fp.GreenWidthDeg:F0}° -> Offset {enemyOffset:F2} ms{driftStr}{curStatStr}{bestStr}");
                            }

                            return (effectiveOffset, slot.TemplateCurve);
                        });

                    if (detectors[cur].JustArmed)
                    {
                        bool isAttack = BattleTacticHelper.IsAttack(activeTactic, cur, activeTachos.Count);
                        int targetX = isAttack ? cfg.GreenButtonX : cfg.BlueButtonX;
                        int targetY = isAttack ? cfg.GreenButtonY : cfg.BlueButtonY;
                        NativeMethods.SetCursorPos(targetX, targetY);
                        if (cur == 0)
                        {
                            dinoCaptureStage = 0;
                            roundDinoSamples.Clear();
                            currentRoundVisual = null;
                            lastRoundVisDist = -1.0;
                        }
                    }

                    // 3-Frame-Erfassung ueber die Gummimatten-Dehn-Animation (bei rem <= 210°, 155°, 100°):
                    // Mittelt Dehnen/Stauchen, Staubwoelkchen und Funken sauber heraus (noch ~300 ms vor Klick!).
                    if (cur == 0 && dinoCaptureStage < 3 &&
                        detectors[0].State == DetectorState.Armed &&
                        detectors[0].LastRemainingDeg > 60.0)
                    {
                        double targetRem = dinoCaptureStage switch { 0 => 210.0, 1 => 155.0, _ => 100.0 };
                        if (detectors[0].LastRemainingDeg <= targetRem)
                        {
                            dinoCaptureStage++;
                            var (dx, dy) = cfg.GetEffectiveDinoBoxCenter();
                            if (dx > 0 && dy > 0)
                            {
                                var (visFp, visPreview) = DinoVisualMatcher.CaptureAndExtract(dx, dy, cfg.DinoBoxWidth, cfg.DinoBoxHeight);
                                if (visFp.IsCurrentVersion)
                                    roundDinoSamples.Add(visFp);

                                currentRoundVisual = DinoVisualMatcher.AverageSamples(roundDinoSamples);
                                lastDinoPreview?.Dispose();
                                lastDinoPreview = visPreview;

                                // Letztes erfasstes Bild immer fuer Diagnose speichern (1 Datei, ueberschrieben)
                                if (dinoCaptureStage == 3 || roundDinoSamples.Count == 1)
                                    DinoVisualMatcher.SaveLastCapturedPreview(visPreview);

                                lastDinoVisualInfo = currentRoundVisual.IsCurrentVersion
                                    ? $"3F({roundDinoSamples.Count}/3) Fg={currentRoundVisual.ForegroundPixels}px AR={currentRoundVisual.AspectRatio:F2}"
                                    : "Kein Dino erkannt (F5?)";
                            }
                        }
                    }

                    if (detectors[cur].State == DetectorState.Armed)
                        isArmed = true;

                    if (detectors[cur].JustAbortedWithoutClick)
                    {
                        LogTelemetryAbort(cur, activeTachos.Count, defaultOffsetMs, activeEnemy?.Name ?? "-", detectors[cur]);
                    }

                    if (triggered)
                    {
                        bool isAttackBtn = BattleTacticHelper.IsAttack(activeTactic, cur, activeTachos.Count);
                        string checkpointsStr = detectors[cur].FormatCheckpointsSummary();
                        double usedOffsetMs = detectors[cur].LastUsedClickOffsetMs;

                        // 1. Button sofort betaetigen
                        TriggerClick(cur, activeTachos.Count, isAttackBtn, activeEnemy?.Name ?? "-", detectors[cur], cfg);
                        lastClickUtc = DateTime.UtcNow;

                        // 2. Klick-Moment sofort visualisieren (gelber Button-Blitz + Foto im Klick-Moment)
                        Bitmap? clickMomentSnap = detectors[cur].CaptureClickMomentSnapshot(curX, curY, cfg);
                        overlay?.FlashButtonClick(isAttackBtn, cur + 1, clickMomentSnap);

                        // 3. Direkt nach dem Klick die Farbe auf den unteren 9x9 Pixeln messen
                        TachoFeedback fb = detectors[cur].MeasureBottomColorAfterClick(cur + 1, curX, curY, cfg);
                        bool wasInferredRedFromNone = false;
                        if (cur == 1 && fb == TachoFeedback.None)
                        {
                            fb = TachoFeedback.TooLateRed;
                            wasInferredRedFromNone = true;
                            Console.WriteLine($"[Nach-Klick {(activeEnemy?.Name ?? "Standard")} T2] Keine Farbe erkannt -> als ROT (zu spaet) gewertet!");
                        }

                        // 4. Sofort den Offset des erkannten Spieler-Setups (mit Treffer-Quoten & Auto-Revert) anpassen
                        double newOffset = HandleTachoFeedback(cur, usedOffsetMs, fb, detectors[cur], cfg, enemyStore, activeEnemy, currentDeploymentId, !wasInferredRedFromNone);

                        // 5. Vollstaendige Telemetrie schreiben (inkl. VisDist & Dino-Metriken)
                        LogTelemetryClick(cur, activeTachos.Count, usedOffsetMs, newOffset, activeEnemy?.Name ?? "-", fb, detectors[cur], checkpointsStr, lastRoundVisDist, currentRoundVisual);

                        lastClickedTachoNumber = cur + 1;
                        lastFeedback = fb;
                        lastPatchR = detectors[cur].LastPatchRed;
                        lastPatchG = detectors[cur].LastPatchGreen;
                        lastPatchB = detectors[cur].LastPatchBlue;
                        lastPatchW = detectors[cur].LastPatchWhite;
                        lastDeltaMs = detectors[cur].LastCorrectionDeltaMs;
                        lastResultNewOffsetMs = newOffset;

                        // 6. Zum naechsten Tacho in der Reihenfolge wechseln
                        expectedTachoIndex = (cur + 1) % activeTachos.Count;
                        detectors[expectedTachoIndex].Reset();
                        isArmed = false;

                        // Bei neuen/unbekannten Spielern (oder Spielern mit gemerktem 3er-Profil im noch unkalibrierten 2er-Modus):
                        // nach Tacho 2 fuer 2,2 s automatisch pruefen, ob ein 3. Tacho erscheint!
                        if (cur == 1 && cfg.ActiveProfile == 2 &&
                            (activeEnemy == null || !activeEnemy.AllTachosFixed || activeEnemy.TachoCount == 3))
                        {
                            probeThirdTacho = true;
                            probeThirdTachoUntilUtc = DateTime.UtcNow.AddMilliseconds(2200.0);
                            detectors[2].Reset();
                        }
                        else
                        {
                            probeThirdTacho = false;
                        }

                        if (overlay != null)
                        {
                            lastOverlayUpdate = DateTime.UtcNow;
                            overlay.UpdateSnapshot(BuildOverlaySnapshot(
                                cfg, enemyStore, activeEnemy, activeEnemyIsNew,
                                detectors, expectedTachoIndex, running, activeTactic,
                                lastClickedTachoNumber, lastFeedback,
                                lastPatchR, lastPatchG, lastPatchB, lastPatchW,
                                lastDeltaMs, lastResultNewOffsetMs,
                                lastDinoPreview, lastDinoVisualInfo));
                        }
                    }
                    else if (probeThirdTacho && cur == 0 && detectors[0].State == DetectorState.WaitingForBlueZone)
                    {
                        if (DateTime.UtcNow > probeThirdTachoUntilUtc)
                        {
                            probeThirdTacho = false;
                        }
                        else if (activeTachos.Count >= 2 && activeTachos[0].IsSet && activeTachos[1].IsSet)
                        {
                            int probeX = (cfg.Profile3.Count >= 3 && cfg.Profile3[2].IsSet)
                                ? cfg.Profile3[2].X
                                : activeTachos[1].X + (activeTachos[1].X - activeTachos[0].X);
                            int probeY = (cfg.Profile3.Count >= 3 && cfg.Profile3[2].IsSet)
                                ? cfg.Profile3[2].Y
                                : activeTachos[1].Y + (activeTachos[1].Y - activeTachos[0].Y);

                            detectors[2].Update(probeX, probeY, cfg.GetClickOffsetMsForTacho(2), cfg);
                            if (detectors[2].State == DetectorState.Armed)
                            {
                                probeThirdTacho = false;
                                var (t3X, t3Y, dx3, dy3, centered3) = TachoDetector.TryAutoCenterOnRing(probeX, probeY, cfg.TachoBoxSize);

                                // 3er-Profil automatisch mit T1, T2 und dem erkannten T3 befuellen
                                while (cfg.Profile3.Count < 3)
                                    cfg.Profile3.Add(new TachoPoint { ClickOffsetMs = 45.0, DefaultClickOffsetMs = 45.0 });
                                if (!cfg.Profile3[0].IsSet) { cfg.Profile3[0].X = activeTachos[0].X; cfg.Profile3[0].Y = activeTachos[0].Y; }
                                if (!cfg.Profile3[1].IsSet) { cfg.Profile3[1].X = activeTachos[1].X; cfg.Profile3[1].Y = activeTachos[1].Y; }
                                if (!cfg.Profile3[2].IsSet) { cfg.Profile3[2].X = t3X; cfg.Profile3[2].Y = t3Y; }
                                cfg.ActiveProfile = 3;
                                cfg.Save();

                                if (activeEnemy != null)
                                {
                                    activeEnemy.TachoCount = 3;
                                    activeEnemy.EnsureSlots();
                                    if (!activeEnemy.Tachos[2].HasPosition)
                                    {
                                        activeEnemy.Tachos[2].X = cfg.Profile3[2].X;
                                        activeEnemy.Tachos[2].Y = cfg.Profile3[2].Y;
                                    }
                                    enemyStore.Save();
                                }

                                expectedTachoIndex = 2;
                                isArmed = true;
                                bool isT3Attack = BattleTacticHelper.IsAttack(activeTactic, 2, 3);
                                NativeMethods.SetCursorPos(
                                    isT3Attack ? cfg.GreenButtonX : cfg.BlueButtonX,
                                    isT3Attack ? cfg.GreenButtonY : cfg.BlueButtonY);
                                string cNote = centered3 ? $" (zentriert {dx3:+0;-0}/{dy3:+0;-0} px)" : "";
                                Console.WriteLine(
                                    $"[Auto-3er-Erkennung] 3. Tacho fuer {(activeEnemy?.Name ?? "Neuer Spieler")} bei ({cfg.Profile3[2].X}, {cfg.Profile3[2].Y}){cNote} erkannt! Profil automatisch auf 3 Tachos umgestellt.");
                            }
                        }
                    }
                    else if (cur > 0 &&
                             detectors[cur].State == DetectorState.WaitingForBlueZone &&
                             (DateTime.UtcNow - lastClickUtc).TotalMilliseconds >= 3500.0)
                    {
                        detectors[0].Reset();
                        detectors[0].Update(activeTachos[0].X, activeTachos[0].Y, GetEffectiveOffsetMs(0, cfg, activeEnemy), cfg);
                        if (detectors[0].State == DetectorState.Armed)
                        {
                            // Falls wir im 3er-Modus auf T3 gewartet haben, bei einem neuen Spieler aber direkt wieder T1 erscheint:
                            // automatisch auf 2 Tachos zurueckstufen!
                            if (cur == 2 && cfg.ActiveProfile == 3 && activeEnemy != null && !activeEnemy.AllTachosFixed &&
                                activeEnemy.Tachos.Count >= 3 && !activeEnemy.Tachos[2].HasSignature)
                            {
                                activeEnemy.TachoCount = 2;
                                if (activeEnemy.Tachos.Count > 2)
                                    activeEnemy.Tachos.RemoveRange(2, activeEnemy.Tachos.Count - 2);
                                enemyStore.Save();
                                cfg.ActiveProfile = 2;
                                cfg.Save();
                                Console.WriteLine($"[Auto-2er-Erkennung] {activeEnemy.Name} hat nur 2 Tachos (kein T3 erschienen) -> Profil automatisch auf 2 Tachos umgestellt.");
                            }
                            else
                            {
                                Console.WriteLine("[Sequenz] Neuer Durchgang erkannt -> wechsle zurueck auf Tacho 1.");
                            }
                            expectedTachoIndex = 0;
                            roundFingerprints.Clear();
                            isArmed = true;
                            bool isT1Attack = BattleTacticHelper.IsAttack(activeTactic, 0, cfg.GetActiveTachos().Count);
                            NativeMethods.SetCursorPos(
                                isT1Attack ? cfg.GreenButtonX : cfg.BlueButtonX,
                                isT1Attack ? cfg.GreenButtonY : cfg.BlueButtonY);
                        }
                    }

                    if (debugMode && (DateTime.UtcNow - lastDebugPrint).TotalMilliseconds >= 200)
                    {
                        lastDebugPrint = DateTime.UtcNow;
                        var d = detectors[expectedTachoIndex];
                        Console.WriteLine(
                            $"Aktiv T{expectedTachoIndex + 1}/{activeTachos.Count} [{activeEnemy?.Name ?? "-"}] | State={d.State} | G={d.LastGreenCount} | Rem={d.LastRemainingDeg:F0}° | n={d.LastCheckpointCount} | v={d.LastSpeedDegPerSec:F0}°/s");
                    }

                    if (!(isArmed && screenCapture.IsDxgiActive))
                    {
                        Thread.Sleep(isArmed ? 1 : Math.Max(1, cfg.PollIntervalMs));
                    }
                }
                else
                {
                    int cur = expectedTachoIndex;
                    if (activeTachos[cur].IsSet)
                    {
                        detectors[cur].Update(activeTachos[cur].X, activeTachos[cur].Y, GetEffectiveOffsetMs(cur, cfg, activeEnemy), cfg);
                        detectors[cur].Reset();
                    }
                    Thread.Sleep(40);
                }

                if (overlay != null && !isArmed && (DateTime.UtcNow - lastOverlayUpdate).TotalMilliseconds >= 80)
                {
                    lastOverlayUpdate = DateTime.UtcNow;
                    overlay.UpdateSnapshot(BuildOverlaySnapshot(
                        cfg, enemyStore, activeEnemy, activeEnemyIsNew,
                        detectors, expectedTachoIndex, running, activeTactic,
                        lastClickedTachoNumber, lastFeedback,
                        lastPatchR, lastPatchG, lastPatchB, lastPatchW,
                        lastDeltaMs, lastResultNewOffsetMs,
                        lastDinoPreview, lastDinoVisualInfo));
                }
            }
        }
        finally
        {
            lastDinoPreview?.Dispose();
            if (overlay != null && overlay.IsHandleCreated && !overlay.IsDisposed)
            {
                overlay.BeginInvoke(() => overlay.Close());
            }
            foreach (var d in detectors)
                d.Dispose();
        }
    }

    private static (int X, int Y) GetEffectiveTachoPos(int tachoIndex, AppConfig cfg, EnemySetup? activeEnemy)
    {
        if (activeEnemy != null && activeEnemy.ProfileSize == cfg.ActiveProfile)
        {
            activeEnemy.EnsureSlots();
            if (tachoIndex >= 0 && tachoIndex < activeEnemy.Tachos.Count &&
                activeEnemy.Tachos[tachoIndex].IsFixed && activeEnemy.Tachos[tachoIndex].HasPosition)
                return (activeEnemy.Tachos[tachoIndex].X, activeEnemy.Tachos[tachoIndex].Y);
        }
        var tachos = cfg.GetActiveTachos();
        return (tachos[tachoIndex].X, tachos[tachoIndex].Y);
    }

    private static double GetEffectiveOffsetMs(int tachoIndex, AppConfig cfg, EnemySetup? activeEnemy)
    {
        if (activeEnemy != null && activeEnemy.ProfileSize == cfg.ActiveProfile)
        {
            activeEnemy.EnsureSlots();
            if (tachoIndex >= 0 && tachoIndex < activeEnemy.Tachos.Count && activeEnemy.Tachos[tachoIndex].HasSignature)
                return activeEnemy.Tachos[tachoIndex].EffectiveClickOffsetMs;
        }
        return cfg.GetClickOffsetMsForTacho(tachoIndex);
    }

    private static void AdjustOffsetManually(int tachoIndex, double deltaMs, AppConfig cfg, EnemySetupStore store, EnemySetup? activeEnemy)
    {
        if (activeEnemy != null && activeEnemy.ProfileSize == cfg.ActiveProfile)
        {
            activeEnemy.EnsureSlots();
            if (tachoIndex >= 0 && tachoIndex < activeEnemy.Tachos.Count)
            {
                var slot = activeEnemy.Tachos[tachoIndex];
                double raw = Math.Clamp(Math.Round(slot.ClickOffsetMs + deltaMs, 2), 0.0, 120.0);
                slot.ClickOffsetMs = slot.ClampAboveRedLimit(raw);
                store.Save();
                string limitNote = slot.RedLimitOffsetMs.HasValue && raw <= slot.RedLimitOffsetMs.Value + 0.01
                    ? $" (Rot-Grenze <= {slot.RedLimitOffsetMs.Value:F2} ms aktiv)"
                    : "";
                Console.WriteLine($"[Manuell {activeEnemy.Name} Tacho {tachoIndex + 1}] {deltaMs:+0.0;-0.0} ms -> {slot.ClickOffsetMs:F2} ms{limitNote}");
                return;
            }
        }

        double val = cfg.AdjustTachoClickOffsetMs(tachoIndex, deltaMs);
        cfg.Save();
        Console.WriteLine($"[Manuell Standard Tacho {tachoIndex + 1}] {deltaMs:+0.0;-0.0} ms -> {val:F1} ms");
    }

    private static OverlaySnapshot BuildOverlaySnapshot(
        AppConfig cfg, EnemySetupStore enemyStore, EnemySetup? activeEnemy, bool activeEnemyIsNew,
        TachoDetector[] detectors, int activeIdx, bool running, BattleTactic activeTactic,
        int lastClickedTachoNumber, TachoFeedback lastFeedback,
        int patchR, int patchG, int patchB, int patchW,
        double lastDeltaMs, double lastResultNewOffsetMs,
        Bitmap? lastDinoPreview, string dinoVisualInfo)
    {
        var tachos = cfg.GetActiveTachos();
        var t = tachos[activeIdx];
        var d = detectors[activeIdx];

        string summary = string.Join(" | ",
            Enumerable.Range(0, tachos.Count).Select(idx => $"T{idx + 1}={GetEffectiveOffsetMs(idx, cfg, activeEnemy):F1}ms"));

        var (posX, posY) = GetEffectiveTachoPos(activeIdx, cfg, activeEnemy);
        var (dinoX, dinoY) = cfg.GetEffectiveDinoBoxCenter();
        string bestProvenInfo = "";
        if (activeEnemy != null && activeEnemy.ProfileSize == cfg.ActiveProfile && activeIdx < activeEnemy.Tachos.Count)
        {
            var sl = activeEnemy.Tachos[activeIdx];
            var best = sl.GetTopPreset(1) ?? sl.GetBestStartupPreset();
            string lockTag = sl.TemplateCurve.IsReady ? "[LOCK] " : "";
            string driftTag = Math.Abs(sl.SessionDriftMs) >= 0.05 ? $" Drift{sl.SessionDriftMs:+0.0;-0.0}" : "";
            if (best != null)
            {
                string rankLbl = best.TopRank > 0 ? $"Top-{best.TopRank} " : "Best ";
                string rateTxt = best.BlueHits > 0
                    ? $"{best.BlueRate * 100:F0}% Blau"
                    : $"{best.GreenHits}G/0R";
                bestProvenInfo = $"{lockTag}{rankLbl}T{activeIdx + 1}: {best.OffsetMs:F2}ms{driftTag} ({rateTxt})";
            }
            else if (!string.IsNullOrEmpty(lockTag))
            {
                bestProvenInfo = $"{lockTag}T{activeIdx + 1}: {sl.EffectiveClickOffsetMs:F2}ms{driftTag}";
            }
        }

        return new OverlaySnapshot
        {
            Running = running,
            ActiveProfile = cfg.ActiveProfile,
            ActiveTachoIndex = activeIdx,
            TotalTachos = tachos.Count,
            AllOffsetsSummary = summary,
            Tactic = activeTactic,
            TachoAttackPlan = BattleTacticHelper.BuildPlan(activeTactic, tachos.Count),
            IsAttackTacho = BattleTacticHelper.IsAttack(activeTactic, activeIdx, tachos.Count),
            EnemySetupName = activeEnemy != null ? $"{activeEnemy.Name} [{activeEnemy.DinoType} | {activeEnemy.TachoCount}T]" : "",
            EnemySetupIsFixed = activeEnemy?.IsTachoFixed(activeIdx) ?? false,
            EnemySetupIsNew = activeEnemyIsNew,
            EnemyBlueHits = activeEnemy?.TotalBlueHits ?? 0,
            EnemyTotalClicks = activeEnemy?.TotalClicks ?? 0,
            TotalSavedEnemies = enemyStore.Setups.Count,
            BestProvenInfo = bestProvenInfo,
            CenterX = posX,
            CenterY = posY,
            State = d.State,
            GreenCount = d.LastGreenCount,
            WhiteCount = d.LastWhiteCount,
            ClickOffsetMs = GetEffectiveOffsetMs(activeIdx, cfg, activeEnemy),
            SpeedDegPerSec = d.LastSpeedDegPerSec,
            CheckpointCount = d.LastCheckpointCount,
            ActivePreview = t.IsSet ? d.CreateDiagnosticBitmap() : null,
            LastClickedTachoNumber = lastClickedTachoNumber,
            LastFeedback = lastFeedback,
            PatchRed = patchR,
            PatchGreen = patchG,
            PatchBlue = patchB,
            PatchWhite = patchW,
            LastDeltaMs = lastDeltaMs,
            LastResultNewOffsetMs = lastResultNewOffsetMs,
            BlueButtonPreview = CaptureSmallPreview(cfg.BlueButtonX, cfg.BlueButtonY, 70),
            GreenButtonPreview = CaptureSmallPreview(cfg.GreenButtonX, cfg.GreenButtonY, 70),
            DinoPreview = lastDinoPreview != null ? (Bitmap)lastDinoPreview.Clone() : null,
            DinoBoxX = dinoX,
            DinoBoxY = dinoY,
            DinoVisualInfo = dinoVisualInfo
        };
    }

    private static Bitmap? CaptureSmallPreview(int centerX, int centerY, int size)
    {
        if (centerX <= 0 || centerY <= 0)
            return null;

        int half = size / 2;
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(Math.Max(0, centerX - half), Math.Max(0, centerY - half), 0, 0, new Size(size, size), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    private static string GetTelemetryLogPath()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        string dir = Directory.Exists(projectRoot) ? projectRoot : AppContext.BaseDirectory;
        return Path.Combine(dir, "tacho_telemetry.log");
    }

    private static void LogTelemetryClick(
        int tachoIndex, int totalTachos, double usedOffsetMs, double newOffsetMs,
        string enemyName, TachoFeedback fb, TachoDetector d, string checkpointsStr,
        double visDist, DinoVisualFingerprint? vis)
    {
        try
        {
            string visInfo = vis != null && vis.IsCurrentVersion
                ? $"Vis={(visDist >= 0 ? visDist.ToString("F3") : "new")}(Fg={vis.ForegroundPixels},AR={vis.AspectRatio:F2})"
                : "Vis=-";
            string vsyncInfo = d.LastClickVSyncPhaseMs >= 0
                ? $"VSync={d.LastClickVSyncPhaseMs:F2}/{d.LastRefreshPeriodMs:F2}ms(Pend={d.LastDwmFramesPending},Drop={d.LastDwmFramesDropped})"
                : "VSync=-";
            string line =
                $"{DateTime.Now:HH:mm:ss.fff} | CLICK | Setup={enemyName} | T{tachoIndex + 1}/{totalTachos} | {visInfo} | " +
                $"Cap={d.LastCaptureSource}({d.LastAvgCaptureMs:F2}ms) | {vsyncInfo} | " +
                $"Reason={d.LastTriggerReason} | Speed={d.LastSpeedDegPerSec:F1}°/s Accel={d.LastAccelDegPerSec2:F0}°/s2 | R2={d.LastRegressionR2:F4} | " +
                $"RemAtTrig={d.LastRemDegAtTrigger:F1}° | WaitMs={d.LastWaitMsAtTrigger:F1} | " +
                $"Offset={usedOffsetMs:F1}->{newOffsetMs:F1}ms | " +
                $"GreenEnd={d.LastTargetEndDeg:F1}° GreenW={d.LastGreenWidthDeg:F0}° BlueW={d.LastBlueWidthDeg:F0}° GreenPx={d.LastInitialGreenCount} BluePx={d.LastBlueSliverCount} | " +
                $"Feedback={fb} (RGB={d.LastAvgR},{d.LastAvgG},{d.LastAvgB} B={d.LastPatchBlue} G={d.LastPatchGreen} R={d.LastPatchRed} W={d.LastPatchWhite}) | " +
                $"Checkpoints={checkpointsStr}";
            File.AppendAllText(GetTelemetryLogPath(), line + Environment.NewLine);
        }
        catch { }
    }

    private static void LogTelemetryAbort(int tachoIndex, int totalTachos, double usedOffsetMs, string enemyName, TachoDetector d)
    {
        try
        {
            string line =
                $"{DateTime.Now:HH:mm:ss.fff} | ABORT_NO_CLICK | Setup={enemyName} | T{tachoIndex + 1}/{totalTachos} | " +
                $"Reason={d.LastAbortReason} | LastRem={d.LastAbortMinRemDeg:F1}° | BottomRedPx={d.LastAbortRedOnBottom} | " +
                $"Offset={usedOffsetMs:F1}ms | GreenEnd={d.LastTargetEndDeg:F0}° GreenW={d.LastGreenWidthDeg:F0}° BlueW={d.LastBlueWidthDeg:F0}° | " +
                $"Checkpoints={d.LastAbortCheckpointSummary}";
            File.AppendAllText(GetTelemetryLogPath(), line + Environment.NewLine);
            Console.WriteLine($"[ABBRUCH OHNE KLICK Tacho {tachoIndex + 1}] {d.LastAbortReason} (LastRem={d.LastAbortMinRemDeg:F0}°)");
        }
        catch { }
    }

    private static double HandleTachoFeedback(
        int tachoIndex, double usedOffsetMs, TachoFeedback fb, TachoDetector detector,
        AppConfig cfg, EnemySetupStore enemyStore, EnemySetup? activeEnemy, int deploymentId,
        bool allowEmergencyJump = true)
    {
        string counts = $"(Spot=({detector.LastPatchScreenX},{detector.LastPatchScreenY}) RGB=({detector.LastAvgR},{detector.LastAvgG},{detector.LastAvgB}) | B={detector.LastPatchBlue} G={detector.LastPatchGreen} R={detector.LastPatchRed} W={detector.LastPatchWhite})";
        string setupLabel = activeEnemy != null ? activeEnemy.Name : "Standard";

        double updated;
        double appliedDelta;
        bool reverted = false;
        OffsetPerformance? revertedStats = null;
        int skippedBadCount = 0;
        bool blockedBadCliff = false;
        bool usedBisection = false;
        double activeDrift = 0.0;

        if (activeEnemy != null && activeEnemy.ProfileSize == cfg.ActiveProfile)
        {
            activeEnemy.EnsureSlots();
            var slot = activeEnemy.Tachos[tachoIndex];

            // 1. Empirische Master-Kurve (TemplateCurve) aus jedem sauberen Spin lernen/verfeinern
            bool wasLockReady = slot.TemplateCurve.IsReady;
            if (detector.LastPredictedZeroTimeMs > 0.0 &&
                slot.TemplateCurve.UpdateFromSpin(detector.GetLastCheckpointsCopy(), detector.LastPredictedZeroTimeMs, detector.LastRegressionR2) &&
                !wasLockReady && slot.TemplateCurve.IsReady)
            {
                Console.WriteLine($"[PHASE-LOCK BEREIT! {setupLabel} T{tachoIndex + 1}] Empirische Master-Kurve aktiv ({slot.TemplateCurve.PopulatedPointCount} Winkel-Stuetzstellen) -> 1-Parameter-Timing aktiv!");
            }

            // 2. Ergebnis unter dem Basis-Preset verbuchen (SessionDriftMs bleibt sauber getrennt!)
            double baseUsedOffset = slot.ClickOffsetMs;
            (updated, appliedDelta, reverted, revertedStats, skippedBadCount, blockedBadCliff, usedBisection) =
                slot.RecordOutcomeAndTune(baseUsedOffset, fb, activeEnemy.IsBird, deploymentId, allowEmergencyJump);
            activeDrift = slot.SessionDriftMs;

            // Wenn auf T1 eine grosse 1-Frame-Notbremse (>= +10 ms) gegriffen hat, hat das Spiel/Browser-Buffering
            // um +1 Frame (+16,7 ms) verschoben -> sofort +12 ms Schutz-Drift auf T2/T3 derselben Runde uebertragen!
            if (tachoIndex == 0 && fb == TachoFeedback.TooLateRed && appliedDelta >= 10.0)
            {
                for (int otherIdx = 1; otherIdx < activeEnemy.Tachos.Count; otherIdx++)
                {
                    var otherSlot = activeEnemy.Tachos[otherIdx];
                    if (otherSlot.SessionDriftMs < 10.0)
                        otherSlot.SessionDriftMs = Math.Clamp(Math.Round(otherSlot.SessionDriftMs + 12.0, 2), -4.0, 24.0);
                }
                Console.WriteLine($"[Frame-Sprung-Schutz {setupLabel}] T1-Notbremse -> +12,0 ms Schutz-Drift sofort auch auf T2/T3 uebertragen!");
            }

            if (detector.LastFingerprint.EndSpeedDegPerSec > 50.0)
            {
                slot.UpdateSignatureEma(detector.LastFingerprint);
            }

            enemyStore.Save();
        }
        else
        {
            appliedDelta = fb switch
            {
                TachoFeedback.TooLateRed => +2.5,
                TachoFeedback.TooEarlyGreen => -0.5,
                _ => 0.0
            };

            if (fb == TachoFeedback.WayTooEarlyWhite)
                updated = cfg.ResetTachoClickOffsetToDefault(tachoIndex);
            else if (appliedDelta != 0.0)
                updated = cfg.AdjustTachoClickOffsetMs(tachoIndex, appliedDelta);
            else
                updated = cfg.GetClickOffsetMsForTacho(tachoIndex);

            cfg.Save();
        }

        detector.LastCorrectionDeltaMs = appliedDelta;
        string driftInfo = Math.Abs(activeDrift) >= 0.05 ? $" [Drift={activeDrift:+0.00;-0.00}ms]" : "";
        string bisectionTag = usedBisection ? " [BISECTION]" : "";

        if (reverted && revertedStats != null)
        {
            string cliffNote = blockedBadCliff ? " (schlechtes Nachbar-Preset gesperrt!)" : "";
            if (revertedStats.TopRank > 0)
            {
                Console.WriteLine(
                    $"[TOP-WECHSEL {setupLabel} T{tachoIndex + 1}] Feedback={fb}{cliffNote} -> Wechsle auf TOP-{revertedStats.TopRank} ({updated:F2} ms{driftInfo} | Blau: {revertedStats.BlueHits}/{revertedStats.Total} = {revertedStats.BlueRate * 100:F0}%, R={revertedStats.RedHits})!");
            }
            else
            {
                Console.WriteLine(
                    $"[AUTO-REVERT {setupLabel} T{tachoIndex + 1}] Feedback={fb}{cliffNote} -> Springe zu Best-Preset {updated:F2} ms{driftInfo} (Blau-Quote: {revertedStats.BlueHits}/{revertedStats.Total} = {revertedStats.BlueRate * 100:F0}%)!");
            }
            return updated;
        }

        string skipNote = skippedBadCount > 0
            ? (blockedBadCliff
                ? " [schlechtes Rot-Preset gesperrt -> bleibe sicher]"
                : $" [{skippedBadCount} schlechte(s) Preset(s) uebersprungen!]")
            : "";

        switch (fb)
        {
            case TachoFeedback.TooLateRed:
                if (appliedDelta >= 4.0)
                    Console.WriteLine($"[NOTBREMSE 2x ROT! {setupLabel} T{tachoIndex + 1}] >>> DEUTLICH FRUEHER KLICKEN! <<< {counts} -> Offset {appliedDelta:+0.00} ms auf {updated:F2} ms{driftInfo}{skipNote}");
                else
                    Console.WriteLine($"[Nach-Klick {setupLabel} T{tachoIndex + 1}] >>> ROT (ZU SPAET!) <<< {counts} -> Offset {appliedDelta:+0.00;-0.00} ms auf {updated:F2} ms{bisectionTag}{driftInfo}{skipNote}");
                break;
            case TachoFeedback.TooEarlyGreen:
                Console.WriteLine($"[Nach-Klick {setupLabel} T{tachoIndex + 1}] >>> GRUEN (ETWAS FRUEH) <<< {counts} -> Offset {appliedDelta:+0.00;-0.00} ms auf {updated:F2} ms{bisectionTag}{driftInfo}{skipNote}");
                break;
            case TachoFeedback.OptimalBlue:
                Console.WriteLine($"[Nach-Klick {setupLabel} T{tachoIndex + 1}] >>> BLAU (OPTIMAL!) <<< {counts} -> Offset ({updated:F2} ms{driftInfo}) bleibt unveraendert.");
                break;
            case TachoFeedback.WayTooEarlyWhite:
                if (appliedDelta > 0)
                    Console.WriteLine($"[Nach-Klick {setupLabel} T{tachoIndex + 1}] >>> WEISS (NACH BLAU / ZU SPAET!) <<< {counts} -> Offset {appliedDelta:+0.00;-0.00} ms auf {updated:F2} ms{bisectionTag}{driftInfo}");
                else
                    Console.WriteLine($"[Nach-Klick {setupLabel} T{tachoIndex + 1}] >>> WEISS (VOR GRUEN / ZU FRUEH!) <<< {counts} -> Offset {appliedDelta:+0.00;-0.00} ms auf {updated:F2} ms{driftInfo}");
                break;
            default:
                Console.WriteLine($"[Nach-Klick {setupLabel} T{tachoIndex + 1}] Keine eindeutige Farbe erkannt {counts} -> Offset bleibt {updated:F2} ms.");
                break;
        }

        return updated;
    }

    private static void TriggerClick(int tachoIndex, int totalTachos, bool isAttack, string enemyName, TachoDetector detector, AppConfig cfg)
    {
        int targetX = isAttack ? cfg.GreenButtonX : cfg.BlueButtonX;
        int targetY = isAttack ? cfg.GreenButtonY : cfg.BlueButtonY;
        string btnLabel = isAttack ? "Gruener Schwerter-Button (Angriff)" : "Blauer Schild-Button (Abwehr)";

        // Erst klicken (0,0 ms Verzoegerung durch Console.WriteLine!), dann VSync-Phase & Log ausgeben!
        long clickQpc = NativeMethods.ClickAt(targetX, targetY, cfg.ClickHoldMs);
        if (NativeMethods.TryGetVSyncPhaseMs(clickQpc, out double phaseMs, out double refreshMs, out long pending, out long dropped))
        {
            detector.LastClickVSyncPhaseMs = phaseMs;
            detector.LastRefreshPeriodMs = refreshMs;
            detector.LastDwmFramesPending = pending;
            detector.LastDwmFramesDropped = dropped;
        }
        else
        {
            detector.LastClickVSyncPhaseMs = -1.0;
        }

        string phaseTag = detector.LastClickVSyncPhaseMs >= 0 ? $" VSync={detector.LastClickVSyncPhaseMs:F1}ms" : "";
        Console.WriteLine(
            $"[TREFFER {enemyName} T{tachoIndex + 1}/{totalTachos}!] {detector.LastTriggerReason} [{detector.LastCaptureSource} {detector.LastAvgCaptureMs:F2}ms{phaseTag}] v={detector.LastSpeedDegPerSec:F0}°/s (Offset={detector.LastUsedClickOffsetMs:F1}ms) -> Klicke {btnLabel}");
    }

    private static void TryAutoStart(AppConfig cfg, ref bool running)
    {
        if (cfg.IsCalibrated && !running)
        {
            running = true;
            Console.WriteLine($"[Status] AKTIV ({cfg.ActiveProfile}er-Modus) - Starte sequenziell bei Tacho 1...");
        }
    }

    private static void ResetDetectors(TachoDetector[] detectors)
    {
        foreach (var d in detectors)
            d.Reset();
    }

    private static void PrintHeader(AppConfig cfg, EnemySetupStore store, DxgiScreenCapture capture, bool running, bool debugMode, BattleTactic tactic)
    {
        var tachos = cfg.GetActiveTachos();
        Console.WriteLine("=================================================================");
        Console.WriteLine("  TachoClicker - Sub-ms DXGI + Auto Gegner- & 2/3-Tacho-Erkennung");
        Console.WriteLine("=================================================================");
        string capLine = capture.IsDxgiActive
            ? $"DXGI Desktop Duplication ({capture.OutputWidth}x{capture.OutputHeight}, Hardware LastPresentTime)"
            : $"DWM Hardware-VBlank Fallback ({capture.LastInitError})";
        string refreshLine = NativeMethods.TryGetDwmTiming(out _, out long qpcPer, out _, out _)
            ? $"{Stopwatch.Frequency / (double)qpcPer:F3} Hz ({qpcPer * 1000.0 / Stopwatch.Frequency:F3} ms)"
            : "unbekannt";
        Console.WriteLine($"Capture-Engine        : {capLine}");
        Console.WriteLine($"Monitor-VSync         : {refreshLine} | Prozess-Prioritaet: High / Highest");
        Console.WriteLine("Hotkeys:");
        Console.WriteLine("  F1  : Taktik Reine Abwehr    (alle Tachos BLAU / Abwehr)");
        Console.WriteLine("  F2  : Taktik 1x Angriff      (letzter Tacho GRUEN, davor BLAU) [Standard]");
        Console.WriteLine("  F3  : Taktik 2x Angriff      (letzte 2 Tachos GRUEN, davor BLAU)");
        Console.WriteLine("  F4  : Taktik Alle Angriff    (alle Tachos GRUEN / Angriff)");
        string calLock = CalibrationHotkeysEnabled ? "" : " [DEAKTIVIERT]";
        Console.WriteLine("  F5  : Position des aktiven eigenen Dinos speichern (optional, sonst automatisch ueber T1)");
        Console.WriteLine($"  F6  : Gruenen Schwerter-Button speichern (+ Auto-Start){calLock}");
        Console.WriteLine("  F7  : Debug-Ausgabe An / Aus");
        Console.WriteLine("  F8  : Automatik Start / Pause (+ Preset-Statistik der Session)");
        Console.WriteLine($"  F9  : Naechste Tacho-Mitte speichern (auto-zentriert){calLock}");
        Console.WriteLine($"  F10 : Blauen Schild-Button speichern (+ Auto-Start){calLock}");
        Console.WriteLine("  +/- : Zeit-Vorlauf des aktuell aktiven Tachos feinjustieren");
        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine($"Spieltaktik           : {BattleTacticHelper.GetLabel(tactic)} -> {BattleTacticHelper.FormatPlan(tactic, tachos.Count)}");
        Console.WriteLine($"Aktives Profil        : {cfg.ActiveProfile}er-Konfiguration | Gespeicherte Gegner-Setups: {store.Setups.Count}");
        foreach (var s in store.Setups)
        {
            string fixedTag = s.AllTachosFixed ? "FEST" : (s.HasAnyFixedTacho ? "TEIL-FEST" : "AUTO");
            string visTag = s.HasVisual ? "VIS" : "NO-VIS";
            string slotDesc = string.Join(" | ", s.Tachos.Select((sl, i) =>
            {
                if (!sl.HasSignature) return $"T{i + 1}:-";
                var best = sl.GetTopPreset(1) ?? sl.GetBestStartupPreset();
                string bestTxt = best != null
                    ? (best.TopRank > 0 ? $",Top-1={best.OffsetMs:F2}ms({best.BlueHits}B/{best.Total})" : $",Best={best.OffsetMs:F2}ms({best.BlueHits}B/{best.Total})")
                    : "";
                string fixSlotTag = (!s.AllTachosFixed && s.HasAnyFixedTacho) ? (sl.IsFixed ? ",FEST" : ",AUTO") : "";
                return $"T{i + 1}:{sl.ClickOffsetMs:F2}ms{fixSlotTag}{bestTxt}";
            }));
            Console.WriteLine($"  - {s.Name} [{s.DinoType}, {s.TachoCount} Tachos, {fixedTag}, {visTag}]: {slotDesc}");
        }
        var (dx0, dy0) = cfg.GetEffectiveDinoBoxCenter();
        Console.WriteLine($"Dino-Sichtfeld        : ({dx0}, {dy0}) [{cfg.DinoBoxWidth}x{cfg.DinoBoxHeight}] {(cfg.DinoBoxX > 0 ? "(manuell F5)" : "(auto ueber T1)")}");
        Console.WriteLine($"Blauer Schild-Button  : ({cfg.BlueButtonX}, {cfg.BlueButtonY})");
        Console.WriteLine($"Gruener Schwert-Btn   : ({cfg.GreenButtonX}, {cfg.GreenButtonY})");
        Console.WriteLine($"Status                : {(running ? "AKTIV" : "PAUSIERT")} | Debug: {(debugMode ? "AN" : "AUS")}");
        Console.WriteLine("=================================================================");
    }

    private static void PrintSessionPresetStats(
        EnemySetupStore store,
        HashSet<int> playedEnemyIds,
        List<(int DeploymentId, int SetupId, string Name)> sessionDeployments)
    {
        Console.WriteLine();
        Console.WriteLine("================ PRESET-STATISTIKEN (GESPIELTE SPIELER) ================");
        if (sessionDeployments.Count > 0)
        {
            string depList = string.Join(" | ", sessionDeployments.Select(d => $"Einsatz #{d.DeploymentId}: {d.Name}"));
            Console.WriteLine($"  Einsaetze: {depList}");
        }
        if (playedEnemyIds.Count == 0)
        {
            Console.WriteLine("  (In dieser Session wurden noch keine Spieler erfasst.)");
            Console.WriteLine("========================================================================");
            return;
        }

        var playedSetups = store.Setups
            .Where(s => playedEnemyIds.Contains(s.Id))
            .OrderBy(s => s.Id)
            .ToList();

        foreach (var setup in playedSetups)
        {
            Console.WriteLine();
            Console.WriteLine($"*** #{setup.Id} {setup.Name} [{setup.DinoType} | {setup.TachoCount} Tachos] ***");
            for (int i = 0; i < setup.Tachos.Count; i++)
            {
                var slot = setup.Tachos[i];
                slot.RefreshTopPresets();
                var top1 = slot.GetTopPreset(1);
                string statusTag = top1 != null
                    ? $"STABIL (Top-1: {top1.OffsetMs:F2} ms, {top1.BlueHits}B/{top1.Total})"
                    : "LERNPHASE";

                string redLimitTag = slot.RedLimitOffsetMs.HasValue
                    ? $" | Rot-Grenze: <= {slot.RedLimitOffsetMs.Value:F2} ms"
                    : "";
                Console.WriteLine(
                    $"  Tacho {i + 1} | Aktuell={slot.ClickOffsetMs:F2} ms | Gesamt: {slot.BlueHits}B/{slot.GreenHits}G/{slot.RedHits}R/{slot.WhiteHits}W ({slot.TotalClicks} Klicks){redLimitTag} | {statusTag}");

                if (slot.OffsetHistory.Count == 0)
                {
                    Console.WriteLine("    (noch keine Preset-Daten)");
                    continue;
                }

                Console.WriteLine("    Preset    | Blau | Gruen | Rot | Weiss |  N  |  Blau% |  Rot% |  Score | Status");
                Console.WriteLine("    ----------+------+-------+-----+-------+-----+--------+-------+--------+-----------------------");

                foreach (var h in slot.OffsetHistory.OrderBy(x => x.OffsetMs))
                {
                    var tags = new List<string>();
                    if (h.TopRank > 0) tags.Add($"TOP-{h.TopRank}");
                    if (h.IsSafeGreen) tags.Add("safe-green");
                    if (slot.RedLimitOffsetMs.HasValue)
                    {
                        if (Math.Abs(h.OffsetMs - slot.RedLimitOffsetMs.Value) < 0.02)
                            tags.Add("ROT-GRENZE");
                        else if (slot.IsBlockedByRedLimit(h.OffsetMs))
                            tags.Add("< ROT-GRENZE");
                    }
                    if (Math.Abs(h.OffsetMs - slot.ClickOffsetMs) < 0.02) tags.Add("AKTIV");
                    string tagStr = tags.Count > 0 ? string.Join(", ", tags) : "-";

                    Console.WriteLine(
                        $"    {h.OffsetMs,6:F2} ms | {h.BlueHits,4} | {h.GreenHits,5} | {h.RedHits,3} | {h.WhiteHits,5} | {h.Total,3} | {h.BlueRate * 100,5:F1}% | {h.RedRate * 100,4:F1}% | {h.SmoothedScore,6:F3} | {tagStr}");
                }
            }
        }

        Console.WriteLine("========================================================================");
        Console.WriteLine();
    }
}
