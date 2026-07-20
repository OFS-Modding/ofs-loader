using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed class ModRegistry(Func<IReadOnlyList<LoadedModDescriptor>> snapshot) : IModRegistry
{
    public IReadOnlyList<LoadedModDescriptor> Loaded =>
        Array.AsReadOnly(snapshot().ToArray());

    public bool IsLoaded(string modId) => Get(modId) is not null;

    public LoadedModDescriptor? Get(string modId)
    {
        ValidateLookup(modId, nameof(modId));
        return snapshot().FirstOrDefault(candidate =>
            string.Equals(candidate.Mod.Id, modId, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<LoadedModDescriptor> FindByCapability(string capability)
    {
        ValidateLookup(capability, nameof(capability));
        return Array.AsReadOnly(snapshot()
            .Where(candidate => candidate.HasCapability(capability))
            .ToArray());
    }

    private static void ValidateLookup(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 3 or > 80 || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new ArgumentException(
                "The lookup must be 3-80 ASCII letters, digits, dots, dashes or underscores.",
                parameterName);
        }
    }
}
