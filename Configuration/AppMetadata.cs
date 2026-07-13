using System.IO;
using System.Reflection;

namespace Mystral.Configuration;

public static class AppMetadata
{
    public const string Name = "Mystral";
    public const string GlobeProductionBaseUrl = "https://chat.ponkis.xyz/";
    public const string GlobeDevelopmentBaseUrl = "http://localhost:3000/";
    public static string Version { get; } = GetInformationalVersion();
    public static string UserAgent { get; } = Name + "/" + Version + " (https://ponkis.xyz/)";

#if APP_ENVIRONMENT_DEVELOPMENT
    public const string EnvironmentName = "Development";
#else
    public const string EnvironmentName = "Production";
#endif

    public static string LocalApplicationDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        EnvironmentName == "Production" ? Name : $"{Name} {EnvironmentName}");

    public static Uri GlobeBaseUri { get; } = ResolveGlobeBaseUri();

    private static string GetInformationalVersion()
    {
        var version = typeof(AppMetadata).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "1.0.0";

        var plusIndex = version.IndexOf('+');
        return plusIndex >= 0 ? version[..plusIndex] : version;
    }

    private static Uri ResolveGlobeBaseUri()
    {
#if APP_ENVIRONMENT_DEVELOPMENT
        var configured = Environment.GetEnvironmentVariable("MYSTRAL_GLOBE_BASE_URL");
        if (Uri.TryCreate(configured, UriKind.Absolute, out var overridden)
            && (overridden.Scheme == Uri.UriSchemeHttp || overridden.Scheme == Uri.UriSchemeHttps))
        {
            return EnsureTrailingSlash(overridden);
        }

        return new Uri(GlobeDevelopmentBaseUrl, UriKind.Absolute);
#else
        return new Uri(GlobeProductionBaseUrl, UriKind.Absolute);
#endif
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);
    }
}
