using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace JXReborn.ScriptMigrator.Generators;

/// <summary>
/// Fast O(1) index over all exported item templates in <c>pack-tieudaotu/data/items/*.items.json</c>
/// supporting both Genre-4 <c>EventItem</c> lookups and general <c>(Genre, Detail, Particular, Level)</c>
/// / <c>(Genre, Detail, Particular)</c> item lookups.
/// </summary>
public sealed class PackItemCatalogIndex
{
    private readonly Dictionary<int, string> _eventItemCodeByDetailId;
    private readonly Dictionary<(int Genre, int Detail, int Particular, int Level), PackItemEntry> _byGdpl;
    private readonly Dictionary<(int Genre, int Detail, int Particular), PackItemEntry> _byGdp;

    private PackItemCatalogIndex(
        Dictionary<int, string> eventItemCodeByDetailId,
        Dictionary<(int Genre, int Detail, int Particular, int Level), PackItemEntry> byGdpl,
        Dictionary<(int Genre, int Detail, int Particular), PackItemEntry> byGdp,
        int totalCount)
    {
        _eventItemCodeByDetailId = eventItemCodeByDetailId;
        _byGdpl = byGdpl;
        _byGdp = byGdp;
        TotalItemCount = totalCount;
    }

    public IReadOnlyDictionary<int, string> EventItemCodeByDetailId => _eventItemCodeByDetailId;

    public int TotalItemCount { get; }

    public static PackItemCatalogIndex Load(string packRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packRoot);

        var eventByPartZero = new Dictionary<int, string>();
        var eventFallback = new Dictionary<int, string>();
        var byGdpl = new Dictionary<(int, int, int, int), PackItemEntry>();
        var byGdp = new Dictionary<(int, int, int), PackItemEntry>();
        int totalCount = 0;

        string itemsDir = Path.Combine(packRoot, "data", "items");
        if (Directory.Exists(itemsDir))
        {
            string[] files = Directory.GetFiles(itemsDir, "*.items.json", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (string file in files)
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(file));
                if (!doc.RootElement.TryGetProperty("items", out JsonElement items) ||
                    items.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement item in items.EnumerateArray())
                {
                    string? code = item.TryGetProperty("code", out JsonElement cEl) && cEl.ValueKind == JsonValueKind.String
                        ? cEl.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        continue;
                    }

                    int genre = item.TryGetProperty("genre", out JsonElement gEl) && gEl.ValueKind == JsonValueKind.Number ? gEl.GetInt32() : -1;
                    int detail = item.TryGetProperty("detailType", out JsonElement dEl) && dEl.ValueKind == JsonValueKind.Number ? dEl.GetInt32() : -1;
                    int part = item.TryGetProperty("particularType", out JsonElement pEl) && pEl.ValueKind == JsonValueKind.Number ? pEl.GetInt32() : 0;
                    int level = item.TryGetProperty("level", out JsonElement lEl) && lEl.ValueKind == JsonValueKind.Number ? lEl.GetInt32() : 1;
                    byte gw = item.TryGetProperty("gridWidth", out JsonElement gwEl) && gwEl.ValueKind == JsonValueKind.Number
                        ? (byte)Math.Clamp(gwEl.GetInt32(), 1, 10)
                        : (byte)1;
                    byte gh = item.TryGetProperty("gridHeight", out JsonElement ghEl) && ghEl.ValueKind == JsonValueKind.Number
                        ? (byte)Math.Clamp(ghEl.GetInt32(), 1, 10)
                        : (byte)1;
                    ushort maxStack = item.TryGetProperty("maxStack", out JsonElement msEl) && msEl.ValueKind == JsonValueKind.Number
                        ? (ushort)Math.Clamp(msEl.GetInt32(), 1, ushort.MaxValue)
                        : (ushort)1;

                    if (genre < 0 || detail < 0)
                    {
                        continue;
                    }

                    totalCount++;
                    var entry = new PackItemEntry(code, genre, detail, part, level, gw, gh, maxStack);
                    byGdpl.TryAdd((genre, detail, part, level), entry);

                    if (!byGdp.TryGetValue((genre, detail, part), out PackItemEntry? existingGdp) ||
                        (existingGdp.Level != 1 && level == 1))
                    {
                        byGdp[(genre, detail, part)] = entry;
                    }

                    if (genre == 4)
                    {
                        if (part == 0)
                        {
                            eventByPartZero.TryAdd(detail, code);
                        }
                        else
                        {
                            eventFallback.TryAdd(detail, code);
                        }
                    }
                }
            }
        }

        foreach (KeyValuePair<int, string> kv in eventFallback)
        {
            eventByPartZero.TryAdd(kv.Key, kv.Value);
        }

        return new PackItemCatalogIndex(eventByPartZero, byGdpl, byGdp, totalCount);
    }

    public bool TryResolveEventItem(int detailType, out string itemCode)
    {
        if (_eventItemCodeByDetailId.TryGetValue(detailType, out string? found))
        {
            itemCode = found;
            return true;
        }

        itemCode = string.Empty;
        return false;
    }

    public bool TryResolveByGdpl(int genre, int detailType, int particularType, int level, out PackItemEntry entry)
    {
        if (_byGdpl.TryGetValue((genre, detailType, particularType, level), out PackItemEntry? exact))
        {
            entry = exact;
            return true;
        }

        // In legacy Lua, level == -1 or 0 for non-equipment (genre 4 / 6) resolves to the single GDP template
        if ((level <= 0 || genre is 4 or 6) &&
            _byGdp.TryGetValue((genre, detailType, particularType), out PackItemEntry? fallback))
        {
            entry = fallback;
            return true;
        }

        entry = null!;
        return false;
    }

    public bool TryResolveByGdp(int genre, int detailType, int particularType, out PackItemEntry entry)
    {
        if (_byGdp.TryGetValue((genre, detailType, particularType), out PackItemEntry? found))
        {
            entry = found;
            return true;
        }

        entry = null!;
        return false;
    }
}
