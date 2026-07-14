using System.IO.Pipes;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Mystral.Services;
using Mystral.Views;

namespace Mystral;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private CancellationTokenSource? _activationListenerCancellation;
    private string? _pendingActivationArgument;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (DesktopActivationService.IsProtocolRegistrationRequest(e.Args))
        {
            Shutdown(DesktopActivationService.TryRegisterProtocol() ? 0 : 1);
            return;
        }

        // Repair a moved/stale unpackaged development registration even when
        // this launch only needs to forward activation to an existing instance.
        _ = DesktopActivationService.TryRegisterProtocol();

        _instanceMutex = new Mutex(
            initiallyOwned: true,
            DesktopActivationService.InstanceMutexName,
            out var isFirstInstance);

        if (!isFirstInstance)
        {
            DesktopActivationService.TryForwardToRunningInstance(
                e.Args.FirstOrDefault() ?? DesktopActivationService.ActivateMessage);
            Shutdown();
            return;
        }

        _ownsInstanceMutex = true;

        if (e.Args.FirstOrDefault() is { } activationArgument)
        {
            QueueOrActivateMainWindow(activationArgument);
        }

        // Create the pipe server before constructing the main window. A second
        // launch can observe the mutex immediately, so waiting until after Show
        // leaves a startup window where its activation would time out and vanish.
        _activationListenerCancellation = new CancellationTokenSource();
        _ = ListenForActivationAsync(_activationListenerCancellation.Token);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Loaded += MainWindow_ActivationReady;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationListenerCancellation?.Cancel();
        _activationListenerCancellation?.Dispose();
        if (_ownsInstanceMutex && _instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private async Task ListenForActivationAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    DesktopActivationService.PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);

                using var reader = new StreamReader(server);
                var message = await reader.ReadToEndAsync(cancellationToken);
                if (message.Length <= DesktopActivationService.MaximumActivationLength)
                {
                    await Dispatcher.InvokeAsync(
                        () => QueueOrActivateMainWindow(message),
                        DispatcherPriority.Normal,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // A malformed or abruptly closed local activation must not stop future links.
            }
        }
    }

    private void MainWindow_ActivationReady(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow mainWindow)
        {
            mainWindow.Loaded -= MainWindow_ActivationReady;
        }

        var activationArgument = _pendingActivationArgument;
        _pendingActivationArgument = null;
        if (activationArgument is not null)
        {
            ActivateMainWindow(activationArgument);
        }
    }

    private void QueueOrActivateMainWindow(string activationArgument)
    {
        if (MainWindow is not MainWindow { IsLoaded: true })
        {
            _pendingActivationArgument = DesktopActivationService.PreferActivation(
                _pendingActivationArgument,
                activationArgument);
            return;
        }

        ActivateMainWindow(activationArgument);
    }

    private void ActivateMainWindow(string activationArgument)
    {
        if (MainWindow is not MainWindow mainWindow)
        {
            return;
        }

        var openSocialSettings = DesktopActivationService.IsSocialSettingsActivation(activationArgument);
        mainWindow.ActivateFromExternalRequest(
            openSocialSettings,
            startSocialLinking: openSocialSettings);
    }
}
