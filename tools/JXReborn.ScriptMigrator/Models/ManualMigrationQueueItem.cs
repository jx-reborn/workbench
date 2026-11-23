using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Models;

public sealed record ManualMigrationQueueItem(
    string RelativePath,
    string Utf8ScriptPath,
    string Sha256,
    int Bucket,
    string BucketName,
    GeneratedScriptKind Kind,
    IReadOnlyList<string> BlockingReasons);
