using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeterReader.Wpf.Config;

namespace MeterReader.Wpf.Cell;

public partial class MeterCell : UserControl
{
    private readonly MeterPosition _data;
    private readonly Action<MeterCell> _onSave;
    private readonly Action<MeterCell> _onCancel;
    private readonly Action<MeterCell, MeterPosition> _onEdit;
    private readonly Action<MeterCell> _onDelete;
    private readonly bool _fromConfig;

    public MeterPosition Data => _data;

    public MeterCell(MeterPosition data, Action<MeterCell> onSave, Action<MeterCell> onCancel,
                     Action<MeterCell, MeterPosition>? onEdit = null, Action<MeterCell>? onDelete = null,
                     bool fromConfig = false)
    {
        InitializeComponent();

        _data = data;
        _onSave = onSave;
        _onCancel = onCancel;
        _onEdit = onEdit ?? ((_, _) => { });
        _onDelete = onDelete ?? ((_) => { });
        _fromConfig = fromConfig;

        TxtIndex.Text = $"#{data.Index}";

        // 颜色
        if (!string.IsNullOrEmpty(data.Color))
        {
            var converter = new BrushConverter();
            ColorSwatch.Background = (Brush)converter.ConvertFromString(data.Color)!;
        }

        // 恢复已保存的仪表类型
        if (!string.IsNullOrEmpty(data.MeterType))
        {
            for (int i = 0; i < CmbMeterType.Items.Count; i++)
            {
                if (((ComboBoxItem)CmbMeterType.Items[i]).Content.ToString() == data.MeterType)
                {
                    CmbMeterType.SelectedIndex = i;
                    break;
                }
            }
        }

        UpdateDisplay();

        if (_fromConfig)
            EnterViewMode();
        else
            EnterEditMode();
    }

    public void UpdateDisplay()
    {
        TxtCoordinate.Text = $"坐标: {_data.X:F0}, {_data.Y:F0}";
        TxtSize.Text = $"宽: {_data.Width:F0}  高: {_data.Height:F0}";
        TxtMeterTypeLabel.Text = _data.MeterType;
    }

    private void EnterViewMode()
    {
        TxtMeterTypeLabel.Visibility = Visibility.Visible;
        CmbMeterType.Visibility = Visibility.Collapsed;

        BtnCellEdit.Visibility = Visibility.Visible;
        BtnCellDelete.Visibility = _fromConfig ? Visibility.Visible : Visibility.Collapsed;
        BtnCellSave.Visibility = Visibility.Collapsed;
        BtnCellCancel.Visibility = Visibility.Collapsed;
    }

    private void EnterEditMode()
    {
        TxtMeterTypeLabel.Visibility = Visibility.Collapsed;
        CmbMeterType.Visibility = Visibility.Visible;

        BtnCellEdit.Visibility = Visibility.Collapsed;
        BtnCellDelete.Visibility = Visibility.Collapsed;
        BtnCellSave.Visibility = Visibility.Visible;
        BtnCellCancel.Visibility = Visibility.Visible;
    }

    public void SetPosition(double x, double y, double w, double h)
    {
        _data.X = x;
        _data.Y = y;
        _data.Width = w;
        _data.Height = h;
        UpdateDisplay();
    }

    private void BtnCellEdit_Click(object sender, RoutedEventArgs e)
    {
        EnterEditMode();
        _onEdit(this, _data);
    }

    private void BtnCellSave_Click(object sender, RoutedEventArgs e)
    {
        _data.MeterType = ((ComboBoxItem)CmbMeterType.SelectedItem).Content.ToString()!;
        UpdateDisplay();
        _onSave(this);
    }

    private void BtnCellCancel_Click(object sender, RoutedEventArgs e)
    {
        _onCancel(this);
    }

    private void BtnCellDelete_Click(object sender, RoutedEventArgs e)
    {
        _onDelete(this);
    }
}
