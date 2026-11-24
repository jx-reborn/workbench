using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace JXReborn.ScriptMigrator.Generators;

/// <summary>
/// Fast O(1) index over all exported item templates in <c>pack-tieudaotu/data/items/*.items.json</c>
/// supporting Genre-4 <c>EventItem</c> lookups (by integer <c>detailType</c> or legacy item name),
/// <c>golden-equipment.items.json</c> 1-based <c>rowId</c> lookups, and general GDPL / GDP lookups.
/// </summary>
public sealed class PackItemCatalogIndex
{
    private readonly Dictionary<int, string> _eventItemCodeByDetailId;
    private readonly Dictionary<string, (int DetailType, string Code)> _eventItemByName;
    private readonly Dictionary<int, PackItemEntry> _goldEquipByRowId;
    private readonly Dictionary<(int Genre, int Detail, int Particular, int Level), PackItemEntry> _byGdpl;
    private readonly Dictionary<(int Genre, int Detail, int Particular), PackItemEntry> _byGdp;

    private PackItemCatalogIndex(
        Dictionary<int, string> eventItemCodeByDetailId,
        Dictionary<string, (int DetailType, string Code)> eventItemByName,
        Dictionary<int, PackItemEntry> goldEquipByRowId,
        Dictionary<(int Genre, int Detail, int Particular, int Level), PackItemEntry> byGdpl,
        Dictionary<(int Genre, int Detail, int Particular), PackItemEntry> byGdp,
        int totalCount)
    {
        _eventItemCodeByDetailId = eventItemCodeByDetailId;
        _eventItemByName = eventItemByName;
        _goldEquipByRowId = goldEquipByRowId;
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
        var eventNamePrimary = new Dictionary<string, (int DetailType, string Code)>(StringComparer.Ordinal);
        var eventNameFallback = new Dictionary<string, (int DetailType, string Code)>(StringComparer.Ordinal);
        var goldEquipByRowId = new Dictionary<int, PackItemEntry>();
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
                bool isGoldenEquipFile = Path.GetFileName(file).Equals("golden-equipment.items.json", StringComparison.OrdinalIgnoreCase);
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

                    string name = item.TryGetProperty("name", out JsonElement nEl) && nEl.ValueKind == JsonValueKind.String
                        ? nEl.GetString() ?? string.Empty
                        : string.Empty;
                    int rowId = item.TryGetProperty("rowId", out JsonElement rEl) && rEl.ValueKind == JsonValueKind.Number ? rEl.GetInt32() : 0;
                    int quality = item.TryGetProperty("quality", out JsonElement qEl) && qEl.ValueKind == JsonValueKind.Number ? qEl.GetInt32() : 0;
                    int genre = item.TryGetProperty("genre", out JsonElement gEl) && gEl.ValueKind == JsonValueKind.Number ? gEl.GetInt32() : -1;
                    int detail = item.TryGetProperty("detailType", out JsonElement dEl) && dEl.ValueKind == JsonValueKind.Number ? dEl.GetInt32() : -1;
                    int part = item.TryGetProperty("particularType", out JsonElement pEl) && pEl.ValueKind == JsonValueKind.Number ? pEl.GetInt32() : 0;
                    int level = item.TryGetProperty("level", out JsonElement lEl) && lEl.ValueKind == JsonValueKind.Number ? lEl.GetInt32() : 1;

                    byte gw = 1;
                    if (item.TryGetProperty("gridWidthCells", out JsonElement gwcEl) && gwcEl.ValueKind == JsonValueKind.Number)
                    {
                        gw = (byte)Math.Clamp(gwcEl.GetInt32(), 1, 10);
                    }
                    else if (item.TryGetProperty("gridWidth", out JsonElement gwEl) && gwEl.ValueKind == JsonValueKind.Number)
                    {
                        gw = (byte)Math.Clamp(gwEl.GetInt32(), 1, 10);
                    }

                    byte gh = 1;
                    if (item.TryGetProperty("gridHeightCells", out JsonElement ghcEl) && ghcEl.ValueKind == JsonValueKind.Number)
                    {
                        gh = (byte)Math.Clamp(ghcEl.GetInt32(), 1, 10);
                    }
                    else if (item.TryGetProperty("gridHeight", out JsonElement ghEl) && ghEl.ValueKind == JsonValueKind.Number)
                    {
                        gh = (byte)Math.Clamp(ghEl.GetInt32(), 1, 10);
                    }

                    ushort maxStack = 1;
                    if (item.TryGetProperty("maxStackCount", out JsonElement mscEl) && mscEl.ValueKind == JsonValueKind.Number)
                    {
                        maxStack = (ushort)Math.Clamp(mscEl.GetInt32(), 1, ushort.MaxValue);
                    }
                    else if (item.TryGetProperty("maxStack", out JsonElement msEl) && msEl.ValueKind == JsonValueKind.Number)
                    {
                        maxStack = (ushort)Math.Clamp(msEl.GetInt32(), 1, ushort.MaxValue);
                    }

                    if (genre < 0 || detail < 0)
                    {
                        continue;
                    }

                    totalCount++;
                    var entry = new PackItemEntry(code, genre, detail, part, level, gw, gh, maxStack, name, rowId);

                    if (isGoldenEquipFile && quality == 1 && rowId > 0)
                    {
                        goldEquipByRowId.TryAdd(rowId, entry);
                        continue;
                    }

                    byGdpl.TryAdd((genre, detail, part, level), entry);

                    if (!byGdp.TryGetValue((genre, detail, part), out PackItemEntry? existingGdp) ||
                        (existingGdp.Level != 1 && level == 1))
                    {
                        byGdp[(genre, detail, part)] = entry;
                    }

                    if (genre == 4)
                    {
                        string trimmedName = name.Trim();
                        if (part == 0)
                        {
                            eventByPartZero.TryAdd(detail, code);
                            if (trimmedName.Length > 0)
                            {
                                eventNamePrimary.TryAdd(trimmedName, (detail, code));
                            }
                        }
                        else
                        {
                            eventFallback.TryAdd(detail, code);
                            if (trimmedName.Length > 0)
                            {
                                eventNameFallback.TryAdd(trimmedName, (detail, code));
                            }
                        }
                    }
                }
            }
        }

        foreach (KeyValuePair<int, string> kv in eventFallback)
        {
            eventByPartZero.TryAdd(kv.Key, kv.Value);
        }

        foreach (KeyValuePair<string, (int DetailType, string Code)> kv in eventNameFallback)
        {
            eventNamePrimary.TryAdd(kv.Key, kv.Value);
        }

        return new PackItemCatalogIndex(eventByPartZero, eventNamePrimary, goldEquipByRowId, byGdpl, byGdp, totalCount);
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

    public bool TryResolveEventItemByName(string itemNameUtf8, out int detailType, out string itemCode)
    {
        if (!string.IsNullOrWhiteSpace(itemNameUtf8) &&
            _eventItemByName.TryGetValue(itemNameUtf8.Trim(), out (int DetailType, string Code) found))
        {
            detailType = found.DetailType;
            itemCode = found.Code;
            return true;
        }

        detailType = -1;
        itemCode = string.Empty;
        return false;
    }

    public bool TryResolveGoldEquipByRowId(int rowId, out PackItemEntry entry)
    {
        if (_goldEquipByRowId.TryGetValue(rowId, out PackItemEntry? found))
        {
            entry = found;
            return true;
        }

        entry = null!;
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
