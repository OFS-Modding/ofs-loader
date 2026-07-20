using System.Text.RegularExpressions;

namespace OFS.Manager;

internal sealed record GameInstallation(
    string GameDirectory,
    string ManifestPath,
    string AppId,
    string Name,
    string BuildId,
    string InstallDirectory);

internal static partial class SteamDiscovery
{
    private const string OfsAppId = "4210580";

    public static GameInstallation FindInstallation(string? explicitGameDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitGameDirectory))
        {
            return FromExplicitDirectory(Path.GetFullPath(explicitGameDirectory));
        }

        foreach (var steamRoot in EnumerateSteamRoots())
        {
            foreach (var libraryRoot in EnumerateLibraryRoots(steamRoot))
            {
                var manifestPath = Path.Combine(libraryRoot, "steamapps", $"appmanifest_{OfsAppId}.acf");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                var fields = ParseManifest(manifestPath);
                var installDirectory = RequireField(fields, "installdir", manifestPath);
                var gameDirectory = Path.Combine(libraryRoot, "steamapps", "common", installDirectory);
                ValidateGameDirectory(gameDirectory);

                return CreateInstallation(gameDirectory, manifestPath, fields);
            }
        }

        throw new IOException("Ore Factory Squad was not found in the discovered Steam libraries.");
    }

    private static GameInstallation FromExplicitDirectory(string gameDirectory)
    {
        ValidateGameDirectory(gameDirectory);

        var steamApps = Directory.GetParent(gameDirectory)?.Parent?.FullName;
        var manifestPath = steamApps is null
            ? string.Empty
            : Path.Combine(steamApps, $"appmanifest_{OfsAppId}.acf");

        if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
        {
            throw new IOException($"Steam manifest {OfsAppId} was not found next to '{gameDirectory}'.");
        }

        return CreateInstallation(gameDirectory, manifestPath, ParseManifest(manifestPath));
    }

    private static GameInstallation CreateInstallation(
        string gameDirectory,
        string manifestPath,
        IReadOnlyDictionary<string, string> fields) => new(
            gameDirectory,
            manifestPath,
            RequireField(fields, "appid", manifestPath),
            RequireField(fields, "name", manifestPath),
            RequireField(fields, "buildid", manifestPath),
            RequireField(fields, "installdir", manifestPath));

    private static IEnumerable<string> EnumerateSteamRoots()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
        };

        return candidates
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateLibraryRoots(string steamRoot)
    {
        yield return steamRoot;

        var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraryFile))
        {
            yield break;
        }

        var content = File.ReadAllText(libraryFile);
        foreach (Match match in VdfPairRegex().Matches(content))
        {
            if (!string.Equals(match.Groups["key"].Value, "path", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = match.Groups["value"].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
            if (Directory.Exists(path) && !string.Equals(path, steamRoot, StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.GetFullPath(path);
            }
        }
    }

    private static Dictionary<string, string> ParseManifest(string manifestPath)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in VdfPairRegex().Matches(File.ReadAllText(manifestPath)))
        {
            fields[match.Groups["key"].Value] = match.Groups["value"].Value;
        }

        return fields;
    }

    private static string RequireField(
        IReadOnlyDictionary<string, string> fields,
        string name,
        string manifestPath) => fields.TryGetValue(name, out var value)
            ? value
            : throw new InvalidDataException($"Field '{name}' is missing from '{manifestPath}'.");

    private static void ValidateGameDirectory(string gameDirectory)
    {
        var executable = Path.Combine(gameDirectory, "Ore Factory Squad.exe");
        var gameAssembly = Path.Combine(gameDirectory, "GameAssembly.dll");
        if (!File.Exists(executable) || !File.Exists(gameAssembly))
        {
            throw new IOException($"'{gameDirectory}' is not a valid Ore Factory Squad installation.");
        }
    }

    [GeneratedRegex("\\\"(?<key>[^\\\"]+)\\\"\\s+\\\"(?<value>[^\\\"]*)\\\"")]
    private static partial Regex VdfPairRegex();
}
