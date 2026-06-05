using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mystral.Services;
using DrawingIcon = System.Drawing.Icon;

namespace Mystral.Views;

public partial class AppDialogWindow : Window
{
    private AppDialogWindow(string title, string message, ImageSource icon, IReadOnlyList<DialogButtonSpec> buttons)
    {
        InitializeComponent();
        Title = title;
        Icon = IconImageSource.LoadBestFitFrame("res/ico.ico", 16);
        DialogTitleText.Text = title;
        DialogDescriptionText.Text = message;
        DialogIcon.Source = icon;
        ConfigureButtons(buttons);
    }

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.Cancel;

    public static MessageBoxResult ShowInformation(Window owner, string title, string message)
    {
        return ShowDialog(owner, title, message, FromSystemIcon(System.Drawing.SystemIcons.Information), ContinueButtons());
    }

    public static MessageBoxResult ShowWarning(Window owner, string title, string message)
    {
        return ShowDialog(owner, title, message, FromSystemIcon(System.Drawing.SystemIcons.Warning), ContinueButtons());
    }

    public static MessageBoxResult ShowQuestion(Window owner, string title, string message)
    {
        return ShowDialog(
            owner,
            title,
            message,
            FromSystemIcon(System.Drawing.SystemIcons.Question),
            [
                new DialogButtonSpec("Yes", MessageBoxResult.Yes, IsDefault: true, IsCancel: false),
                new DialogButtonSpec("No", MessageBoxResult.No, IsDefault: false, IsCancel: false),
                new DialogButtonSpec("Cancel", MessageBoxResult.Cancel, IsDefault: false, IsCancel: true)
            ]);
    }

    private static MessageBoxResult ShowDialog(Window owner, string title, string message, ImageSource icon, IReadOnlyList<DialogButtonSpec> buttons)
    {
        var dialog = new AppDialogWindow(title, message, icon, buttons);
        bool originalTopmost = false;
        bool hasTopmostOwner = owner != null && owner.Topmost;

        if (owner != null && owner.IsVisible)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            if (hasTopmostOwner)
            {
                originalTopmost = owner.Topmost;
                owner.Topmost = false;
            }
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();

        if (hasTopmostOwner && owner != null)
        {
            owner.Topmost = originalTopmost;
        }

        return dialog.Result;
    }

    private static IReadOnlyList<DialogButtonSpec> ContinueButtons()
    {
        return [new DialogButtonSpec("Continue", MessageBoxResult.OK, IsDefault: true, IsCancel: true)];
    }

    private void ConfigureButtons(IReadOnlyList<DialogButtonSpec> buttons)
    {
        ButtonPanel.Children.Clear();
        foreach (var spec in buttons)
        {
            var button = new Button
            {
                Content = spec.Caption,
                Foreground = System.Windows.Media.Brushes.Black,
                Height = 23,
                MinWidth = 72,
                Margin = new Thickness(ButtonPanel.Children.Count == 0 ? 0 : 10, 0, 0, 0),
                IsDefault = spec.IsDefault,
                IsCancel = spec.IsCancel
            };
            button.Click += (_, _) =>
            {
                Result = spec.Result;
                Close();
            };
            ButtonPanel.Children.Add(button);
        }
    }

    private static ImageSource FromSystemIcon(DrawingIcon icon)
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(32, 32));
        source.Freeze();
        return source;
    }

    private sealed record DialogButtonSpec(string Caption, MessageBoxResult Result, bool IsDefault, bool IsCancel);
}
