using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MeterReader.Wpf.Config;
using MeterReader.Wpf.Core;
using MeterReader.Wpf.ImageReader;
using MeterReader.Wpf.ImageReader.Data;
using MeterReader.Wpf.Models;
using Microsoft.Win32;
using OpenCvSharp;
using Window = System.Windows.Window;
using Rect = OpenCvSharp.Rect;

namespace MeterReader.Wpf;

public partial class MainWindow : Window
{
    private readonly List<MeterDisplayItem> _meterItems = new();
    private string? _currentFolder;
    private List<MeterPosition> _meterPositions = new();
    private readonly Dictionary<int, string> _latestImages = new();

    public MainWindow()
    {
        InitializeComponent();

        Width = SystemParameters.PrimaryScreenWidth * 0.8;
        Height = SystemParameters.PrimaryScreenHeight * 0.8;

        LoadConfig();
    }

    private static string ConfigDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
    private static string ConfigPath => Path.Combine(ConfigDir, "meterconfig.json");

    private void LoadConfig()
    {
        if (!File.Exists(ConfigPath))
        {
            // 配置文件不存在，清空列表
            _meterPositions.Clear();
            _meterItems.Clear();
            _latestImages.Clear();
            MeterListView.ItemsSource = null;
            return;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<MeterConfig>(json);
            if (config?.Meters == null)
            {
                // 配置文件为空或无仪表数据，清空列表
                _meterPositions.Clear();
                _meterItems.Clear();
                _latestImages.Clear();
                MeterListView.ItemsSource = null;
                return;
            }

            _meterPositions = config.Meters;

            _meterItems.Clear();
            foreach (var m in config.Meters.OrderBy(m => m.Index))
            {
                _meterItems.Add(new MeterDisplayItem
                {
                    Index = m.Index,
                    MeterType = m.MeterType,
                    MinValue = m.MinValue,
                    MaxValue = m.MaxValue,
                    Color = m.Color,
                    Reading = "",
                    RecognitionTime = ""
                });
            }

            MeterListView.ItemsSource = _meterItems;

            // 读取当天最后识别结果
            LoadLatestReadings();

            // 重新扫描已生成的图片
            _latestImages.Clear();
            ScanLatestImages();
        }
        catch { }
    }

    private void LoadLatestReadings()
    {
        string imagesRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Images");
        if (!Directory.Exists(imagesRoot)) return;

        string todayFile = $"ReaderResult_{DateTime.Now:yyyyMMdd}.json";

        foreach (var item in _meterItems)
        {
            string meterDir = Path.Combine(imagesRoot, $"Meters_{item.Index}");
            string jsonPath = Path.Combine(meterDir, todayFile);
            if (!File.Exists(jsonPath)) continue;

            try
            {
                var json = File.ReadAllText(jsonPath);
                var file = JsonSerializer.Deserialize<ReaderDailyFile>(json);
                if (file?.ReaderResult == null || file.ReaderResult.Count == 0) continue;

                // 取最后一次识别结果
                var last = file.ReaderResult.Last();
                item.Reading = last.Black > 0 ? last.Black.ToString("F2") : "--";
                item.BlackValue = last.Black;
                item.RedValue = last.Red;
                item.GreenValue = last.Green;
                item.RecognitionTime = last.Time;
            }
            catch { }
        }
    }

    private void ScanLatestImages()
    {
        string imagesRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Images");
        if (!Directory.Exists(imagesRoot)) return;

        foreach (var item in _meterItems)
        {
            string meterDir = Path.Combine(imagesRoot, $"Meters_{item.Index}");
            if (!Directory.Exists(meterDir)) continue;

            var pngFiles = Directory.GetFiles(meterDir, "*.png");
            if (pngFiles.Length > 0)
            {
                // 按文件名排序取最新
                var latest = pngFiles.OrderByDescending(f => f).First();
                _latestImages[item.Index] = latest;
            }
        }
    }

    private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择图片文件夹",
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
        {
            TxtFolderPath.Text = dialog.FolderName;
            _currentFolder = dialog.FolderName;
            LoadConfig();
        }
    }

    private void BtnRecognize_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFolder) || !Directory.Exists(_currentFolder))
        {
            MessageBox.Show("请先选择包含图片的文件夹。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_meterPositions.Count == 0)
        {
            MessageBox.Show("配置文件中没有仪表数据。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var imageFiles = Directory.GetFiles(_currentFolder)
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (imageFiles.Count == 0)
        {
            MessageBox.Show("所选文件夹中没有找到图片文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string imagesRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Images");
        var updatedItems = new HashSet<int>();
        int resultId = 0;

        foreach (var imageFile in imageFiles)
        {
            Mat? src = null;
            try
            {
                src = Cv2.ImRead(imageFile, ImreadModes.Color);
                if (src.Empty()) continue;

                int imgW = src.Width, imgH = src.Height;
                resultId++;

                foreach (var meter in _meterPositions)
                {
                    int x = (int)meter.X;
                    int y = (int)meter.Y;
                    int w = (int)meter.Width;
                    int h = (int)meter.Height;

                    x = Math.Max(0, x);
                    y = Math.Max(0, y);
                    if (x + w > imgW) w = imgW - x;
                    if (y + h > imgH) h = imgH - y;
                    if (w <= 0 || h <= 0) continue;

                    using var crop = new Mat(src, new Rect(x, y, w, h));
                    string meterDir = Path.Combine(imagesRoot, $"Meters_{meter.Index}");
                    Directory.CreateDirectory(meterDir);

                    string nowStr = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    string cropFileName = $"{nowStr}.png";
                    string savePath = Path.Combine(meterDir, cropFileName);
                    Cv2.ImWrite(savePath, crop);

                    _latestImages[meter.Index] = savePath;
                    updatedItems.Add(meter.Index);

                    // 仪表识别
                    var item = _meterItems.FirstOrDefault(i => i.Index == meter.Index);
                    if (item != null && meter.MeterType == "压力表")
                    {
                        string nowTime = DateTime.Now.ToString("yyyyMMdd-HH:mm:ss");
                        try
                        {
                            var gaugeResult = PressureRecognizer.Recognize(crop.Clone(), meterDir, $"{nowStr}_meter{meter.Index}");

                            item.RedValue = gaugeResult.redValue;
                            item.GreenValue = gaugeResult.greenValue;
                            item.BlackValue = gaugeResult.blackValue;
                            item.Reading = gaugeResult.reading;
                            item.RecognitionTime = nowTime;

                            // 保存到按天 JSON
                            SaveGaugeResult(meter.Index, gaugeResult);
                        }
                        catch
                        {
                            item.Reading = "--";
                            item.RecognitionTime = "--";
                        }
                    }
                    else if (item != null && meter.MeterType == "流量计")
                    {
                        string nowTime = DateTime.Now.ToString("yyyyMMdd-HH:mm:ss");
                        try
                        {
                            // 生成增强图（灰度CLAHE）供流量计边缘检测
                            using var gray = new Mat();
                            Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
                            using var clahe = Cv2.CreateCLAHE(2.0, new OpenCvSharp.Size(8, 8));
                            using var enhanced = new Mat();
                            clahe.Apply(gray, enhanced);

                            var flowResult = FlowMeterRecognizer.Recognize(crop.Clone(), enhanced, meterDir, $"{nowStr}_meter{meter.Index}");

                            item.Reading = flowResult.reading;
                            item.RecognitionTime = nowTime;

                            // 保存到按天 JSON（流量计只有 Black 值）
                            SaveFlowResult(meter.Index, flowResult);
                        }
                        catch
                        {
                            item.Reading = "--";
                            item.RecognitionTime = "--";
                        }
                    }
                    else if (item != null)
                    {
                        item.RecognitionTime = "--";
                    }
                }
            }
            catch { /* 跳过处理失败的图片 */ }
            finally { src?.Dispose(); }
        }

        MeterListView.ItemsSource = null;
        MeterListView.ItemsSource = _meterItems;

        if (updatedItems.Count > 0)
        {
            var first = _meterItems.First(i => updatedItems.Contains(i.Index));
            MeterListView.SelectedItem = first;
        }

        MessageBox.Show($"识别完成，共处理 {imageFiles.Count} 张图片，{updatedItems.Count} 个仪表。",
            "完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void SaveGaugeResult(int instrumentNo, GaugeResult result)
    {
        string imagesRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Images");
        string meterDir = Path.Combine(imagesRoot, $"Meters_{instrumentNo}");
        string jsonPath = Path.Combine(meterDir, $"ReaderResult_{DateTime.Now:yyyyMMdd}.json");

        JsonHelper.AppendResult(jsonPath, instrumentNo, result);
    }

    private static void SaveFlowResult(int instrumentNo, FlowResult result)
    {
        // 将流量计结果转为 GaugeResult 格式统一存储（只有 Black 值）
        var gauge = new GaugeResult
        {
            reading = result.reading,
            blackValue = double.TryParse(result.reading, out var v) ? v : 0
        };
        SaveGaugeResult(instrumentNo, gauge);
    }

    private void MeterListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MeterListView.SelectedItem is not MeterDisplayItem item) return;

        if (_latestImages.TryGetValue(item.Index, out var imagePath) && File.Exists(imagePath))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(imagePath);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            MeterImage.Source = bitmap;
            MeterImage.Visibility = Visibility.Visible;
            TxtNoImage.Visibility = Visibility.Collapsed;
        }
        else
        {
            MeterImage.Visibility = Visibility.Collapsed;
            TxtNoImage.Visibility = Visibility.Visible;
            TxtNoImage.Text = $"仪表 #{item.Index} 暂无图片";
        }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        LoadConfig();
        MeterImage.Source = null;
        MeterImage.Visibility = Visibility.Collapsed;
        TxtNoImage.Visibility = Visibility.Visible;
        TxtNoImage.Text = "请选择左侧仪表查看图片";
    }

    private void CkbDebugImages_Changed(object sender, RoutedEventArgs e)
    {
        bool on = CkbDebugImages.IsChecked == true;
        PressureRecognizer.SaveDebugImages = on;
        FlowMeterRecognizer.SaveDebugImages = on;
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow();
        settingsWindow.Owner = this;
        settingsWindow.Show();
    }
}
