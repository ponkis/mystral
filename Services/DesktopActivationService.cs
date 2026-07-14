using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Mystral.Configuration;

namespace Mystral.Services;

internal static class DesktopActivationService
{
    private const uint AssociationChangedEvent = 0x08000000;
    private const uint NotifyByIdList = 0;

    internal const string ActivateMessage = "activate";
    internal const string RegisterProtocolArgument = "--register-protocol";
    internal const int MaximumActivationLength = 2048;

    internal static string ProtocolScheme =>
        AppMetadata.EnvironmentName == "Production" ? "mystral" : "mystral-dev";

    internal static bool CanSelfRegisterProtocol =>
        AppMetadata.EnvironmentName != "Production";

    internal static string InstanceMutexName =>
        $"Local\\ponkis.mystral.{AppMetadata.EnvironmentName}.single-instance";

    internal static string PipeName =>
        $"ponkis.mystral.{AppMetadata.EnvironmentName.ToLowerInvariant()}.activation";

    internal static bool IsProtocolRegistrationRequest(IReadOnlyList<string>? arguments)
    {
        return arguments is { Count: 1 }
            && string.Equals(
                arguments[0],
                RegisterProtocolArgument,
                StringComparison.OrdinalIgnoreCase);
    }

    internal static string? BuildProtocolOpenCommand(
        string? processPath,
        string? entryAssemblyPath)
    {
        if (string.IsNullOrWhiteSpace(processPath)
            || !Path.IsPathFullyQualified(processPath)
            || processPath.Contains('"'))
        {
            return null;
        }

        var isDotnetHost = string.Equals(
            Path.GetFileName(processPath),
            "dotnet.exe",
            StringComparison.OrdinalIgnoreCase);
        if (!isDotnetHost)
        {
            return $"\"{processPath}\" \"%1\"";
        }

        if (string.IsNullOrWhiteSpace(entryAssemblyPath)
            || !Path.IsPathFullyQualified(entryAssemblyPath)
            || entryAssemblyPath.Contains('"'))
        {
            return null;
        }

        return $"\"{processPath}\" exec \"{entryAssemblyPath}\" \"%1\"";
    }

    internal static bool IsSocialSettingsActivation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumActivationLength
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, ProtocolScheme, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var route = string.IsNullOrEmpty(uri.Host)
            ? uri.AbsolutePath.Trim('/')
            : $"{uri.Host}/{uri.AbsolutePath.Trim('/')}".TrimEnd('/');
        return string.Equals(route, "settings/social", StringComparison.OrdinalIgnoreCase);
    }

    internal static string PreferActivation(string? pending, string incoming)
    {
        if (string.IsNullOrEmpty(pending)
            || IsSocialSettingsActivation(incoming))
        {
            return incoming;
        }

        // Do not let a generic wake-up replace a pending deep link while the
        // first window is still loading.
        return pending;
    }

    internal static void TryForwardToRunningInstance(string activationArgument)
    {
        var message = activationArgument.Length <= MaximumActivationLength
            ? activationArgument
            : ActivateMessage;

        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.None);
            client.Connect(1800);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.Write(message);
        }
        catch
        {
            // Never start a second media-controller instance merely because activation failed.
        }
    }

    internal static bool TryRegisterProtocol()
    {
        try
        {
            // The production installer owns mystral://. Runtime registration is
            // intentionally limited to unpackaged development builds so a source
            // Release run cannot replace the stable installed executable path.
            if (!CanSelfRegisterProtocol)
            {
                return false;
            }

            var executablePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
            var openCommand = BuildProtocolOpenCommand(executablePath, entryAssemblyPath);
            if (string.IsNullOrWhiteSpace(openCommand))
            {
                return false;
            }

            var isDotnetHost = string.Equals(
                Path.GetFileName(executablePath),
                "dotnet.exe",
                StringComparison.OrdinalIgnoreCase);
            var registrationTarget = isDotnetHost ? entryAssemblyPath! : executablePath!;

            using var protocolKey = Registry.CurrentUser.CreateSubKey(
                $"Software\\Classes\\{ProtocolScheme}");
            if (protocolKey is null)
            {
                return false;
            }

            protocolKey.SetValue(null, $"URL:{AppMetadata.Name} Protocol");
            protocolKey.SetValue("URL Protocol", string.Empty);
            protocolKey.SetValue("MystralRegistrationOwner", "ponkis.mystral.development");
            protocolKey.SetValue("MystralRegistrationTarget", registrationTarget);

            using var iconKey = protocolKey.CreateSubKey("DefaultIcon");
            if (iconKey is null)
            {
                return false;
            }
            iconKey.SetValue(null, $"\"{registrationTarget}\",0");

            using var commandKey = protocolKey.CreateSubKey("shell\\open\\command");
            if (commandKey is null)
            {
                return false;
            }

            commandKey.SetValue(null, openCommand);

            using var mergedCommandKey = Registry.ClassesRoot.OpenSubKey(
                $"{ProtocolScheme}\\shell\\open\\command");
            if (!string.Equals(
                mergedCommandKey?.GetValue(null) as string,
                openCommand,
                StringComparison.Ordinal))
            {
                return false;
            }

            SHChangeNotify(
                AssociationChangedEvent,
                NotifyByIdList,
                IntPtr.Zero,
                IntPtr.Zero);
            return true;
        }
        catch
        {
            // The installer owns production registration; this enables unpackaged dev builds.
            return false;
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        IntPtr item1,
        IntPtr item2);
}
