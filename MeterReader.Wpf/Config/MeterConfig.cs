using System.Collections.Generic;

namespace MeterReader.Wpf.Config;

public class MeterConfig
{
    public string? TemplateImage { get; set; }
    public int LastMeterIndex { get; set; }
    public List<MeterPosition> Meters { get; set; } = new();
}
