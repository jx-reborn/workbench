using System.Collections.Frozen;
using System.Text.Json;
using JXReborn.WireParityProxy.Navigation;

namespace JXReborn.WireParityProxy.ShadowStates;

/// <summary>
/// Lazily loads <c>data/system/string-resources.json</c> exported from <c>server1/settings/stringresource.txt</c>
/// so numeric <c>Say(nStringId, ...)</c> and <c>Talk(n, cb, nStringId)</c> packets (<c>Param1 = 1</c>) decode to
/// human-readable Vietnamese text identically on both Primary and Shadow.
/// </summary>
internal static class ProxyStringResourceTable
{
    private static readonly Lazy<FrozenDictionary<int, string>> CachedTable =
        new(LoadCore, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool TryResolve(int stringId, out string text) =>
        CachedTable.Value.TryGetValue(stringId, out text!);

    private static FrozenDictionary<int, string> LoadCore()
    {
        string? packRoot = ProxyMapCatalogLoader.ResolvePackRootDirectory();
        if (string.IsNullOrWhiteSpace(packRoot))
        {
            return FrozenDictionary<int, string>.Empty;
        }

        string jsonPath = Path.Combine(packRoot, "data", "system", "string-resources.json");
        if (!File.Exists(jsonPath))
        {
            return FrozenDictionary<int, string>.Empty;
        }

        try
        {
            using FileStream stream = File.OpenRead(jsonPath);
            using JsonDocument doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("entries", out JsonElement entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                return FrozenDictionary<int, string>.Empty;
            }

            var map = new Dictionary<int, string>();
            foreach (JsonElement item in entries.EnumerateArray())
            {
                if (item.TryGetProperty("id", out JsonElement idProp) &&
                    idProp.TryGetInt32(out int id) &&
                    item.TryGetProperty("text", out JsonElement textProp) &&
                    textProp.GetString() is { } text)
                {
                    map[id] = text;
                }
            }

            return map.ToFrozenDictionary();
        }
        catch
        {
            return FrozenDictionary<int, string>.Empty;
        }
    }
}
