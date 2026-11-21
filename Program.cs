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
        Console.Title = "TachoClicker (F2=2er | F3=3er | F6=Gruen | F8=Start/Pause | F9=Tacho | F10=Blau)";

        // Request 1 ms Windows timer resolution for accurate polling
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
                            $"[Kalibrierung {cfg.ActiveProfile}er-Modus] Tacho {justSet}/{tachos.Count} gesetzt auf: ({pt.X}, {pt.Y}) | Naechster mit F9: Tacho {nextTachoIndex + 1}/{tachos.Count}");
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
                            ? $"[Status] AKTIV ({cfg.ActiveProfile}er-Modus) - Ueberwache Tachos..."
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
                    cfg.AdjustAllLeadAngles(1.0);
                    cfg.Save();
                    Console.WriteLine($"[Vorlauf] Alle um +1° erhoeht -> T1={cfg.GetLeadAngleForTacho(0):F1}°, T2={cfg.GetLeadAngleForTacho(1):F1}°, T3={cfg.GetLeadAngleForTacho(2):F1}°");
                }

                if (NativeMethods.IsAnyKeyJustPressed(NativeMethods.VK_OEM_MINUS, NativeMethods.VK_SUBTRACT, ref minusPrev))
                {
                    cfg.AdjustAllLeadAngles(-1.0);
                    cfg.Save();
                    Console.WriteLine($"[Vorlauf] Alle um -1° verringert -> T1={cfg.GetLeadAngleForTacho(0):F1}°, T2={cfg.GetLeadAngleForTacho(1):F1}°, T3={cfg.GetLeadAngleForTacho(2):F1}°");
                }

                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_PRIOR, ref pgUpPrev))
                {
                    cfg.AdjustStepLeadAngles(1.0);
                    cfg.Save();
                    Console.WriteLine($"[Stufen-Bonus] Erhoeht -> T1={cfg.GetLeadAngleForTacho(0):F1}°, T2={cfg.GetLeadAngleForTacho(1):F1}°, T3={cfg.GetLeadAngleForTacho(2):F1}°");
                }

                if (NativeMethods.IsKeyJustPressed(NativeMethods.VK_NEXT, ref pgDnPrev))
                {
                    cfg.AdjustStepLeadAngles(-1.0);
                    cfg.Save();
                    Console.WriteLine($"[Stufen-Bonus] Verringert -> T1={cfg.GetLeadAngleForTacho(0):F1}°, T2={cfg.GetLeadAngleForTacho(1):F1}°, T3={cfg.GetLeadAngleForTacho(2):F1}°");
                }

                var activeTachos = cfg.GetActiveTachos();
                bool anyArmed = false;

                if (running && cfg.IsCalibrated)
                {
                    for (int i = 0; i < activeTachos.Count; i++)
                    {
                        double tachoLead = cfg.GetLeadAngleForTacho(i);
                        bool triggered = detectors[i].Update(activeTachos[i].X, activeTachos[i].Y, tachoLead, cfg);

                        if (detectors[i].ConsumeFeedback(out TachoFeedback fb))
                        {
                            HandleTachoFeedback(i, fb, cfg);
                        }

                        if (detectors[i].JustArmed)
                        {
                            // Maus sofort auf den Ziel-Button vorpositionieren, damit beim Klick 0 ms Fahrweg anfallen
                            bool isLast = i == activeTachos.Count - 1;
                            int targetX = isLast ? cfg.GreenButtonX : cfg.BlueButtonX;
                            int targetY = isLast ? cfg.GreenButtonY : cfg.BlueButtonY;
                            NativeMethods.SetCursorPos(targetX, targetY);
                        }

                        if (detectors[i].State == DetectorState.Armed)
                            anyArmed = true;

                        if (triggered)
                        {
                            TriggerClick(i, activeTachos.Count, cfg);
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
                            parts[i] = $"T{i + 1}[{d.State}, G={d.LastBlueCount}, W={d.LastWhiteCount}, Rem={d.LastRemainingDeg:F0}/{cfg.GetLeadAngleForTacho(i):F0}°]";
                        }
                        Console.WriteLine(string.Join(" | ", parts));
                    }

                    Thread.Sleep(anyArmed ? 1 : Math.Max(1, cfg.PollIntervalMs));
                }
                else
                {
                    // Even when paused, refresh detector images periodically so the Live-Inspektor shows live camera previews!
                    for (int i = 0; i < activeTachos.Count; i++)
                    {
                        if (activeTachos[i].IsSet)
                        {
                            detectors[i].Update(activeTachos[i].X, activeTachos[i].Y, cfg.GetLeadAngleForTacho(i), cfg);
                            detectors[i].Reset();
                        }
                    }
                    Thread.Sleep(40);
                }

                // Skip heavy bitmap preview generation while a tacho is actively spinning (Armed) so we lose 0 ms!
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
            double tachoLead = cfg.GetLeadAngleForTacho(i);
            items.Add(new TachoOverlayItem
            {
                Index = i,
                CenterX = tachos[i].X,
                CenterY = tachos[i].Y,
                IsLast = i == tachos.Count - 1,
                State = detectors[i].State,
                BlueCount = detectors[i].LastBlueCount,
                WhiteCount = detectors[i].LastWhiteCount,
                RemainingDeg = detectors[i].LastRemainingDeg,
                TachoLeadDeg = tachoLead,
                LastFeedback = detectors[i].LastFeedback,
                Preview = tachos[i].IsSet ? detectors[i].CreateDiagnosticBitmap(tachoLead) : null
            });
        }

        return new OverlaySnapshot
        {
            Running = running,
            ActiveProfile = cfg.ActiveProfile,
            LeadAngleDeg = cfg.LeadAngleDeg,
            ExtraLeadPerTachoDeg = cfg.ExtraLeadPerTachoDeg,
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

    private static void HandleTachoFeedback(int tachoIndex, TachoFeedback fb, AppConfig cfg)
    {
        switch (fb)
        {
            case TachoFeedback.TooLateRed:
            {
                double updated = cfg.ApplyAutoCorrection(tachoIndex, +1.5);
                cfg.Save();
                Console.WriteLine($"[Auto-Tuning Tacho {tachoIndex + 1}] ROT (zu spaet) erkannt -> Vorlauf +1.5° auf {updated:F1}° erhoeht!");
                break;
            }
            case TachoFeedback.TooEarlyGreen:
            {
                double updated = cfg.ApplyAutoCorrection(tachoIndex, -0.7);
                cfg.Save();
                Console.WriteLine($"[Auto-Tuning Tacho {tachoIndex + 1}] GRUEN (etwas zu frueh) erkannt -> Vorlauf -0.7° auf {updated:F1}° gesenkt!");
                break;
            }
            case TachoFeedback.OptimalBlue:
            {
                double current = cfg.GetLeadAngleForTacho(tachoIndex);
                Console.WriteLine($"[Auto-Tuning Tacho {tachoIndex + 1}] BLAU (OPTIMAL!) erkannt -> Vorlauf {current:F1}° bleibt unveraendert.");
                break;
            }
        }
    }

    private static void TriggerClick(int tachoIndex, int totalTachos, AppConfig cfg)
    {
        bool isLast = tachoIndex == totalTachos - 1;
        int targetX = isLast ? cfg.GreenButtonX : cfg.BlueButtonX;
        int targetY = isLast ? cfg.GreenButtonY : cfg.BlueButtonY;
        string btnLabel = isLast ? "Gruener Schwerter-Button" : "Blauer Schild-Button";

        Console.WriteLine($"[TREFFER Tacho {tachoIndex + 1}/{totalTachos}!] Blaues Optimum -> Klicke {btnLabel} ({targetX}, {targetY})");
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

    private static void PrintHeader(AppConfig cfg, bool running, bool debugMode)
    {
        var tachos = cfg.GetActiveTachos();
        Console.WriteLine("=================================================================");
        Console.WriteLine("  TachoClicker - C# (.NET 8) mit Live-Inspektor");
        Console.WriteLine("=================================================================");
        Console.WriteLine("Hotkeys:");
        Console.WriteLine("  F2  : Auf 2er-Konfiguration umschalten (T1=Blau, T2=Gruen)");
        Console.WriteLine("  F3  : Auf 3er-Konfiguration umschalten (T1=Blau, T2=Blau, T3=Gruen)");
        Console.WriteLine("  F9  : Naechste Tacho-Mitte speichern (zyklisch Tacho 1..N)");
        Console.WriteLine("  F10 : Blauen Schild-Button speichern (+ Auto-Start)");
        Console.WriteLine("  F6  : Gruenen Schwerter-Button speichern (+ Auto-Start)");
        Console.WriteLine("  F8  : Automatik Start / Pause");
        Console.WriteLine("  F7  : Debug-Ausgabe An / Aus");
        Console.WriteLine("  +/- : Basis-Vorlauf (Tacho 1) um 1° vergroessern / verkleinern");
        Console.WriteLine("  BildAuf/Ab : Zusatz-Vorlauf fuer schnellere Tachos (Tacho 2/3) +/- 1°");
        Console.WriteLine("-----------------------------------------------------------------");
        Console.WriteLine($"Aktives Profil        : {cfg.ActiveProfile}er-Konfiguration | Vorlauf: {cfg.LeadAngleDeg}°");
        for (int i = 0; i < tachos.Count; i++)
        {
            string btn = (i == tachos.Count - 1) ? "Gruen" : "Blau ";
            Console.WriteLine($"  Tacho {i + 1}/{tachos.Count} [{btn}]    : {(tachos[i].IsSet ? $"({tachos[i].X}, {tachos[i].Y})" : "noch nicht gesetzt")}");
        }
        Console.WriteLine($"Blauer Schild-Button  : ({cfg.BlueButtonX}, {cfg.BlueButtonY})");
        Console.WriteLine($"Gruener Schwert-Btn   : ({cfg.GreenButtonX}, {cfg.GreenButtonY})");
        Console.WriteLine($"Status                : {(running ? "AKTIV" : "PAUSIERT")} | Debug: {(debugMode ? "AN" : "AUS")}");
        Console.WriteLine("=================================================================");
    }
}
