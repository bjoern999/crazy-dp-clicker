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
        Console.Title = "TachoClicker (10°-Hochrechnung | F2=2er | F3=3er | F6=Gruen | F8=Start | F9=Tacho | F10=Blau)";

        // Request 1 ms Windows timer resolution for accurate polling & sub-ms Stopwatch waits
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

        bool f2Prev = false;
        bool f3Prev = false;
        bool f6Prev = false;
        bool f7Prev = false;
        bool f8Prev = false;
        bool f9Prev = false;
        bool f10Prev = false;
        bool plusPrev = false;
        bool minusPrev = false;
        bool pgUpPrev = false;
        bool pgDnPrev = false;

        PrintHeader(cfg, running, debugMode);

        DateTime lastDebugPrint = DateTime.MinValue;
        DateTime lastOverlayUpdate = DateTime.MinValue;

        try
        {
            while (true)
            {
                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_F2, ref f2Prev))
                {
                    SwitchProfile(cfg, detectors, 2, ref nextTachoIndex, ref running);
                }

                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_F3, ref f3Prev))
                {
                    SwitchProfile(cfg, detectors, 3, ref nextTachoIndex, ref running);
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

                        int justSet = nextTachoIndex + 1;
                        nextTachoIndex = (nextTachoIndex + 1) % tachos.Count;

                        Console.WriteLine(
                            $"[Kalibrierung {cfg.ActiveProfile}er-Modus] Tacho {justSet}/{tachos.Count} gesetzt auf: ({pt.X}, {pt.Y}) [Zeit-Vorlauf: {tachos[justSet - 1].ClickOffsetMs:F1} ms] | Naechster mit F9: Tacho {nextTachoIndex + 1}/{tachos.Count}");
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
                        overlay?.EnsureVisibleOnTop();
                        Console.WriteLine(running
                            ? $"[Status] AKTIV ({cfg.ActiveProfile}er-Modus) - Ueberwache Tachos mit 10°-Hochrechnung..."
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
                    double val = cfg.AdjustTachoClickOffsetMs(0, +2.0);
                    cfg.Save();
                    Console.WriteLine($"[Zeit-Vorlauf Tacho 1] +2.0 ms -> T1={val:F1} ms");
                }

                if (NativeMethods.IsAnyKeyJustPressed(NativeMethods.VK_OEM_MINUS, NativeMethods.VK_SUBTRACT, ref minusPrev))
                {
                    double val = cfg.AdjustTachoClickOffsetMs(0, -2.0);
                    cfg.Save();
                    Console.WriteLine($"[Zeit-Vorlauf Tacho 1] -2.0 ms -> T1={val:F1} ms");
                }

                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_PRIOR, ref pgUpPrev))
                {
                    double val = cfg.AdjustTachoClickOffsetMs(1, +2.0);
                    cfg.Save();
                    Console.WriteLine($"[Zeit-Vorlauf Tacho 2] +2.0 ms -> T2={val:F1} ms");
                }

                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_NEXT, ref pgDnPrev))
                {
                    double val = cfg.AdjustTachoClickOffsetMs(1, -2.0);
                    cfg.Save();
                    Console.WriteLine($"[Zeit-Vorlauf Tacho 2] -2.0 ms -> T2={val:F1} ms");
                }

                var activeTachos = cfg.GetActiveTachos();
                bool anyArmed = false;

                if (running && cfg.IsCalibrated)
                {
                    for (int i = 0; i < activeTachos.Count; i++)
                    {
                        double clickOffsetMs = cfg.GetClickOffsetMsForTacho(i);
                        bool triggered = detectors[i].Update(activeTachos[i].X, activeTachos[i].Y, clickOffsetMs, cfg);

                        if (detectors[i].JustArmed)
                        {
                            // Maus sofort auf den Ziel-Button vorpositionieren
                            bool isLast = i == activeTachos.Count - 1;
                            int targetX = isLast ? cfg.GreenButtonX : cfg.BlueButtonX;
                            int targetY = isLast ? cfg.GreenButtonY : cfg.BlueButtonY;
                            NativeMethods.SetCursorPos(targetX, targetY);
                        }

                        if (detectors[i].State == DetectorState.Armed)
                            anyArmed = true;

                        if (triggered)
                        {
                            // 1. Exakt zum hochgerechneten Zeitpunkt Button betaetigen
                            TriggerClick(i, activeTachos.Count, detectors[i], cfg);

                            // 2. Exakt 200 ms nach dem Klick die Tacho-Farbe (Blau / Gruen / Rot / Weiss) messen
                            TachoFeedback fb = detectors[i].MeasureFeedbackRightAfterClick(
                                activeTachos[i].X, activeTachos[i].Y, cfg);

                            // 3. Sofort den Zeit-Vorlauf (ClickOffsetMs) dieses Tachos anpassen
                            HandleTachoFeedback(i, fb, detectors[i], cfg);

                            // 4. Live-Inspektor sofort mit +200 ms Bild & neuem Offset aktualisieren
                            if (overlay != null)
                            {
                                lastOverlayUpdate = DateTime.UtcNow;
                                overlay.UpdateSnapshot(BuildOverlaySnapshot(cfg, detectors, running));
                            }

                            anyArmed = false;
                            break;
                        }
                    }

                    if (debugMode && (DateTime.UtcNow - lastDebugPrint).TotalMilliseconds >= 200)
                    {
                        lastDebugPrint = DateTime.UtcNow;
                        var parts = new string[activeTachos.Count];
                        for (int i = 0; i < activeTachos.Count; i++)
                        {
                            var d = detectors[i];
                            parts[i] = $"T{i + 1}[{d.State}, G={d.LastBlueCount}, Rem={d.LastRemainingDeg:F0}°, n={d.LastCheckpointCount}, v={d.LastSpeedDegPerSec:F0}°/s]";
                        }
                        Console.WriteLine(string.Join(" | ", parts));
                    }

                    Thread.Sleep(anyArmed ? 1 : Math.Max(1, cfg.PollIntervalMs));
                }
                else
                {
                    for (int i = 0; i < activeTachos.Count; i++)
                    {
                        if (activeTachos[i].IsSet)
                        {
                            detectors[i].Update(activeTachos[i].X, activeTachos[i].Y, cfg.GetClickOffsetMsForTacho(i), cfg);
                            detectors[i].Reset();
                        }
                    }
                    Thread.Sleep(40);
                }

                if (overlay != null && !anyArmed && (DateTime.UtcNow - lastOverlayUpdate).TotalMilliseconds >= 80)
                {
                    lastOverlayUpdate = DateTime.UtcNow;
                    overlay.UpdateSnapshot(BuildOverlaySnapshot(cfg, detectors, running));
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

    private static OverlaySnapshot BuildOverlaySnapshot(AppConfig cfg, TachoDetector[] detectors, bool running)
    {
        var tachos = cfg.GetActiveTachos();
        var items = new List<TachoOverlayItem>(tachos.Count);
        for (int i = 0; i < tachos.Count; i++)
        {
            items.Add(new TachoOverlayItem
            {
                Index = i,
                CenterX = tachos[i].X,
                CenterY = tachos[i].Y,
                IsLast = i == tachos.Count - 1,
                State = detectors[i].State,
                GreenCount = detectors[i].LastBlueCount,
                WhiteCount = detectors[i].LastWhiteCount,
                RemainingDeg = detectors[i].LastRemainingDeg,
                ClickOffsetMs = cfg.GetClickOffsetMsForTacho(i),
                SpeedDegPerSec = detectors[i].LastSpeedDegPerSec,
                CheckpointCount = detectors[i].LastCheckpointCount,
                LastFeedback = detectors[i].LastFeedback,
                PeakRed = detectors[i].LastPeakRed,
                PeakGreen = detectors[i].LastPeakGreen,
                PeakBlue = detectors[i].LastPeakBlue,
                PeakWhite = detectors[i].LastPeakWhite,
                LastDeltaMs = detectors[i].LastCorrectionDeltaMs,
                Preview = tachos[i].IsSet ? detectors[i].CreateDiagnosticBitmap() : null
            });
        }

        return new OverlaySnapshot
        {
            Running = running,
            ActiveProfile = cfg.ActiveProfile,
            BlueButtonPreview = CaptureSmallPreview(cfg.BlueButtonX, cfg.BlueButtonY, 70),
            GreenButtonPreview = CaptureSmallPreview(cfg.GreenButtonX, cfg.GreenButtonY, 70),
            Tachos = items
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

    private static void HandleTachoFeedback(int tachoIndex, TachoFeedback fb, TachoDetector detector, AppConfig cfg)
    {
        string counts = $"(B={detector.LastPeakBlue}, G={detector.LastPeakGreen}, R={detector.LastPeakRed}, W={detector.LastPeakWhite})";
        switch (fb)
        {
            case TachoFeedback.TooLateRed:
            {
                const double deltaMs = +4.0;
                detector.LastCorrectionDeltaMs = deltaMs;
                double updated = cfg.AdjustTachoClickOffsetMs(tachoIndex, deltaMs);
                cfg.Save();
                Console.WriteLine($"[200ms-Farbe Tacho {tachoIndex + 1}] >>> ROT (ZU SPAET!) <<< {counts} -> Zeit-Vorlauf T{tachoIndex + 1} +{deltaMs:F1} ms auf {updated:F1} ms");
                break;
            }
            case TachoFeedback.TooEarlyGreen:
            {
                const double deltaMs = -1.0;
                detector.LastCorrectionDeltaMs = deltaMs;
                double updated = cfg.AdjustTachoClickOffsetMs(tachoIndex, deltaMs);
                cfg.Save();
                Console.WriteLine($"[200ms-Farbe Tacho {tachoIndex + 1}] >>> GRUEN (ETWAS FRUEH) <<< {counts} -> Zeit-Vorlauf T{tachoIndex + 1} {deltaMs:F1} ms auf {updated:F1} ms");
                break;
            }
            case TachoFeedback.OptimalBlue:
            {
                detector.LastCorrectionDeltaMs = 0.0;
                double current = cfg.GetClickOffsetMsForTacho(tachoIndex);
                Console.WriteLine($"[200ms-Farbe Tacho {tachoIndex + 1}] >>> BLAU (OPTIMAL!) <<< {counts} -> Zeit-Vorlauf T{tachoIndex + 1} ({current:F1} ms) bleibt unveraendert.");
                break;
            }
            case TachoFeedback.WayTooEarlyWhite:
            {
                detector.LastCorrectionDeltaMs = 0.0;
                double resetVal = cfg.ResetTachoClickOffsetToDefault(tachoIndex);
                cfg.Save();
                Console.WriteLine($"[200ms-Farbe Tacho {tachoIndex + 1}] >>> WEISS (VIEL ZU FRUEH!) <<< {counts} -> Zeit-Vorlauf T{tachoIndex + 1} auf Startwert {resetVal:F1} ms zurueckgesetzt!");
                break;
            }
            default:
            {
                Console.WriteLine($"[200ms-Farbe Tacho {tachoIndex + 1}] Keine eindeutige Farbe erkannt {counts}.");
                break;
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
            $"[TREFFER Tacho {tachoIndex + 1}/{totalTachos}!] v={detector.LastSpeedDegPerSec:F0}°/s (n={detector.LastCheckpointCount} Messpunkte) -> Klicke {btnLabel} ({targetX}, {targetY})");
        NativeMethods.ClickAt(targetX, targetY, cfg.ClickHoldMs);
    }

    private static void TryAutoStart(AppConfig cfg, ref bool running)
    {
        if (cfg.IsCalibrated && !running)
        {
            running = true;
            Console.WriteLine($"[Status] AKTIV ({cfg.ActiveProfile}er-Modus) - Ueberwache Tachos...");
        }
    }

    private static void SwitchProfile(AppConfig cfg, TachoDetector[] detectors, int profile, ref int nextTachoIndex, ref bool running)
    {
        cfg.ActiveProfile = profile;
        cfg.Save();
        nextTachoIndex = 0;
        ResetDetectors(detectors);

        var tachos = cfg.GetActiveTachos();
        Console.WriteLine($"[Modus] Umgeschaltet auf {profile}er-Konfiguration ({tachos.Count} Tachos).");

        for (int i = 0; i < tachos.Count; i++)
        {
            string btn = (i == tachos.Count - 1) ? "Gruen" : "Blau";
            Console.WriteLine($"  Tacho {i + 1} ({btn}) [Zeit-Vorlauf: {tachos[i].ClickOffsetMs:F1} ms]: {(tachos[i].IsSet ? $"({tachos[i].X}, {tachos[i].Y})" : "noch nicht gesetzt (mit F9 festlegen)")}");
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
        Console.WriteLine("  TachoClicker - 10°-Hochrechnung + 200ms Farb-Tuning");
        Console.WriteLine("=================================================================");
        Console.WriteLine("Hotkeys:");
        Console.WriteLine("  F2         : Auf 2er-Konfiguration umschalten (T1=Blau, T2=Gruen)");
        Console.WriteLine("  F3         : Auf 3er-Konfiguration umschalten (T1=Blau, T2=Blau, T3=Gruen)");
        Console.WriteLine("  F9         : Naechste Tacho-Mitte speichern (zyklisch Tacho 1..N)");
        Console.WriteLine("  F10        : Blauen Schild-Button speichern (+ Auto-Start)");
        Console.WriteLine("  F6         : Gruenen Schwerter-Button speichern (+ Auto-Start)");
        Console.WriteLine("  F8         : Automatik Start / Pause");
        Console.WriteLine("  F7         : Debug-Ausgabe An / Aus");
        Console.WriteLine("  +/-        : Zeit-Vorlauf Tacho 1 manuell +/- 2 ms");
        Console.WriteLine("  BildAuf/Ab : Zeit-Vorlauf Tacho 2 manuell +/- 2 ms");
        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine($"Aktives Profil        : {cfg.ActiveProfile}er-Konfiguration (Messpunkte alle {cfg.CheckpointStepDeg:F0}°)");
        for (int i = 0; i < tachos.Count; i++)
        {
            string btn = (i == tachos.Count - 1) ? "Gruen" : "Blau ";
            Console.WriteLine($"  Tacho {i + 1}/{tachos.Count} [{btn}]    : {(tachos[i].IsSet ? $"({tachos[i].X}, {tachos[i].Y})" : "noch nicht gesetzt")} | Zeit-Vorlauf: {tachos[i].ClickOffsetMs:F1} ms");
        }
        Console.WriteLine($"Blauer Schild-Button  : ({cfg.BlueButtonX}, {cfg.BlueButtonY})");
        Console.WriteLine($"Gruener Schwert-Btn   : ({cfg.GreenButtonX}, {cfg.GreenButtonY})");
        Console.WriteLine($"Status                : {(running ? "AKTIV" : "PAUSIERT")} | Debug: {(debugMode ? "AN" : "AUS")}");
        Console.WriteLine("=================================================================");
    }
}
