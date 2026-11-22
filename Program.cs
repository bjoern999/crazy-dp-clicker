using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace TachoClicker;

internal static class Program
{
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        NativeMethods.SetProcessDPIAware();
        Console.Title = "TachoClicker (Gegner-Erkennung | F2=2er | F3=3er | F6=Gruen | F8=Start | F9=Tacho | F10=Blau)";

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

    private static void RunLoop()
    {
        AppConfig cfg = AppConfig.Load();
        EnemySetupStore enemyStore = EnemySetupStore.Load();
        var detectors = new[] { new TachoDetector(), new TachoDetector(), new TachoDetector() };

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

        // Letztes Feedback fuer die Anzeige im Live-Inspektor
        int lastClickedTachoNumber = 0;
        TachoFeedback lastFeedback = TachoFeedback.None;
        int lastPatchR = 0, lastPatchG = 0, lastPatchB = 0, lastPatchW = 0;
        double lastDeltaMs = 0;
        double lastResultNewOffsetMs = 0;

        bool f2Prev = false;
        bool f3Prev = false;
        bool f5Prev = false;
        bool f6Prev = false;
        bool f7Prev = false;
        bool f8Prev = false;
        bool f9Prev = false;
        bool f10Prev = false;
        bool plusPrev = false;
        bool minusPrev = false;

        PrintHeader(cfg, enemyStore, running, debugMode);

        DateTime lastDebugPrint = DateTime.MinValue;
        DateTime lastOverlayUpdate = DateTime.MinValue;

        try
        {
            while (true)
            {
                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_F2, ref f2Prev))
                {
                    SwitchProfile(cfg, detectors, 2, ref nextTachoIndex, ref expectedTachoIndex, ref running);
                    activeEnemy = null;
                    lastConfirmedEnemy = null;
                    roundFingerprints.Clear();
                }

                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_F3, ref f3Prev))
                {
                    SwitchProfile(cfg, detectors, 3, ref nextTachoIndex, ref expectedTachoIndex, ref running);
                    activeEnemy = null;
                    lastConfirmedEnemy = null;
                    roundFingerprints.Clear();
                }

                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_F9, ref f9Prev))
                {
                    if (NativeMethods.GetCursorPos(out var pt))
                    {
                        var tachos = cfg.GetActiveTachos();
                        if (nextTachoIndex >= tachos.Count)
                            nextTachoIndex = 0;

                        tachos[nextTachoIndex].X = pt.X;
                        tachos[nextTachoIndex].Y = pt.Y;
                        cfg.Save();
                        detectors[nextTachoIndex].Reset();

                        int justSetIndex = nextTachoIndex;
                        expectedTachoIndex = justSetIndex;
                        nextTachoIndex = (nextTachoIndex + 1) % tachos.Count;

                        Console.WriteLine(
                            $"[Kalibrierung {cfg.ActiveProfile}er-Modus] Tacho {justSetIndex + 1}/{tachos.Count} gesetzt auf: ({pt.X}, {pt.Y}) | Naechster mit F9: Tacho {nextTachoIndex + 1}/{tachos.Count}");
                    }
                }

                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_F10, ref f10Prev))
                {
                    if (NativeMethods.GetCursorPos(out var pt))
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
                    if (NativeMethods.GetCursorPos(out var pt))
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
                        }
                        overlay?.EnsureVisibleOnTop();
                        Console.WriteLine(running
                            ? $"[Status] AKTIV ({cfg.ActiveProfile}er-Modus) - Starte sequenziell bei Tacho 1..."
                            : "[Status] PAUSIERT.");
                        if (!running)
                        {
                            PrintSessionPresetStats(enemyStore, sessionPlayedEnemyIds);
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
                    double manualStep = (activeEnemy != null && activeEnemy.IsFixed) ? +0.5 : +2.0;
                    AdjustOffsetManually(expectedTachoIndex, manualStep, cfg, enemyStore, activeEnemy);
                }

                if (NativeMethods.IsAnyKeyJustPressed(NativeMethods.VK_OEM_MINUS, NativeMethods.VK_SUBTRACT, ref minusPrev))
                {
                    double manualStep = (activeEnemy != null && activeEnemy.IsFixed) ? -0.5 : -2.0;
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
                            activeEnemy = matched;
                            activeEnemyIsNew = isNew;
                            sessionPlayedEnemyIds.Add(matched.Id);
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
                                        string meisterTag = matched.Tachos[sIdx].IsMeisterTacho ? " [MEISTER-TACHO]" : "";
                                        string statInfo = chosen != null
                                            ? $"{rankTag}{meisterTag} (Blau: {chosen.BlueHits}/{chosen.Total} = {chosen.BlueRate * 100:F0}%, G={chosen.GreenHits}, R={chosen.RedHits})"
                                            : "(schlechtes Preset gemieden)";
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
                            else if (slot.IsPresetKnownBad(slot.ClickOffsetMs))
                            {
                                var (ch, oldOff, newOff, chosen) = slot.ApplyBestKnownPreset();
                                if (ch)
                                {
                                    presetAutoSwitched = true;
                                    enemyStore.Save();
                                    Console.WriteLine(
                                        $"[Bad-Preset vermieden {matched.Name} T{cur + 1}] {oldOff:F2} ms (schlechte Quote) -> {newOff:F2} ms");
                                }
                            }

                            double enemyOffset = slot.ClickOffsetMs;

                            if (changedEnemy || cur == 0 || presetAutoSwitched)
                            {
                                string tag = isNew ? "NEU ANGELEGT" : (matched.IsFixed ? $"FEST (Dist={dist:F2})" : $"ERKANNT (Dist={dist:F2})");
                                string visStr = visDist >= 0 ? $", Vis={visDist:F2}{(learnedVisual ? " [NEU]" : "")}" : "";
                                var curPresetStat = slot.FindPresetStats(enemyOffset);
                                string curStatStr = curPresetStat != null
                                    ? $" [Preset: {curPresetStat.BlueHits}B/{curPresetStat.GreenHits}G/{curPresetStat.RedHits}R]"
                                    : " [Preset: neu]";
                                var best = slot.GetBestProvenOffset();
                                string bestStr = best != null ? $", Best={best.OffsetMs:F2}ms ({best.BlueHits}/{best.Total} Blau)" : "";
                                int badCnt = slot.CountKnownBadPresets();
                                string badStr = badCnt > 0 ? $", Gesperrt={badCnt}" : "";
                                Console.WriteLine(
                                    $"[Spieler-Setup] {matched.Name} ({matched.DinoType}) [{tag}{visStr}] | T{cur + 1}: v={fp.EndSpeedDegPerSec:F1}°/s, a={fp.AccelDegPerSec2:F0}°/s², T55={fp.SpinTimeTo55Ms:F0}ms, G={fp.GreenWidthDeg:F0}° -> Offset {enemyOffset:F2} ms{curStatStr}{bestStr}{badStr}");
                            }

                            return enemyOffset;
                        });

                    if (detectors[cur].JustArmed)
                    {
                        bool isLast = cur == activeTachos.Count - 1;
                        int targetX = isLast ? cfg.GreenButtonX : cfg.BlueButtonX;
                        int targetY = isLast ? cfg.GreenButtonY : cfg.BlueButtonY;
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
                        bool isLastBtn = cur == activeTachos.Count - 1;
                        string checkpointsStr = detectors[cur].FormatCheckpointsSummary();
                        double usedOffsetMs = detectors[cur].LastUsedClickOffsetMs;

                        // 1. Button sofort betaetigen
                        TriggerClick(cur, activeTachos.Count, activeEnemy?.Name ?? "-", detectors[cur], cfg);
                        lastClickUtc = DateTime.UtcNow;

                        // 2. Klick-Moment sofort visualisieren (gelber Button-Blitz + Foto im Klick-Moment)
                        Bitmap? clickMomentSnap = detectors[cur].CaptureClickMomentSnapshot(curX, curY, cfg);
                        overlay?.FlashButtonClick(isLastBtn, cur + 1, clickMomentSnap);

                        // 3. Direkt nach dem Klick die Farbe auf den unteren 9x9 Pixeln messen
                        TachoFeedback fb = detectors[cur].MeasureBottomColorAfterClick(cur + 1, curX, curY, cfg);
                        if (cur == 1 && fb == TachoFeedback.None)
                        {
                            fb = TachoFeedback.TooLateRed;
                            Console.WriteLine($"[Nach-Klick {(activeEnemy?.Name ?? "Standard")} T2] Keine Farbe erkannt -> als ROT (zu spaet) gewertet!");
                        }

                        // 4. Sofort den Offset des erkannten Spieler-Setups (mit Treffer-Quoten & Auto-Revert) anpassen
                        double newOffset = HandleTachoFeedback(cur, usedOffsetMs, fb, detectors[cur], cfg, enemyStore, activeEnemy);

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

                        if (overlay != null)
                        {
                            lastOverlayUpdate = DateTime.UtcNow;
                            overlay.UpdateSnapshot(BuildOverlaySnapshot(
                                cfg, enemyStore, activeEnemy, activeEnemyIsNew,
                                detectors, expectedTachoIndex, running,
                                lastClickedTachoNumber, lastFeedback,
                                lastPatchR, lastPatchG, lastPatchB, lastPatchW,
                                lastDeltaMs, lastResultNewOffsetMs,
                                lastDinoPreview, lastDinoVisualInfo));
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
                            Console.WriteLine("[Sequenz] Neuer Durchgang erkannt -> wechsle zurueck auf Tacho 1.");
                            expectedTachoIndex = 0;
                            roundFingerprints.Clear();
                            isArmed = true;
                            NativeMethods.SetCursorPos(cfg.BlueButtonX, cfg.BlueButtonY);
                        }
                    }

                    if (debugMode && (DateTime.UtcNow - lastDebugPrint).TotalMilliseconds >= 200)
                    {
                        lastDebugPrint = DateTime.UtcNow;
                        var d = detectors[expectedTachoIndex];
                        Console.WriteLine(
                            $"Aktiv T{expectedTachoIndex + 1}/{activeTachos.Count} [{activeEnemy?.Name ?? "-"}] | State={d.State} | G={d.LastGreenCount} | Rem={d.LastRemainingDeg:F0}° | n={d.LastCheckpointCount} | v={d.LastSpeedDegPerSec:F0}°/s");
                    }

                    Thread.Sleep(isArmed ? 1 : Math.Max(1, cfg.PollIntervalMs));
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
                        detectors, expectedTachoIndex, running,
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
        if (activeEnemy != null && activeEnemy.IsFixed && activeEnemy.ProfileSize == cfg.ActiveProfile)
        {
            activeEnemy.EnsureSlots();
            if (tachoIndex >= 0 && tachoIndex < activeEnemy.Tachos.Count && activeEnemy.Tachos[tachoIndex].HasPosition)
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
                return activeEnemy.Tachos[tachoIndex].ClickOffsetMs;
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
                slot.ClickOffsetMs = Math.Clamp(Math.Round(slot.ClickOffsetMs + deltaMs, 2), 0.0, 120.0);
                store.Save();
                Console.WriteLine($"[Manuell {activeEnemy.Name} Tacho {tachoIndex + 1}] {deltaMs:+0.0;-0.0} ms -> {slot.ClickOffsetMs:F1} ms");
                return;
            }
        }

        double val = cfg.AdjustTachoClickOffsetMs(tachoIndex, deltaMs);
        cfg.Save();
        Console.WriteLine($"[Manuell Standard Tacho {tachoIndex + 1}] {deltaMs:+0.0;-0.0} ms -> {val:F1} ms");
    }

    private static OverlaySnapshot BuildOverlaySnapshot(
        AppConfig cfg, EnemySetupStore enemyStore, EnemySetup? activeEnemy, bool activeEnemyIsNew,
        TachoDetector[] detectors, int activeIdx, bool running,
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
            int badCnt = sl.CountKnownBadPresets();
            string meisterPrefix = sl.IsMeisterTacho ? "[MEISTER] " : "";
            string badPart = badCnt > 0 ? $" | Gesperrt: {badCnt}" : "";
            if (best != null)
            {
                string rankLbl = best.TopRank > 0 ? $"Top-{best.TopRank} " : "Best ";
                string rateTxt = best.BlueHits > 0
                    ? $"{best.BlueRate * 100:F0}% Blau"
                    : $"{best.GreenHits}G/0R";
                bestProvenInfo = $"{meisterPrefix}{rankLbl}T{activeIdx + 1}: {best.OffsetMs:F2}ms ({rateTxt}){badPart}";
            }
            else if (badCnt > 0)
            {
                bestProvenInfo = $"{meisterPrefix}Gesperrt T{activeIdx + 1}: {badCnt}";
            }
        }

        return new OverlaySnapshot
        {
            Running = running,
            ActiveProfile = cfg.ActiveProfile,
            ActiveTachoIndex = activeIdx,
            TotalTachos = tachos.Count,
            AllOffsetsSummary = summary,
            EnemySetupName = activeEnemy != null ? $"{activeEnemy.Name} [{activeEnemy.DinoType}]" : "",
            EnemySetupIsFixed = activeEnemy?.IsFixed ?? false,
            EnemySetupIsNew = activeEnemyIsNew,
            EnemyBlueHits = activeEnemy?.TotalBlueHits ?? 0,
            EnemyTotalClicks = activeEnemy?.TotalClicks ?? 0,
            TotalSavedEnemies = enemyStore.Setups.Count,
            BestProvenInfo = bestProvenInfo,
            CenterX = posX,
            CenterY = posY,
            IsLastTacho = activeIdx == tachos.Count - 1,
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
            string line =
                $"{DateTime.Now:HH:mm:ss.fff} | CLICK | Setup={enemyName} | T{tachoIndex + 1}/{totalTachos} | {visInfo} | " +
                $"Reason={d.LastTriggerReason} | Speed={d.LastSpeedDegPerSec:F1}°/s Accel={d.LastAccelDegPerSec2:F0}°/s2 | R2={d.LastRegressionR2:F4} | " +
                $"RemAtTrig={d.LastRemDegAtTrigger:F1}° | WaitMs={d.LastWaitMsAtTrigger:F1} | " +
                $"Offset={usedOffsetMs:F1}->{newOffsetMs:F1}ms | " +
                $"GreenEnd={d.LastTargetEndDeg:F0}° GreenW={d.LastGreenWidthDeg:F0}° BlueW={d.LastBlueWidthDeg:F0}° GreenPx={d.LastInitialGreenCount} BluePx={d.LastBlueSliverCount} | " +
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
        AppConfig cfg, EnemySetupStore enemyStore, EnemySetup? activeEnemy)
    {
        string counts = $"(Spot=({detector.LastPatchScreenX},{detector.LastPatchScreenY}) RGB=({detector.LastAvgR},{detector.LastAvgG},{detector.LastAvgB}) | B={detector.LastPatchBlue} G={detector.LastPatchGreen} R={detector.LastPatchRed} W={detector.LastPatchWhite})";
        string setupLabel = activeEnemy != null ? activeEnemy.Name : "Standard";

        double updated;
        double appliedDelta;
        bool reverted = false;
        OffsetPerformance? revertedStats = null;
        int skippedBadCount = 0;
        bool blockedBadCliff = false;

        if (activeEnemy != null && activeEnemy.ProfileSize == cfg.ActiveProfile)
        {
            activeEnemy.EnsureSlots();
            var slot = activeEnemy.Tachos[tachoIndex];
            (updated, appliedDelta, reverted, revertedStats, skippedBadCount, blockedBadCliff) =
                slot.RecordOutcomeAndTune(usedOffsetMs, fb, activeEnemy.IsFixed, activeEnemy.IsBird);

            if (detector.LastFingerprint.EndSpeedDegPerSec > 50.0)
            {
                slot.UpdateSignatureEma(detector.LastFingerprint, activeEnemy.IsFixed);
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

        if (reverted && revertedStats != null)
        {
            string cliffNote = blockedBadCliff ? " (schlechtes Nachbar-Preset gesperrt!)" : "";
            if (revertedStats.TopRank > 0)
            {
                Console.WriteLine(
                    $"[TOP-WECHSEL {setupLabel} T{tachoIndex + 1}] Feedback={fb}{cliffNote} -> Wechsle auf TOP-{revertedStats.TopRank} ({updated:F2} ms | Blau: {revertedStats.BlueHits}/{revertedStats.Total} = {revertedStats.BlueRate * 100:F0}%, R={revertedStats.RedHits})!");
            }
            else
            {
                Console.WriteLine(
                    $"[AUTO-REVERT {setupLabel} T{tachoIndex + 1}] Feedback={fb}{cliffNote} -> Springe zu Best-Preset {updated:F2} ms (Blau-Quote: {revertedStats.BlueHits}/{revertedStats.Total} = {revertedStats.BlueRate * 100:F0}%)!");
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
                Console.WriteLine($"[Nach-Klick {setupLabel} T{tachoIndex + 1}] >>> ROT (ZU SPAET!) <<< {counts} -> Offset {appliedDelta:+0.00;-0.00} ms auf {updated:F2} ms{skipNote}");
                break;
            case TachoFeedback.TooEarlyGreen:
                Console.WriteLine($"[Nach-Klick {setupLabel} T{tachoIndex + 1}] >>> GRUEN (ETWAS FRUEH) <<< {counts} -> Offset {appliedDelta:+0.00;-0.00} ms auf {updated:F2} ms{skipNote}");
                break;
            case TachoFeedback.OptimalBlue:
                Console.WriteLine($"[Nach-Klick {setupLabel} T{tachoIndex + 1}] >>> BLAU (OPTIMAL!) <<< {counts} -> Offset ({updated:F2} ms) bleibt unveraendert.");
                break;
            case TachoFeedback.WayTooEarlyWhite:
                if (activeEnemy?.IsBird == true)
                {
                    string birdDir = appliedDelta > 0
                        ? "WEISS (VOGEL: NACH BLAU / ZU SPAET!)"
                        : "WEISS (VOGEL: VOR GRUEN / ZU FRUEH!)";
                    Console.WriteLine($"[Nach-Klick {setupLabel} T{tachoIndex + 1}] >>> {birdDir} <<< {counts} -> Offset {appliedDelta:+0.00;-0.00} ms auf {updated:F2} ms{skipNote}");
                }
                else
                {
                    Console.WriteLine($"[Nach-Klick {setupLabel} T{tachoIndex + 1}] >>> WEISS (VIEL ZU FRUEH!) <<< {counts} -> Offset auf Startwert {updated:F2} ms zurueckgesetzt!");
                }
                break;
            default:
                Console.WriteLine($"[Nach-Klick {setupLabel} T{tachoIndex + 1}] Keine eindeutige Farbe erkannt {counts} -> Offset bleibt {updated:F2} ms.");
                break;
        }

        return updated;
    }

    private static void TriggerClick(int tachoIndex, int totalTachos, string enemyName, TachoDetector detector, AppConfig cfg)
    {
        bool isLast = tachoIndex == totalTachos - 1;
        int targetX = isLast ? cfg.GreenButtonX : cfg.BlueButtonX;
        int targetY = isLast ? cfg.GreenButtonY : cfg.BlueButtonY;
        string btnLabel = isLast ? "Gruener Schwerter-Button" : "Blauer Schild-Button";

        Console.WriteLine(
            $"[TREFFER {enemyName} T{tachoIndex + 1}/{totalTachos}!] {detector.LastTriggerReason} v={detector.LastSpeedDegPerSec:F0}°/s (Offset={detector.LastUsedClickOffsetMs:F1}ms) -> Klicke {btnLabel}");
        NativeMethods.ClickAt(targetX, targetY, cfg.ClickHoldMs);
    }

    private static void TryAutoStart(AppConfig cfg, ref bool running)
    {
        if (cfg.IsCalibrated && !running)
        {
            running = true;
            Console.WriteLine($"[Status] AKTIV ({cfg.ActiveProfile}er-Modus) - Starte sequenziell bei Tacho 1...");
        }
    }

    private static void SwitchProfile(
        AppConfig cfg, TachoDetector[] detectors, int profile,
        ref int nextTachoIndex, ref int expectedTachoIndex, ref bool running)
    {
        cfg.ActiveProfile = profile;
        cfg.Save();
        nextTachoIndex = 0;
        expectedTachoIndex = 0;
        ResetDetectors(detectors);

        var tachos = cfg.GetActiveTachos();
        Console.WriteLine($"[Modus] Umgeschaltet auf {profile}er-Konfiguration ({tachos.Count} Tachos, sequenziell).");

        for (int i = 0; i < tachos.Count; i++)
        {
            string btn = (i == tachos.Count - 1) ? "Gruen" : "Blau";
            Console.WriteLine($"  Tacho {i + 1} ({btn}): {(tachos[i].IsSet ? $"({tachos[i].X}, {tachos[i].Y})" : "noch nicht gesetzt (mit F9 festlegen)")}");
        }

        if (!cfg.IsCalibrated && running)
        {
            running = false;
            Console.WriteLine("[Status] PAUSIERT (erst alle Positionen dieses Profils festlegen).");
        }
    }

    private static void ResetDetectors(TachoDetector[] detectors)
    {
        foreach (var d in detectors)
            d.Reset();
    }

    private static void PrintHeader(AppConfig cfg, EnemySetupStore store, bool running, bool debugMode)
    {
        var tachos = cfg.GetActiveTachos();
        Console.WriteLine("=================================================================");
        Console.WriteLine("  TachoClicker - Automatische Gegner-Erkennung & Setups");
        Console.WriteLine("=================================================================");
        Console.WriteLine("Hotkeys:");
        Console.WriteLine("  F2  : Auf 2er-Konfiguration umschalten (T1=Blau -> T2=Gruen)");
        Console.WriteLine("  F3  : Auf 3er-Konfiguration umschalten (T1=Blau -> T2=Blau -> T3=Gruen)");
        Console.WriteLine("  F9  : Naechste Tacho-Mitte speichern (zyklisch Tacho 1..N)");
        Console.WriteLine("  F10 : Blauen Schild-Button speichern (+ Auto-Start)");
        Console.WriteLine("  F5  : Position des aktiven eigenen Dinos speichern (optional, sonst automatisch ueber T1)");
        Console.WriteLine("  F6  : Gruenen Schwerter-Button speichern (+ Auto-Start)");
        Console.WriteLine("  F8  : Automatik Start / Pause (startet immer bei Tacho 1)");
        Console.WriteLine("  F7  : Debug-Ausgabe An / Aus");
        Console.WriteLine("  +/- : Zeit-Vorlauf des aktuell aktiven Tachos / Gegners +/- 2 ms");
        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine($"Aktives Profil        : {cfg.ActiveProfile}er-Konfiguration | Gespeicherte Gegner-Setups: {store.Setups.Count}");
        foreach (var s in store.Setups)
        {
            string fixedTag = s.IsFixed ? "FEST" : "AUTO";
            string visTag = s.HasVisual ? "VIS" : "NO-VIS";
            string slotDesc = string.Join(" | ", s.Tachos.Select((sl, i) =>
            {
                if (!sl.HasSignature) return $"T{i + 1}:-";
                var best = sl.GetTopPreset(1) ?? sl.GetBestStartupPreset();
                string bestTxt = best != null
                    ? (best.TopRank > 0 ? $",Top-1={best.OffsetMs:F2}ms({best.BlueHits}B/{best.Total})" : $",Best={best.OffsetMs:F2}ms({best.BlueHits}B/{best.Total})")
                    : "";
                string mTag = sl.IsMeisterTacho ? ",MEISTER" : "";
                int bad = sl.CountKnownBadPresets();
                string badTxt = bad > 0 ? $",Bad={bad}" : "";
                return $"T{i + 1}:{sl.ClickOffsetMs:F2}ms{bestTxt}{mTag}{badTxt}";
            }));
            Console.WriteLine($"  - {s.Name} [{s.DinoType}, {fixedTag}, {visTag}, {s.ProfileSize}er]: {slotDesc}");
        }
        var (dx0, dy0) = cfg.GetEffectiveDinoBoxCenter();
        Console.WriteLine($"Dino-Sichtfeld        : ({dx0}, {dy0}) [{cfg.DinoBoxWidth}x{cfg.DinoBoxHeight}] {(cfg.DinoBoxX > 0 ? "(manuell F5)" : "(auto ueber T1)")}");
        Console.WriteLine($"Blauer Schild-Button  : ({cfg.BlueButtonX}, {cfg.BlueButtonY})");
        Console.WriteLine($"Gruener Schwert-Btn   : ({cfg.GreenButtonX}, {cfg.GreenButtonY})");
        Console.WriteLine($"Status                : {(running ? "AKTIV" : "PAUSIERT")} | Debug: {(debugMode ? "AN" : "AUS")}");
        Console.WriteLine("=================================================================");
    }

    private static void PrintSessionPresetStats(EnemySetupStore store, HashSet<int> playedEnemyIds)
    {
        Console.WriteLine();
        Console.WriteLine("================ PRESET-STATISTIKEN (GESPIELTE SPIELER) ================");
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
            Console.WriteLine($"*** #{setup.Id} {setup.Name} [{setup.DinoType}] ({setup.ProfileSize}er) ***");
            for (int i = 0; i < setup.Tachos.Count; i++)
            {
                var slot = setup.Tachos[i];
                slot.RefreshTopPresets();
                int topCount = slot.OffsetHistory.Count(h => h.TopRank > 0);
                string statusTag = slot.IsMeisterTacho
                    ? "*** MEISTER-TACHO (nur Top-1/2/3 aktiv) ***"
                    : $"Top-Presets gereift: {topCount}/3";

                Console.WriteLine(
                    $"  Tacho {i + 1} | Aktuell={slot.ClickOffsetMs:F2} ms | Gesamt: {slot.BlueHits}B/{slot.GreenHits}G/{slot.RedHits}R/{slot.WhiteHits}W ({slot.TotalClicks} Klicks) | {statusTag}");

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
                    if (h.IsMature) tags.Add("MATURE");
                    if (h.IsKnownBad) tags.Add("GESPERRT");
                    if (Math.Abs(h.OffsetMs - slot.ClickOffsetMs) < 0.05) tags.Add("AKTIV");
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
