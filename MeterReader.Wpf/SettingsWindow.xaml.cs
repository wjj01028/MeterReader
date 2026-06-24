using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using MeterReader.Wpf.Cell;
using MeterReader.Wpf.Config;
using Microsoft.Win32;

namespace MeterReader.Wpf;

public partial class SettingsWindow : Window
{
    private readonly List<MeterCell> _cells = new();
    private int _nextIndex = 1;
    private MeterCell? _pendingCell;
    private string? _templateImagePath;
    private int _colorIndex;

    // 预设高对比度颜色（10种），用完后自动随机生成
    private static readonly string[] PresetColors = new[]
    {
        "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#FF00FF",
        "#00FFFF", "#FF6600", "#FF0066", "#00CC44", "#CC00FF"
    };
    private static readonly Random _rng = new();

    private string NextColor()
    {
        if (_colorIndex < PresetColors.Length)
            return PresetColors[_colorIndex++];

        // 预设用完后随机生成高饱和颜色
        byte r = (byte)(_rng.Next(0, 2) * 255);  // 0 or 255
        byte g = (byte)(_rng.Next(0, 2) * 255);
        byte b = (byte)(_rng.Next(0, 2) * 255);

        // 确保不全黑
        if (r + g + b < 128)
        {
            int pick = _rng.Next(3);
            if (pick == 0) r = 255;
            else if (pick == 1) g = 255;
            else b = 255;
        }

        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static string ConfigDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
    private static string ConfigPath => Path.Combine(ConfigDir, "meterconfig.json");
    private static string ImagesDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Images");

    public SettingsWindow()
    {
        InitializeComponent();

        Width = SystemParameters.PrimaryScreenWidth * 0.8;
        Height = SystemParameters.PrimaryScreenHeight * 0.8;

        LoadConfig();
        UpdateBrowseButton();
    }

    private void UpdateBrowseButton()
    {
        var hasImage = !string.IsNullOrEmpty(_templateImagePath);
        BtnBrowseOrReset.Content = hasImage ? "重置" : "浏览";
    }

    private void LoadConfig()
    {
        if (!File.Exists(ConfigPath)) return;

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<MeterConfig>(json);
            if (config == null) return;

            if (!string.IsNullOrEmpty(config.TemplateImage))
            {
                var imagePath = Path.Combine(ConfigDir, config.TemplateImage);
                if (File.Exists(imagePath))
                {
                    _templateImagePath = imagePath;
                    TxtImagePath.Text = imagePath;
                    ImageSelector.LoadImage(imagePath);
                }
                else
                {
                    _templateImagePath = null;
                    TxtImagePath.Text = "未选择文件";
                }
            }
            else
            {
                _templateImagePath = null;
                TxtImagePath.Text = "未选择文件";
            }

            _cells.Clear();
            MeterCellPanel.Children.Clear();

            if (config.Meters != null)
            {
                foreach (var m in config.Meters)
                {
                    var cell = new MeterCell(m, OnCellSave, OnCellCancel, OnCellEdit, OnCellDelete, fromConfig: true);
                    _cells.Add(cell);
                    MeterCellPanel.Children.Add(cell);

                    if (m.Index >= _nextIndex)
                        _nextIndex = m.Index + 1;
                }
            }

            // 使用配置中保存的最后编号（一次性编号）
            if (config.LastMeterIndex >= _nextIndex)
                _nextIndex = config.LastMeterIndex + 1;

            if (config.Meters != null && config.Meters.Count > 0 && _templateImagePath != null)
            {
                ImageSelector.RenderMeterOverlays(config.Meters);
            }
        }
        catch { }
    }

    private void SaveConfig(string? templateImageFileName = null)
    {
        Directory.CreateDirectory(ConfigDir);

        var meters = new List<MeterPosition>();
        foreach (var cell in _cells)
            meters.Add(cell.Data);

        var config = new MeterConfig
        {
            TemplateImage = templateImageFileName,
            LastMeterIndex = _nextIndex > 1 ? _nextIndex - 1 : 0,
            Meters = meters
        };

        if (string.IsNullOrEmpty(config.TemplateImage) && File.Exists(ConfigPath))
        {
            try
            {
                var existingJson = File.ReadAllText(ConfigPath);
                var existing = JsonSerializer.Deserialize<MeterConfig>(existingJson);
                if (existing != null)
                    config.TemplateImage = existing.TemplateImage;
            }
            catch { }
        }

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        File.WriteAllText(ConfigPath, json);
    }

    private void BtnBrowseOrReset_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_templateImagePath))
        {
            var result = MessageBox.Show(
                "确定要重置配置吗？将清除所有仪表数据和模板图片。",
                "确认重置",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            DeleteConfigImage();

            if (File.Exists(ConfigPath))
                File.Delete(ConfigPath);

            // 清空 Images 文件夹
            DeleteImagesFolder();

            _templateImagePath = null;
            TxtImagePath.Text = "未选择文件";
            _cells.Clear();
            MeterCellPanel.Children.Clear();
            _nextIndex = 1;
            _pendingCell = null;
            _colorIndex = 0;

            ImageSelector.DisplayImage.Source = null;
            ImageSelector.Placeholder.Visibility = Visibility.Visible;
            ImageSelector.SelectionRect.Visibility = Visibility.Collapsed;
            ImageSelector.OverlayCanvas.Children.Clear();

            UpdateBrowseButton();
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择模板图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            _templateImagePath = dialog.FileName;
            TxtImagePath.Text = dialog.FileName;
            ImageSelector.LoadImage(dialog.FileName);

            var fileName = Path.GetFileName(dialog.FileName);
            var destPath = Path.Combine(ConfigDir, fileName);
            File.Copy(dialog.FileName, destPath, overwrite: true);

            SaveConfig(fileName);
            RenderOverlaysFromConfig();
            UpdateBrowseButton();
        }
    }

    private void RenderOverlaysFromConfig()
    {
        if (!File.Exists(ConfigPath)) return;
        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<MeterConfig>(json);
            if (config?.Meters != null && config.Meters.Count > 0)
            {
                ImageSelector.RenderMeterOverlays(config.Meters);
            }
        }
        catch { }
    }

    private void DeleteConfigImage()
    {
        if (!File.Exists(ConfigPath)) return;
        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<MeterConfig>(json);
            if (config != null && !string.IsNullOrEmpty(config.TemplateImage))
            {
                var imagePath = Path.Combine(ConfigDir, config.TemplateImage);
                if (File.Exists(imagePath))
                    File.Delete(imagePath);
            }
        }
        catch { }
    }

    private static void DeleteImagesFolder()
    {
        try
        {
            if (Directory.Exists(ImagesDir))
                Directory.Delete(ImagesDir, recursive: true);
        }
        catch { }
    }

    private void BtnAddMeter_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_templateImagePath))
        {
            MessageBox.Show("请先选择模板图片。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var color = NextColor();
        var position = new MeterPosition
        {
            Index = _nextIndex++,
            MeterType = "流量计",
            Color = color
        };

        // 设置框选矩形颜色
        ImageSelector.SetSelectionColor(color);

        var cell = new MeterCell(position, OnCellSave, OnCellCancel, OnCellEdit, OnCellDelete, fromConfig: false);
        _cells.Add(cell);
        _pendingCell = cell;
        MeterCellPanel.Children.Insert(0, cell);
        ImageSelector.ClearSelection();
    }

    private void OnCellEdit(MeterCell cell, MeterPosition data)
    {
        _pendingCell = cell;

        // 使用该仪表已有的颜色
        var color = string.IsNullOrEmpty(data.Color) ? "#0044CC" : data.Color;
        ImageSelector.SetSelectionColor(color);
        ImageSelector.ShowSelectionRect(data.X, data.Y, data.Width, data.Height);
    }

    private void ImageSelector_RegionSelected(double x, double y, double width, double height)
    {
        if (_pendingCell == null) return;

        if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(width) || double.IsNaN(height))
            return;
        if (width <= 0 || height <= 0)
            return;

        _pendingCell.SetPosition(x, y, width, height);
    }

    private void OnCellSave(MeterCell cell)
    {
        if (_pendingCell == cell)
            _pendingCell = null;

        SaveConfig();
        RefreshList();
        RenderOverlaysFromConfig();
    }

    private void OnCellCancel(MeterCell cell)
    {
        if (_pendingCell == cell)
            _pendingCell = null;

        ClearCellList();
        LoadConfig();
    }

    private void OnCellDelete(MeterCell cell)
    {
        var result = MessageBox.Show(
            $"确定要删除仪表 #{cell.Data.Index} 吗？",
            "确认删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        _cells.Remove(cell);
        MeterCellPanel.Children.Remove(cell);

        // 同时从配置中删除
        SaveConfig();
        RenderOverlaysFromConfig();
    }

    private void ClearCellList()
    {
        _cells.Clear();
        MeterCellPanel.Children.Clear();
    }

    private void RefreshList()
    {
        _cells.Clear();
        MeterCellPanel.Children.Clear();

        if (!File.Exists(ConfigPath)) return;

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<MeterConfig>(json);
            if (config?.Meters == null) return;

            foreach (var m in config.Meters)
            {
                var cell = new MeterCell(m, OnCellSave, OnCellCancel, OnCellEdit, OnCellDelete, fromConfig: true);
                _cells.Add(cell);
                MeterCellPanel.Children.Add(cell);

                if (m.Index >= _nextIndex)
                    _nextIndex = m.Index + 1;
            }
        }
        catch { }
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
