using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using System.Text.Json;

namespace app.Services;

[SupportedOSPlatform("windows")]
public sealed class SearchHistoryService
{
    private const string PrefsKey = "search.history.v1";

    public static SearchHistoryService Instance { get; } = new();
    private SearchHistoryService() { }

    private ObservableCollection<string>? _items;
    public ObservableCollection<string> Items => _items ??= Load();

    private int MaxItems => AppSettings.SearchHistoryMaxItems;

    public void Push(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        var items = Items;
        var dup = items.FirstOrDefault(x =>
            string.Equals(x, query, StringComparison.OrdinalIgnoreCase));
        if (dup != null) items.Remove(dup);
        items.Insert(0, query);
        while (items.Count > MaxItems)
            items.RemoveAt(items.Count - 1);
        Save();
    }

    public void Remove(string query)
    {
        var dup = Items.FirstOrDefault(x =>
            string.Equals(x, query, StringComparison.OrdinalIgnoreCase));
        if (dup != null) { Items.Remove(dup); Save(); }
    }

    public void Clear() { Items.Clear(); Save(); }

    private ObservableCollection<string> Load()
    {
        try
        {
            var json = Preferences.Default.Get(PrefsKey, "[]");
            var list = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            return new ObservableCollection<string>(list);
        }
        catch { return []; }
    }

    private void Save()
    {
        try { Preferences.Default.Set(PrefsKey, JsonSerializer.Serialize(Items.ToList())); }
        catch { }
    }
}
