using System.Text.Json;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed class ModConfig(string root) : IModConfig
{
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public bool Exists(string relativePath)
    {
        var path = Resolve(relativePath);
        lock (_gate)
        {
            return File.Exists(path);
        }
    }

    public T Load<T>(string relativePath, Func<T> createDefault)
    {
        ArgumentNullException.ThrowIfNull(createDefault);
        var path = Resolve(relativePath);
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                return createDefault();
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, _options)
                ?? throw new InvalidDataException($"Config '{relativePath}' deserialized to null.");
        }
    }

    public void Save<T>(string relativePath, T value)
    {
        var path = Resolve(relativePath);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = path + $".tmp-{Guid.NewGuid():N}";
            try
            {
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                {
                    JsonSerializer.Serialize(stream, value, _options);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    private string Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (!string.Equals(Path.GetExtension(relativePath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Mod config paths must use the .json extension.");
        }
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Mod config paths must be relative.");
        }

        var normalizedRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!resolved.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Mod config path escapes its assigned directory.");
        }
        return resolved;
    }
}
