using JXReborn.WireParityProxy.Scenarios;
using Xunit;

namespace JXReborn.WireParityProxy.Tests.Scenarios;

public sealed class SafeDialogCloseSelectionTests
{
    [Fact]
    public void PickSafeCloseOptionIndex_WhenAffirmativeOptionStartsWithKhongCoVanDeGi_ShouldSelectDeLanSauDeclineOption()
    {
        // Ground Truth: \script\两湖区\南岳镇\npc\南岳镇_路人1彭大爷对话.lua W76_get():
        //   option 0 = "Không có vấn đề gì, nhất định sẽ làm được " -> W76_get_yes (accepts quest!)
        //   option 1 = "Hôm nay ta có việc, hay là để lần sau " -> no (safe close!)
        string[] options =
        [
            "Không có vấn đề gì, nhất định sẽ làm được ",
            "Hôm nay ta có việc, hay là để lần sau ",
        ];

        int selected = WireNpcDialogCrawler.PickSafeCloseOptionIndex(options);
        Assert.Equal(1, selected);
    }

    [Fact]
    public void PickSafeCloseOptionIndex_WhenAffirmativeOptionStartsWithKhongSaoDau_ShouldSelectDeclineOption()
    {
        // Ground Truth: \script\两湖区\南岳镇\npc\路人_沈驹.lua the_buy():
        //   option 0 = "không sao đâu, ta nhất định sẽ đối xử tốt với nó mà " -> buy_yes (spends 15,000,000!)
        //   option 1 = "Nhất thời làm sao tìm ra được số tiền này " -> buy_no (safe decline!)
        string[] options =
        [
            "không sao đâu, ta nhất định sẽ đối xử tốt với nó mà ",
            "Nhất thời làm sao tìm ra được số tiền này ",
        ];

        int selected = WireNpcDialogCrawler.PickSafeCloseOptionIndex(options);
        Assert.Equal(1, selected);
    }
}
