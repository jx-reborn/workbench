# Bảng Ánh Xạ Chuẩn: Legacy Lua 4.0 / Settings sang `JXReborn.Scripts` & Data Standard v1

Tài liệu tham chiếu nhanh khi thực thi skill `port-legacy-feature`. Tuyệt đối giữ nguyên ngữ nghĩa làm tròn số nguyên, thứ tự kiểm tra điều kiện, và loại gói tin UI của `jx-linux`.

---

## 1. Hội Thoại, Khung Nhập Liệu & Cửa Hàng (`S2C 0x63 / 0x84 / 0x88 / 0xA3`)

| Mẫu lệnh trong Legacy Lua (`server1/script`) | Đối ứng chuẩn trong `JXReborn.Scripts` (`C# 14`) | Lưu ý Parity quan trọng |
| :--- | :--- | :--- |
| `Say("Prompt", n, "Opt1/cb1", "Opt2/no")` | `new FluentDialogBuilder(ctx).Prompt("Prompt").Option("Opt1", Cb1Async).CloseOption("Opt2").SendSay();` | Khung `Say` cổ điển (`OperateType = 0`). Nếu `n == 0` (cáo thị/bia đá), gọi `ctx.Say("Prompt")` không option. |
| `Say(11256, 2, ...)` *(StringResource ID)* | `ctx.SayByStringId(11256, new DialogOption(...))` | Giữ nguyên ID số để khớp bảng `stringtable_core.txt` phía client. |
| `Describe("Title", ...)` / `CreateNewSayEx(szTitle, tbOpt)` / `tbDailog = DailogClass:new(...)` | `new FluentDialogBuilder(ctx).Prompt(szTitle)...SendDescribe();` | Bắt buộc dùng `SendDescribe()` (`UI=12` / `OperateType = 2`); đổi sang `SendSay()` sẽ gây finding lệch loại UI trên `WireParityProxy`. |
| `Talk(3, "next_fn", "Trang 1", "Trang 2", "Trang 3")` | `ctx.Talk(["Trang 1", "Trang 2", "Trang 3"], NextFnAsync)` | Phát tuần tự đủ số trang; callback chỉ chạy sau khi người chơi bấm xác nhận hết trang cuối. |
| `Sale(shopId)` / `Sale(shopId, currencyType)` | `ctx.OpenShop(shopId)` / `ctx.OpenShop(shopId, currencyType)` | Kiểm tra kỹ `shops.txt` và `buysell.txt` trong `legacy-server/bin/server1/settings/` (VD: tiệm thu tiền vạn vs điểm Tống Kim/Phúc Duyên). |
| `OpenBox()` + `SetRevPos(revPosId)` | `ctx.OpenStoreBox(); ctx.SetRevivalPoint(revPosId);` | Phải gọi đủ cả hai nếu script rương gốc có `SetRevPos` để khớp điểm hồi sinh khi tử vong. |
| `AskClientForNumber("cb", min, max, "Title")` | `ctx.AskClientForNumber("Title", min, max, (c, val) => ...)` | Trong callback luôn kiểm tra lại điều kiện tiền/vật phẩm (`val >= min && c.InventoryMoney >= val`) chống giả mạo gói tin `0x80`/`0x82`. |
| `GiveItemUI("Title", "Desc", "cb", "cancel")` | `ctx.OpenGiveItemUi("Title", "Desc", OnSubmitAsync, OnCancelAsync)` | Xác minh quyền sở hữu `RuntimeItemId` thực tế trước khi xóa vật phẩm và trao thưởng. |

---

## 2. Biến Nhiệm Vụ (`Task`, `TempTask`, `Byte/Bit`)

| Mẫu lệnh trong Legacy Lua | Đối ứng chuẩn trong `JXReborn` | Cạm bẫy thường gặp |
| :--- | :--- | :--- |
| `GetTask(id)` / `SetTask(id, val)` | `ctx.GetTask(PlayerTaskConsts.X)` / `ctx.SetTask(PlayerTaskConsts.X, val)` | Khai báo hằng số vào `PlayerTaskConsts.cs` và giải nghĩa rõ các bước giá trị (`0 -> 10 -> 20`). |
| `GetByte(GetTask(id), byteIdx)` / `SetByte(v, byteIdx, b)` | `ctx.GetTaskByte(taskId, byteIdx)` / `ctx.SetTaskByte(taskId, byteIdx, b)` | **Chỉ số `byteIdx` trong Lua của JX1 là `1..4` (1-based, little-endian)**, tuyệt đối không dịch nhầm thành `0..3` mà không qua helper chuẩn! |
| `GetBit(GetTask(id), bitIdx)` / `SetBit(v, bitIdx, bit)` | `ctx.GetTaskBit(taskId, bitIdx)` / `ctx.SetTaskBit(taskId, bitIdx, bit)` | **Chỉ số `bitIdx` trong Lua của JX1 là `1..32` (1-based)**. |
| `GetTaskTemp(id)` / `SetTaskTemp(id, val)` | `ctx.GetTempTask(id)` / `ctx.SetTempTask(id, val)` | Chỉ sống trong phiên đăng nhập hiện tại, tự xóa khi rời mạng. |

---

## 3. Hành Trang, Vật Phẩm, Kỹ Năng & Thuộc Tính Nhân Vật

1. **Kiểm tra sức chứa túi đồ trước khi trao vật phẩm**:
   - Trong Lua gốc: `CalcFreeItemCellCount() < N` → Trong C#: luôn gọi `ctx.CanFitBagItem(itemCode)` (hoặc kiểm tra số ô trống trên lưới `EquipRoom` `6x6` / `6x10` tùy cấu hình) **trước khi** `SetTask` hoàn thành nhiệm vụ hoặc trừ tiền/nguyên liệu.
2. **Trao vật phẩm**:
   - `AddEventItem(detailId)` → Tra `code` của vật phẩm có `genre == 4, detailType == detailId` trong `pack-tieudaotu/data/items/consumables-and-scripts.items.json`, gọi `ctx.TryGrantItemByCode(code)`.
   - `AddItem(0, detail, particular, level, series, luck, maxMagics)` → Tra `code` trong `equipment.items.json`, gọi `ctx.TryGrantEquipmentByCode(...)` để sinh đúng dòng xanh ngẫu nhiên theo bảng ma thuật.
   - `AddGoldItem(0, goldRowId)` → Tra trong `golden-equipment.items.json`.
3. **Kỹ năng & Chiến đấu**:
   - `HaveMagic(skillId)` returns `-1` khi chưa học trên Legacy Lua (`>= 0` khi đã có, kể cả cấp `0`!). Cẩn thận biểu thức `if (HaveMagic(210) == -1)` vs `> 0`.
   - `OnDeath` của quái nhiệm vụ (`INpcCombatScript.OnNpcDeath`): kiểm tra đúng `SlainNpcTemplateId` và khoảng giá trị `Task` y hệt Lua gốc.
