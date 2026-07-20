using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed class LocalizationApi : ILocalizationApi
{
    private static readonly Dictionary<string, OwnedTerm> OwnedTerms =
        new(StringComparer.Ordinal);

    private readonly string _ownerId;
    private readonly IUnsafeIl2CppApi _api;
    private readonly nint _managerClass;
    private readonly nint _sourceClass;
    private readonly nint _termClass;

    public LocalizationApi(string ownerId, IUnsafeIl2CppApi api)
    {
        _ownerId = ownerId;
        _api = api;
        _managerClass = RequireClass("I2.Loc", "LocalizationManager");
        _sourceClass = RequireClass("I2.Loc", "LanguageSourceData");
        _termClass = RequireClass("I2.Loc", "TermData");
    }

    public string CurrentLanguage => ReadManagerString("get_CurrentLanguage");
    public string CurrentLanguageCode => ReadManagerString("get_CurrentLanguageCode");

    public string GetTerm(string key)
    {
        ValidateKey(key);
        return $"Mods/{_ownerId}/{key.Trim('/')}";
    }

    public string Translate(string term, string? languageCode = null) =>
        TryTranslate(term, out var translation, languageCode) ? translation : term;

    public unsafe bool TryTranslate(
        string term,
        out string translation,
        string? languageCode = null)
    {
        EnsureMainThread();
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        Initialize();
        var source = GetSource(term, fallbackToFirst: false);
        if (source == 0 || GetTermData(source, term) == 0)
        {
            translation = string.Empty;
            return false;
        }

        var overrideLanguage = string.IsNullOrWhiteSpace(languageCode)
            ? 0
            : GetLanguageName(languageCode);
        if (!string.IsNullOrWhiteSpace(languageCode) && overrideLanguage == 0)
        {
            translation = string.Empty;
            return false;
        }

        byte skipDisabled = 0;
        byte allowCategoryMismatch = 0;
        nint* arguments = stackalloc nint[5];
        arguments[0] = _api.NewString(term);
        arguments[1] = overrideLanguage;
        arguments[2] = 0;
        arguments[3] = (nint)(&skipDisabled);
        arguments[4] = (nint)(&allowCategoryMismatch);
        var result = _api.RuntimeInvoke(
            RequireMethod(_sourceClass, "GetTranslation", 5),
            source,
            (nint)arguments);
        if (result == 0)
        {
            translation = string.Empty;
            return false;
        }

        translation = _api.ReadString(result);
        return true;
    }

    public ILocalizationRegistration Register(LocalizationTermDefinition definition)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(definition);
        ValidateKey(definition.Key);
        ArgumentNullException.ThrowIfNull(definition.Translations);
        if (definition.Translations.Count == 0)
        {
            throw new ArgumentException("A localization term needs at least one translation.", nameof(definition));
        }
        if (definition.Translations.Count > 100)
        {
            throw new ArgumentException("A localization term has too many translations.", nameof(definition));
        }

        Initialize();
        var term = GetTerm(definition.Key);
        if (OwnedTerms.TryGetValue(term, out var owned))
        {
            throw new InvalidOperationException(
                $"Localization term '{term}' is already owned by mod '{owned.OwnerId}'.");
        }

        var source = GetSource(term, fallbackToFirst: true);
        if (source == 0)
        {
            throw new InvalidOperationException("I2 Localization has no initialized language source.");
        }
        if (GetTermData(source, term) != 0)
        {
            throw new InvalidOperationException(
                $"Localization term '{term}' already exists in the game language source.");
        }

        var resolved = new List<(int Index, string Translation)>();
        foreach (var (languageCode, value) in definition.Translations)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);
            ArgumentNullException.ThrowIfNull(value);
            var languageIndex = GetLanguageIndex(source, languageCode);
            if (languageIndex < 0)
            {
                throw new ArgumentException(
                    $"Language code '{languageCode}' is not available in the game.",
                    nameof(definition));
            }
            if (resolved.Any(candidate => candidate.Index == languageIndex))
            {
                throw new ArgumentException(
                    $"Multiple language codes resolve to I2 language index {languageIndex}.",
                    nameof(definition));
            }
            resolved.Add((languageIndex, value));
        }

        var termData = AddTerm(source, term);
        try
        {
            foreach (var (index, value) in resolved)
            {
                SetTranslation(termData, index, value);
            }
            UpdateDictionary(source);
            var registration = new Registration(this, definition.Key, term);
            OwnedTerms.Add(term, new OwnedTerm(_ownerId, source, registration));
            Refresh();
            return registration;
        }
        catch
        {
            RemoveTerm(source, term);
            UpdateDictionary(source);
            throw;
        }
    }

    public unsafe void Refresh()
    {
        EnsureMainThread();
        byte force = 1;
        nint* arguments = stackalloc nint[1];
        arguments[0] = (nint)(&force);
        _ = _api.RuntimeInvoke(
            RequireMethod(_managerClass, "LocalizeAll", 1),
            0,
            (nint)arguments);
    }

    private string ReadManagerString(string getter)
    {
        EnsureMainThread();
        Initialize();
        var result = _api.RuntimeInvoke(RequireMethod(_managerClass, getter, 0), 0, 0);
        return result == 0 ? string.Empty : _api.ReadString(result);
    }

    private void Initialize() =>
        _ = _api.RuntimeInvoke(RequireMethod(_managerClass, "InitializeIfNeeded", 0), 0, 0);

    private unsafe nint GetSource(string term, bool fallbackToFirst)
    {
        byte fallback = fallbackToFirst ? (byte)1 : (byte)0;
        nint* arguments = stackalloc nint[2];
        arguments[0] = _api.NewString(term);
        arguments[1] = (nint)(&fallback);
        return _api.RuntimeInvoke(
            RequireMethod(_managerClass, "GetSourceContaining", 2),
            0,
            (nint)arguments);
    }

    private unsafe nint GetTermData(nint source, string term)
    {
        byte allowCategoryMismatch = 0;
        nint* arguments = stackalloc nint[2];
        arguments[0] = _api.NewString(term);
        arguments[1] = (nint)(&allowCategoryMismatch);
        return _api.RuntimeInvoke(
            RequireMethod(_sourceClass, "GetTermData", 2),
            source,
            (nint)arguments);
    }

    private unsafe int GetLanguageIndex(nint source, string languageCode)
    {
        var exact = InvokeLanguageIndex(source, languageCode, exactMatch: true);
        return exact >= 0 ? exact : InvokeLanguageIndex(source, languageCode, exactMatch: false);
    }

    private unsafe int InvokeLanguageIndex(nint source, string languageCode, bool exactMatch)
    {
        byte exact = exactMatch ? (byte)1 : (byte)0;
        byte ignoreDisabled = 0;
        nint* arguments = stackalloc nint[3];
        arguments[0] = _api.NewString(languageCode);
        arguments[1] = (nint)(&exact);
        arguments[2] = (nint)(&ignoreDisabled);
        var boxed = _api.RuntimeInvoke(
            RequireMethod(_sourceClass, "GetLanguageIndexFromCode", 3),
            source,
            (nint)arguments);
        var value = _api.Unbox(boxed);
        return value == 0 ? -1 : Marshal.ReadInt32(value);
    }

    private unsafe nint GetLanguageName(string languageCode)
    {
        byte exact = 0;
        nint* arguments = stackalloc nint[2];
        arguments[0] = _api.NewString(languageCode);
        arguments[1] = (nint)(&exact);
        return _api.RuntimeInvoke(
            RequireMethod(_managerClass, "GetLanguageFromCode", 2),
            0,
            (nint)arguments);
    }

    private unsafe nint AddTerm(nint source, string term)
    {
        nint* arguments = stackalloc nint[1];
        arguments[0] = _api.NewString(term);
        var result = _api.RuntimeInvoke(
            RequireMethod(_sourceClass, "AddTerm", 1),
            source,
            (nint)arguments);
        return result != 0
            ? result
            : throw new InvalidOperationException($"I2 Localization did not create term '{term}'.");
    }

    private unsafe void SetTranslation(nint termData, int languageIndex, string translation)
    {
        nint* arguments = stackalloc nint[3];
        arguments[0] = (nint)(&languageIndex);
        arguments[1] = _api.NewString(translation);
        arguments[2] = 0;
        _ = _api.RuntimeInvoke(
            RequireMethod(_termClass, "SetTranslation", 3),
            termData,
            (nint)arguments);
    }

    private unsafe void RemoveTerm(nint source, string term)
    {
        nint* arguments = stackalloc nint[1];
        arguments[0] = _api.NewString(term);
        _ = _api.RuntimeInvoke(
            RequireMethod(_sourceClass, "RemoveTerm", 1),
            source,
            (nint)arguments);
    }

    private unsafe void UpdateDictionary(nint source)
    {
        byte force = 1;
        nint* arguments = stackalloc nint[1];
        arguments[0] = (nint)(&force);
        _ = _api.RuntimeInvoke(
            RequireMethod(_sourceClass, "UpdateDictionary", 1),
            source,
            (nint)arguments);
    }

    private void Unregister(Registration registration)
    {
        EnsureMainThread();
        if (!registration.IsRegistered)
        {
            return;
        }
        if (!OwnedTerms.TryGetValue(registration.Term, out var owned) ||
            owned.OwnerId != _ownerId ||
            !ReferenceEquals(owned.Registration, registration))
        {
            throw new InvalidOperationException(
                $"Mod '{_ownerId}' no longer owns localization term '{registration.Term}'.");
        }

        RemoveTerm(owned.Source, registration.Term);
        UpdateDictionary(owned.Source);
        OwnedTerms.Remove(registration.Term);
        registration.MarkUnregistered();
        Refresh();
    }

    internal void RemoveAll()
    {
        var registrations = OwnedTerms.Values
            .Where(value => value.OwnerId == _ownerId)
            .Select(value => value.Registration)
            .ToArray();
        if (registrations.Length == 0) return;
        EnsureMainThread();
        foreach (var registration in registrations)
        {
            Unregister(registration);
        }
    }

    private nint RequireClass(string namespaze, string name)
    {
        var klass = _api.FindClass("Assembly-CSharp.dll", namespaze, name);
        return klass != 0
            ? klass
            : throw new TypeLoadException($"Game class '{namespaze}.{name}' was not found.");
    }

    private nint RequireMethod(nint klass, string name, int argumentCount)
    {
        var method = _api.FindMethod(klass, name, argumentCount);
        return method != 0
            ? method
            : throw new MissingMethodException($"Game method '{name}/{argumentCount}' was not found.");
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 160 || key.Contains("..", StringComparison.Ordinal) || key.Contains('\\'))
        {
            throw new ArgumentException(
                "Localization key must be at most 160 characters and cannot contain '..' or backslashes.",
                nameof(key));
        }
    }

    private static void EnsureMainThread()
    {
        if (!ModRuntime.IsMainThread)
        {
            throw new InvalidOperationException(
                "Localization API calls must run on Unity's main thread. Use a lifecycle event or MainThread.Post().");
        }
    }

    private sealed record OwnedTerm(
        string OwnerId,
        nint Source,
        Registration Registration);

    private sealed class Registration(
        LocalizationApi owner,
        string key,
        string term) : ILocalizationRegistration
    {
        public string Key { get; } = key;
        public string Term { get; } = term;
        public bool IsRegistered { get; private set; } = true;

        public void Unregister() => owner.Unregister(this);
        public void Dispose() => Unregister();
        public void MarkUnregistered() => IsRegistered = false;
    }
}
