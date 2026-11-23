using System.Text.Json.Serialization;

namespace JXReborn.WireParityProxy.Scenarios;

/// <summary>
/// Declarative end-to-end map survey scenario executed through <c>WireParityProxy</c> against 1 headless <c>game.exe</c>.
/// </summary>
public sealed record WireParityScenario(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mapId")] ushort MapId,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("account")] string Account = "gm001",
    [property: JsonPropertyName("password")] string Password = "123456",
    [property: JsonPropertyName("roleName")] string RoleName = "SurveyHero01",
    [property: JsonPropertyName("nativePlaceId")] int NativePlaceId = 53,
    [property: JsonPropertyName("autoExportFailingCaptures")] bool AutoExportFailingCaptures = true,
    [property: JsonPropertyName("steps")] IReadOnlyList<WireScenarioStep>? Steps = null);
