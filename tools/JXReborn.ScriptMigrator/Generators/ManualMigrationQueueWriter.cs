using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using JXReborn.ScriptMigrator.Models;

namespace JXReborn.ScriptMigrator.Generators;

/// <summary>
/// Writes the deterministic fail-closed manual migration queue (<c>manual-migration-queue.md</c>
/// and <c>manual-migration-queue.json</c>) documenting every legacy Lua file rejected by the
/// code generator together with its exact blocking reasons.
/// </summary>
public sealed class ManualMigrationQueueWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void WriteOutputs(ScriptGenerationResult result, string migrationArtifactsDir)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationArtifactsDir);

        Directory.CreateDirectory(migrationArtifactsDir);

        File.WriteAllText(
            Path.Combine(migrationArtifactsDir, "manual-migration-queue.json"),
            JsonSerializer.Serialize(result.ManualQueue, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var sb = new StringBuilder(result.ManualQueue.Count * 140);
        sb.AppendLine("# Manual Migration Queue (Fail-Closed Rejections — Milestone 3 Input)");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC)**: `{DateTime.UtcNow:O}`");
        sb.AppendLine($"- **Kind Filter**: `{result.KindFilter}`");
        sb.AppendLine($"- **Candidates Evaluated**: **{result.TotalCandidatesEvaluated:N0}**");
        sb.AppendLine($"- **Auto-Generated C# Scripts (100% Fail-Closed Verified)**: **{result.GeneratedScriptCount:N0}** (`Traps={result.GeneratedTrapCount:N0}`, `Objects={result.GeneratedObjectCount:N0}`, `Items={result.GeneratedItemCount:N0}`, `Monsters={result.GeneratedMonsterCount:N0}`, `NPCs={result.GeneratedNpcCount:N0}`)");
        sb.AppendLine($"- **Skipped (Already Owned by Handcrafted C# / Static Dialogs / Data Pipeline)**: **{result.SkippedAlreadyOwnedCount:N0}**");
        sb.AppendLine($"- **Queued for Manual Migration (Fail-Closed)**: **{result.ManualQueue.Count:N0}**");
        sb.AppendLine();

        sb.AppendLine("## Breakdown by Script Kind");
        sb.AppendLine("| Kind | Queued Files |");
        sb.AppendLine("| :--- | ---: |");
        foreach (IGrouping<GeneratedScriptKind, ManualMigrationQueueItem> grp in result.ManualQueue
            .GroupBy(static q => q.Kind)
            .OrderByDescending(static g => g.Count())
            .ThenBy(static g => g.Key.ToString(), StringComparer.Ordinal))
        {
            sb.AppendLine($"| `{grp.Key}` | {grp.Count():N0} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Breakdown by Legacy Bucket");
        sb.AppendLine("| Bucket | Queued Files |");
        sb.AppendLine("| :--- | ---: |");
        foreach (IGrouping<string, ManualMigrationQueueItem> grp in result.ManualQueue
            .GroupBy(static q => $"{q.Bucket}: {q.BucketName}")
            .OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"| {grp.Key} | {grp.Count():N0} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Top 40 Blocking Reasons");
        sb.AppendLine("| Blocking Reason | File Count |");
        sb.AppendLine("| :--- | ---: |");
        var reasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ManualMigrationQueueItem item in result.ManualQueue)
        {
            foreach (string r in item.BlockingReasons.Distinct(StringComparer.Ordinal))
            {
                string key = NormalizeReasonBucket(r);
                reasonCounts[key] = reasonCounts.GetValueOrDefault(key) + 1;
            }
        }

        foreach (KeyValuePair<string, int> kv in reasonCounts
            .OrderByDescending(static kv => kv.Value)
            .ThenBy(static kv => kv.Key, StringComparer.Ordinal)
            .Take(40))
        {
            sb.AppendLine($"| `{SanitizeMarkdownCodeCell(kv.Key)}` | {kv.Value:N0} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Detailed Manual Migration Queue");
        sb.AppendLine("| Bucket | Kind | UTF-8 Script Path | Blocking Reasons |");
        sb.AppendLine("| ---: | :--- | :--- | :--- |");
        foreach (ManualMigrationQueueItem item in result.ManualQueue
            .OrderBy(static q => q.Bucket)
            .ThenBy(static q => q.Kind.ToString(), StringComparer.Ordinal)
            .ThenBy(static q => q.Utf8ScriptPath, StringComparer.OrdinalIgnoreCase))
        {
            string reasonsText = string.Join(", ", item.BlockingReasons.Take(6));
            if (item.BlockingReasons.Count > 6)
            {
                reasonsText += $" (+{item.BlockingReasons.Count - 6} more)";
            }

            sb.Append("| ")
              .Append(item.Bucket)
              .Append(" | `")
              .Append(item.Kind)
              .Append("` | `")
              .Append(SanitizeMarkdownCodeCell(item.Utf8ScriptPath))
              .Append("` | `")
              .Append(SanitizeMarkdownCodeCell(reasonsText))
              .AppendLine("` |");
        }

        File.WriteAllText(
            Path.Combine(migrationArtifactsDir, "manual-migration-queue.md"),
            sb.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string NormalizeReasonBucket(string reason)
    {
        int atLine = reason.IndexOf("@line", StringComparison.Ordinal);
        return atLine > 0 ? reason[..atLine] : reason;
    }

    private static string SanitizeMarkdownCodeCell(string text) =>
        text.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("`", "'", StringComparison.Ordinal);
}
