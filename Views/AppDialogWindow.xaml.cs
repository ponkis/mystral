using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Mystral.Configuration;
using Mystral.Services;
using DrawingIcon = System.Drawing.Icon;

namespace Mystral.Views;

public partial class AppDialogWindow : Window
{
    private AppDialogWindow(string title, string message, ImageSource icon, IReadOnlyList<DialogButtonSpec> buttons)
        : this(title, icon, buttons)
    {
        DialogDescriptionText.Text = message;
    }

    private AppDialogWindow(string title, ImageSource icon, IReadOnlyList<DialogButtonSpec> buttons)
    {
        InitializeComponent();
        Title = title;
        Icon = IconImageSource.LoadBestFitFrame("res/ico.ico", 16);
        DialogTitleText.Text = title;
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

    public static MessageBoxResult ShowError(Window owner, string title, string message)
    {
        return ShowDialog(owner, title, message, FromSystemIcon(System.Drawing.SystemIcons.Error), ContinueButtons());
    }

    public static MessageBoxResult ShowAbout(Window owner)
    {
        var dialog = new AppDialogWindow(
            $"About {AppMetadata.Name}",
            IconImageSource.LoadBestFitFrame("res/ico.ico", 32),
            OkButtons());

        dialog.AboutStampImage.Visibility = Visibility.Visible;
        dialog.DialogTitleText.Text = AppMetadata.Name;
        dialog.DialogDescriptionText.Inlines.Clear();
        dialog.DialogDescriptionText.Inlines.Add(new Run($"Version {AppMetadata.Version}"));
        dialog.DialogDescriptionText.Inlines.Add(new LineBreak());
        dialog.DialogDescriptionText.Inlines.Add(new LineBreak());
        dialog.DialogDescriptionText.Inlines.Add(new Run("Made with love by "));
        var link = new Hyperlink(new Run("ponkis"))
        {
            NavigateUri = new Uri("https://ponkis.xyz/"),
            Foreground = new SolidColorBrush(Color.FromRgb(0, 102, 204))
        };
        link.RequestNavigate += Hyperlink_RequestNavigate;
        dialog.DialogDescriptionText.Inlines.Add(link);

        return ShowDialog(owner, dialog);
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
        return ShowDialog(owner, dialog);
    }

    private static MessageBoxResult ShowDialog(Window owner, AppDialogWindow dialog)
    {
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

    private static void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenExternalUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private static void OpenExternalUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private static IReadOnlyList<DialogButtonSpec> ContinueButtons()
    {
        return [new DialogButtonSpec("Continue", MessageBoxResult.OK, IsDefault: true, IsCancel: true)];
    }

    private static IReadOnlyList<DialogButtonSpec> OkButtons()
    {
        return [new DialogButtonSpec("OK", MessageBoxResult.OK, IsDefault: true, IsCancel: true)];
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
