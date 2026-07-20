using System.Security.Cryptography;
using System.Text.Json;
using OFS.Sdk;

namespace OFS.Manager;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            PrintUsage();
            return 0;
        }
        if (args is ["--version"])
        {
            Console.WriteLine(ModManifestValidator.CurrentSdkVersion.ToString(3));
            return 0;
        }

        try
        {
            if (args.Length == 0 || string.Equals(args[0], "scan", StringComparison.OrdinalIgnoreCase))
            {
                var explicitPath = args.Length > 1 ? args[1] : null;
                var installation = SteamDiscovery.FindInstallation(explicitPath);
                return WriteJson(await BuildScanner.ScanAsync(installation));
            }

            if (string.Equals(args[0], "bootstrap", StringComparison.OrdinalIgnoreCase))
            {
                return await RunBootstrapCommand(args);
            }

            if (string.Equals(args[0], "mod", StringComparison.OrdinalIgnoreCase))
            {
                return await RunModCommand(args);
            }

            if (string.Equals(args[0], "catalog", StringComparison.OrdinalIgnoreCase))
            {
                return await RunCatalogCommand(args);
            }

            if (string.Equals(args[0], "profile", StringComparison.OrdinalIgnoreCase))
            {
                return await RunProfileCommand(args);
            }

            Console.Error.WriteLine($"Unknown command: {args[0]}");
            PrintUsage();
            return 2;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException or
            CryptographicException or ArgumentException or JsonException)
        {
            Console.Error.WriteLine($"Command failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunCatalogCommand(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("A catalog action is required.");
            PrintUsage();
            return 2;
        }

        var action = args[1].ToLowerInvariant();
        switch (action)
        {
            case "official-sync":
                {
                    var installation = SteamDiscovery.FindInstallation(args.Length > 2 ? args[2] : null);
                    return WriteJson(await OfficialCatalog.SyncAsync(installation));
                }
            case "keygen":
                {
                    if (args.Length < 5)
                    {
                        Console.Error.WriteLine("catalog keygen requires key-id, private PEM and public PEM paths.");
                        return 2;
                    }
                    return WriteJson(await CatalogTrustManager.GenerateAsync(args[2], args[3], args[4]));
                }
            case "sign":
                {
                    if (args.Length < 5)
                    {
                        Console.Error.WriteLine("catalog sign requires catalog, private PEM and key-id.");
                        return 2;
                    }
                    return WriteJson(await CatalogTrustManager.SignAsync(
                        args[2], args[3], args[4], args.Length > 5 ? args[5] : null));
                }
            case "verify":
                {
                    if (args.Length < 4)
                    {
                        Console.Error.WriteLine("catalog verify requires signed catalog and public PEM.");
                        return 2;
                    }
                    var result = await CatalogTrustManager.VerifyAsync(
                        args[2], args[3], args.Length > 4 ? args[4] : null);
                    WriteJson(result);
                    return result.Valid ? 0 : 1;
                }
            case "trust-add":
                {
                    if (args.Length < 4)
                    {
                        Console.Error.WriteLine("catalog trust-add requires key-id and public PEM.");
                        return 2;
                    }
                    var installation = SteamDiscovery.FindInstallation(args.Length > 4 ? args[4] : null);
                    return WriteJson(await CatalogTrustManager.AddTrustedKeyAsync(
                        installation, args[2], args[3]));
                }
            case "trust-list":
                {
                    var installation = SteamDiscovery.FindInstallation(args.Length > 2 ? args[2] : null);
                    return WriteJson(await CatalogTrustManager.ListAsync(installation));
                }
            case "trust-remove":
                {
                    if (args.Length < 3)
                    {
                        Console.Error.WriteLine("catalog trust-remove requires a key-id.");
                        return 2;
                    }
                    var installation = SteamDiscovery.FindInstallation(args.Length > 3 ? args[3] : null);
                    return WriteJson(await CatalogTrustManager.RemoveTrustedKeyAsync(installation, args[2]));
                }
            case "validate":
                {
                    if (args.Length < 3) return CatalogSourceRequired(action);
                    var source = args[2];
                    var result = await CatalogManager.ValidateAsync(source);
                    WriteJson(result);
                    return result.Valid ? 0 : 1;
                }
            case "resolve":
                {
                    if (args.Length < 4)
                    {
                        Console.Error.WriteLine("catalog resolve requires a mod id.");
                        return 2;
                    }
                    var result = await CatalogManager.ResolveAsync(
                        args[2],
                        args[3],
                        args.Length > 4 ? args[4] : "*");
                    WriteJson(result);
                    return result.Success ? 0 : 1;
                }
            case "sync":
                {
                    if (args.Length < 3) return CatalogSourceRequired(action);
                    var installation = SteamDiscovery.FindInstallation(args.Length > 3 ? args[3] : null);
                    return WriteJson(await CatalogManager.SyncAsync(args[2], installation));
                }
            case "install":
                {
                    if (args.Length < 4)
                    {
                        Console.Error.WriteLine("catalog install requires a mod id.");
                        return 2;
                    }
                    var installation = SteamDiscovery.FindInstallation(args.Length > 4 ? args[4] : null);
                    return WriteJson(await CatalogManager.InstallAsync(args[2], args[3], installation));
                }
            case "thumbnail":
                {
                    if (args.Length < 4)
                    {
                        Console.Error.WriteLine("catalog thumbnail requires a signed catalog and mod id.");
                        return 2;
                    }
                    var installation = SteamDiscovery.FindInstallation(args.Length > 4 ? args[4] : null);
                    return WriteJson(await CatalogManager.CacheThumbnailAsync(
                        args[2],
                        args[3],
                        installation));
                }
            default:
                Console.Error.WriteLine($"Unknown catalog action: {action}");
                PrintUsage();
                return 2;
        }
    }

    private static int CatalogSourceRequired(string action)
    {
        Console.Error.WriteLine($"catalog {action} requires a source.");
        return 2;
    }

    private static async Task<int> RunProfileCommand(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("A profile action is required.");
            PrintUsage();
            return 2;
        }
        var action = args[1].ToLowerInvariant();
        switch (action)
        {
            case "list":
                {
                    var installation = SteamDiscovery.FindInstallation(args.Length > 2 ? args[2] : null);
                    return WriteJson(await ProfileManager.ListAsync(installation));
                }
            case "enable":
            case "disable":
                {
                    if (args.Length < 3)
                    {
                        Console.Error.WriteLine($"profile {action} requires a mod id.");
                        return 2;
                    }
                    var installation = SteamDiscovery.FindInstallation(args.Length > 3 ? args[3] : null);
                    return WriteJson(await ProfileManager.ChangeAsync(
                        installation,
                        args[2],
                        action == "enable"));
                }
            case "discard":
                {
                    var installation = SteamDiscovery.FindInstallation(args.Length > 2 ? args[2] : null);
                    return WriteJson(await ProfileManager.DiscardPendingAsync(installation));
                }
            default:
                Console.Error.WriteLine($"Unknown profile action: {action}");
                PrintUsage();
                return 2;
        }
    }

    private static async Task<int> RunModCommand(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("A mod action is required.");
            PrintUsage();
            return 2;
        }

        var action = args[1].ToLowerInvariant();
        if (action == "quarantine-list")
        {
            var installation = SteamDiscovery.FindInstallation(args.Length > 2 ? args[2] : null);
            return WriteJson(await ModSafetyManager.ListAsync(installation));
        }
        if (action == "diagnose")
        {
            var installation = SteamDiscovery.FindInstallation(args.Length > 2 ? args[2] : null);
            return WriteJson(await ModDiagnosticsManager.ReadAsync(installation));
        }
        if (action == "quarantine-clear")
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("mod quarantine-clear requires a mod id or --all.");
                return 2;
            }
            var installation = SteamDiscovery.FindInstallation(args.Length > 3 ? args[3] : null);
            return WriteJson(await ModSafetyManager.ClearAsync(installation, args[2]));
        }
        if (action == "new")
        {
            if (args.Length < 4)
            {
                Console.Error.WriteLine("mod new requires an id and output directory.");
                return 2;
            }
            return WriteJson(await ModProjectScaffolder.CreateAsync(
                args[2],
                args[3],
                args.Length > 4 ? args[4] : null,
                args.Length > 5 ? args[5] : null));
        }
        if (args.Length < 3)
        {
            Console.Error.WriteLine($"mod {action} requires a source path.");
            return 2;
        }
        var source = args[2];
        switch (action)
        {
            case "validate":
                {
                    var result = await ModPackageManager.ValidateAsync(source);
                    WriteJson(result);
                    return result.Valid ? 0 : 1;
                }
            case "pack":
                return WriteJson(await ModPackageManager.PackAsync(
                    source,
                    args.Length > 3 ? args[3] : null));
            case "install":
                {
                    var installation = SteamDiscovery.FindInstallation(
                        args.Length > 3 ? args[3] : null);
                    return WriteJson(await ModPackageManager.InstallAsync(source, installation));
                }
            default:
                Console.Error.WriteLine($"Unknown mod action: {action}");
                PrintUsage();
                return 2;
        }
    }

    private static async Task<int> RunBootstrapCommand(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("A bootstrap action is required.");
            PrintUsage();
            return 2;
        }

        var action = args[1].ToLowerInvariant();
        var explicitPath = args.Length > 2 ? args[2] : null;
        var installation = SteamDiscovery.FindInstallation(explicitPath);

        return action switch
        {
            "status" => WriteJson(await BootstrapInstaller.GetStatusAsync(installation)),
            "install" => await InstallBootstrapAsync(installation),
            "uninstall" => WriteJson(await BootstrapInstaller.UninstallAsync(installation)),
            _ => UnknownBootstrapAction(action),
        };
    }

    private static async Task<int> InstallBootstrapAsync(GameInstallation installation)
    {
        var artifacts = ResolveBootstrapArtifacts();
        var bootstrap = await BootstrapInstaller.InstallAsync(
            installation,
            artifacts.Bootstrap,
            artifacts.Runtime);
        OfficialCatalogSyncResult? catalog = null;
        string? catalogWarning = null;
        try
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("OFS_SKIP_CATALOG_SYNC"),
                    "1",
                    StringComparison.Ordinal))
            {
                catalog = await OfficialCatalog.SyncAsync(installation);
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or
            HttpRequestException or CryptographicException or ArgumentException or JsonException)
        {
            catalogWarning =
                $"Loader installed, but the official catalog could not be refreshed: {exception.Message} " +
                "The in-game Mod Hub will retry automatically.";
        }
        return WriteJson(new BootstrapInstallResult(bootstrap, catalog, catalogWarning));
    }

    private static BootstrapArtifacts ResolveBootstrapArtifacts()
    {
        var candidates = new List<string>();
        if (Environment.ProcessPath is { Length: > 0 } processPath)
        {
            var executableDirectory = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(executableDirectory))
                candidates.Add(executableDirectory);
        }
        candidates.Add(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts")));

        foreach (var root in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bootstrap = Path.Combine(root, "bootstrap", "version.dll");
            var runtime = Path.Combine(root, "runtime");
            if (File.Exists(bootstrap) && Directory.Exists(runtime))
                return new BootstrapArtifacts(bootstrap, runtime);
        }
        throw new IOException(
            "OFS bootstrap artifacts were not found. Keep bootstrap/ and runtime/ " +
            "next to ofs-manager.exe, or run from an OFS-SDK checkout with artifacts built.");
    }

    private static int UnknownBootstrapAction(string action)
    {
        Console.Error.WriteLine($"Unknown bootstrap action: {action}");
        PrintUsage();
        return 2;
    }

    private static int WriteJson<T>(T value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
        return 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static void PrintUsage()
    {
        Console.WriteLine("OFS Manager CLI");
        Console.WriteLine($"Version {ModManifestValidator.CurrentSdkVersion.ToString(3)}");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  ofs-manager scan [game-directory]");
        Console.WriteLine("  ofs-manager --version");
        Console.WriteLine("  ofs-manager bootstrap status [game-directory]");
        Console.WriteLine("  ofs-manager bootstrap install [game-directory]");
        Console.WriteLine("  ofs-manager bootstrap uninstall [game-directory]");
        Console.WriteLine("  ofs-manager mod new <id> <directory> [display-name] [author]");
        Console.WriteLine("  ofs-manager mod diagnose [game-directory]");
        Console.WriteLine("  ofs-manager mod quarantine-list [game-directory]");
        Console.WriteLine("  ofs-manager mod quarantine-clear <mod-id|--all> [game-directory]");
        Console.WriteLine("  ofs-manager mod validate <directory|package.ofmod>");
        Console.WriteLine("  ofs-manager mod pack <directory> [package.ofmod]");
        Console.WriteLine("  ofs-manager mod install <directory|package.ofmod> [game-directory]");
        Console.WriteLine("  ofs-manager catalog validate <catalog.json|https-url>");
        Console.WriteLine("  ofs-manager catalog keygen <key-id> <private.pem> <public.pem>");
        Console.WriteLine("  ofs-manager catalog sign <catalog.json> <private.pem> <key-id> [signed.json]");
        Console.WriteLine("  ofs-manager catalog verify <signed.json> <public.pem> [expected-key-id]");
        Console.WriteLine("  ofs-manager catalog trust-add <key-id> <public.pem> [game-directory]");
        Console.WriteLine("  ofs-manager catalog trust-list [game-directory]");
        Console.WriteLine("  ofs-manager catalog trust-remove <key-id> [game-directory]");
        Console.WriteLine("  ofs-manager catalog resolve <catalog.json|https-url> <mod-id> [version-range]");
        Console.WriteLine("  ofs-manager catalog sync <catalog.json|https-url> [game-directory]");
        Console.WriteLine("  ofs-manager catalog thumbnail <signed-catalog|https-url> <mod-id> [game-directory]");
        Console.WriteLine("  ofs-manager catalog install <catalog.json|https-url> <mod-id> [game-directory]");
        Console.WriteLine("  ofs-manager profile list [game-directory]");
        Console.WriteLine("  ofs-manager profile enable <mod-id> [game-directory]");
        Console.WriteLine("  ofs-manager profile disable <mod-id> [game-directory]");
        Console.WriteLine("  ofs-manager profile discard [game-directory]");
    }

    private sealed record BootstrapArtifacts(string Bootstrap, string Runtime);

    private sealed record BootstrapInstallResult(
        BootstrapStatus Bootstrap,
        OfficialCatalogSyncResult? Catalog,
        string? CatalogWarning);
}
