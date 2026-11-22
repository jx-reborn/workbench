using JXReborn.ScriptMigrator.Rules;
using Xunit;

namespace JXReborn.ScriptMigrator.Tests;

public sealed class LegacyExclusionRulesTests
{
    [Theory]
    [InlineData("server1/script/battles/marshal/simtk.lua", true)]
    [InlineData("server1/script/battles/marshal/npcdeath_simcity.lua", true)]
    [InlineData("server1/script/global/nobitaxd/vdk/simcity/core.lua", true)]
    [InlineData("server1/script/missions/leaguematch/sims/bot.lua", true)]
    [InlineData("gateway/s3relay/script/simcity.lua", true)]
    [InlineData("server1/script/global/station.lua", false)]
    [InlineData("server1/script/item/baowubox.lua", false)]
    [InlineData("gateway/s3relay/script/worldboss/boss_schedule.lua", false)]
    public void IsExcludedSimBotScript_MatchesCanonicalProfileRules(string path, bool expectedExcluded)
    {
        Assert.Equal(expectedExcluded, LegacyExclusionRules.IsExcludedSimBotScript(path));
    }
}
