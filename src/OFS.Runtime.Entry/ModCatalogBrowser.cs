using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed class ModCatalogBrowser
{
    private readonly IReadOnlyList<ModCatalogEntry> _entries;
    private readonly int _pageSize;
    private IReadOnlyList<ModCatalogEntry> _filtered;

    public ModCatalogBrowser(IReadOnlyList<ModCatalogEntry> entries, int pageSize = 2)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        _entries = entries.ToArray();
        _filtered = _entries;
        _pageSize = pageSize;
    }

    public string Query { get; private set; } = string.Empty;
    public int PageIndex { get; private set; }
    public int PageCount => Math.Max(1, (_filtered.Count + _pageSize - 1) / _pageSize);
    public int MatchCount => _filtered.Count;
    public ModCatalogEntry? Selected { get; private set; }

    public IReadOnlyList<ModCatalogEntry> PageEntries => _filtered
        .Skip(PageIndex * _pageSize)
        .Take(_pageSize)
        .ToArray();

    public void SetQuery(string? query)
    {
        Query = (query ?? string.Empty).TrimStart();
        var searchTerm = Query.Trim();
        _filtered = searchTerm.Length == 0
            ? _entries
            : _entries.Where(entry => Matches(entry, searchTerm)).ToArray();
        PageIndex = 0;
        Selected = null;
    }

    public bool NextPage()
    {
        if (PageIndex + 1 >= PageCount)
        {
            return false;
        }

        PageIndex++;
        Selected = null;
        return true;
    }

    public bool PreviousPage()
    {
        if (PageIndex == 0)
        {
            return false;
        }

        PageIndex--;
        Selected = null;
        return true;
    }

    public bool Select(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Selected = _filtered.FirstOrDefault(entry =>
            string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
        return Selected is not null;
    }

    public void BackToResults() => Selected = null;

    private static bool Matches(ModCatalogEntry entry, string query)
    {
        if (Contains(entry.Name, query) ||
            Contains(entry.Id, query) ||
            Contains(entry.Author, query) ||
            Contains(entry.Summary, query) ||
            Contains(entry.Multiplayer, query))
        {
            return true;
        }

        return entry.Capabilities.Any(value => Contains(value, query));
    }

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
}
