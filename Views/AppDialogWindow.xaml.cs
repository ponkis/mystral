using System.Diagnostics;
using System.IO;
using System.Media;
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
        Icon = IconImageSource.LoadBestFitFrame("Resources/ico.ico", 16);
        DialogTitleText.Text = title;
        DialogIcon.Source = icon;
        ConfigureButtons(buttons);
    }

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.Cancel;

    public static MessageBoxResult ShowInformation(Window owner, string title, string message)
    {
        return ShowDialog(owner, title, message, FromSystemIcon(System.Drawing.SystemIcons.Information), ContinueButtons());
    }

    public static MessageBoxResult ShowConfirmation(Window owner, string title, string message)
    {
        return ShowDialog(owner, title, message, FromSystemIcon(System.Drawing.SystemIcons.Information), ContinueButtons(), "confirmation.wav");
    }

    internal static MessageBoxResult ShowConfirmationWithLink(
        Window owner,
        string title,
        string message,
        string linkCaption,
        Uri linkUri)
    {
        ArgumentNullException.ThrowIfNull(linkUri);

        var dialog = new AppDialogWindow(
            title,
            FromSystemIcon(System.Drawing.SystemIcons.Information),
            ContinueButtons());
        dialog.DialogDescriptionText.Inlines.Add(new Run(message));
        dialog.DialogDescriptionText.Inlines.Add(new LineBreak());
        var link = new Hyperlink(new Run(linkCaption))
        {
            NavigateUri = linkUri
        };
        link.RequestNavigate += dialog.Hyperlink_RequestNavigate;
        dialog.DialogDescriptionText.Inlines.Add(link);
        return ShowDialog(owner, dialog, "confirmation.wav");
    }

    internal static MessageBoxResult ShowConfirmationWithIcon(
        Window owner,
        string title,
        string message,
        string iconPath)
    {
        return ShowDialog(
            owner,
            title,
            message,
            IconImageSource.LoadBestFitFrame(iconPath, 32),
            ContinueButtons(),
            "confirmation.wav");
    }

    public static MessageBoxResult ShowWarning(Window owner, string title, string message)
    {
        return ShowDialog(
            owner,
            title,
            message,
            FromSystemIcon(System.Drawing.SystemIcons.Warning),
            ContinueButtons(),
            "warning.wav");
    }

    public static MessageBoxResult ShowError(Window owner, string title, string message)
    {
        return ShowDialog(owner, title, message, FromSystemIcon(System.Drawing.SystemIcons.Error), ContinueButtons(), "error.wav");
    }

    public static MessageBoxResult ShowConfirmationWithBadge(
        Window owner,
        string title,
        string message,
        string primaryIconPath,
        string badgeIconPath)
    {
        return ShowDialog(
            owner,
            title,
            message,
            IconImageSource.LoadOverlayIcon(primaryIconPath, badgeIconPath),
            ContinueButtons(),
            "confirmation.wav");
    }

    internal static bool ShowAction(
        Window owner,
        string title,
        string message,
        string actionCaption,
        bool isError = false,
        bool isWarning = false,
        bool placeActionOnNewLine = false)
    {
        var actionSelected = false;
        var dialog = new AppDialogWindow(
            title,
            FromSystemIcon(
                isWarning
                    ? System.Drawing.SystemIcons.Warning
                    : isError
                        ? System.Drawing.SystemIcons.Error
                        : System.Drawing.SystemIcons.Information),
            ContinueButtons());
        dialog.DialogDescriptionText.Inlines.Add(new Run(message));
        dialog.DialogDescriptionText.Inlines.Add(
            placeActionOnNewLine ? new LineBreak() : new Run(" "));
        var action = new Hyperlink(new Run(actionCaption));
        action.Click += (_, _) =>
        {
            actionSelected = true;
            dialog.Result = MessageBoxResult.OK;
            dialog.Close();
        };
        dialog.DialogDescriptionText.Inlines.Add(action);
        ShowDialog(
            owner,
            dialog,
            isWarning ? "warning.wav" : isError ? "error.wav" : "confirmation.wav");
        return actionSelected;
    }

    public static MessageBoxResult ShowAbout(Window owner, Func<Window, Button, Task>? checkForUpdates = null)
    {
        AppDialogWindow? dialog = null;
        dialog = new AppDialogWindow(
            $"About {AppMetadata.Name}",
            IconImageSource.LoadBestFitFrame("Resources/ico.ico", 32),
            checkForUpdates is null
                ? OkButtons()
                :
                [
                    new DialogButtonSpec("Check for updates", MessageBoxResult.None, IsDefault: false, IsCancel: false, ClickAsync: button => checkForUpdates(dialog!, button)),
                    new DialogButtonSpec("OK", MessageBoxResult.OK, IsDefault: true, IsCancel: true)
                ]);

        dialog.Width = 430;
        dialog.MinHeight = 0;
        dialog.Height = 340;
        dialog.SizeToContent = SizeToContent.Manual;
        dialog.StandardDialogContent.Visibility = Visibility.Collapsed;
        dialog.AboutDialogContent.Visibility = Visibility.Visible;
        dialog.AboutLogoImage.Source = IconImageSource.LoadBestFrame("Resources/ico.ico");
        dialog.AboutAppNameText.Text = AppMetadata.Name;
        dialog.AboutVersionText.Text = $"version {AppMetadata.Version}";
        dialog.AboutCopyrightText.Text = $"Copyright © {DateTime.Now.Year} ponkis.";

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
            ],
            "confirmation.wav");
    }

    internal static MessageBoxResult ShowRetryCancel(Window owner, string title, string message)
    {
        return ShowDialog(
            owner,
            title,
            message,
            FromSystemIcon(System.Drawing.SystemIcons.Warning),
            [
                new DialogButtonSpec("Retry", MessageBoxResult.Yes, IsDefault: true, IsCancel: false),
                new DialogButtonSpec("Cancel", MessageBoxResult.Cancel, IsDefault: false, IsCancel: true)
            ],
            "warning.wav");
    }

    internal static MessageBoxResult ShowWarningWithIcon(
        Window owner,
        string title,
        string message,
        string iconPath)
    {
        return ShowDialog(
            owner,
            title,
            message,
            IconImageSource.LoadBestFitFrame(iconPath, 32),
            ContinueButtons(),
            "warning.wav");
    }

    private static MessageBoxResult ShowDialog(
        Window owner,
        string title,
        string message,
        ImageSource icon,
        IReadOnlyList<DialogButtonSpec> buttons,
        string? soundFile = null)
    {
        var dialog = new AppDialogWindow(title, message, icon, buttons);
        return ShowDialog(owner, dialog, soundFile);
    }

    private static MessageBoxResult ShowDialog(Window owner, AppDialogWindow dialog, string? soundFile = null)
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

        PlayDialogSound(soundFile);
        dialog.ShowDialog();

        if (hasTopmostOwner && owner != null)
        {
            owner.Topmost = originalTopmost;
        }

        return dialog.Result;
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            OpenExternalUrl(e.Uri.AbsoluteUri);
        }
        catch (Exception ex)
        {
            ShowWarning(this, "Could not open link", ex.Message);
        }
        finally
        {
            e.Handled = true;
        }
    }

    private static void OpenExternalUrl(string url)
    {
        if (Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        }) is null)
        {
            throw new InvalidOperationException("Windows did not open the link.");
        }
    }

    private static void PlayDialogSound(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Audio", fileName);
            if (File.Exists(path))
            {
                new SoundPlayer(path).Play();
            }
        }
        catch
        {
        }
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
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(ButtonPanel.Children.Count == 0 ? 0 : 10, 0, 0, 0),
                IsDefault = spec.IsDefault,
                IsCancel = spec.IsCancel
            };
            button.Click += (_, _) =>
            {
                if (spec.ClickAsync is not null)
                {
                    _ = spec.ClickAsync(button);
                    return;
                }

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

    private sealed record DialogButtonSpec(string Caption, MessageBoxResult Result, bool IsDefault, bool IsCancel, Func<Button, Task>? ClickAsync = null);
}
