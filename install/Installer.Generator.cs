using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using WixSharp;

namespace Installer;

public static partial class Generator
{
    /// <summary>
    ///     Generates Wix entities, features and directories for the installer.
    /// </summary>
    public static WixEntity[] GenerateWixEntities(string[] args)
    {
        var versionStorages = new Dictionary<string, List<WixEntity>>();
        var seenDirectories = new Dictionary<string, string>();   // Revit year → the directory that claimed it
        var revitFeature = new Feature
        {
            Name = "Revit Add-in",
            Description = "Revit add-in installation files",
            Display = FeatureDisplay.expand
        };

        foreach (var directory in args)
        {
            var directoryInfo = new DirectoryInfo(directory);
            if (!TryParseVersion(directoryInfo.FullName, out var fileVersion))
            {
                throw new Exception($"Could not parse version from directory name: {directoryInfo.FullName}");
            }

            var feature = new Feature
            {
                Name = fileVersion,
                Description = $"Install add-in for Revit {fileVersion}",
                ConfigurableDir = $"INSTALL{fileVersion}"
            };

            revitFeature.Add(feature);

            var files = new Files(feature, $@"{directory}\*.*");
            if (versionStorages.TryGetValue(fileVersion, out _))
            {
                // FAIL LOUDLY. Two source trees resolving to the same Revit year (the classic case: a stale
                // bin\Debug.R25\publish sitting beside bin\Release.R25\publish — TryParseVersion only reads
                // trailing digits, so Debug vs Release is invisible to it) get merged into one Dir, and the
                // identical relative paths then produce DUPLICATE WiX component IDs. That surfaces much
                // later as an opaque WiX failure; naming both directories here explains it immediately.
                throw new Exception(
                    $"Two payload directories both resolve to Revit {fileVersion}: '{seenDirectories[fileVersion]}' " +
                    $"and '{directoryInfo.FullName}'. Delete the stale one (usually a leftover Debug publish " +
                    "folder next to the Release one) and rebuild — merging them yields duplicate WiX component IDs.");
            }

            seenDirectories[fileVersion] = directoryInfo.FullName;
            versionStorages.Add(fileVersion, [files]);

            LogFeatureFiles(directory, fileVersion);
        }

        return versionStorages
            .Select(storage => new Dir(new Id($"INSTALL{storage.Key}"), storage.Key, storage.Value.ToArray()))
            .Cast<WixEntity>()
            .ToArray();
    }

    /// <summary>
    ///     Parse a version string from the given input.
    /// </summary>
    private static bool TryParseVersion(string input, [NotNullWhen(true)] out string? version)
    {
        version = null;
        var match = VersionRegex().Match(input);
        if (!match.Success) return false;

        switch (match.Value.Length)
        {
            case 4:
                version = match.Value;
                return true;
            case 2:
                version = $"20{match.Value}";
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    ///    Write a list of installer files.
    /// </summary>
    private static void LogFeatureFiles(string directory, string fileVersion)
    {
        var assemblies = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
        Console.WriteLine($"Installer files for version {fileVersion}:");

        foreach (var assembly in assemblies)
        {
            Console.WriteLine($"- {assembly}");
        }
    }

    /// <summary>
    ///     A regular expression to match the last sequence of numeric characters in a string.
    /// </summary>
    [GeneratedRegex(@"(\d+)(?!.*\d)")]
    private static partial Regex VersionRegex();
}