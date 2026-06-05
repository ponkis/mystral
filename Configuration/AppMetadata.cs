using System.IO;
using System.Reflection;

namespace Mystral.Configuration;

public static class AppMetadata
{
    public const string Name = "Mystral";
    public static string Version { get; } = GetInformationalVersion();
    public static string UserAgent { get; } = Name + "/" + Version;

#if APP_ENVIRONMENT_DEVELOPMENT
    public const string EnvironmentName = "Development";
#else
    public const string EnvironmentName = "Production";
#endif

    public static string LocalApplicationDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        EnvironmentName == "Production" ? Name : $"{Name} {EnvironmentName}");

    private static string GetInformationalVersion()
    {
        var version = typeof(AppMetadata).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "1.0.0";

        var plusIndex = version.IndexOf('+');
        return plusIndex >= 0 ? version[..plusIndex] : version;
    }
}
