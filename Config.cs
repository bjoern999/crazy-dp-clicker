using System.Text.Json;

namespace TachoClicker;

internal sealed class TachoPoint
{
    public int X { get; set; }
    public int Y { get; set; }

    // Unabhaengiger Zeit-Vorlauf in Millisekunden (kompensiert Display- & Klick-Latenz)
    public double ClickOffsetMs { get; set; } = 25.0;
    public double DefaultClickOffsetMs { get; set; } = 25.0;

    public bool IsSet => X > 0 && Y > 0;
}

internal sealed class AppConfig
{
    // 2 = 2er-Konfiguration, 3 = 3er-Konfiguration
    public int ActiveProfile { get; set; } = 2;

    public List<TachoPoint> Profile2 { get; set; } = new()
    {
        new TachoPoint { ClickOffsetMs = 25.0, DefaultClickOffsetMs = 25.0 },
        new TachoPoint { ClickOffsetMs = 25.0, DefaultClickOffsetMs = 25.0 }
    };

    public List<TachoPoint> Profile3 { get; set; } = new()
    {
        new TachoPoint { ClickOffsetMs = 25.0, DefaultClickOffsetMs = 25.0 },
        new TachoPoint { ClickOffsetMs = 25.0, DefaultClickOffsetMs = 25.0 },
        new TachoPoint { ClickOffsetMs = 25.0, DefaultClickOffsetMs = 25.0 }
    };

    public int TachoBoxSize { get; set; } = 180;

    public int BlueButtonX { get; set; } = 0;
    public int BlueButtonY { get; set; } = 0;

    public int GreenButtonX { get; set; } = 0;
    public int GreenButtonY { get; set; } = 0;

    public int PollIntervalMs { get; set; } = 2;
    public int ClickHoldMs { get; set; } = 15;
    public int CooldownMs { get; set; } = 700;

    // Abstand der Winkel-Messpunkte fuer die Geschwindigkeits-Hochrechnung
    public double CheckpointStepDeg { get; set; } = 10.0;

    // Minimum green pixels required to consider the tacho green zone active (low for narrow green zones)
    public int MinGreenPixels { get; set; } = 6;

    public List<TachoPoint> GetActiveTachos()
    {
        var list = ActiveProfile == 3 ? Profile3 : Profile2;
        int expected = ActiveProfile == 3 ? 3 : 2;
        while (list.Count < expected)
        {
            list.Add(new TachoPoint { ClickOffsetMs = 25.0, DefaultClickOffsetMs = 25.0 });
        }
        return list;
    }

    public double GetClickOffsetMsForTacho(int index)
    {
        var tachos = GetActiveTachos();
        if (index >= 0 && index < tachos.Count)
            return tachos[index].ClickOffsetMs;
        return 25.0;
    }

    public double AdjustTachoClickOffsetMs(int index, double deltaMs)
    {
        var tachos = GetActiveTachos();
        if (index >= 0 && index < tachos.Count)
        {
            tachos[index].ClickOffsetMs = Math.Clamp(Math.Round(tachos[index].ClickOffsetMs + deltaMs, 2), -40.0, 150.0);
            return tachos[index].ClickOffsetMs;
        }
        return 25.0;
    }

    public double ResetTachoClickOffsetToDefault(int index)
    {
        var tachos = GetActiveTachos();
        if (index >= 0 && index < tachos.Count)
        {
            double def = tachos[index].DefaultClickOffsetMs > 0 ? tachos[index].DefaultClickOffsetMs : 25.0;
            tachos[index].ClickOffsetMs = def;
            return def;
        }
        return 25.0;
    }

    public bool IsCalibrated
    {
        get
        {
            if (BlueButtonX <= 0 || BlueButtonY <= 0 || GreenButtonX <= 0 || GreenButtonY <= 0)
                return false;

            var tachos = GetActiveTachos();
            return tachos.All(t => t.IsSet);
        }
    }

    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "config.json");

    public static AppConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new AppConfig();

        try
        {
            string json = File.ReadAllText(ConfigPath);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            if (cfg.ActiveProfile != 2 && cfg.ActiveProfile != 3)
                cfg.ActiveProfile = 2;
            _ = cfg.GetActiveTachos();
            return cfg;
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(this, options);
        File.WriteAllText(ConfigPath, json);

        try
        {
            string projectRootConfig = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "config.json"));
            if (File.Exists(projectRootConfig))
            {
                File.WriteAllText(projectRootConfig, json);
            }
        }
        catch
        {
            // Ignore if running standalone outside project directory
        }
    }
}
