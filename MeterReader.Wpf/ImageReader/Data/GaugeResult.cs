using System.Collections.Generic;
using OpenCvSharp;

namespace MeterReader.Wpf.ImageReader.Data;

public class GaugeResult
{
    public string method { get; set; } = "";
    public string reading { get; set; } = "";
    public string details { get; set; } = "";
    public int tickCount { get; set; }
    public List<PointerInfo> pointers { get; set; } = new();
    public OpenCvSharp.Point center { get; set; }
    public int radius { get; set; }
    public double blackValue { get; set; }
    public double redValue { get; set; }
    public double greenValue { get; set; }
}

public class PointerInfo
{
    public string colorName { get; set; } = "";
    public Scalar drawColor { get; set; }
    public double angleDeg { get; set; }
    public double value { get; set; }
    public OpenCvSharp.Point tipPoint { get; set; }
}
