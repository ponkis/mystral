using System.Windows;
using System.Windows.Media;

namespace Mystral.Views;

public partial class ThemeColorPickerWindow : Window
{
    public ThemeColorPickerWindow(Color initialColor)
    {
        InitializeComponent();
        ThemeColorPicker.SelectedColor = Color.FromRgb(
            initialColor.R,
            initialColor.G,
            initialColor.B);
        ThemeColorPicker.ColorChanged += (_, _) =>
            SelectedColorChanged?.Invoke(this, SelectedColor);
    }

    internal event EventHandler<Color>? SelectedColorChanged;

    public Color SelectedColor
    {
        get
        {
            var selected = ThemeColorPicker.SelectedColor;
            return Color.FromRgb(selected.R, selected.G, selected.B);
        }
    }

    private void UseColorButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
