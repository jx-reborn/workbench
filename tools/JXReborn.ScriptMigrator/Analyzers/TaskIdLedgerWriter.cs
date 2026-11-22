using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JXReborn.ScriptMigrator.Models;

namespace JXReborn.ScriptMigrator.Analyzers;

/// <summary>
/// Emits the global TaskId / TempTaskId / GlobalValueId ledger (<c>lua-task-id-registry.md</c>)
/// directly from the 100% static analysis manifest so no TaskId collisions occur during migration.
/// </summary>
public sealed class TaskIdLedgerWriter
{
    public void WriteLedger(ScriptInventoryManifest manifest, string outputMarkdownPath)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var taskMap = new SortedDictionary<int, List<string>>();
        var tempTaskMap = new SortedDictionary<int, List<string>>();
        var globalValueMap = new SortedDictionary<int, List<string>>();

        foreach (ScriptInventoryEntry entry in manifest.Entries.Where(e => !e.IsExcludedSimBot))
        {
            foreach (int id in entry.ReferencedTaskIds)
            {
                AddUsage(taskMap, id, entry.Utf8DisplayPath);
            }

            foreach (int id in entry.ReferencedTempTaskIds)
            {
                AddUsage(tempTaskMap, id, entry.Utf8DisplayPath);
            }

            foreach (int id in entry.ReferencedGlobalValueIds)
            {
                AddUsage(globalValueMap, id, entry.Utf8DisplayPath);
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Global Legacy TaskId / TempTaskId / GlobalValueId Ledger");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC)**: `{manifest.GeneratedAtUtc}`");
        sb.AppendLine($"- **Unique Persistent `TaskId`s**: **{taskMap.Count:N0}**");
        sb.AppendLine($"- **Unique Session `TempTaskId`s**: **{tempTaskMap.Count:N0}**");
        sb.AppendLine($"- **Unique World `GlobalValueId`s**: **{globalValueMap.Count:N0}**");
        sb.AppendLine();

        AppendSection(sb, "Persistent TaskIds (`GetTask` / `SetTask`)", taskMap);
        AppendSection(sb, "Session TempTaskIds (`GetTaskTemp` / `SetTaskTemp`)", tempTaskMap);
        AppendSection(sb, "World GlobalValueIds (`GetGlbValue` / `SetGlbValue`)", globalValueMap);

        string? dir = Path.GetDirectoryName(outputMarkdownPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(outputMarkdownPath, sb.ToString(), new UTF8Encoding(false));
    }

    private static void AddUsage(SortedDictionary<int, List<string>> map, int id, string path)
    {
        if (!map.TryGetValue(id, out List<string>? list))
        {
            list = [];
            map[id] = list;
        }

        list.Add(path);
    }

    private static void AppendSection(StringBuilder sb, string title, SortedDictionary<int, List<string>> map)
    {
        sb.AppendLine($"## {title}");
        sb.AppendLine("| ID | File Count | Sample Files (up to 5) |");
        sb.AppendLine("| ---: | ---: | :--- |");
        foreach (KeyValuePair<int, List<string>> kv in map)
        {
            string sample = string.Join(", ", kv.Value.Take(5).Select(p => $"`{p}`"));
            if (kv.Value.Count > 5)
            {
                sample += $" *(+{kv.Value.Count - 5} more)*";
            }

            sb.AppendLine($"| `{kv.Key}` | {kv.Value.Count} | {sample} |");
        }

        sb.AppendLine();
    }
}
