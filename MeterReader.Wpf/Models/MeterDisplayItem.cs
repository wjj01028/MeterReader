using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MeterReader.Wpf.Models;

/// <summary>仪表列表展示项</summary>
public class MeterDisplayItem : INotifyPropertyChanged
{
    private string _reading = "";
    private string _recognitionTime = "";
    private bool _isNormal = true;

    public int Index { get; set; }
    public string MeterType { get; set; } = "";
    public double MinValue { get; set; }
    public double MaxValue { get; set; } = 100;
    public string Color { get; set; } = "";
    public double RedValue { get; set; } = double.NaN;
    public double GreenValue { get; set; } = double.NaN;
    public double BlackValue { get; set; } = double.NaN;

    /// <summary>正常值区间文本</summary>
    public string NormalRange => $"{MinValue:F0} - {MaxValue:F0}";

    /// <summary>读数（识别结果）</summary>
    public string Reading
    {
        get => _reading;
        set { _reading = value; OnPropertyChanged(); UpdateIsNormal(); }
    }

    /// <summary>识别时间</summary>
    public string RecognitionTime
    {
        get => _recognitionTime;
        set { _recognitionTime = value; OnPropertyChanged(); }
    }

    /// <summary>是否在正常范围内</summary>
    public bool IsNormal
    {
        get => _isNormal;
        private set { _isNormal = value; OnPropertyChanged(); }
    }

    private void UpdateIsNormal()
    {
        if (string.IsNullOrEmpty(_reading))
        {
            IsNormal = true;
            return;
        }
        if (double.TryParse(_reading, out var value))
            IsNormal = value >= MinValue && value <= MaxValue;
        else
            IsNormal = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
