using System.Text;
using System.Text.Json;
using OFS.Sdk;

namespace OFS.Manager;

internal static class ModProjectScaffolder
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
    };

    public static async Task<ModProjectCreationResult> CreateAsync(
        string id,
        string outputDirectory,
        string? displayName,
        string? author)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var name = string.IsNullOrWhiteSpace(displayName) ? id : displayName.Trim();
        var namespaceSuffix = ToIdentifier(id);
        var rootNamespace = $"OFS.Mods.{namespaceSuffix}";
        var assemblyName = $"OFS.Mod.{namespaceSuffix}";
        var entryPoint = $"{rootNamespace}.Mod";
        var manifest = new ModManifest
        {
            Id = id,
            Name = name,
            Version = "0.1.0",
            Description = $"An Ore Factory Squad mod named {name}.",
            Author = author?.Trim() ?? string.Empty,
            Assembly = $"{assemblyName}.dll",
            EntryPoint = entryPoint,
            SdkVersion = ModManifestValidator.CurrentSdkVersion.ToString(3),
            Capabilities = ["ui.main-menu"],
            Multiplayer = "client",
        };
        var errors = ModManifestValidator.Validate(manifest);
        if (errors.Count != 0)
        {
            throw new InvalidDataException(string.Join(" ", errors));
        }

        var output = Path.GetFullPath(outputDirectory);
        if (File.Exists(output) || Directory.Exists(output))
        {
            throw new IOException($"Output path already exists: '{output}'.");
        }

        var parent = Path.GetDirectoryName(output)
            ?? throw new InvalidDataException("Output path has no parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(output)}.tmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var sourceDirectory = Path.Combine(staging, "src");
            Directory.CreateDirectory(sourceDirectory);
            await WriteTextAsync(
                Path.Combine(staging, $"{assemblyName}.csproj"),
                CreateProjectFile(assemblyName, rootNamespace));
            await WriteTextAsync(
                Path.Combine(sourceDirectory, "Mod.cs"),
                CreateModSource(rootNamespace));
            await WriteTextAsync(
                Path.Combine(staging, "manifest.json"),
                JsonSerializer.Serialize(manifest, ManifestJson) + Environment.NewLine);
            await WriteTextAsync(
                Path.Combine(staging, ".gitignore"),
                "bin/\nobj/\ndist/\npackages/\n");
            await WriteTextAsync(
                Path.Combine(staging, "README.md"),
                CreateReadme(id, name, assemblyName));

            await PromoteDirectoryAsync(staging, output);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                await DeleteDirectoryWithRetryAsync(staging);
            }
        }

        return new ModProjectCreationResult(
            output,
            id,
            name,
            assemblyName,
            entryPoint,
            "Created. Build Release, then validate and pack the dist directory.");
    }

    private static string CreateProjectFile(string assemblyName, string rootNamespace)
    {
        var sdkVersion = ModManifestValidator.CurrentSdkVersion.ToString(3);
        return $$"""
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
            <AssemblyName>{{assemblyName}}</AssemblyName>
            <RootNamespace>{{rootNamespace}}</RootNamespace>
            <OFSSdkVersion>{{sdkVersion}}</OFSSdkVersion>
            <OutputPath>dist\</OutputPath>
            <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
          </PropertyGroup>

          <ItemGroup Condition="'$(OFSSdkProject)' != ''">
            <ProjectReference Include="$(OFSSdkProject)" Private="false" />
          </ItemGroup>

          <ItemGroup Condition="'$(OFSSdkProject)' == ''">
            <PackageReference Include="OFS.Sdk" Version="$(OFSSdkVersion)" ExcludeAssets="runtime" />
          </ItemGroup>

          <ItemGroup>
            <None Include="manifest.json" CopyToOutputDirectory="PreserveNewest" />
          </ItemGroup>

        </Project>
        """ + Environment.NewLine;
    }

    private static string CreateModSource(string rootNamespace) =>
        $$"""
        using OFS.Sdk;

        namespace {{rootNamespace}};

        public sealed class Mod : IOFSMod
        {
            private IModContext? _context;
            private IMenuPanel? _panel;

            public void Load(IModContext context)
            {
                _context = context;
                context.Events.MainMenuReady += OnMainMenuReady;
                context.Log.Info("Mod loaded.");
            }

            public void Unload()
            {
                _panel?.Dispose();
                _context?.Log.Info("Mod unloaded.");
            }

            private void OnMainMenuReady(IMainMenuApi menu)
            {
                var context = _context
                    ?? throw new InvalidOperationException("Mod context is unavailable.");
                _panel = menu.AddPanel(new MainMenuPanelDefinition("main", context.Mod.Name));
                _panel.AddText(new MenuPanelTextDefinition("status", "MOD LOADED"));
                _panel.AddButton(new MenuPanelButtonDefinition("close", "CLOSE", _panel.Close));
                menu.AddButton(new MainMenuButtonDefinition("open", context.Mod.Name, _panel.Show));
            }
        }
        """ + Environment.NewLine;

    private static string CreateReadme(string id, string name, string assemblyName) =>
        $$"""
        # {{name}}

        Ore Factory Squad mod id: `{{id}}`.

        ## Build

        After `OFS.Sdk` is published to the configured NuGet source:

        ```powershell
        dotnet build -c Release
        ```

        While developing inside an OFS-SDK checkout, reference its project explicitly:

        ```powershell
        dotnet build -c Release -p:OFSSdkProject=C:\path\to\OFS-SDK\src\OFS.Sdk\OFS.Sdk.csproj
        ```

        The package root is `dist`. Validate and pack it with OFS Manager:

        ```powershell
        ofs-manager mod validate dist
        ofs-manager mod pack dist packages\{{id}}-0.1.0.ofmod
        ```

        The entry assembly is `{{assemblyName}}.dll`. Do not copy `OFS.Sdk.dll` into
        `dist`; the framework supplies that shared contract assembly at runtime.
        """ + Environment.NewLine;

    private static string ToIdentifier(string id)
    {
        var builder = new StringBuilder(id.Length);
        var uppercaseNext = true;
        foreach (var character in id)
        {
            if (!char.IsAsciiLetterOrDigit(character))
            {
                uppercaseNext = true;
                continue;
            }

            var candidate = uppercaseNext ? char.ToUpperInvariant(character) : character;
            if (builder.Length == 0 && char.IsAsciiDigit(candidate))
            {
                builder.Append('M');
            }
            builder.Append(candidate);
            uppercaseNext = false;
        }

        return builder.Length != 0
            ? builder.ToString()
            : throw new InvalidDataException("Mod id cannot produce a C# identifier.");
    }

    private static async Task WriteTextAsync(string path, string content)
    {
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static async Task PromoteDirectoryAsync(string source, string destination)
    {
        for (var attempt = 0; ; ++attempt)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception exception) when (
                (exception is IOException or UnauthorizedAccessException) && attempt < 7)
            {
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    throw new IOException($"Output path appeared during creation: '{destination}'.", exception);
                }
                await Task.Delay(25 * (attempt + 1));
            }
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        for (var attempt = 0; ; ++attempt)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (
                (exception is IOException or UnauthorizedAccessException) && attempt < 7)
            {
                await Task.Delay(25 * (attempt + 1));
            }
        }
    }
}

internal sealed record ModProjectCreationResult(
    string Directory,
    string Id,
    string Name,
    string Assembly,
    string EntryPoint,
    string Detail);
