namespace MeterReader.Wpf;

public class MeterPosition
{
    public int Index { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string MeterType { get; set; } = "流量计";
    public string Color { get; set; } = "";
    public double MinValue { get; set; }
    public double MaxValue { get; set; } = 100;
}
