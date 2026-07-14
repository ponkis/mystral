using System.IO;
using System.Reflection;

namespace Mystral.Configuration;

public static class AppMetadata
{
    public const string Name = "Mystral";
    public const string GlobeProductionBaseUrl = "https://chat.ponkis.xyz/";
    public const string GlobeDevelopmentBaseUrl = "http://localhost:3000/";
    public const string GlobeDefaultAvatarCdnBaseUrl =
        "https://pub-1b00f16de14f4f76b980cb2115a8f12a.r2.dev/";
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
    public static Uri? GlobeAvatarCdnBaseUri { get; } = ResolveGlobeAvatarCdnBaseUri();

    public static bool IsTrustedGlobeAvatarUri(Uri uri, Uri? globeBaseUri = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var globeBase = globeBaseUri ?? GlobeBaseUri;
        if (HasSameOrigin(uri, globeBase))
        {
            return uri.Scheme == Uri.UriSchemeHttps
                   || (globeBase.Scheme == Uri.UriSchemeHttp
                       && uri.Scheme == Uri.UriSchemeHttp);
        }

        var cdnBase = GlobeAvatarCdnBaseUri;
        if (cdnBase is null || !HasSameOrigin(uri, cdnBase))
        {
            return false;
        }

        var trustedPath = cdnBase.AbsolutePath.TrimEnd('/') + "/";
        return (trustedPath == "/"
                || uri.AbsolutePath.StartsWith(trustedPath, StringComparison.Ordinal))
               && (uri.Scheme == Uri.UriSchemeHttps
                   || (cdnBase.Scheme == Uri.UriSchemeHttp
                       && uri.Scheme == Uri.UriSchemeHttp));
    }

    private static string GetInformationalVersion()
    {
        var assembly = typeof(AppMetadata).Assembly;
        var version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(version))
        {
            var assemblyVersion = assembly.GetName().Version
                ?? throw new InvalidOperationException("The Mystral assembly version is unavailable.");
            version = assemblyVersion.ToString(3);
        }

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

    private static Uri? ResolveGlobeAvatarCdnBaseUri()
    {
        var configured = typeof(AppMetadata).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Key, "GlobeAvatarCdnUrl", StringComparison.Ordinal))
            ?.Value;
#if APP_ENVIRONMENT_DEVELOPMENT
        configured = Environment.GetEnvironmentVariable("MYSTRAL_GLOBE_AVATAR_CDN_URL")
                     ?? configured;
#endif
        configured ??= GlobeDefaultAvatarCdnBaseUrl;
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme != Uri.UriSchemeHttps
                && (EnvironmentName != "Development" || uri.Scheme != Uri.UriSchemeHttp)))
        {
            return null;
        }

        return EnsureTrailingSlash(uri);
    }

    private static bool HasSameOrigin(Uri first, Uri second)
    {
        return string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(first.IdnHost, second.IdnHost, StringComparison.OrdinalIgnoreCase)
               && first.Port == second.Port;
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);
    }
}
