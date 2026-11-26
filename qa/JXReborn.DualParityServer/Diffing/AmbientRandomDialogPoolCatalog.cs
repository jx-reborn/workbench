using System.Text.Json;
using System.Text.RegularExpressions;

namespace JXReborn.DualParityServer.Diffing;

/// <summary>
/// Loads verified multi-line random NPC dialog pools from <c>ambient-npcs.dialogs.json</c>
/// and starter village quest-NPC fallback <c>random()</c> lore branches so independent PRNG draws
/// within the same verified pool are classified as <see cref="FindingSeverity.Warning"/> while
/// any cross-NPC or out-of-pool text remains <see cref="FindingSeverity.Critical"/>.
/// </summary>
public static class AmbientRandomDialogPoolCatalog
{
    private static readonly Lazy<(IReadOnlyList<HashSet<string>> AllPools, HashSet<string> DuKhachCalligraphyPool)> Pools = new(LoadPools);

    public static bool AreInSameVerifiedPool(string normalizedLegacyPrompt, string normalizedRebornPrompt)
    {
        if (string.IsNullOrWhiteSpace(normalizedLegacyPrompt) ||
            string.IsNullOrWhiteSpace(normalizedRebornPrompt))
        {
            return false;
        }

        foreach (HashSet<string> pool in Pools.Value.AllPools)
        {
            if (pool.Contains(normalizedLegacyPrompt) && pool.Contains(normalizedRebornPrompt))
            {
                return true;
            }
        }

        return false;
    }

    public static bool AreInDuKhachCalligraphyPool(string normalizedLegacyPrompt, string normalizedRebornPrompt)
    {
        if (string.IsNullOrWhiteSpace(normalizedLegacyPrompt) ||
            string.IsNullOrWhiteSpace(normalizedRebornPrompt))
        {
            return false;
        }

        HashSet<string> pool = Pools.Value.DuKhachCalligraphyPool;
        return pool.Contains(normalizedLegacyPrompt) && pool.Contains(normalizedRebornPrompt);
    }

    private static readonly string[] ValidDaTauObjectivePrefixes =
    [
        "Hãy đi <color=yellow>",
        "Hãy giúp ta đi tìm món này: <color=yellow>",
        "Ngươi hãy đến <color=yellow>",
        "Hãy đi nâng cấp <color=yellow>",
        "Hãy đi tìm giúp ta <color=yellow>",
    ];

    public static bool AreInDaTauRolledQuestPromptPool(string normalizedLegacyPrompt, string normalizedRebornPrompt) =>
        TryParseValidDaTauMainDialogPrompt(normalizedLegacyPrompt, out int legacyQuestNum) &&
        TryParseValidDaTauMainDialogPrompt(normalizedRebornPrompt, out int rebornQuestNum) &&
        legacyQuestNum == rebornQuestNum;

    public static bool IsDaTauPrisePrompt(string normalizedPrompt) =>
        !string.IsNullOrWhiteSpace(normalizedPrompt) &&
        normalizedPrompt.StartsWith("Ngươi vất vả quá, xin mời vị ", StringComparison.Ordinal) &&
        normalizedPrompt.EndsWith("chọn món mình thích đi!", StringComparison.Ordinal);

    private static bool TryParseValidDaTauMainDialogPrompt(string prompt, out int questNumber)
    {
        questNumber = 0;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        const string Prefix = "Đây là nhiệm vụ thứ <color=green>";
        const string Separator = "<color>,";

        int prefixIdx = prompt.IndexOf(Prefix, StringComparison.Ordinal);
        if (prefixIdx < 0)
        {
            return false;
        }

        int numStart = prefixIdx + Prefix.Length;
        int sepIdx = prompt.IndexOf(Separator, numStart, StringComparison.Ordinal);
        if (sepIdx <= numStart)
        {
            return false;
        }

        if (!int.TryParse(prompt.AsSpan(numStart, sepIdx - numStart), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out questNumber) ||
            questNumber <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> objective = prompt.AsSpan(sepIdx + Separator.Length).Trim();
        if (objective.IsEmpty)
        {
            return false;
        }

        foreach (string validPrefix in ValidDaTauObjectivePrefixes)
        {
            if (objective.StartsWith(validPrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static (IReadOnlyList<HashSet<string>> AllPools, HashSet<string> DuKhachCalligraphyPool) LoadPools()
    {
        var result = new List<HashSet<string>>();

        if (TryResolveAmbientDialogsJsonPath() is { } jsonPath)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(jsonPath));
                if (doc.RootElement.TryGetProperty("entries", out JsonElement entries) &&
                    entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement entry in entries.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("lines", out JsonElement linesEl) ||
                            linesEl.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        var set = new HashSet<string>(StringComparer.Ordinal);
                        foreach (JsonElement lineEl in linesEl.EnumerateArray())
                        {
                            if (lineEl.ValueKind == JsonValueKind.String)
                            {
                                string norm = NormalizePoolLine(lineEl.GetString());
                                if (!string.IsNullOrEmpty(norm))
                                {
                                    set.Add(norm);
                                }
                            }
                        }

                        if (set.Count >= 2)
                        {
                            result.Add(set);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Fall back to built-in verified quest fallback pools if JSON cannot be parsed.
            }
        }

        // Verified quest-NPC fallback random() pools from GeneratedNpcDialogScript.cs across all maps
        // (quest-gated NPCs are excluded from ambient-npcs.dialogs.json because their primary script has quest branches).
        LoadGeneratedQuestFallbackRandomPools(result);

        // Built-in fallback pools in case source tree is absent at runtime:
        AddPool(result,
            "Độc Cô phu nhân thật có phúc, tướng công là một đại anh hùng,thật khiến ta ngưỡng mộ",
            "Quan trọng nhất của một đời con gái chính là không nên gả nhầm người. Sau này ta cũng phải lấy một Đại hiệp có tình cảm dịu dàng");

        AddPool(result,
            "Ta muốn đi học kinh thư, nhưng cha ta không cho, nói con gái đi học không thể ra tiến cử công danh, chỉ là lãng phí tiền.",
            "Cha ta cứ đánh ta, nói ta là kẻ tiêu tiền, ngay cả lúc có mẹ ta cũng đánh. Nếu như ta có bản lĩnh như huynh, cha ta sẽ không còn đánh ta nữa");

        AddPool(result,
            "Nghe nói phu nhân của Độc Cô Đại Hiệp không chỉ rõ võ nghệ cao cường, mà còn là một mỹ nhân nữa",
            "Nghe nói Độc Cô Đại Hiệp và Độc Cô phu nhân vốn là kẻ thù, không ngờ lại yêu nhau vứt bỏ hiềm nghi,kết thành gia quyến. Trên giang hồ còn lưu lại một giai thoại đấy.",
            "Con gái Độc Cô Đại Hiệp Độc Cô Văn thường đến thị trấn chơi, là người nghịch ngợm lanh lợi");

        AddPool(result,
            "Khách quan! Đã đến Hoành sơn nhất định phải thử trà Vân Vụ ở chỗ chúng ta đấy.",
            "'Danh sơn danh thủy xuất danh trà', trà Vân Vụ là đặc sản của Hoành sơn chúng ta từ lâu đã thành danh, sớm đã được liệt vào danh sách cống phẩm đời Đường.",
            "Mùi vị trà Vân Vụ rất nồng, hương khí ngào ngạt, tinh khiết mà không nhạt, nồng mà không chát, pha lên 1 ấm trà, không chỉ có thể được giải khát mà còn có thể giải độc");

        // Verified quest-NPC fallback random() pools from server1/script/两湖区/巴陵县/npc/*.lua
        // (quest NPCs are excluded from ambient-npcs.dialogs.json because their primary script has quest branches).
        AddPool(result,
            "Bố cháu là ngư dân trên Động Đình hồ,mỗi ngày đều phải ra hồ đánh cá, nếu hôm nào gặp sóng to gió lớn, có khi đến mấy ngày liền không thể về nhà.",
            "Cháu rất lo cho bố, nếu cháu là con trai thì tốt quá, lúc đó cháu sẽ giúp bố đánh cá.");

        AddPool(result,
            "Các ngư dân thật là khổ! Ta có hai đứa con đi đánh cá trên hồ đều bị sóng lớn dìm chết.",
            "Đến người đánh cá cũng không có cá mà ăn, không đánh cá thì ngay cả đến cơm ăn cũng không có!");

        AddPool(result,
            "Con gái của ngư dân, cuối cùng cũng gả vào nhà ngư dân, làm vợ của ngư dân.",
            "Bố mẹ đã sắp xếp hôn sự cho em, nhưng em không muốn làm vợ một ngư dân, nhưng không còn cách nào khác, lời của cha mẹ không thể cãi lại.");

        AddPool(result,
            "Động Đình hồ này còn được gọi là 'Vân Mộng Trạch', phía Bắc giáp Trường Giang, Nam giáp bốn con sông Tương, Tư, Nguyên, Phong, danh hiệu là 'Bát Bách Lý Động Đình Hồ'.",
            "Động Đình hồ rộng mênh mông, núi non chót vót, đặc điểm lớn nhất là ngoài hồ có hồ, trong hồ có núi,cá nhiều vô số, lau sậy um tùm, nước trời một sắc, hải âu bay lượn. Xuân thu tứ quý luôn thay đổi.",
            "Du khách?Phạm Văn Chính Công trong bài 'Nhạc dương lầu ký' có viết?'Dữ quan phu Ba Lăng thắng trạng,tại Động Đình nhất hồ. Hàm viễn sơn, Thôn Trường Giang, hạo hạo thang thang, hoành vô tế nhai?triều huy tịch âm, khí tượng vạn thiên',Động Đình hồ này xứng đáng được tôn là 'Thiên hạ đệ nhất Động'!",
            "Ôi!...Bao giờ thì ta mới có thể tìm được một bức thư pháp quý như thế này?");

        // Du Khách calligraphy sale 2-page Talk (100 * (random(0, 5) + 10) -> 1000..1500 gold, concatenated by ProxyShadowClientState):
        var calligraphyPool = new HashSet<string>(StringComparer.Ordinal);
        for (int gold = 1000; gold <= 1500; gold += 100)
        {
            calligraphyPool.Add(NormalizePoolLine(
                $"Nếu ngươi đã đồng ý bán cho ta, thì không được đổi ý đấy! Để xem ta còn bao nhiêu tiền, 5, 10, 15, 20... Tốt rồi, đưa ngươi {gold}ngân lượng."));
            calligraphyPool.Add(NormalizePoolLine(
                $"Nếu ngươi đã đồng ý bán cho ta, thì không được đổi ý đấy! Để xem ta còn bao nhiêu tiền, 5, 10, 15, 20... \nTốt rồi, đưa ngươi {gold}ngân lượng."));
            calligraphyPool.Add(NormalizePoolLine(
                $"Nếu ngươi đã đồng ý bán cho ta, thì không được đổi ý đấy! Để xem ta còn bao nhiêu tiền, 5, 10, 15, 20...\nTốt rồi, đưa ngươi {gold}ngân lượng."));
            calligraphyPool.Add(NormalizePoolLine(
                $"Nếu ngươi đã đồng ý bán cho ta, thì không được đổi ý đấy! Để xem ta còn bao nhiêu tiền, 5, 10, 15, 20... Được rồi! Chỗ này ta có {gold} lượng, cầm lấy đi!"));
        }

        result.Add(calligraphyPool);
        return (result, calligraphyPool);
    }

    private static void AddPool(List<HashSet<string>> pools, params string[] lines)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            string norm = NormalizePoolLine(line);
            if (!string.IsNullOrEmpty(norm))
            {
                set.Add(norm);
            }
        }

        if (set.Count >= 2)
        {
            pools.Add(set);
        }
    }

    private static string NormalizePoolLine(string? text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static string? TryResolveAmbientDialogsJsonPath()
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? dir = new(start);
            while (dir is not null)
            {
                if (visited.Add(dir.FullName))
                {
                    string candidate = Path.Combine(dir.FullName, "pack-tieudaotu", "data", "dialogs", "ambient-npcs.dialogs.json");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    string distCandidate = Path.Combine(dir.FullName, "pack-tieudaotu", "dist", "server", "data", "dialogs", "ambient-npcs.dialogs.json");
                    if (File.Exists(distCandidate))
                    {
                        return distCandidate;
                    }
                }

                dir = dir.Parent;
            }
        }

        return null;
    }

    private static void LoadGeneratedQuestFallbackRandomPools(List<HashSet<string>> pools)
    {
        string? genPath = TryResolveGeneratedNpcDialogScriptPath();
        if (genPath is null)
        {
            return;
        }

        try
        {
            string source = File.ReadAllText(genPath);
            string[] methods = source.Split("private static ValueTask S", StringSplitOptions.RemoveEmptyEntries);
            Regex literalCallRegex = new(
                @"context\.(?:Say|Talk)\(\s*\[?\s*""((?:\\.|[^""\\])*)""\s*\]?\s*(?:,\s*null)?\s*\)",
                RegexOptions.CultureInvariant);

            foreach (string method in methods)
            {
                int rngIdx = method.IndexOf("ScriptContextRandom.NextInclusive(", StringComparison.Ordinal);
                if (rngIdx < 0)
                {
                    continue;
                }

                string rngTail = method[rngIdx..];
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match m in literalCallRegex.Matches(rngTail))
                {
                    string unescaped = Regex.Unescape(m.Groups[1].Value);
                    string norm = NormalizePoolLine(unescaped);
                    if (!string.IsNullOrEmpty(norm))
                    {
                        set.Add(norm);
                    }
                }

                if (set.Count >= 2)
                {
                    pools.Add(set);
                }
            }
        }
        catch (Exception)
        {
            // Ignore source-inspection failures and rely on built-in pools.
        }
    }

    private static string? TryResolveGeneratedNpcDialogScriptPath()
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? dir = new(start);
            while (dir is not null)
            {
                if (visited.Add(dir.FullName))
                {
                    string candidate = Path.Combine(
                        dir.FullName,
                        "pack-tieudaotu",
                        "src",
                        "JXReborn.Packs.TieuDaoTu",
                        "Scripts",
                        "Generated",
                        "GeneratedNpcDialogScript.cs");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                dir = dir.Parent;
            }
        }

        return null;
    }
}
