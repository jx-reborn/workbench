# Báo Cáo Kiểm Toán Ground-Truth & Dự Báo Lỗ Hổng Parity: 9 Thôn Trấn & 7 Đại Thành Thị

- **Phạm vi kiểm toán**: **16 bản đồ trọng yếu** gồm **9 Thôn Trấn** (`20` Giang Tân Thôn, `53` Ba Lăng Huyện, `54` Nam Nhạc Trấn, `99` Vĩnh Lạc Trấn, `100` Chu Tiên Trấn, `101` Đạo Hương Thôn, `121` Long Môn Trấn, `153` Thạch Cổ Trấn, `174` Long Tuyền Thôn) và **7 Đại Thành Thị** (`1` Phượng Tường, `11` Thành Đô, `37` Biện Kinh, `78` Tương Dương, `80` Dương Châu, `162` Đại Lý, `176` Lâm An).
- **Nguồn Ground Truth**: `100%` nhị phân `Region_S.dat` + `.wor` trong `legacy-server/bin/server1/pak/maps.pak`, `settings/npcs.txt`, `settings/obj/ObjData.txt`, `lang/vn/replacename_npc.txt`, `script/global/autoexec.lua`, `script/global/addspreadernpc.lua`, `script/missions/leaguematch/wlls_autoexec.lua`, và cây mã nguồn `.lua` thực tế trên đĩa được trích xuất qua [audit_map_scenario.py](../../.agents/skills/khao-sat-ban-do/scripts/audit_map_scenario.py) và [read_jx_file.py](../../.agents/skills/legacy-explore/scripts/read_jx_file.py).
- **Đối chứng Reborn**: [TieuDaoTuScriptCodeResolver.cs](../../../pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Hosting/TieuDaoTuScriptCodeResolver.cs), [TieuDaoTuExactScriptPathManifest.cs](../../../pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Hosting/TieuDaoTuExactScriptPathManifest.cs), `Scripts/Generated/*.cs`, `Scripts/Dialogs/*.cs`, `Scripts/Quests/**/*.cs`, và [ambient-npcs.dialogs.json](../../../pack-tieudaotu/data/dialogs/ambient-npcs.dialogs.json).

---

## 1. Bảng Thống Kê Định Lượng Toàn Diện 16 Bản Đồ

> **Giải thích cột phân loại `DialogNpc`**:
> - `Hand C#`: Lớp C# viết tay gắn trực tiếp `[LegacySource]`.
> - `Gen C#`: Lớp C# hạ mã AST tự động trong `Scripts/Generated/*.cs`.
> - `Ambient`: Hội thoại tĩnh nạp từ `ambient-npcs.dialogs.json`.
> - `Shared Manifest`: Ánh xạ qua `TieuDaoTuExactScriptPathManifest.DialogNpcPaths` vào wrapper dùng chung *(nhiều thực thể tuy không trả `null` nhưng đang lệch `Sale(id)`, `CurWharf`, `UI=12 vs UI=0` hoặc mất nhánh `TaskID` địa phương — xem Mục 2 & 3)*.
> - `MISSING`: `ResolveNpcScriptCode(...) == null` — click vào không phản hồi (no-op hoàn toàn).

### 1.1. Khối 9 Thôn Trấn (`311` DialogNpc | `68` MapObject | `70` Trap | `26` Nhóm Quái Script)

| Map ID | Tên Bản Đồ | `inMinWorld` | `DialogNpc` Active *(Có Script)* | Đã Resolve (`Hand` / `Gen` / `Amb` / `Manifest`) | `MISSING` (`null`) | Tỷ Lệ Resolve NPC | `MapObject` *(Ported)* | `Trap` *(Ported)* | `ScriptedMonster` *(Nhóm)* | Quái Thường (`Ambient`) | Trạng Thái Hội Tụ |
| :---: | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| **53** | **Ba Lăng Huyện** | `True` | `39` (`39`) | `39` (`31` / `0` / `6` / `2`) | **`0`** | `100.0%` | `11/11` | `8/8` | `3/3` (`30`) | `812` | `zero_drift_locked` |
| **54** | **Nam Nhạc Trấn** | `True` | `20` (`20`) | `20` (`9` / `11` / `0` / `0`) | **`0`** | `100.0%` | `10/10` | `5/5` | `0/0` (`0`) | `146` | `zero_drift_locked` |
| **101** | **Đạo Hương Thôn** | `True` | `38` (`38`) | `38` (`23` / `5` / `10` / `0`) | **`0`** | `100.0%` | `6/6` | `12/12` | `3/3` (`30`) | `857` | `zero_drift_locked` |
| **20** | **Giang Tân Thôn** | `False` | `35` (`35`) | `28` (`14` / `5` / `4` / `5`*) | **`7`** | `80.0%` | `12/12` | `8/8`† | `3/3` (`30`) | `636` | Đã có Scenario `020` |
| **99** | **Vĩnh Lạc Trấn** | `False` | `39` (`39`) | `36` (`14` / `4` / `11` / `7`*) | **`3`** | `92.3%` | `6/6` | `7/7` | `3/3` (`28`) | `855` | Chưa khóa E2E |
| **100** | **Chu Tiên Trấn** | `False` | `41` (`41`) | `39` (`14` / `7` / `9` / `9`*) | **`2`** | `95.1%` | `7/7` | `7/7` | `3/3` (`30`) | `1,294` | Chưa khóa E2E |
| **121** | **Long Môn Trấn** | `False` | `36` (`36`) | `33` (`14` / `4` / `8` / `7`*) | **`3`** | `91.7%` | `7/7` | `10/10` | `4/4` (`23`) | `1,220` | Chưa khóa E2E |
| **153** | **Thạch Cổ Trấn** | `False` | `29` (`29`) | `26` (`14` / `2` / `4` / `6`*) | **`3`** | `89.7%` | `2/2` | `4/4` | `4/4` (`34`) | `2,016` | Chưa khóa E2E |
| **174** | **Long Tuyền Thôn** | `False` | `34` (`33`‡) | `31` (`15` / `4` / `8` / `4`*) | **`2`** | `93.9%` | `7/7` | `9/9` | `3/3` (`11`) | `731` | Chưa khóa E2E |
| **Tổng** | **9 Thôn Trấn** | **3/9** | **311 (`310`)** | **290 (`148`/`42`/`60`/`40`)** | **`20`** | **`93.5%`** | **`68/68` (`100%`)** | **`70/70` (`100%`)** | **`26/26` (`100%`)** | **`8,567`** | **3/9 Locked** |

*\*Ghi chú 9 Thôn Trấn*:
- `†` Tại `Map 20` (Giang Tân Thôn), thư mục trap trên đĩa Legacy đặt tên là `\script\西南北区\江津村\连接trap\` (tương tự `Map 1` và `Map 11` dùng `连接trap` thay vì `\trap\`). `GeneratedPortalTrapScript.cs` và `GeneratedFightToggleTrapScript.cs` trong `pack-tieudaotu` đã port đủ `8/8` trap, nhưng hàm `scan_trap_scripts()` hiện tại của `audit_map_scenario.py` lọc chuỗi `"\\trap"` nên bỏ qua metadata `minLv`/`biGate` của thư mục `连接trap`.
- `‡` `Map 174` có `1` NPC `Học trò thợ rèn` `(1611, 3244)` (`tpl=202`) vốn không gắn script trong `Region_S.dat` gốc của Legacy.

### 1.2. Khối 7 Đại Thành Thị (`651` DialogNpc | `108` MapObject | `59` Trap | `1` Nhóm Quái Script)

| Map ID | Tên Thành Thị | `inMinWorld` | `DialogNpc` Active *(Có Script)* | Đã Resolve (`Hand` / `Gen` / `Amb` / `Manifest`) | `MISSING` (`null`) | Tỷ Lệ Resolve NPC | `MapObject` *(Ported)* | `Trap` *(Ported)* | `ScriptedMonster` *(Nhóm)* | Quái Thường (`Ambient`) | Trạng Thái Hội Tụ |
| :---: | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| **1** | **Phượng Tường** | `False` | `76` (`76`) | `57` (`7` / `17` / `21` / `12`*) | **`19`** | `75.0%` | `15/15`§ | `7/7` | `0/0` | `1,344` | Chưa khảo sát |
| **11** | **Thành Đô** | `False` | `85` (`84`¶) | `66` (`6` / `21` / `25` / `14`*) | **`18`** | `78.6%` | `24/24`§ | `11/11` | `1/1` (`136`) | `682` | Chưa khảo sát |
| **37** | **Biện Kinh** | `False` | `81` (`81`) | `57` (`7` / `19` / `16` / `15`*) | **`24`** | `70.4%` | `14/14`§ | `10/10` | `0/0` | `3,030` | Chưa khảo sát |
| **78** | **Tương Dương** | `True` | `94` (`94`) | `75` (`25` / `0` / `32` / `18`*) | **`19`** | `79.8%` | `25/25`§ | `9/9` | `0/0` | `1,265` | Có `078` sơ bộ |
| **80** | **Dương Châu** | `True` | `105` (`105`) | `80` (`11` / `28` / `28` / `13`*) | **`25`** | `76.2%` | `11/11`§ | `7/7` | `0/0` | `952` | Chưa khảo sát |
| **162** | **Đại Lý Phủ** | `False` | `84` (`84`) | `69` (`5` / `13` / `40` / `11`*) | **`15`** | `82.1%` | `13/13`§ | `8/8` | `0/0` | `637` | Chưa khảo sát |
| **176** | **Lâm An** | `False` | `126` (`126`) | `95` (`17` / `27` / `43` / `8`*) | **`31`**# | `75.4%` | `6/6`§ | `7/7` | `0/0` | `1,000` | Chưa khảo sát |
| **Tổng** | **7 Đại Thành Thị** | **2/7** | **651 (`650`)** | **499 (`78`/`125`/`205`/`91`)** | **`151`** | **`76.8%`** | **`108/108` (`100%`)** | **`59/59` (`100%`)** | **`1/1` (`100%`)** | **`8,910`** | **0/7 Locked** |
| **TỔNG** | **16 BẢN ĐỒ** | **5/16** | **962 (`960`)** | **789 (`226`/`167`/`265`/`131`)** | **`171`** | **`82.2%`** | **`176/176` (`100%`)** | **`129/129` (`100%`)** | **`27/27` (`100%`)** | **`17,477`** | **3/16 Locked** |

*\*Ghi chú 7 Đại Thành Thị*:
- `¶` `Map 11` có `1` NPC `Người chăm sóc ngựa` `(3163, 5092)` (`tpl=233`) không gắn script trong `Region_S.dat` gốc.
- `§` Toàn bộ `19` bảng cáo thị công thành `\script\missions\citywar_global\citybulletin.lua` trên 7 thành thị đang resolve vào `MapSignboardObjectScript.cs`, nhưng thân hàm đang hardcode cố định chuỗi `"Thông tin thành thị Tương Dương: Hiện tại thành thị đang thái bình thịnh trị."` cho cả 6 thành thị còn lại và chưa mở `OpenCityManageUI` cho Thái Thú.
- `#` Tại `Map 176` (Lâm An), `1` trong `31` NPC `MISSING` là `Thống lĩnh cấm Vệ` `(1475, 3233)` trỏ tới `\script\江南区\临安\临安\npc\路人_皇宫卫兵.lua` — file này **không tồn tại cả trên đĩa `legacy-server/bin/`** (dead script binding gốc của VNG/Kingsoft).

---

## 2. Kiểm Toán Chi Tiết Lỗ Hổng 6 Tân Thủ Thôn Chưa Khóa (`Map 20, 99, 100, 121, 153, 174`)

Ba bản đồ `53` (Ba Lăng Huyện), `54` (Nam Nhạc Trấn) và `101` (Đạo Hương Thôn) đã đạt `zero_drift_locked`. Ở **6 Tân Thủ Thôn còn lại**, tổng cộng còn **20 NPC `MISSING`** và **26 điểm lệch hành vi/UI trong Shared Manifest**:

### 2.1. Danh sách cụ thể các Nhiệm Vụ Thôn Trấn (`TaskID`) & NPC bị thiếu / đứt gãy

| Map | Chuỗi Nhiệm Vụ / Dịch Vụ | `TaskID` & Vật Phẩm `(G, D)` Ground Truth | NPC Liên Quan & Trạng Thái Hiện Tại Trong `pack-tieudaotu` | Lỗ Hổng Cụ Thể Cần Đóng |
| :---: | :--- | :--- | :--- | :--- |
| **20** *(Giang Tân)* | **Hổ Tử Bái Sư** | `Task 44`: `0 -> 10 -> 20 -> 100`; thưởng mũ `(0,7,6\|8,1)` + `Repute +5` | `Phụ Thân Hổ Tử` `(3413,6220)` [**MISSING**], `Võ Sư` `(3637,6262)` [**MISSING** — `江津村武师.lua` chưa vào Manifest!] | Cả 2 NPC đều `null`; khi port `Võ Sư` phải giữ nhánh `Task 44 == 10 -> 20` trước khi gọi `learn()` (`Task 32`). |
| **20** *(Giang Tân)* | **Giàn Ná Hổ Tử** (`Lv >= 5`) | `Task 45`: `0 -> 10 -> 20 -> 100`; `Chạc cây` `(4,177)` + `Da trâu` `(4,178)` -> `Giàn ná` `(4,176)`; thưởng vũ khí cấp 2 `(0,0,0..5,2)` + `Repute +8` | `Hổ Tử` `(3573,6192)` [**MISSING**], `Thợ rèn` `(3439,6220)` [`TownBlacksmithDialogScript`], `Thiên Thiên` `(3490,6236)` [**MISSING**], 4 Object `Chạc cây`/`Da trâu` [`GeneratedGeneralObjectScript` — đã port] | Port `Hổ Tử` + `Thiên Thiên`, và bổ sung nhánh `Task 45 == 10/20` vào `TownBlacksmithDialogScript` cho `MapId == 20` (kèm `TIEJIANG_DIALOG` riêng của Giang Tân). |
| **20** *(Giang Tân)* | **Thuốc Ngô Lão Gia** (`Lv >= 3`) | `Task 49`: `0 -> 1 -> 3 -> 10`; `Xuyên Bối Hoàn` `(4,179)` giá `200 lượng`; thưởng giày `(0,5,0\|2,1)` + `Repute +6` | `Ngô Lão gia` [`GeneratedNpcDialogScript` — đã port], `Ông chủ dược điếm` [`TownPharmacistDialogScript` — đã có mua `(4,179)` nhưng thiếu `AddNote` và sai câu thoại gốc], `Ngô Hồng Mai` `(3483,6150)` [**MISSING**] | Port `Ngô Hồng Mai` (`路人8吴红梅对话.lua`) để trả bước cuối `Task 49 == 3 -> 10`; chuẩn hóa câu thoại Dược điếm Giang Tân (`Sale(99,1)`). |
| **20** *(Giang Tân)* | **Kể chuyện & Hôn nhân** | `La Tiên Sinh` (`PlayMusic`), `Nguyệt Hạ lão nhân` (`Task 67, 1779, 1564, 1565`, `TaskTemp 5..7`, `DailogClass:new` `UI=12`) | `La Tiên Sinh` `(3478,6126)` [**MISSING**], `Nguyệt Hạ lão nhân` `(3522,6117)` [**MISSING**] | Port `说书先生.lua` (`PlayMusic` `\Music\Music901..903.mp3`) và `月老.lua` (`UI=12` 6 lựa chọn cầu hôn / ly hôn / đơn phương ly hôn / hòa giải). |
| **99** *(Vĩnh Lạc)* | **Mượn Sách Kê Thần Lục** | `Task 70`: `0 -> 2 -> 4 -> 6 -> 8 -> 10`; `Di Kiên Chí` `(4,189)` <-> `Kê Thần Lục` `(4,188)`; thưởng `Ngưu Bì Yêu Đái` `(0,6,0,1)` + `Repute +5` | `Lỗ Gia` `(1693,3205)` [**MISSING**], `Cát Gia` `(1663,3328)` [**MISSING**] | Port `永乐镇_鲁爷对话.lua` và `永乐镇_葛爷对话.lua` (`RndItemProp(6,10)`). |
| **99** *(Vĩnh Lạc)* | **Tâm Ý Lý Dự & Xuân Hương** (`Lv >= 5`) | `Task 69`: `0 -> 2 -> 4 -> 6 -> 8 -> 10`; `Quạt lá sen` `(4,190)`, đố thơ `"Liễu Thất"`; thưởng nhẫn `(0,3,0,1\|2)` + `Repute +7` | `Lý Dự` `(1565,3262)` [**MISSING**], `Xuân Hương` `(1685,3288)` [`GeneratedNpcDialogScript` — đã port] | Port `永乐镇_李豫对话.lua` nối khép kín với `Xuân Hương` đã hạ mã tự động. |
| **100** *(Chu Tiên)* | **Chiếc Nhẫn Song Song** (`Lv >= 6`) | `Task 68`: `0 -> 1 -> 2 -> 3 -> 4\|5`; mua `Đậu hũ Ngũ Hương` `(4,187)` `100 lượng` tại Tửu điếm -> đổi nhẫn `(4,191)` -> thưởng `200 lượng` + `Repute +7` hoặc Yêu đái `(0,6,0,2)` + `Repute +9` | `Song Song` `(1717,3137)` [**MISSING**], `Ăn mày` & `Tiểu Hùng` [`Generated` — đã port], `Ông chủ tửu điếm` [`TownInnkeeperDialogScript` — **mất nhánh bán Đậu hũ Ngũ Hương!**] | Port `朱仙镇_双双对话.lua` và thêm nhánh `MapId == 100` (`Task 68 == 3\|4` bán `(4,187)` giá `100 lượng`) vào `TownInnkeeperDialogScript`. |
| **100** *(Chu Tiên)* | **Khăn Tay Doanh Doanh** (`Lv >= 2`) | `Task 72`: `0 -> 2 -> 4 -> 10`; `Khăn tay` `(4,192)`; thưởng giày `(0,5,0\|2,1)` + `Repute +5` | `Doanh Doanh` `(1736,3162)` [**MISSING**], `Trương Đại Nguyên` [`Generated` — đã port] | Port `朱仙镇_盈盈对话.lua`. *(Nhiệm vụ thứ 3 của Chu Tiên là `Task 74` Chu Tẩu — Lão Chu đã được hạ mã đủ cả 2 đầu).* |
| **121** *(Long Môn)* | **Đưa Tin Mi Nhi** (`Lv >= 2`) | `Task 26`: `0 -> 3 -> 6 -> 10`; thưởng Hộ uyển `(0,8,0\|1,1)` + `Repute +5` | `Ăn mày` `(1872,4495)` [**MISSING**], `Triệu Mi Nhi` [`Generated` — đã port] | Port `\script\西北北区\龙门镇\npc\路人_乞丐.lua`. |
| **121** *(Long Môn)* | **Giới Thiệu Việc & Thịt Heo Rừng** | `Task 25`: `0 -> 3 -> 6 -> 10` (vũ khí cấp 1 + `Repute +3`); `Task 27` (`Lv >= 5`): `0 -> 5 -> 10` (`3x Thịt heo rừng` `(4,228)` từ `战斗_野猪.lua` -> mũ cấp 2 + `Repute +9`) | `Thượng Quan Thu` `(1925,4516)` [**MISSING**], `Bà chủ` `(1975,4537)` [**MISSING**], quái `Heo rừng` x4 [`GeneratedMonsterDeathScript` — đã port] | Port `路人_上官秋.lua` và `职能_客栈老板娘.lua`. |
| **153** *(Thạch Cổ)* | **Đưa Cơm Cho Võ Sư** | `Task 22`: `0 -> 5 -> 10`; `Cơm` `(4,233)`; thưởng vũ khí cấp 1 `(0,0,0..5,1)` + `Repute +3` | `Liễu Diệp Nhi` [`Generated` — đã port], `Võ Sư` `(1660,3257)` [`VillageMartialInstructorDialogScript` — **mất nhánh nhận cơm `Task 22`!**] | Thêm nhánh `MapId == 153 && Task 22 == 5 && HasItem(233)` vào `VillageMartialInstructorDialogScript`. |
| **153** *(Thạch Cổ)* | **Trả Nợ Thẩm Gia (`Lv >= 3`) & Nhẫn A Kinh (`Lv >= 5`)** | `Task 23`: `0 -> 3 -> 6 -> 10` (áo `(0,2,2\|10,1)` + `Repute +7`); `Task 24`: `0 -> 5 -> 10` (đánh `Linh Miêu` rớt nhẫn `(4,234)` -> hạng liên/ngọc bội `(0,4,0\|1,1)` + `Repute +7`) | `A Toàn` & `A Mã` `(1671,3234)` [**MISSING**], `Thẩm Gia` [`Generated` — đã port], `A Kinh` `(1650,3192)` [**MISSING**], `Linh Miêu` x4 [`GeneratedMonsterDeathScript` — đã port] | Port `路人_阿玛&阿全.lua` và `路人_阿琼.lua`. |
| **174** *(Long Tuyền)* | **Cha Kế Khổng Hiểu & Canh Dương Xuân Bạch Tuyết** | `Task 18`: `0 -> 3 -> 6 -> 10` (áo `(0,2,6\|13,1)` + `Repute +5`); `Task 20` (`Lv >= 5`): `0 -> 5 -> 10` (`Bạch Tuyết Bách Hợp` `(4,232)` nhặt đất + `Dương Xuân Linh Chi` `(4,231)` tặng miễn phí nếu xong `Task 18` hoặc mua `500 lượng` tại Tạp hóa -> ngọc bội `(0,9,0\|1,1)` + `Repute +8`) | `Chung Cầm` `(1644,3229)` [**MISSING**], `Chủ tiệm tạp hóa` [`TownGrocerDialogScript` — **mất toàn bộ `Task 18` & `Task 20`!**], `Khổng Hiểu` & Object Bách Hợp [`Generated` — đã port] | Port `路人_钟离.lua` và bổ sung máy trạng thái `Task 18` + `Task 20` vào `TownGrocerDialogScript` khi `MapId == 174`. |
| **174** *(Long Tuyền)* | **Sửa Bảo Đao Bành Phóng** (`Lv >= 2`) | `Task 19`: `0 -> 2 -> 4 (Timer 7) -> 6 -> 8 -> 10`; đao hỏng `(4,229)`, phí `120 lượng`, đao tốt `(4,230)`, thưởng Hộ uyển `(0,8,0\|1,1)` + `Repute +6` | `Bành Phóng` `(1665,3251)` [**MISSING** — dùng StringId `10903..10907`], `Thợ rèn` [`TownBlacksmithDialogScript` — **mất nhánh sửa đao `Task 19` + `Timer 7`!**] | Giải mã StringId `10903..10907` từ `stringtable`, port `路人_彭放.lua` và thêm nhánh `Task 19` (`SetTimer` ID `7`) vào `TownBlacksmithDialogScript` cho `MapId == 174`. |

---

## 3. Kiểm Toán Hệ Thống Dịch Vụ & Lệch UI (`UI=12` vs `UI=0`) Trên 7 Đại Thành Thị & Thôn Trấn

Đây là nhóm lỗ hổng có **hệ số nhân tác động lớn nhất** vì một lỗi trong shared script gây finding Critical đồng loạt trên 7–14 bản đồ:

### 3.1. Các lỗi sai hành vi nghiêm trọng trong Shared Manifest (`TieuDaoTuExactScriptPathManifest`)

1. **`FerryBoatmanDialogScript.cs` — Lái nhầm 100% bến thuyền Giang Tân, Long Môn và 5 Đại Thành Thị sang Động Đình Hồ (`Map 59`)**:
   - **Hiện trạng Reborn**: [FerryBoatmanDialogScript.cs](../../../pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Scripts/Dialogs/FerryBoatmanDialogScript.cs#L24-L61) chỉ xử lý riêng `MapId == 78` (Tương Dương) và chuỗi `"长江码头"` (bến Trường Giang Ba Lăng). Mọi NPC gắn `npc-ferry-boatman` còn lại trong Manifest (`Map 20` Giang Tân `CurWharf=2`, `Map 121` Long Môn `CurWharf=10`, `Map 1` Phượng Tường `CurWharf=1`, `Map 11` Thành Đô `CurWharf=9`, `Map 37` Biện Kinh `CurWharf=3`, `Map 80` Dương Châu `CurWharf=6`, `Map 162` Đại Lý `CurWharf=9`, `Map 176` Lâm An `CurWharf=11`) đều bị rơi vào nhánh mặc định: *"Động Đình hồ không chỗ nào ta không biết! -> Đi Thiên Vương đảo (`Map 59`)"*!
   - **Ground Truth Legacy (`settings/wharf.txt` + `settings/wharfprice.txt` + `station.lua`)**: Phải tra bảng giá ma trận `wharfprice.txt` theo `CurWharf` thực của từng bến (ví dụ `CurWharf=2` Giang Tân đi Tương Dương `400l`, Ba Lăng `400l`, Dương Châu `800l`, Đại Lý `100l`, Lâm An `800l`; `CurWharf=1` Phượng Tường đi Biện Kinh `400l`, Long Môn `100l`; riêng Dương Châu có thêm nhánh hoa thuyền `Task 2885` / Đào Hoa Đảo).

2. **`TownBlacksmithDialogScript.cs` — Lệch `UI=0` vs `UI=12` ở cả 7 Thành Thị, thiếu `Sale(13)` Thành Đô & `Khúc Thiết Tượng` Biện Kinh**:
   - **Hiện trạng Reborn**: Khi `isCity == true`, [TownBlacksmithDialogScript.cs](../../../pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Scripts/Dialogs/TownBlacksmithDialogScript.cs#L46-L80) gọi `.SendSay()` (`UI=0`), mở chung `OpenFoundryUi(0)` cho cả `"Vật phẩm đúc"` lẫn `"Vật phẩm khảm nạm"`, và trả `Talk` giả cho `"Xử lý <trang bị đã bị hỏng>"`. Ngoài ra `Thợ rèn` Thành Đô (`西南北区-成都府--铁匠铺老板对话.lua`, `Sale(13)`, `Task 1` Nga My) và `Khúc Thiết Tượng` Biện Kinh (`汴京_铁匠1对话.lua`, `Sale(7)`, `Task 4, 28`) **chưa có trong Manifest** nên bị `MISSING`!
   - **Ground Truth Legacy (`global_tiejiang.lua` + `cfg_server.lua`)**: `tiejiang_city()` tạo `DailogClass:new("Thợ rèn trong thành")` (`UI=12` `Describe`), với cờ `CFG_CuaHangTinhLuc=0`, `CFG_TrangBiHuyenTinh=1`, `CFG_KhamNamDoXanh=1`: gồm 5 dòng (`"Giao dịch"` -> `Sale`, `"Vật phẩm đúc"` -> `onFoundry` / `FoundryItem`, `"Vật phẩm khảm nạm"` -> `CreateTaskSay` 3 nhánh gọi `EnchaseItem()`, `"Xử lý <trang bị đã bị hỏng>"` -> `deal_brokenequip`, `"Ta đến nhận nhiệm vụ Sơ nhập"` -> `education_tiejiang`) + `"Kết thúc đối thoại"` tự động của `DailogClass`.

3. **`HorseMerchantDialogScript.cs` — Bán sai cửa hàng ngựa `Sale(46)` trên Biện Kinh, Dương Châu, Đại Lý**:
   - **Hiện trạng Reborn**: [HorseMerchantDialogScript.cs](../../../pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Scripts/Dialogs/HorseMerchantDialogScript.cs#L25-L30) hardcode `OpenShop(46)` (shop ngựa Tương Dương) cho mọi thành thị.
   - **Ground Truth Legacy**: Biện Kinh mở `Sale(45)`, Dương Châu mở `Sale(44)`, Tương Dương mở `Sale(46)`, Đại Lý mở `Sale(48)`, Lâm An mở `Sale(49)` (mỗi thành có câu thoại mở đầu riêng).

4. **`TownGrocerDialogScript.cs` — Lệch danh sách nút `store_sel_extend()` & `store_sel_village_extend()` trên 13 bản đồ**:
   - **Hiện trạng Reborn**: Chỉ `Map 101` có `"Mua Kim thạch"` (`Sale(165)`). Tất cả 6 thôn còn lại (`20, 99, 100, 121, 153, 174`) và cả 7 Đại Thành Thị đang đặt `"Giao dịch"` ở `optionIndex = 0` và thiếu nút sự kiện năm `2007..2050`.
   - **Ground Truth Legacy (`global_zahuodian.lua`)**: Do `nDate <= 20500225 / 20500321`, tại **Thôn trấn** (`store_sel_village_extend`) luôn chèn `"Mua Kim thạch"` (`Sale(165)`) lên `index 0` (5 nút: `Mua Kim thạch`, `Giao dịch`, `Hoạt động cửa hàng ` `Sale(141,3)`, `Ta đến nhận nhiệm vụ Sơ nhập`, `Thôi không cần đâu`); tại **Thành thị** (`store_sel_extend`) chèn cả `"Mua Kim thạch"` (`index 0`, `Sale(165)`) và `"Mua thiệp chúc Tết"` (`index 1`, `Sale(148)`) trước `"Giao dịch"` (`index 2`)! Riêng Tạp hóa Lâm An còn có menu chọn đồ sứ/gỗ/khác (`Sale(2)`, `Sale(101)`, `Sale(147)`), và Tạp hóa Thành Đô có nhánh nhiệm vụ Nga My/Đường Môn (`Task 1, 2, 29`).

5. **`TownPharmacistDialogScript.cs` — Đúng `Sale(99, 1)` nhưng sai kiểu tiền tệ (`Gold` thay vì `SongJinPoints`?) / sai câu thoại & thiếu nhiệm vụ môn phái**:
   - Trên bản custom `legacy-server/bin/` (`[09/10/2026]`), hầu hết chủ dược điếm gọi `Sale(99, 1)` (tham số thứ 2 `nCurrencyType = 1`, trong khi `TownPharmacistDialogScript` truyền `ShopCurrencyType.Gold` = `0` — cần đối chiếu chính xác byte currency trong gói `s2c_opensalebox` trên Oracle!). Ngoài ra Dược điếm Dương Châu có nhánh `Task 10` (Ngũ Độc).

6. **`TownTeleporterDialogScript.cs` — Lệch menu `CityStationCommon` trên 7 Đại Thành Thị & câu chào từng thôn**:
   - **Ground Truth Legacy (`station.lua`)**: Trong `aryCondition`, `CityStationCommon` bật cố định mục `[4] "Đi Hoa sơn cảnh kỹ trường"` (`NewWorld(2, 2605, 3592)`) bên cạnh `[1..3]` và `[7] "Đi đảo Tẩy Tủy"`, đồng thời bật động `[5] Công thành`, `[6] Huấn luyện`, `[8] Dã Tẩu` (`Task 1021 == 4`), `[9] Tín Sứ` (`Task 1204 ~= 0`). Hiện tại `TownTeleporterDialogScript` thiếu mục `"Đi Hoa sơn cảnh kỹ trường"` và mục `"Đi nơi đặc biệt làm Nhiệm vụ Tín Sứ"`.

7. **`GuildGuideDialogScript.cs` (`Người hướng dẫn bang hội`) — Gửi `Say` (`UI=0`) thay vì `DailogClass` / `CreateTaskSay` (`UI=12`)**:
   - **Ground Truth Legacy (`帮会接引人.lua`)**: Dùng `DailogClass:new` ở menu gốc và `CreateTaskSay` ở các trang cẩm nang `32..40` (đều là `Describe` `UI=12`), trong khi [GuildGuideDialogScript.cs](../../../pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Scripts/Dialogs/GuildGuideDialogScript.cs#L62-L90) gọi `context.Say(...)` (`UI=0`) kèm tiền tố `<dec><npc>` thô!

8. **`MoneychangerDialogScript.cs` (`Chủ Tiền trang`) — Thiếu bước `Talk` đầu tiên trên Biện Kinh, Dương Châu, Tương Dương, Phượng Tường**:
   - **Ground Truth Legacy (`汴京_钱庄老板对话.lua` -> `钱庄功能.lua`)**: Phát `Talk(1, "main_proc", "Danh dự của Tiền trang...")` trước, sau khi người chơi bấm qua trang mới mở `Say("Ông chủ tiền trang: Vị thiếu hiệp/cô nương kia cần lão phu giúp gì ? ", 1, "Ta chỉ ghé qua thôi./no")`. Hiện tại `MoneychangerDialogScript` bỏ qua trang `Talk` đầu và nhảy thẳng vào `Say`.

### 3.2. Danh Mục Dịch Vụ Thành Thị Còn Thiếu Hoàn Toàn (`MISSING` trên 7 Đại Thành Thị)

| Nhóm Dịch Vụ Thành Thị | Số Thành Có Mặt | Số NPC `MISSING` | Đường Dẫn Legacy & Hành Vi Cốt Lõi |
| :--- | :---: | :---: | :--- |
| **1. Vệ binh Thành môn (`城门卫兵`)** | `6/7` thành *(trừ Tương Dương đã port)* | **`46` NPC** *(PT: 8, TĐ: 8, BK: 8, DC: 8, ĐL: 6, LA: 8)* | Gọi `\script\global\map_helper.lua` (5 lựa chọn: Phong Vân Luận Kiếm Tống Kim `Lv >= 40`, Tìm hiểu khu luyện công, Tìm hiểu võ nghệ bổn môn, Tán gẫu theo từng cổng, Rời khỏi). Riêng Biện Kinh có nhánh `Task 38`, Đại Lý có nhánh `Task 42`. Chỉ cần mở rộng `[LegacySource]` + bảng câu thoại cổng trên [CityGateGuardDialogScript.cs](../../../pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Scripts/Dialogs/CityGateGuardDialogScript.cs) là đóng ngay **46 NPC**! |
| **2. Võ Lâm Liên Đấu (`Sứ giả liên đấu` / `Sứ giả kiệt xuất`)** | `7/7` thành (`wlls_autoexec.lua`) | **`14` NPC** *(2 NPC / thành)* | `officer.lua` (`tpl=308`, vào hội trường Liên Đấu, nhận thưởng xếp hạng, mở shop Vinh dự/Uy danh `shopliendau.lua` `Sale(146,11)`/`Sale(173,13)`, `Task 1122, 2601`) và `helper.lua` (`tpl=87`, tra cứu/đăng ký hồ sơ tuyển thủ `Describe` `UI=12`). |
| **3. Thất Thành Đại Chiến (`Tiếp Đầu Công Thành Chiến`) & Bảng Cáo Thị Thành** | `7/7` thành (`autoexec.lua` + `Region_S`) | **`7` NPC** + **`19` Object lệch** | `\script\missions\sevencity\dialog_npc.lua` (`CFG_ThatThanhDaiChien = 1` -> `Say` 8 dòng quản lý bang chiến / báo danh / vào chiến trường / nhận thưởng lễ bao) và `\script\missions\citywar_global\citybulletin.lua` (`GetCitySummary(nCityID)` + `OpenCityManageUI`). |
| **4. Nha Dịch Rửa PK / Đại Lao (`职能_衙役`, `衙门卫兵/差役`)** | `6/7` thành | **`12` NPC** | `\script\global\洗pk的衙役.lua` (Biện Kinh, Lâm An: đầu thú rửa PK `SetTimer(..., 9)` đưa vào Đại Lao `Map 208`, thăm tù tốn `5.000 lượng`, `Task 94..96`) + vệ binh nha môn Thành Đô (`Task 11`), Phượng Tường, Dương Châu, Đại Lý. |
| **5. Dịch Quan (`职能_驿官`) tại Đại Lý & Lâm An** | `2/7` thành *(4 thành kia đã vào Manifest)* | **`2` NPC** | `\script\西南南区\大理府\大理府\npc\职能_驿官.lua` và `\script\江南区\临安\临安\npc\职能_驿官.lua` chỉ thiếu khai báo trong `TieuDaoTuExactScriptPathManifest.DialogNpcPaths` -> `"npc-courier-official"`! |
| **6. Viêm Đế Bảo Tàng (`Bình Bình Cô Nương`) & Cung Nữ Boss (`Chưởng Đăng Cung Nữ`) & Quốc Chiến (`Nội các thượng thư`)** | Biện Kinh (`5` NPC) & Lâm An (`9` NPC) | **`14` NPC** | `yandibaozang_main.lua` (`UI=12` báo danh vượt ải Viêm Đế, `CFG_ViemDe = 1`), `dangboss\gongnv_npc.lua` (`UI=12`), `nationalwar\npc.lua` (Thiên Tử Ngọc Tỷ), `bairenleitai\npc_enter.lua` (Bách Nhân Lôi Đài tại Biện Kinh). |
| **7. Cụm 15 NPC Chế Tạo / Đổi Điểm Custom Tại Trung Tâm Tương Dương (`Map 78` `autoexec.lua`)** | Tương Dương (`15` NPC) + Biện Kinh (`1` NPC) + Lâm An (`2` NPC) | **`18` NPC** | `Hoàng Bảo Câu` (`hoanbaocau.lua`), `Thợ Rèn Huyền Tinh` (`npcghephuyentin.lua`), `Thợ Kim Hoàng` (`thorensieunhan.lua`), `Thợ Luyện Đồ Xanh` (`npc_tho_luyen_doxanh.lua`), `Thợ Rèn Truyền Thuyết` (`npcphangiai.lua`), `Thợ Rèn Vô Song` (`npcvosong.lua`), `Dược Sư Thần Bí` (`npcdoidiemtk.lua`), `Nông Dân Đảo Đào Hoa` (`npcdoiphucduyen.lua`), `Công Trạng Quan` (`npctinsu.lua`), `Bậc Thầy Dị Dung` (`vuadidung.lua`), `Thảo Dược Tỷ Tỷ`, `Túy Tiên`, `Cột Cờ`, `Thương Nhân Vô Danh`, `Thương Nhân Trang Bị`, `Thần Bí Thương Nhân` & `Thần bí thiết tượng` (Lâm An). |
| **8. Sứ Giả Hoạt Động & 12 NPC Sự Kiện Tháng (`sukien_nam`)** | `7/7` thành (`Sứ Giả Hoạt Động`) + Phượng Tường (`4`) + Dương Châu (`9`) | **`20` NPC** | `npc_jxweb.lua` (`7` thành), `sukien_nam\thang1..12` (Phượng Tường & Dương Châu, kiểm tra tháng hiện hành qua `SKN_THANG_TEST` / `GetLocalDate("%m")`), `Tô Tam Nương` (`粽嫂.lua`, `Task 51` 150-skill tại Dương Châu & Lâm An), `Phạt ác sứ` & `Thưởng thiện sứ` (`Dương Châu`). |
| **9. NPC Nhiệm Vụ Môn Phái / Chính Tuyến Tại Thành Thị Còn Sót** | Thành Đô (`3`), Biện Kinh (`2`), Lâm An (`3`), Đại Lý (`2`), Phượng Tường (`1`) | **`11` NPC** | Thành Đô: `Cô gái hái thuốc` (`Task 132, 133`), `Tôn Uyển` (`Task 2` Đường Môn), `Du Tương Tân` (`enemy_yuxiangjin.lua` `Task 1052, 1056`); Biện Kinh: `Bạch Công Tử` (`Task 12, 123`), `Trình Đại Gia`; Lâm An: `Quan Viên thoái nhiệm` (`Task 2, 20, 80, 86, 137`), `Người bán dầu` & `Diệp Tiểu Ngọc` (nhiệm vụ kỹ năng 150). |

---

## 4. Lộ Trình Ưu Tiên Triển Khai Theo Từng Đợt (High-ROI Wave Plan)

### Đợt 1 — Khóa Sạch Shared Services & Chuẩn Hóa 6 Tân Thủ Thôn Còn Lại (`Map 20 -> 99 -> 100 -> 121 -> 153 -> 174`)
- **Mục tiêu**: Đưa toàn bộ **9/9 Thôn Trấn** lên chuẩn **`zero_drift_locked`** (`0` `MISSING`, `0` lệch UI/Shop/Wharf).
- **Hạng mục cốt lõi**:
  1. **Sửa ngay 4 Shared Wrappers dùng chung**:
     - `FerryBoatmanDialogScript.cs`: Đọc `wharf.txt` + `wharfprice.txt` theo `CurWharf = 1..11` thay vì ép về Động Đình Hồ.
     - `TownGrocerDialogScript.cs`: Chuẩn hóa `store_sel_village_extend()` (`Mua Kim thạch` `Sale(165)` ở đầu) và `store_sel_extend()` cho thành thị.
     - `GuildGuideDialogScript.cs`: Đổi `Say` (`UI=0`) sang `Describe` (`UI=12`) khớp `DailogClass` / `CreateTaskSay`.
     - Bổ sung Map `20, 99, 100, 121, 153, 174` (cùng các map động sơ cấp `200..204`) vào `MINIMAL_TEST_MAPS` của `pack_legacy_profiles.py` và sửa `scan_trap_scripts()` trong `audit_map_scenario.py` nhận diện cả thư mục `连接trap`.
  2. **Đóng dứt điểm Map 20 (Giang Tân Thôn)**: Port 7 NPC `MISSING` (`Phụ Thân Hổ Tử`, `Võ Sư` `Task 44`, `Hổ Tử`, `Thiên Thiên`, `Ngô Hồng Mai`, `La Tiên Sinh`, `Nguyệt Hạ lão nhân`) + nhánh `Task 45` tại `Thợ rèn`, chạy kịch bản [020_giang-tan-thon.scenario.json](../../qa/JXReborn.WireParityProxy/Scenarios/Maps/020_giang-tan-thon.scenario.json) đạt `0` Critical.
  3. **Cuốn chiếu 5 thôn kế tiếp** (mỗi thôn chỉ còn đúng **2–3 NPC nhiệm vụ** chưa port!): Vĩnh Lạc (`Task 69, 70`), Chu Tiên (`Task 68, 72`), Long Môn (`Task 25, 26, 27`), Thạch Cổ (`Task 22, 23, 24`), Long Tuyền (`Task 18, 19, 20`).

### Đợt 2 — Khóa Hạ Tầng Dịch Vụ Dùng Chung Xuyên Suốt 7 Đại Thành Thị (Đóng ~110 NPC Chỉ Bằng 6 Module)
- **Mục tiêu**: Nâng tỷ lệ phủ chính xác của cả 7 Đại Thành Thị từ `~76%` lên `>92%` trước khi đi vào từng thành riêng lẻ.
- **Hạng mục cốt lõi**:
  1. Mở rộng `CityGateGuardDialogScript.cs` phủ toàn bộ **46 Vệ binh Thành môn** của 6 thành còn lại (`map_helper.lua` + nhánh `Task 38` Biện Kinh / `Task 42` Đại Lý).
  2. Chuẩn hóa `TownBlacksmithDialogScript.cs` sang `UI=12` (`tiejiang_city`), thêm `Thợ rèn` Thành Đô (`Sale 13`, `Task 1`) và `Khúc Thiết Tượng` Biện Kinh (`Sale 7`, `Task 4, 28`), nối `EnchaseItem()` / `FoundryItem()`.
  3. Chuẩn hóa `HorseMerchantDialogScript.cs` (`Sale 44/45/46/48/49`), `MoneychangerDialogScript.cs` (`Talk` -> `Say` + Hồng bao), `CourierOfficialDialogScript.cs` (thêm Dịch quan Đại Lý & Lâm An vào Manifest), và `TownTeleporterDialogScript.cs` (`Đi Hoa sơn cảnh kỹ trường` + `Tín Sứ`).
  4. Port bộ đôi `洗pk的衙役.lua` (Nha Dịch rửa PK / Đại Lao `Map 208`), `citybulletin.lua` (Bảng cáo thị 7 thành), `sevencity\dialog_npc.lua` (Tiếp Đầu Công Thành), và `leaguematch\npc\officer.lua` + `helper.lua` (14 Sứ giả Liên Đấu).

### Đợt 3 — Khóa Sâu Từng Đại Thành Thị Theo Thứ Tự Liên Kết Nhiệm Vụ (`78 -> 11 -> 37 -> 80 -> 1 -> 162 -> 176`)
- **Bước 3A (`Map 78` Tương Dương & `Map 80` Dương Châu)**: Đóng cụm 15 NPC chế tạo/đổi điểm custom tại quảng trường Tương Dương (`hoanbaocau`, `npcghephuyentin`, `npc_tho_luyen_doxanh`, `npcdoidiemtk`, `npctinsu`...) — mở khóa toàn bộ vòng lặp trang bị Huyền Tinh / Đồ Xanh / Tống Kim.
- **Bước 3B (`Map 11` Thành Đô & `Map 37` Biện Kinh & `Map 1` Phượng Tường)**: Đóng các nhánh nhiệm vụ môn phái Đường Môn / Nga My / Thiên Nhẫn / Thiếu Lâm / Thiên Vương đi qua thành thị + `Bình Bình Cô Nương` (Viêm Đế).
- **Bước 3C (`Map 162` Đại Lý & `Map 176` Lâm An)**: Đóng nhiệm vụ Hoàng Cung Lâm An, kỹ năng 150 (`Tô Tam Nương`, `Người bán dầu`, `Diệp Tiểu Ngọc`), `Thần Bí Thương Nhân` / `Thần bí thiết tượng` và xử lý an toàn dead-script `Thống lĩnh cấm Vệ`.
