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
        Console.Title = "TachoClicker (Sequenziell + 10°-Hochrechnung | F2=2er | F3=3er | F6=Gruen | F8=Start | F9=Tacho | F10=Blau)";

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

        // Sequenzielle Erkennung: Tacho 2 erscheint erst, wenn Tacho 1 vorueber ist!
        int expectedTachoIndex = 0;
        DateTime lastClickUtc = DateTime.MinValue;

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
                if (expectedTachoIndex >= activeTachos.Count)
                    expectedTachoIndex = 0;

                bool anyArmed = false;

                if (running && cfg.IsCalibrated)
                {
                    // 1. Nicht-blockierende Nach-Klick-Farbmessung (30..165 ms) fuer bereits geklickte Tachos ausfuehren
                    for (int i = 0; i < activeTachos.Count; i++)
                    {
                        if (i == expectedTachoIndex) continue;
                        if (detectors[i].State == DetectorState.AwaitFeedback || detectors[i].State == DetectorState.Cooldown)
                        {
                            detectors[i].PollPostClickFeedback(activeTachos[i].X, activeTachos[i].Y, cfg);
                            if (detectors[i].ConsumeFeedback(out TachoFeedback fb))
                            {
                                HandleTachoFeedback(i, fb, detectors[i], cfg);
                                if (overlay != null)
                                {
                                    lastOverlayUpdate = DateTime.UtcNow;
                                    overlay.UpdateSnapshot(BuildOverlaySnapshot(cfg, detectors, expectedTachoIndex, running));
                                }
                            }
                        }
                    }

                    // 2. Aktuell erwarteten Tacho in strenger Reihenfolge (Tacho 1 -> Tacho 2 -> Tacho 3 -> Tacho 1) scannen
                    int cur = expectedTachoIndex;
                    double clickOffsetMs = cfg.GetClickOffsetMsForTacho(cur);
                    bool triggered = detectors[cur].Update(activeTachos[cur].X, activeTachos[cur].Y, clickOffsetMs, cfg);

                    if (detectors[cur].ConsumeFeedback(out TachoFeedback curFb))
                    {
                        HandleTachoFeedback(cur, curFb, detectors[cur], cfg);
                    }

                    if (detectors[cur].JustArmed)
                    {
                        // Maus sofort auf den Ziel-Button dieses Tachos vorpositionieren
                        bool isLast = cur == activeTachos.Count - 1;
                        int targetX = isLast ? cfg.GreenButtonX : cfg.BlueButtonX;
                        int targetY = isLast ? cfg.GreenButtonY : cfg.BlueButtonY;
                        NativeMethods.SetCursorPos(targetX, targetY);
                    }

                    if (detectors[cur].State == DetectorState.Armed)
                        anyArmed = true;

                    if (triggered)
                    {
                        // Klick sofort ausfuehren
                        TriggerClick(cur, activeTachos.Count, detectors[cur], cfg);

                        // Nach-Klick-Farbmessung (30..165 ms) nicht-blockierend starten
                        detectors[cur].StartPostClickFeedback(cfg);
                        lastClickUtc = DateTime.UtcNow;

                        // Sofort zum naechsten Tacho in der Sequenz wechseln (T1 -> T2 -> (T3) -> T1), ohne Tacho 2 zu blockieren!
                        expectedTachoIndex = (cur + 1) % activeTachos.Count;
                        detectors[expectedTachoIndex].Reset();
                        anyArmed = false;
                    }
                    else if (cur > 0 &&
                             detectors[cur].State == DetectorState.WaitingForBlueZone &&
                             detectors[0].State == DetectorState.WaitingForBlueZone &&
                             (DateTime.UtcNow - lastClickUtc).TotalMilliseconds >= 1200.0)
                    {
                        // Auto-Resync: Falls ein Kampf vorzeitig endet und nach >1.2 s direkt wieder Tacho 1 erscheint
                        detectors[0].Update(activeTachos[0].X, activeTachos[0].Y, cfg.GetClickOffsetMsForTacho(0), cfg);
                        if (detectors[0].State == DetectorState.Armed)
                        {
                            Console.WriteLine("[Sequenz] Neuer Durchgang erkannt -> springe zurueck auf Tacho 1.");
                            expectedTachoIndex = 0;
                            anyArmed = true;
                            NativeMethods.SetCursorPos(cfg.BlueButtonX, cfg.BlueButtonY);
                        }
                    }

                    if (debugMode && (DateTime.UtcNow - lastDebugPrint).TotalMilliseconds >= 200)
                    {
                        lastDebugPrint = DateTime.UtcNow;
                        var d = detectors[expectedTachoIndex];
                        Console.WriteLine(
                            $"Erwarte T{expectedTachoIndex + 1}/{activeTachos.Count} | State={d.State} | G={d.LastBlueCount} | Rem={d.LastRemainingDeg:F0}° | n={d.LastCheckpointCount} | v={d.LastSpeedDegPerSec:F0}°/s");
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
                    overlay.UpdateSnapshot(BuildOverlaySnapshot(cfg, detectors, expectedTachoIndex, running));
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

    private static OverlaySnapshot BuildOverlaySnapshot(AppConfig cfg, TachoDetector[] detectors, int expectedIdx, bool running)
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
            ExpectedTachoIndex = expectedIdx,
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
                Console.WriteLine($"[Nach-Klick-Farbe Tacho {tachoIndex + 1}] >>> ROT (ZU SPAET!) <<< {counts} -> Zeit-Vorlauf T{tachoIndex + 1} +{deltaMs:F1} ms auf {updated:F1} ms");
                break;
            }
            case TachoFeedback.TooEarlyGreen:
            {
                const double deltaMs = -1.0;
                detector.LastCorrectionDeltaMs = deltaMs;
                double updated = cfg.AdjustTachoClickOffsetMs(tachoIndex, deltaMs);
                cfg.Save();
                Console.WriteLine($"[Nach-Klick-Farbe Tacho {tachoIndex + 1}] >>> GRUEN (ETWAS FRUEH) <<< {counts} -> Zeit-Vorlauf T{tachoIndex + 1} {deltaMs:F1} ms auf {updated:F1} ms");
                break;
            }
            case TachoFeedback.OptimalBlue:
            {
                detector.LastCorrectionDeltaMs = 0.0;
                double current = cfg.GetClickOffsetMsForTacho(tachoIndex);
                Console.WriteLine($"[Nach-Klick-Farbe Tacho {tachoIndex + 1}] >>> BLAU (OPTIMAL!) <<< {counts} -> Zeit-Vorlauf T{tachoIndex + 1} ({current:F1} ms) bleibt unveraendert.");
                break;
            }
            case TachoFeedback.WayTooEarlyWhite:
            {
                detector.LastCorrectionDeltaMs = 0.0;
                double resetVal = cfg.ResetTachoClickOffsetToDefault(tachoIndex);
                cfg.Save();
                Console.WriteLine($"[Nach-Klick-Farbe Tacho {tachoIndex + 1}] >>> WEISS (VIEL ZU FRUEH!) <<< {counts} -> Zeit-Vorlauf T{tachoIndex + 1} auf Startwert {resetVal:F1} ms zurueckgesetzt!");
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
        Console.WriteLine("  TachoClicker - Sequenziell + 10°-Hochrechnung");
        Console.WriteLine("=================================================================");
        Console.WriteLine("Hotkeys:");
        Console.WriteLine("  F2         : Auf 2er-Konfiguration umschalten (T1=Blau -> T2=Gruen)");
        Console.WriteLine("  F3         : Auf 3er-Konfiguration umschalten (T1=Blau -> T2=Blau -> T3=Gruen)");
        Console.WriteLine("  F9         : Naechste Tacho-Mitte speichern (zyklisch Tacho 1..N)");
        Console.WriteLine("  F10        : Blauen Schild-Button speichern (+ Auto-Start)");
        Console.WriteLine("  F6         : Gruenen Schwerter-Button speichern (+ Auto-Start)");
        Console.WriteLine("  F8         : Automatik Start / Pause (startet immer bei Tacho 1)");
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
