using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OFS.Installer;

internal static partial class Program
{
    private const string PayloadResource = "OFS.Loader.Payload.zip";
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const int MaximumEntries = 10_000;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        InstallerOptions options;
        try
        {
            options = InstallerOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"OFS Installer: {exception.Message}");
            Console.Error.WriteLine("Run OFS-Installer.exe --help for usage.");
            return 2;
        }

        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }
        if (options.ShowVersion)
        {
            Console.WriteLine(GetVersion());
            return 0;
        }

        var extractionRoot = options.ExtractDirectory is null
            ? Path.Combine(Path.GetTempPath(), "OFS-Modding", $"installer-{Guid.NewGuid():N}")
            : Path.GetFullPath(options.ExtractDirectory);
        var cleanExtraction = options.ExtractDirectory is null;
        try
        {
            EnsureEmptyDestination(extractionRoot);
            if (!options.Quiet)
            {
                Console.Error.WriteLine($"OFS Installer {GetVersion()}");
                Console.Error.WriteLine("Verifying embedded loader payload...");
            }
            var payloadRoot = ExtractAndVerifyPayload(extractionRoot);
            if (options.ExtractDirectory is not null)
            {
                Console.WriteLine(payloadRoot);
                return 0;
            }

            var manager = Path.Combine(payloadRoot, "ofs-manager.exe");
            if (!File.Exists(manager))
            {
                throw new InvalidDataException("Embedded payload does not contain ofs-manager.exe.");
            }
            if (!options.Quiet)
            {
                Console.Error.WriteLine("Payload verified. Locating Ore Factory Squad...");
            }
            return await RunManagerAsync(manager, options).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"OFS Installer failed: {exception.Message}");
            return 1;
        }
        finally
        {
            if (cleanExtraction && Directory.Exists(extractionRoot))
            {
                try
                {
                    Directory.Delete(extractionRoot, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine(
                        $"OFS Installer warning: temporary cleanup was incomplete: {exception.Message}");
                }
            }
        }
    }

    private static string ExtractAndVerifyPayload(string extractionRoot)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidOperationException(
                "This executable does not contain an installer payload. Use an official release build.");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count is 0 or > MaximumEntries)
        {
            throw new InvalidDataException("Embedded payload has an invalid entry count.");
        }

        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaximumExpandedBytes)
            {
                throw new InvalidDataException("Embedded payload exceeds the expansion limit.");
            }
            if (IsSymbolicLink(entry))
            {
                throw new InvalidDataException($"Embedded payload contains a link: '{entry.FullName}'.");
            }

            var entryPath = entry.FullName.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(entryPath))
            {
                throw new InvalidDataException("Embedded payload contains an empty path.");
            }
            var destination = ResolveConfinedPath(extractionRoot, entryPath);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }

        var topDirectories = Directory.GetDirectories(extractionRoot);
        if (topDirectories.Length != 1 || Directory.GetFiles(extractionRoot).Length != 0)
        {
            throw new InvalidDataException("Embedded payload must contain exactly one release root.");
        }
        VerifyChecksums(topDirectories[0]);
        return topDirectories[0];
    }

    private static void VerifyChecksums(string payloadRoot)
    {
        var checksumPath = Path.Combine(payloadRoot, "SHA256SUMS");
        if (!File.Exists(checksumPath))
        {
            throw new InvalidDataException("Embedded payload is missing SHA256SUMS.");
        }

        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(checksumPath))
        {
            var match = ChecksumLine().Match(line);
            if (!match.Success || !declared.TryAdd(match.Groups[2].Value, match.Groups[1].Value))
            {
                throw new InvalidDataException("Embedded SHA256SUMS is malformed or contains duplicates.");
            }
        }

        var actualFiles = Directory.GetFiles(payloadRoot, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, checksumPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (actualFiles.Length != declared.Count)
        {
            throw new InvalidDataException("Embedded SHA256SUMS does not cover the complete payload.");
        }
        foreach (var pair in declared)
        {
            var path = ResolveConfinedPath(payloadRoot, pair.Key);
            if (!File.Exists(path))
            {
                throw new InvalidDataException($"Embedded payload file is missing: '{pair.Key}'.");
            }
            using var input = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            if (!string.Equals(actual, pair.Value, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Embedded payload checksum failed: '{pair.Key}'.");
            }
        }
    }

    private static async Task<int> RunManagerAsync(string manager, InstallerOptions options)
    {
        var start = new ProcessStartInfo(manager)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(manager)!,
        };
        foreach (var argument in options.ManagerArguments)
        {
            start.ArgumentList.Add(argument);
        }
        if (options.SkipCatalogSync)
        {
            start.Environment["OFS_SKIP_CATALOG_SYNC"] = "1";
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the embedded OFS Manager.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (!string.IsNullOrEmpty(output)) Console.Out.Write(output);
        if (!string.IsNullOrEmpty(error)) Console.Error.Write(error);
        return process.ExitCode;
    }

    private static string ResolveConfinedPath(string root, string relative)
    {
        var normalized = relative.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') ||
            normalized.Contains(':') || normalized.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"Embedded payload contains an unsafe path: '{relative}'.");
        }
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Embedded payload path escapes its root: '{relative}'.");
        }
        return resolved;
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        return unixMode == UnixSymbolicLink ||
            ((FileAttributes)entry.ExternalAttributes).HasFlag(FileAttributes.ReparsePoint);
    }

    private static void EnsureEmptyDestination(string destination)
    {
        if (Directory.Exists(destination) &&
            Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new IOException($"Extraction destination is not empty: '{destination}'.");
        }
        Directory.CreateDirectory(destination);
    }

    private static string GetVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

    private static void PrintHelp()
    {
        Console.WriteLine("""
            OFS Installer

            Usage:
              OFS-Installer.exe [--game-dir PATH] [--no-catalog-sync] [--quiet]
              OFS-Installer.exe --status [--game-dir PATH]
              OFS-Installer.exe --scan [--game-dir PATH]
              OFS-Installer.exe --extract-only DIRECTORY
              OFS-Installer.exe --manager <ofs-manager arguments...>

            With no arguments, the installer locates Ore Factory Squad through Steam,
            installs or updates OFS Loader, and synchronizes the official mod catalog.
            --manager exposes the complete developer CLI from the embedded release.
            """);
    }

    [GeneratedRegex("^([a-f0-9]{64})  (.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumLine();
}

internal sealed record InstallerOptions(
    bool ShowHelp,
    bool ShowVersion,
    bool Quiet,
    bool SkipCatalogSync,
    string? ExtractDirectory,
    IReadOnlyList<string> ManagerArguments)
{
    public static InstallerOptions Parse(string[] args)
    {
        var quiet = false;
        var skipCatalog = false;
        string? gameDirectory = null;
        string? extractDirectory = null;
        var action = "install";
        IReadOnlyList<string>? managerArguments = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "-h" or "--help":
                    return new(true, false, quiet, skipCatalog, null, []);
                case "--version":
                    return new(false, true, quiet, skipCatalog, null, []);
                case "--quiet":
                    quiet = true;
                    break;
                case "--no-catalog-sync":
                    skipCatalog = true;
                    break;
                case "--game-dir":
                    gameDirectory = RequireValue(args, ref index, "--game-dir");
                    break;
                case "--status":
                    action = SetAction(action, "status");
                    break;
                case "--scan":
                    action = SetAction(action, "scan");
                    break;
                case "--extract-only":
                    action = SetAction(action, "extract");
                    extractDirectory = RequireValue(args, ref index, "--extract-only");
                    break;
                case "--manager":
                    if (index + 1 >= args.Length)
                    {
                        throw new ArgumentException("--manager requires at least one manager argument.");
                    }
                    managerArguments = args[(index + 1)..];
                    index = args.Length;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[index]}'.");
            }
        }

        if (managerArguments is not null)
        {
            if (gameDirectory is not null || extractDirectory is not null || action != "install")
            {
                throw new ArgumentException("--manager cannot be combined with installer actions or --game-dir.");
            }
            return new(false, false, quiet, skipCatalog, null, managerArguments);
        }
        if (action == "extract")
        {
            if (gameDirectory is not null || skipCatalog)
            {
                throw new ArgumentException("--extract-only cannot be combined with game or catalog options.");
            }
            return new(false, false, quiet, false, extractDirectory, []);
        }

        var manager = action switch
        {
            "scan" => new List<string> { "scan" },
            "status" => new List<string> { "bootstrap", "status" },
            _ => new List<string> { "bootstrap", "install" },
        };
        if (gameDirectory is not null) manager.Add(gameDirectory);
        return new(false, false, quiet, skipCatalog, null, manager);
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{option} requires a value.");
        }
        return args[index];
    }

    private static string SetAction(string current, string requested)
    {
        if (current != "install")
        {
            throw new ArgumentException("Only one installer action may be selected.");
        }
        return requested;
    }
}
