using System.Text.Json.Serialization;

namespace JXReborn.WireParityProxy.Scenarios;

/// <summary>
/// Single atomic step inside a <see cref="WireParityScenario"/>.
/// </summary>
public sealed record WireScenarioStep(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("region")] string? Region = null,
    [property: JsonPropertyName("mapId")] int? MapId = null,
    [property: JsonPropertyName("tileX")] int? TileX = null,
    [property: JsonPropertyName("tileY")] int? TileY = null,
    [property: JsonPropertyName("mpsX")] int? MpsX = null,
    [property: JsonPropertyName("mpsY")] int? MpsY = null,
    [property: JsonPropertyName("trapId")] uint? TrapId = null,
    [property: JsonPropertyName("allowedPassThroughTrapId")] uint? AllowedPassThroughTrapId = null,
    [property: JsonPropertyName("npcName")] string? NpcName = null,
    [property: JsonPropertyName("templateId")] int? TemplateId = null,
    [property: JsonPropertyName("objectName")] string? ObjectName = null,
    [property: JsonPropertyName("dataId")] int? DataId = null,
    [property: JsonPropertyName("maxRadiusTiles")] int? MaxRadiusTiles = null,
    [property: JsonPropertyName("optionIndex")] int? OptionIndex = null,
    [property: JsonPropertyName("taskId")] int? TaskId = null,
    [property: JsonPropertyName("value")] int? Value = null,
    [property: JsonPropertyName("genre")] int? Genre = null,
    [property: JsonPropertyName("detailType")] int? DetailType = null,
    [property: JsonPropertyName("particularType")] int? ParticularType = null,
    [property: JsonPropertyName("level")] int? Level = null,
    [property: JsonPropertyName("maxBranches")] int? MaxBranches = null,
    [property: JsonPropertyName("resetDrift")] bool? ResetDrift = null,
    [property: JsonPropertyName("expectedMapId")] int? ExpectedMapId = null,
    [property: JsonPropertyName("expectedFightMode")] int? ExpectedFightMode = null,
    [property: JsonPropertyName("excludeNpcNames")] IReadOnlyList<string>? ExcludeNpcNames = null,
    [property: JsonPropertyName("notes")] string? Notes = null);
