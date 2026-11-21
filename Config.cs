using System.Text.Json;

namespace TachoClicker;

internal sealed class TachoPoint
{
    public int X { get; set; }
    public int Y { get; set; }
    public double? LeadAngleDeg { get; set; }

    public bool IsSet => X > 0 && Y > 0;
}

internal sealed class AppConfig
{
    // 2 = 2er-Konfiguration, 3 = 3er-Konfiguration
    public int ActiveProfile { get; set; } = 2;

    public List<TachoPoint> Profile2 { get; set; } = new()
    {
        new TachoPoint(),
        new TachoPoint()
    };

    public List<TachoPoint> Profile3 { get; set; } = new()
    {
        new TachoPoint(),
        new TachoPoint(),
        new TachoPoint()
    };

    public int TachoBoxSize { get; set; } = 180;

    public int BlueButtonX { get; set; } = 0;
    public int BlueButtonY { get; set; } = 0;

    public int GreenButtonX { get; set; } = 0;
    public int GreenButtonY { get; set; } = 0;

    public int PollIntervalMs { get; set; } = 2;
    public int ClickHoldMs { get; set; } = 15;
    public int CooldownMs { get; set; } = 900;

    // Minimum green pixels required to consider the tacho green zone active
    public int MinBluePixels { get; set; } = 12;

    // Base trigger angle before the end of the green zone (for Tacho 1)
    public double LeadAngleDeg { get; set; } = 10.0;

    // Extra lead angle added per subsequent faster tacho (Tacho 2 = +1x, Tacho 3 = +2x)
    public double ExtraLeadPerTachoDeg { get; set; } = 6.0;

    public double GetLeadAngleForTacho(int index)
    {
        var tachos = GetActiveTachos();
        if (index >= 0 && index < tachos.Count)
        {
            if (!tachos[index].LeadAngleDeg.HasValue)
            {
                tachos[index].LeadAngleDeg = Math.Round(LeadAngleDeg + (index * ExtraLeadPerTachoDeg), 1);
            }
            return tachos[index].LeadAngleDeg!.Value;
        }
        return LeadAngleDeg + (index * ExtraLeadPerTachoDeg);
    }

    public void AdjustAllLeadAngles(double delta)
    {
        LeadAngleDeg = Math.Clamp(Math.Round(LeadAngleDeg + delta, 1), -15.0, 55.0);
        foreach (var t in Profile2)
        {
            if (t.LeadAngleDeg.HasValue)
                t.LeadAngleDeg = Math.Clamp(Math.Round(t.LeadAngleDeg.Value + delta, 1), -15.0, 55.0);
        }
        foreach (var t in Profile3)
        {
            if (t.LeadAngleDeg.HasValue)
                t.LeadAngleDeg = Math.Clamp(Math.Round(t.LeadAngleDeg.Value + delta, 1), -15.0, 55.0);
        }
    }

    public void AdjustStepLeadAngles(double deltaStep)
    {
        ExtraLeadPerTachoDeg = Math.Clamp(Math.Round(ExtraLeadPerTachoDeg + deltaStep, 1), 0.0, 35.0);
        for (int i = 1; i < Profile2.Count; i++)
        {
            if (Profile2[i].LeadAngleDeg is double val)
                Profile2[i].LeadAngleDeg = Math.Clamp(Math.Round(val + i * deltaStep, 1), -15.0, 55.0);
        }
        for (int i = 1; i < Profile3.Count; i++)
        {
            if (Profile3[i].LeadAngleDeg is double val)
                Profile3[i].LeadAngleDeg = Math.Clamp(Math.Round(val + i * deltaStep, 1), -15.0, 55.0);
        }
    }

    public double ApplyAutoCorrection(int index, double delta)
    {
        double current = GetLeadAngleForTacho(index);
        double updated = Math.Clamp(Math.Round(current + delta, 1), -15.0, 55.0);
        var tachos = GetActiveTachos();
        if (index >= 0 && index < tachos.Count)
        {
            tachos[index].LeadAngleDeg = updated;
        }
        if (index == 0)
        {
            LeadAngleDeg = updated;
        }
        return updated;
    }

    // Fallback: trigger if at least this many previously-blue pixels turn white
    public int TriggerOverlapPixels { get; set; } = 1;

    public List<TachoPoint> GetActiveTachos()
    {
        var list = ActiveProfile == 3 ? Profile3 : Profile2;
        int expected = ActiveProfile == 3 ? 3 : 2;
        while (list.Count < expected)
            list.Add(new TachoPoint());
        return list;
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
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, options));
    }
}
