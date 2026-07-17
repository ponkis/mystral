namespace Mystral.Services;

internal static class GitHubReleaseLinks
{
    private const string RepositoryUrl = "https://github.com/ponkis/mystral";

    public static Uri? CreateCompareUri(string? previousVersion, string? currentVersion)
    {
        if (!ReleaseVersion.TryParse(previousVersion, out var previous)
            || !ReleaseVersion.TryParse(currentVersion, out var current)
            || previous.IsDevelopment
            || current.IsDevelopment
            || current.CompareTo(previous) <= 0)
        {
            return null;
        }

        return new Uri(
            $"{RepositoryUrl}/compare/{previous.Tag}...{current.Tag}",
            UriKind.Absolute);
    }

    private readonly record struct ReleaseVersion(
        int Major,
        int Minor,
        int Patch,
        string? Prerelease) : IComparable<ReleaseVersion>
    {
        public bool IsDevelopment =>
            string.Equals(Prerelease, "dev", StringComparison.OrdinalIgnoreCase)
            || Prerelease?.StartsWith("dev.", StringComparison.OrdinalIgnoreCase) == true;

        public string Tag =>
            $"v{Major}.{Minor}.{Patch}{(Prerelease is null ? string.Empty : $"-{Prerelease}")}";

        public int CompareTo(ReleaseVersion other)
        {
            var comparison = Major.CompareTo(other.Major);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = Minor.CompareTo(other.Minor);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = Patch.CompareTo(other.Patch);
            if (comparison != 0)
            {
                return comparison;
            }

            if (Prerelease is null)
            {
                return other.Prerelease is null ? 0 : 1;
            }

            if (other.Prerelease is null)
            {
                return -1;
            }

            return ComparePrerelease(Prerelease, other.Prerelease);
        }

        public static bool TryParse(string? value, out ReleaseVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            value = value.Trim();
            if (value[0] is 'v' or 'V')
            {
                value = value[1..];
            }

            var suffixIndex = value.IndexOf('-');
            var numericPart = suffixIndex < 0 ? value : value[..suffixIndex];
            var prerelease = suffixIndex < 0 ? null : value[(suffixIndex + 1)..];
            var numericIdentifiers = numericPart.Split('.');
            if (numericIdentifiers.Length != 3
                || !TryParseNumericIdentifier(numericIdentifiers[0], out var major)
                || !TryParseNumericIdentifier(numericIdentifiers[1], out var minor)
                || !TryParseNumericIdentifier(numericIdentifiers[2], out var patch)
                || (prerelease is not null && !IsValidPrerelease(prerelease)))
            {
                return false;
            }

            version = new ReleaseVersion(major, minor, patch, prerelease);
            return true;
        }

        private static bool TryParseNumericIdentifier(string value, out int number)
        {
            number = 0;
            return value.Length > 0
                   && (value.Length == 1 || value[0] != '0')
                   && value.All(char.IsAsciiDigit)
                   && int.TryParse(value, out number);
        }

        private static bool IsValidPrerelease(string value)
        {
            var identifiers = value.Split('.');
            return identifiers.All(identifier =>
                identifier.Length > 0
                && identifier.All(character =>
                    char.IsAsciiLetterOrDigit(character) || character == '-')
                && (!identifier.All(char.IsAsciiDigit)
                    || identifier.Length == 1
                    || identifier[0] != '0'));
        }

        private static int ComparePrerelease(string first, string second)
        {
            var firstIdentifiers = first.Split('.');
            var secondIdentifiers = second.Split('.');
            var sharedLength = Math.Min(firstIdentifiers.Length, secondIdentifiers.Length);
            for (var index = 0; index < sharedLength; index++)
            {
                var firstIdentifier = firstIdentifiers[index];
                var secondIdentifier = secondIdentifiers[index];
                var firstIsNumeric = firstIdentifier.All(char.IsAsciiDigit);
                var secondIsNumeric = secondIdentifier.All(char.IsAsciiDigit);
                int comparison;
                if (firstIsNumeric && secondIsNumeric)
                {
                    comparison = firstIdentifier.Length.CompareTo(secondIdentifier.Length);
                    if (comparison == 0)
                    {
                        comparison = string.Compare(firstIdentifier, secondIdentifier, StringComparison.Ordinal);
                    }
                }
                else if (firstIsNumeric)
                {
                    comparison = -1;
                }
                else if (secondIsNumeric)
                {
                    comparison = 1;
                }
                else
                {
                    comparison = string.Compare(firstIdentifier, secondIdentifier, StringComparison.Ordinal);
                }

                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return firstIdentifiers.Length.CompareTo(secondIdentifiers.Length);
        }
    }
}
