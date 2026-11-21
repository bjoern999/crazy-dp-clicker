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
        Console.Title = "TachoClicker (Sequenziell | F2=2er | F3=3er | F6=Gruen | F8=Start | F9=Tacho | F10=Blau)";

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

        PrintHeader(cfg, running, debugMode);

        DateTime lastDebugPrint = DateTime.MinValue;
        DateTime lastOverlayUpdate = DateTime.MinValue;

        try
        {
            while (true)
            {
                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_F2, ref f2Prev))
                {
                    SwitchProfile(cfg, detectors, 2, ref nextTachoIndex, ref expectedTachoIndex, ref running);
                }

                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_F3, ref f3Prev))
                {
                    SwitchProfile(cfg, detectors, 3, ref nextTachoIndex, ref expectedTachoIndex, ref running);
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
                        expectedTachoIndex = justSetIndex; // Zeige im Inspektor direkt den eben kalibrierten Tacho
                        nextTachoIndex = (nextTachoIndex + 1) % tachos.Count;

                        Console.WriteLine(
                            $"[Kalibrierung {cfg.ActiveProfile}er-Modus] Tacho {justSetIndex + 1}/{tachos.Count} gesetzt auf: ({pt.X}, {pt.Y}) [Offset: {tachos[justSetIndex].ClickOffsetMs:F1} ms] | Naechster mit F9: Tacho {nextTachoIndex + 1}/{tachos.Count}");
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
                    int targetIdx = expectedTachoIndex;
                    double val = cfg.AdjustTachoClickOffsetMs(targetIdx, +2.0);
                    cfg.Save();
                    Console.WriteLine($"[Zeit-Vorlauf Tacho {targetIdx + 1}] +2.0 ms -> T{targetIdx + 1}={val:F1} ms");
                }

                if (NativeMethods.IsAnyKeyJustPressed(NativeMethods.VK_OEM_MINUS, NativeMethods.VK_SUBTRACT, ref minusPrev))
                {
                    int targetIdx = expectedTachoIndex;
                    double val = cfg.AdjustTachoClickOffsetMs(targetIdx, -2.0);
                    cfg.Save();
                    Console.WriteLine($"[Zeit-Vorlauf Tacho {targetIdx + 1}] -2.0 ms -> T{targetIdx + 1}={val:F1} ms");
                }

                var activeTachos = cfg.GetActiveTachos();
                if (expectedTachoIndex >= activeTachos.Count)
                    expectedTachoIndex = 0;

                bool isArmed = false;

                if (running && cfg.IsCalibrated)
                {
                    // Identische Logik fuer JEDEN Tacho (T1 -> T2 -> (T3) -> T1)
                    int cur = expectedTachoIndex;
                    double clickOffsetMs = cfg.GetClickOffsetMsForTacho(cur);
                    bool triggered = detectors[cur].Update(activeTachos[cur].X, activeTachos[cur].Y, clickOffsetMs, cfg);

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
                        LogTelemetryAbort(cur, activeTachos.Count, clickOffsetMs, detectors[cur]);
                    }

                    if (triggered)
                    {
                        bool isLastBtn = cur == activeTachos.Count - 1;
                        string checkpointsStr = detectors[cur].FormatCheckpointsSummary();

                        // 1. Button sofort betaetigen
                        TriggerClick(cur, activeTachos.Count, detectors[cur], cfg);
                        lastClickUtc = DateTime.UtcNow;

                        // 2. Klick-Moment sofort visualisieren (gelber Button-Blitz + Foto im Klick-Moment)
                        Bitmap? clickMomentSnap = detectors[cur].CaptureClickMomentSnapshot(
                            activeTachos[cur].X, activeTachos[cur].Y, cfg);
                        overlay?.FlashButtonClick(isLastBtn, cur + 1, clickMomentSnap);

                        // 3. NUR direkt nach dem Klick die Farbe auf den wenigen unteren Pixeln (9x9) dieses Tachos messen
                        TachoFeedback fb = detectors[cur].MeasureBottomColorAfterClick(
                            cur + 1, activeTachos[cur].X, activeTachos[cur].Y, cfg);

                        // 4. Sofort den unabhaengigen Parameter dieses Tachos anpassen
                        double newOffset = HandleTachoFeedback(cur, fb, detectors[cur], cfg);

                        // 5. Vollstaendige Telemetrie fuer Analyse in tacho_telemetry.log schreiben
                        LogTelemetryClick(cur, activeTachos.Count, clickOffsetMs, newOffset, fb, detectors[cur], checkpointsStr);

                        lastClickedTachoNumber = cur + 1;
                        lastFeedback = fb;
                        lastPatchR = detectors[cur].LastPatchRed;
                        lastPatchG = detectors[cur].LastPatchGreen;
                        lastPatchB = detectors[cur].LastPatchBlue;
                        lastPatchW = detectors[cur].LastPatchWhite;
                        lastDeltaMs = detectors[cur].LastCorrectionDeltaMs;
                        lastResultNewOffsetMs = newOffset;

                        // 4. Zum naechsten Tacho in der Reihenfolge wechseln (T1 -> T2 -> (T3) -> T1)
                        expectedTachoIndex = (cur + 1) % activeTachos.Count;
                        detectors[expectedTachoIndex].Reset();
                        isArmed = false;

                        // 5. Live-Inspektor sofort auf den naechsten aktiven Tacho umschalten + Feedback anzeigen
                        if (overlay != null)
                        {
                            lastOverlayUpdate = DateTime.UtcNow;
                            overlay.UpdateSnapshot(BuildOverlaySnapshot(
                                cfg, detectors, expectedTachoIndex, running,
                                lastClickedTachoNumber, lastFeedback,
                                lastPatchR, lastPatchG, lastPatchB, lastPatchW,
                                lastDeltaMs, lastResultNewOffsetMs));
                        }
                    }
                    else if (cur > 0 &&
                             detectors[cur].State == DetectorState.WaitingForBlueZone &&
                             (DateTime.UtcNow - lastClickUtc).TotalMilliseconds >= 3500.0)
                    {
                        // Auto-Resync: Falls der Kampf nach Tacho 1/2 vorbei war und nach >1.2 s wieder Tacho 1 startet
                        detectors[0].Reset();
                        detectors[0].Update(activeTachos[0].X, activeTachos[0].Y, cfg.GetClickOffsetMsForTacho(0), cfg);
                        if (detectors[0].State == DetectorState.Armed)
                        {
                            Console.WriteLine("[Sequenz] Neuer Durchgang erkannt -> wechsle zurueck auf Tacho 1.");
                            expectedTachoIndex = 0;
                            isArmed = true;
                            NativeMethods.SetCursorPos(cfg.BlueButtonX, cfg.BlueButtonY);
                        }
                    }

                    if (debugMode && (DateTime.UtcNow - lastDebugPrint).TotalMilliseconds >= 200)
                    {
                        lastDebugPrint = DateTime.UtcNow;
                        var d = detectors[expectedTachoIndex];
                        Console.WriteLine(
                            $"Aktiv T{expectedTachoIndex + 1}/{activeTachos.Count} | State={d.State} | G={d.LastGreenCount} | Rem={d.LastRemainingDeg:F0}° | n={d.LastCheckpointCount} | v={d.LastSpeedDegPerSec:F0}°/s");
                    }

                    Thread.Sleep(isArmed ? 1 : Math.Max(1, cfg.PollIntervalMs));
                }
                else
                {
                    int cur = expectedTachoIndex;
                    if (activeTachos[cur].IsSet)
                    {
                        detectors[cur].Update(activeTachos[cur].X, activeTachos[cur].Y, cfg.GetClickOffsetMsForTacho(cur), cfg);
                        detectors[cur].Reset();
                    }
                    Thread.Sleep(40);
                }

                if (overlay != null && !isArmed && (DateTime.UtcNow - lastOverlayUpdate).TotalMilliseconds >= 80)
                {
                    lastOverlayUpdate = DateTime.UtcNow;
                    overlay.UpdateSnapshot(BuildOverlaySnapshot(
                        cfg, detectors, expectedTachoIndex, running,
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

    private static OverlaySnapshot BuildOverlaySnapshot(
        AppConfig cfg, TachoDetector[] detectors, int activeIdx, bool running,
        int lastClickedTachoNumber, TachoFeedback lastFeedback,
        int patchR, int patchG, int patchB, int patchW,
        double lastDeltaMs, double lastResultNewOffsetMs)
    {
        var tachos = cfg.GetActiveTachos();
        var t = tachos[activeIdx];
        var d = detectors[activeIdx];

        string summary = string.Join(" | ", tachos.Select((tp, idx) => $"T{idx + 1}={tp.ClickOffsetMs:F1}ms"));

        return new OverlaySnapshot
        {
            Running = running,
            ActiveProfile = cfg.ActiveProfile,
            ActiveTachoIndex = activeIdx,
            TotalTachos = tachos.Count,
            AllOffsetsSummary = summary,
            CenterX = t.X,
            CenterY = t.Y,
            IsLastTacho = activeIdx == tachos.Count - 1,
            State = d.State,
            GreenCount = d.LastGreenCount,
            WhiteCount = d.LastWhiteCount,
            ClickOffsetMs = t.ClickOffsetMs,
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
        TachoFeedback fb, TachoDetector d, string checkpointsStr)
    {
        try
        {
            string line =
                $"{DateTime.Now:HH:mm:ss.fff} | CLICK | T{tachoIndex + 1}/{totalTachos} | " +
                $"Reason={d.LastTriggerReason} | Speed={d.LastSpeedDegPerSec:F1}°/s Accel={d.LastAccelDegPerSec2:F0}°/s2 | R2={d.LastRegressionR2:F4} | " +
                $"RemAtTrig={d.LastRemDegAtTrigger:F1}° | WaitMs={d.LastWaitMsAtTrigger:F1} | " +
                $"Offset={usedOffsetMs:F1}->{newOffsetMs:F1}ms | " +
                $"GreenEnd={d.LastTargetEndDeg:F0}° Width={d.LastGreenWidthDeg:F0}° GreenPx={d.LastInitialGreenCount} BlueSliverPx={d.LastBlueSliverCount} | " +
                $"Feedback={fb} (RGB={d.LastAvgR},{d.LastAvgG},{d.LastAvgB} B={d.LastPatchBlue} G={d.LastPatchGreen} R={d.LastPatchRed} W={d.LastPatchWhite}) | " +
                $"Checkpoints={checkpointsStr}";
            File.AppendAllText(GetTelemetryLogPath(), line + Environment.NewLine);
        }
        catch { }
    }

    private static void LogTelemetryAbort(int tachoIndex, int totalTachos, double usedOffsetMs, TachoDetector d)
    {
        try
        {
            string line =
                $"{DateTime.Now:HH:mm:ss.fff} | ABORT_NO_CLICK | T{tachoIndex + 1}/{totalTachos} | " +
                $"Reason={d.LastAbortReason} | LastRem={d.LastAbortMinRemDeg:F1}° | BottomRedPx={d.LastAbortRedOnBottom} | " +
                $"Offset={usedOffsetMs:F1}ms | GreenEnd={d.LastTargetEndDeg:F0}° Width={d.LastGreenWidthDeg:F0}° GreenPx={d.LastInitialGreenCount} BlueSliverPx={d.LastBlueSliverCount} | " +
                $"Checkpoints={d.LastAbortCheckpointSummary}";
            File.AppendAllText(GetTelemetryLogPath(), line + Environment.NewLine);
            Console.WriteLine($"[ABBRUCH OHNE KLICK Tacho {tachoIndex + 1}] {d.LastAbortReason} (LastRem={d.LastAbortMinRemDeg:F0}°, BottomRed={d.LastAbortRedOnBottom})");
        }
        catch { }
    }

    private static double HandleTachoFeedback(int tachoIndex, TachoFeedback fb, TachoDetector detector, AppConfig cfg)
    {
        string counts = $"(Spot=({detector.LastPatchScreenX},{detector.LastPatchScreenY}) RGB=({detector.LastAvgR},{detector.LastAvgG},{detector.LastAvgB}) | B={detector.LastPatchBlue} G={detector.LastPatchGreen} R={detector.LastPatchRed} W={detector.LastPatchWhite})";
        switch (fb)
        {
            case TachoFeedback.TooLateRed:
            {
                const double deltaMs = +2.0;
                detector.LastCorrectionDeltaMs = deltaMs;
                double updated = cfg.AdjustTachoClickOffsetMs(tachoIndex, deltaMs);
                cfg.Save();
                Console.WriteLine($"[Nach-Klick Tacho {tachoIndex + 1}] >>> ROT (ZU SPAET!) <<< {counts} -> Offset T{tachoIndex + 1} +{deltaMs:F1} ms auf {updated:F1} ms");
                return updated;
            }
            case TachoFeedback.TooEarlyGreen:
            {
                const double deltaMs = -0.5;
                detector.LastCorrectionDeltaMs = deltaMs;
                double updated = cfg.AdjustTachoClickOffsetMs(tachoIndex, deltaMs);
                cfg.Save();
                Console.WriteLine($"[Nach-Klick Tacho {tachoIndex + 1}] >>> GRUEN (ETWAS FRUEH) <<< {counts} -> Offset T{tachoIndex + 1} {deltaMs:F1} ms auf {updated:F1} ms");
                return updated;
            }
            case TachoFeedback.OptimalBlue:
            {
                detector.LastCorrectionDeltaMs = 0.0;
                double current = cfg.GetClickOffsetMsForTacho(tachoIndex);
                Console.WriteLine($"[Nach-Klick Tacho {tachoIndex + 1}] >>> BLAU (OPTIMAL!) <<< {counts} -> Offset T{tachoIndex + 1} ({current:F1} ms) bleibt unveraendert.");
                return current;
            }
            case TachoFeedback.WayTooEarlyWhite:
            {
                detector.LastCorrectionDeltaMs = 0.0;
                double resetVal = cfg.ResetTachoClickOffsetToDefault(tachoIndex);
                cfg.Save();
                Console.WriteLine($"[Nach-Klick Tacho {tachoIndex + 1}] >>> WEISS (VIEL ZU FRUEH!) <<< {counts} -> Offset T{tachoIndex + 1} auf Startwert {resetVal:F1} ms zurueckgesetzt!");
                return resetVal;
            }
            default:
            {
                detector.LastCorrectionDeltaMs = 0.0;
                double current = cfg.GetClickOffsetMsForTacho(tachoIndex);
                Console.WriteLine($"[Nach-Klick Tacho {tachoIndex + 1}] Keine eindeutige Farbe erkannt {counts} -> Offset bleibt {current:F1} ms.");
                return current;
            }
        }
    }

    private static void TriggerClick(int tachoIndex, int totalTachos, TachoDetector detector, AppConfig cfg)
    {
        bool isLast = tachoIndex == totalTachos - 1;
        int targetX = isLast ? cfg.GreenButtonX : cfg.BlueButtonX;
        int targetY = isLast ? cfg.GreenButtonY : cfg.BlueButtonY;
        string btnLabel = isLast ? "Gruener Schwerter-Button" : "Blauer Schild-Button";

        Console.WriteLine(
            $"[TREFFER Tacho {tachoIndex + 1}/{totalTachos}!] v={detector.LastSpeedDegPerSec:F0}°/s (n={detector.LastCheckpointCount}) -> Klicke {btnLabel} ({targetX}, {targetY})");
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
            Console.WriteLine($"  Tacho {i + 1} ({btn}) [Offset: {tachos[i].ClickOffsetMs:F1} ms]: {(tachos[i].IsSet ? $"({tachos[i].X}, {tachos[i].Y})" : "noch nicht gesetzt (mit F9 festlegen)")}");
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

    private static void PrintHeader(AppConfig cfg, bool running, bool debugMode)
    {
        var tachos = cfg.GetActiveTachos();
        Console.WriteLine("=================================================================");
        Console.WriteLine("  TachoClicker - Sequenziell (nur aktiver Tacho) + 9x9 Unten-Check");
        Console.WriteLine("=================================================================");
        Console.WriteLine("Hotkeys:");
        Console.WriteLine("  F2         : Auf 2er-Konfiguration umschalten (T1=Blau -> T2=Gruen)");
        Console.WriteLine("  F3         : Auf 3er-Konfiguration umschalten (T1=Blau -> T2=Blau -> T3=Gruen)");
        Console.WriteLine("  F9         : Naechste Tacho-Mitte speichern (zyklisch Tacho 1..N)");
        Console.WriteLine("  F10        : Blauen Schild-Button speichern (+ Auto-Start)");
        Console.WriteLine("  F6         : Gruenen Schwerter-Button speichern (+ Auto-Start)");
        Console.WriteLine("  F8         : Automatik Start / Pause (startet immer bei Tacho 1)");
        Console.WriteLine("  F7         : Debug-Ausgabe An / Aus");
        Console.WriteLine("  +/-        : Zeit-Vorlauf des aktuell aktiven Tachos +/- 2 ms");
        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine($"Aktives Profil        : {cfg.ActiveProfile}er-Konfiguration (Messpunkte alle {cfg.CheckpointStepDeg:F0}°)");
        for (int i = 0; i < tachos.Count; i++)
        {
            string btn = (i == tachos.Count - 1) ? "Gruen" : "Blau ";
            Console.WriteLine($"  Tacho {i + 1}/{tachos.Count} [{btn}]    : {(tachos[i].IsSet ? $"({tachos[i].X}, {tachos[i].Y})" : "noch nicht gesetzt")} | Offset: {tachos[i].ClickOffsetMs:F1} ms");
        }
        Console.WriteLine($"Blauer Schild-Button  : ({cfg.BlueButtonX}, {cfg.BlueButtonY})");
        Console.WriteLine($"Gruener Schwert-Btn   : ({cfg.GreenButtonX}, {cfg.GreenButtonY})");
        Console.WriteLine($"Status                : {(running ? "AKTIV" : "PAUSIERT")} | Debug: {(debugMode ? "AN" : "AUS")}");
        Console.WriteLine("=================================================================");
    }
}
