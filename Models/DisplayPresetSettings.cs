namespace VNotch.Models;

public sealed class DisplayPresetSettings
{
    public string Name { get; set; } = string.Empty;

    public Dictionary<string, DisplayPresetMonitorSettings> Monitors { get; set; } =
        new(StringComparer.Ordinal);

    public DisplayPresetSettings Clone()
    {
        var clone = new DisplayPresetSettings { Name = Name };
        foreach (var (monitorId, values) in Monitors)
            clone.Monitors[monitorId] = values.Clone();
        return clone;
    }
}

public sealed class DisplayPresetMonitorSettings
{
    public double Brightness { get; set; }
    public double Contrast { get; set; }

    public DisplayPresetMonitorSettings Clone() => new()
    {
        Brightness = Brightness,
        Contrast = Contrast
    };
}
