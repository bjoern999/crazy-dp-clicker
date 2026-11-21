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

        // Aktuell erkannter Gegner & Fingerabdruecke der laufenden Runde
        EnemySetup? activeEnemy = null;
        bool activeEnemyIsNew = false;
        var roundFingerprints = new Dictionary<int, TachoFingerprint>();

        // Letztes Feedback fuer die Anzeige im Live-Inspektor
        int lastClickedTachoNumber = 0;
        TachoFeedback lastFeedback = TachoFeedback.None;
        int lastPatchR = 0, lastPatchG = 0, lastPatchB = 0, lastPatchW = 0;
        double lastDeltaMs = 0;
        double lastResultNewOffsetMs = 0;

        bool f2Prev = false;
        bool f3Prev = false;
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
                    roundFingerprints.Clear();
                }

                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_F3, ref f3Prev))
                {
                    SwitchProfile(cfg, detectors, 3, ref nextTachoIndex, ref expectedTachoIndex, ref running);
                    activeEnemy = null;
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
                        overlay?.EnsureVisibleOnTop();
                        Console.WriteLine(running
                            ? $"[Status] AKTIV ({cfg.ActiveProfile}er-Modus) - Starte sequenziell bei Tacho 1..."
                            : "[Status] PAUSIERT.");
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
                    AdjustOffsetManually(expectedTachoIndex, +2.0, cfg, enemyStore, activeEnemy);
                }

                if (NativeMethods.IsAnyKeyJustPressed(NativeMethods.VK_OEM_MINUS, NativeMethods.VK_SUBTRACT, ref minusPrev))
                {
                    AdjustOffsetManually(expectedTachoIndex, -2.0, cfg, enemyStore, activeEnemy);
                }

                var activeTachos = cfg.GetActiveTachos();
                if (expectedTachoIndex >= activeTachos.Count)
                    expectedTachoIndex = 0;

                bool isArmed = false;

                if (running && cfg.IsCalibrated)
                {
                    int cur = expectedTachoIndex;
                    double defaultOffsetMs = GetEffectiveOffsetMs(cur, cfg, activeEnemy);

                    bool triggered = detectors[cur].Update(
                        activeTachos[cur].X,
                        activeTachos[cur].Y,
                        defaultOffsetMs,
                        cfg,
                        fp =>
                        {
                            if (cur == 0)
                                roundFingerprints.Clear();

                            roundFingerprints[cur] = fp;

                            var (matched, isNew, dist) = enemyStore.MatchOrCreate(
                                cfg.ActiveProfile,
                                cur,
                                fp,
                                cur > 0 ? activeEnemy : null,
                                roundFingerprints,
                                cfg.GetClickOffsetMsForTacho(cur));

                            bool changedEnemy = activeEnemy == null || activeEnemy.Id != matched.Id || isNew;
                            activeEnemy = matched;
                            activeEnemyIsNew = isNew;

                            double enemyOffset = matched.Tachos[cur].ClickOffsetMs;

                            if (changedEnemy || cur == 0)
                            {
                                string tag = isNew ? "NEU ANGELEGT" : $"ERKANNT (Dist={dist:F2})";
                                Console.WriteLine(
                                    $"[Gegner-Setup] {matched.Name} [{tag}] | T{cur + 1}: v={fp.EndSpeedDegPerSec:F0}°/s, a={fp.AccelDegPerSec2:F0}°/s², G={fp.GreenWidthDeg:F0}°, B={fp.BlueWidthDeg:F0}° -> lade Offset {enemyOffset:F1} ms");
                            }

                            return enemyOffset;
                        });

                    if (detectors[cur].JustArmed)
                    {
                        bool isLast = cur == activeTachos.Count - 1;
                        int targetX = isLast ? cfg.GreenButtonX : cfg.BlueButtonX;
                        int targetY = isLast ? cfg.GreenButtonY : cfg.BlueButtonY;
                        NativeMethods.SetCursorPos(targetX, targetY);
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
                        Bitmap? clickMomentSnap = detectors[cur].CaptureClickMomentSnapshot(
                            activeTachos[cur].X, activeTachos[cur].Y, cfg);
                        overlay?.FlashButtonClick(isLastBtn, cur + 1, clickMomentSnap);

                        // 3. Direkt nach dem Klick die Farbe auf den unteren 9x9 Pixeln messen
                        TachoFeedback fb = detectors[cur].MeasureBottomColorAfterClick(
                            cur + 1, activeTachos[cur].X, activeTachos[cur].Y, cfg);

                        // 4. Sofort den Offset des erkannten Gegner-Setups (und Fallback-Config) anpassen
                        double newOffset = HandleTachoFeedback(cur, fb, detectors[cur], cfg, enemyStore, activeEnemy);

                        // 5. Vollstaendige Telemetrie schreiben
                        LogTelemetryClick(cur, activeTachos.Count, usedOffsetMs, newOffset, activeEnemy?.Name ?? "-", fb, detectors[cur], checkpointsStr);

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
                                lastDeltaMs, lastResultNewOffsetMs));
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
                        lastDeltaMs, lastResultNewOffsetMs));
                }
            }
        }
        finally
        {
            if (overlay != null && overlay.IsHandleCreated && !overlay.IsDisposed)
            {
                overlay.BeginInvoke(() => overlay.Close());
            }
            foreach (var d in detectors)
                d.Dispose();
        }
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
        double lastDeltaMs, double lastResultNewOffsetMs)
    {
        var tachos = cfg.GetActiveTachos();
        var t = tachos[activeIdx];
        var d = detectors[activeIdx];

        string summary = string.Join(" | ",
            Enumerable.Range(0, tachos.Count).Select(idx => $"T{idx + 1}={GetEffectiveOffsetMs(idx, cfg, activeEnemy):F1}ms"));

        return new OverlaySnapshot
        {
            Running = running,
            ActiveProfile = cfg.ActiveProfile,
            ActiveTachoIndex = activeIdx,
            TotalTachos = tachos.Count,
            AllOffsetsSummary = summary,
            EnemySetupName = activeEnemy?.Name ?? "",
            EnemySetupIsNew = activeEnemyIsNew,
            EnemyBlueHits = activeEnemy?.TotalBlueHits ?? 0,
            EnemyTotalClicks = activeEnemy?.TotalClicks ?? 0,
            TotalSavedEnemies = enemyStore.Setups.Count,
            CenterX = t.X,
            CenterY = t.Y,
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
            GreenButtonPreview = CaptureSmallPreview(cfg.GreenButtonX, cfg.GreenButtonY, 70)
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
        string enemyName, TachoFeedback fb, TachoDetector d, string checkpointsStr)
    {
        try
        {
            string line =
                $"{DateTime.Now:HH:mm:ss.fff} | CLICK | Setup={enemyName} | T{tachoIndex + 1}/{totalTachos} | " +
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
        int tachoIndex, TachoFeedback fb, TachoDetector detector,
        AppConfig cfg, EnemySetupStore enemyStore, EnemySetup? activeEnemy)
    {
        string counts = $"(Spot=({detector.LastPatchScreenX},{detector.LastPatchScreenY}) RGB=({detector.LastAvgR},{detector.LastAvgG},{detector.LastAvgB}) | B={detector.LastPatchBlue} G={detector.LastPatchGreen} R={detector.LastPatchRed} W={detector.LastPatchWhite})";
        string setupLabel = activeEnemy != null ? activeEnemy.Name : "Standard";

        double deltaMs = fb switch
        {
            TachoFeedback.TooLateRed => +2.0,
            TachoFeedback.TooEarlyGreen => -0.5,
            _ => 0.0
        };
        detector.LastCorrectionDeltaMs = deltaMs;

        double updated;
        if (activeEnemy != null && activeEnemy.ProfileSize == cfg.ActiveProfile)
        {
            activeEnemy.EnsureSlots();
            var slot = activeEnemy.Tachos[tachoIndex];
            if (fb != TachoFeedback.None)
            {
                slot.TotalClicks++;
                if (fb == TachoFeedback.OptimalBlue) slot.BlueHits++;
            }

            if (fb == TachoFeedback.WayTooEarlyWhite)
                slot.ClickOffsetMs = slot.DefaultClickOffsetMs > 0 ? slot.DefaultClickOffsetMs : 41.0;
            else if (deltaMs != 0.0)
                slot.ClickOffsetMs = Math.Clamp(Math.Round(slot.ClickOffsetMs + deltaMs, 2), 0.0, 120.0);

            updated = slot.ClickOffsetMs;
            enemyStore.Save();
        }
        else
        {
            if (fb == TachoFeedback.WayTooEarlyWhite)
                updated = cfg.ResetTachoClickOffsetToDefault(tachoIndex);
            else if (deltaMs != 0.0)
                updated = cfg.AdjustTachoClickOffsetMs(tachoIndex, deltaMs);
            else
                updated = cfg.GetClickOffsetMsForTacho(tachoIndex);

            cfg.Save();
        }

        switch (fb)
        {
            case TachoFeedback.TooLateRed:
                Console.WriteLine($"[Nach-Klick {setupLabel} T{tachoIndex + 1}] >>> ROT (ZU SPAET!) <<< {counts} -> Offset +{deltaMs:F1} ms auf {updated:F1} ms");
                break;
            case TachoFeedback.TooEarlyGreen:
                Console.WriteLine($"[Nach-Klick {setupLabel} T{tachoIndex + 1}] >>> GRUEN (ETWAS FRUEH) <<< {counts} -> Offset {deltaMs:F1} ms auf {updated:F1} ms");
                break;
            case TachoFeedback.OptimalBlue:
                Console.WriteLine($"[Nach-Klick {setupLabel} T{tachoIndex + 1}] >>> BLAU (OPTIMAL!) <<< {counts} -> Offset ({updated:F1} ms) bleibt unveraendert.");
                break;
            case TachoFeedback.WayTooEarlyWhite:
                Console.WriteLine($"[Nach-Klick {setupLabel} T{tachoIndex + 1}] >>> WEISS (VIEL ZU FRUEH!) <<< {counts} -> Offset auf Startwert {updated:F1} ms zurueckgesetzt!");
                break;
            default:
                Console.WriteLine($"[Nach-Klick {setupLabel} T{tachoIndex + 1}] Keine eindeutige Farbe erkannt {counts} -> Offset bleibt {updated:F1} ms.");
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
        Console.WriteLine("  F6  : Gruenen Schwerter-Button speichern (+ Auto-Start)");
        Console.WriteLine("  F8  : Automatik Start / Pause (startet immer bei Tacho 1)");
        Console.WriteLine("  F7  : Debug-Ausgabe An / Aus");
        Console.WriteLine("  +/- : Zeit-Vorlauf des aktuell aktiven Tachos / Gegners +/- 2 ms");
        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine($"Aktives Profil        : {cfg.ActiveProfile}er-Konfiguration | Gespeicherte Gegner-Setups: {store.Setups.Count}");
        foreach (var s in store.Setups)
        {
            string slotDesc = string.Join(" | ", s.Tachos.Select((sl, i) =>
                sl.HasSignature ? $"T{i + 1}:{sl.ClickOffsetMs:F1}ms(v={sl.EndSpeedDegPerSec:F0},G={sl.GreenWidthDeg:F0}°)" : $"T{i + 1}:-"));
            Console.WriteLine($"  - {s.Name} ({s.ProfileSize}er): {slotDesc}");
        }
        Console.WriteLine($"Blauer Schild-Button  : ({cfg.BlueButtonX}, {cfg.BlueButtonY})");
        Console.WriteLine($"Gruener Schwert-Btn   : ({cfg.GreenButtonX}, {cfg.GreenButtonY})");
        Console.WriteLine($"Status                : {(running ? "AKTIV" : "PAUSIERT")} | Debug: {(debugMode ? "AN" : "AUS")}");
        Console.WriteLine("=================================================================");
    }
}
