using System;
using System.Collections.Generic;
using System.Linq;
using JXReborn.GameAssets.PlayerTemplates;
using JXReborn.Primitives.Characters;
using JXReborn.TestBase.Fakes;

namespace JXReborn.ZoneEngine.Tests.Scripting;

internal sealed class StubFactionPlayerTemplateCatalog : IPlayerTemplateCatalog
{
    private readonly FakePlayerTemplateCatalog _inner = new();
    private readonly Dictionary<byte, FactionTemplate> _factions;

    public StubFactionPlayerTemplateCatalog(params FactionTemplate[] factions)
    {
        _factions = factions.ToDictionary(static f => f.Id);
        AllFactions = factions;
    }

    public IReadOnlyCollection<CharacterBaseTemplate> CharacterTemplates => _inner.CharacterTemplates;
    public IReadOnlyCollection<StarterVillageTemplate> StarterVillages => _inner.StarterVillages;
    public IReadOnlyList<StarterItemTemplate> DefaultStarterItems => _inner.DefaultStarterItems;
    public PkRulesTemplate PkRules => _inner.PkRules;
    public IReadOnlyList<FactionTemplate> AllFactions { get; }

    public bool TryGetCharacterTemplate(ElementSeries series, CharacterGender gender, out CharacterBaseTemplate? template) =>
        _inner.TryGetCharacterTemplate(series, gender, out template);

    public bool TryGetStarterVillage(ushort villageMapId, out StarterVillageTemplate? village) =>
        _inner.TryGetStarterVillage(villageMapId, out village);

    public PlayerSeriesGrowthRuleTemplate GetSeriesGrowthRule(ElementSeries series) =>
        _inner.GetSeriesGrowthRule(series);

    public long GetRequiredExpForNextLevel(int currentLevel) =>
        _inner.GetRequiredExpForNextLevel(currentLevel);

    public bool IsRoleNameProhibited(string roleNameUnicode) =>
        _inner.IsRoleNameProhibited(roleNameUnicode);

    public PkPenaltyRuleTemplate GetPkPenaltyRule(int pkValue) =>
        _inner.GetPkPenaltyRule(pkValue);

    public bool TryGetFactionById(byte factionId, out FactionTemplate? faction) =>
        _factions.TryGetValue(factionId, out faction);

    public bool TryGetFactionByCode(string code, out FactionTemplate? faction)
    {
        faction = AllFactions.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));
        return faction is not null;
    }
}
