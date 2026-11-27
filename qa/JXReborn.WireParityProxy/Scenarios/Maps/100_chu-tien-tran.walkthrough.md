# Khảo Sát Toàn Diện Bản Đồ 100 — Chu Tiên Trấn (`legacy-server/bin/` 100% Ground Truth)

Tài liệu đặc tả khảo sát đầu-cuối đồng bộ 1-1 với kịch bản tự động [100_chu-tien-tran.scenario.json](./100_chu-tien-tran.scenario.json), được kiểm toán định lượng bằng [audit_map_scenario.py](../../../../.agents/skills/khao-sat-ban-do/scripts/audit_map_scenario.py) đạt **`[PASS]` 100% độ phủ thực thể & nhánh trạng thái**.

---

## 1. Thông tin Bản đồ & Kiểm kê Thực thể Ground Truth

- **Map ID**: `100` (`Chu Tiên trấn` — Tân thủ thôn vùng Trung Nguyên Bắc, nối Biện Kinh Phủ (`Map 37`), Điểm báo danh Tống Kim (`Map 323/324/325`) và Phi Thiên Động (`Map 204`))
- **Đường dẫn nhị phân (`maplist.ini`)**: `中原北区\朱仙镇` (`Regions: X = 80..121, Y = 87..109`)
- **Điểm hồi sinh (`revivepos.ini`)**:
  - `RevPos = 45` (`SetRevPos(45)` tại Bảo rương 1 Tam Kiều `(1634, 3197)`)
  - `RevPos = 46` (`SetRevPos(46)` tại Bảo rương 2 Đông môn `(1701, 3194)`)
- **Trạm xe & Bến thuyền (`station.txt`, `wharf.txt`)**:
  - `CurStation = 15` (`4` NPC `Xa phu` tại `(1610, 3097)`, `(1678, 3102)`, `(1603, 3167)`, `(1719, 3216)` gọi `NewcomerStationCommon("Muốn đi xe không? ")`): chặn `Lv < 5`; mở khóa `Lv >= 5`.
- **Tổng kiểm kê thực thể Runtime Active trên Legacy Oracle (`v1`)**:
  - **Dialog NPCs (`Kind == 3`)**: **41/41** có script (tổng `41` thực thể tĩnh + `autoexec.lua`: `Người hướng dẫn bang hội` `(1614, 3167)`, `Long Ngũ` `(1621, 3170)`; loại trừ NPC sự kiện Trung Thu `shitouernpc.lua` không kích hoạt).
  - **Map Objects (`OBJ_SECTION`)**: **7/7** (`dấu hiệu 1` `dataId=6` `(1593,3077)`, `dấu hiệu 1` `dataId=6` `(1703,3078)`, `bảng cáo thị 2` `dataId=4` `(1592,3176)`, `Bảo rương 1` `dataId=1` `(1634,3197)`, `Bảo rương 1` `dataId=1` `(1701,3194)`, `dấu hiệu 1` `dataId=6` `(1602,3220)`, `dấu hiệu 1` `dataId=6` `(1738,3233)`).
  - **Traps / Portals (`TRAP_SECTION`)**: **7/7** (`4` cổng thôn đổi `FightMode` song hướng `0 <-> 1` + `3` portal liên bản đồ: `2569752374 -> Map 323` (`minLv=40`), `2891399607 -> Map 37` (`minLv=5`), `4188258588 -> Map 204` (`minLv=None`)).
  - **Scripted Monsters (`OnDeath`)**: **3/3 nhóm** (`Mộc nhân` x10 `\script\中原北区\朱仙镇\npc\朱仙镇木人.lua`, `Cọc gỗ` x10 `\script\中原北区\朱仙镇\npc\朱仙镇木桩.lua`, `Bao cát` x10 `\script\中原北区\朱仙镇\npc\朱仙镇沙袋.lua`).
  - **Tổng phân bố quái (`Region_N.dat`)**: `{'Heo trắng': 503, 'Hươu đốm': 492, 'Kim Miêu': 269, 'Cọc gỗ': 10, 'Bao cát': 10, 'Mộc nhân': 10}`.

### Bảng kiểm kê chi tiết Dialog NPCs

| # | Tên NPC | Nguồn | Tpl | Tọa độ `(X,Y)` | Cổng cấp | Shop / Task | Script `.lua` |
| :---: | :--- | :---: | :---: | :---: | :---: | :--- | :--- |
| 1 | `Xa phu` | `Region_S` | `238` | `(1610,3097)` | `5` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_车夫对话.lua` |
| 2 | `Xa phu` | `Region_S` | `237` | `(1678,3102)` | `5` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_车夫对话.lua` |
| 3 | `Võ Sư` | `Region_S` | `311` | `(1738,3085)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇武师.lua` |
| 4 | `Chủ tiệm cầm đồ Chu Tiên Trấn` | `Region_S` | `217` | `(1629,3120)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_当铺老板对话.lua` |
| 5 | `Đệ tử Cái Bang` | `Region_S` | `103` | `(1619,3122)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_职能_丐帮弟子.lua` |
| 6 | `Ông chủ tạp hóa` | `Region_S` | `215` | `(1641,3126)` | `—` | Sale=[87] | `\script\中原北区\朱仙镇\npc\朱仙镇_杂货店老板对话.lua` |
| 7 | `Nga My Cẩm Y Ni` | `Region_S` | `83` | `(1632,3133)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_峨嵋弟子对话.lua` |
| 8 | `Chủ dược điếm Chu Tiên Trấn` | `Region_S` | `203` | `(1657,3123)` | `—` | Sale=[88, 99] | `\script\中原北区\朱仙镇\npc\朱仙镇_药店老板对话.lua` |
| 9 | `Thiếu Lâm La Hán` | `Region_S` | `189` | `(1666,3116)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_职能_少林罗汉.lua` |
| 10 | `A Mao` | `Region_S` | `330` | `(1686,3117)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_阿毛对话.lua` |
| 11 | `Xa phu` | `Region_S` | `239` | `(1603,3167)` | `5` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_车夫对话.lua` |
| 12 | `Thiên Nhẫn Tử sĩ` | `Region_S` | `181` | `(1624,3139)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_天忍教徒对话.lua` |
| 13 | `Thúy Yên Hoa Sứ` | `Region_S` | `171` | `(1623,3149)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_职能_翠烟花使.lua` |
| 14 | `Thiên Vương Tướng Lĩnh` | `Region_S` | `184` | `(1634,3152)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_天王帮众对话.lua` |
| 15 | `Nguyệt Nguyệt` | `Region_S` | `362` | `(1639,3158)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_月月对话.lua` |
| 16 | `Thợ rèn` | `Region_S` | `387` | `(1659,3148)` | `—` | Sale=[86] | `\script\中原北区\朱仙镇\npc\朱仙镇_铁匠对话.lua` |
| 17 | `Ăn mày Chu Tiên Trấn` | `Region_S` | `321` | `(1648,3163)` | `—` | Task=[68] | `\script\中原北区\朱仙镇\npc\朱仙镇_乞丐对话.lua` |
| 18 | `Võ Đang Đạo Nhân` | `Region_S` | `188` | `(1648,3136)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_武当弟子对话.lua` |
| 19 | `Đường Môn Thị Vệ` | `Region_S` | `177` | `(1654,3157)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_唐门弟子对话.lua` |
| 20 | `Song Song` | `Region_S` | `354` | `(1717,3137)` | `6` | Task=[68] | `\script\中原北区\朱仙镇\npc\朱仙镇_双双对话.lua` |
| 21 | `Chu Tẩu` | `Region_S` | `381` | `(1729,3154)` | `—` | Task=[74] | `\script\中原北区\朱仙镇\npc\朱仙镇_朱嫂对话.lua` |
| 22 | `Doanh Doanh` | `Region_S` | `342` | `(1736,3162)` | `2` | Task=[72] | `\script\中原北区\朱仙镇\npc\朱仙镇_盈盈对话.lua` |
| 23 | `Dã Tẩu` | `Region_S` | `108` | `(1615,3199)` | `10` | Task=[1027, 1029, 1030, 1032] | `\script\global\seasonnpc.lua` |
| 24 | `Lễ Quan` | `Region_S` | `377` | `(1621,3181)` | `—` | Task=[67, 1313] | `\script\global\路人_礼官.lua` |
| 25 | `Côn Lôn Hộ Pháp` | `Region_S` | `309` | `(1641,3170)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_职能_昆仑护法.lua` |
| 26 | `Ngũ ĐộcTán nhân` | `Region_S` | `186` | `(1659,3179)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_职能_五毒散人.lua` |
| 27 | `Chủ lữ quán Chu Tiên Trấn` | `Region_S` | `195` | `(1680,3168)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_客栈老板对话.lua` |
| 28 | `Trương Đại Nguyên` | `Region_S` | `329` | `(1689,3194)` | `—` | Task=[72] | `\script\中原北区\朱仙镇\npc\朱仙镇_张大元对话.lua` |
| 29 | `Lệ Quyên` | `Region_S` | `355` | `(1690,3193)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_丽娟对话.lua` |
| 30 | `Vân Nhi` | `Region_S` | `352` | `(1721,3179)` | `—` | Task=[1003] | `\script\中原北区\朱仙镇\npc\朱仙镇_云儿对话.lua` |
| 31 | `Công thành quan` | `Region_S` | `373` | `(1628,3216)` | `—` | — | `\script\missions\citywar_global\infocenter.lua` |
| 32 | `A Hắc` | `Region_S` | `332` | `(1636,3202)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_阿黑对话.lua` |
| 33 | `Tiểu Hùng` | `Region_S` | `361` | `(1651,3230)` | `—` | Task=[68] | `\script\中原北区\朱仙镇\npc\朱仙镇_小熊对话.lua` |
| 34 | `Trần Tẩu` | `Region_S` | `383` | `(1649,3215)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_陈嫂对话.lua` |
| 35 | `Ông chủ tửu điếm` | `Region_S` | `212` | `(1691,3225)` | `—` | Task=[68] | `\script\中原北区\朱仙镇\npc\朱仙镇_酒店老板对话.lua` |
| 36 | `Xa phu` | `Region_S` | `239` | `(1719,3216)` | `5` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_车夫对话.lua` |
| 37 | `Lão Mã` | `Region_S` | `370` | `(1676,3253)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_老马对话.lua` |
| 38 | `Lão Chu` | `Region_S` | `379` | `(1680,3236)` | `—` | Task=[74] | `\script\中原北区\朱仙镇\npc\朱仙镇_老朱对话.lua` |
| 39 | `Tôn Lão đầu` | `Region_S` | `312` | `(1710,3234)` | `—` | — | `\script\中原北区\朱仙镇\npc\朱仙镇_孙老头对话.lua` |
| 40 | `Người hướng dẫn bang hội` | `autoexec` | `1783` | `(1614,3167)` | `—` | — | `\script\global\帮会接引人.lua` |
| 41 | `Long Ngũ` | `autoexec` | `663` | `(1621,3170)` | `—` | — | `\script\task\newtask\education\路人_龙五.lua` |

---

## 2. Chuẩn bị Nhân vật & Lộ trình Mốc Cấp độ / Trạng thái

1. **Mốc `Lv 1` (`T=0` tạo nhân vật tại `nativePlaceId = 100`, `FightMode = 0`)**:
   - Kiểm chứng toàn bộ vật thể tĩnh phi nhiệm vụ trên bản đồ (`OpenBox + SetRevPos`, `ShowLadder`, bảng cáo thị / cột lộ tiêu).
   - Kiểm chứng nhánh chặn cấp thấp tại tất cả portal có điều kiện cấp độ (`2569752374` (`Lv < 40`), `2891399607` (`Lv < 5`)).
   - Kiểm chứng nhánh chặn/thoại fallback ở `Lv 1` trên toàn bộ **7 thực thể NPC có `GetLevel()`** (`Xa phu` `(1610,3097)` (`Lv < 5`), `Xa phu` `(1678,3102)` (`Lv < 5`), `Xa phu` `(1603,3167)` (`Lv < 5`), `Song Song` `(1717,3137)` (`Lv < 6`), `Doanh Doanh` `(1736,3162)` (`Lv < 2`), `Dã Tẩu` `(1615,3199)` (`Lv < 10`), `Xa phu` `(1719,3216)` (`Lv < 5`)) cộng `Võ Sư` (`Lv < 2`).
   - Kiểm chứng đủ **2 chiều** (`0 -> 1` xuất thôn và `1 -> 0` nhập thôn) trên toàn bộ **4 cổng thôn**.
2. **Mốc `Lv 20` + `50.000 lượng` + `Hàng Long Bất Vũ (ID 14, Lv 5)`**:
   - Quét phủ 100% `41/41` Dialog NPCs có script qua `10` tâm phủ Set-Cover (`R=22` tiles, nằm trọn trong biên `3x3 Region AOI`), cộng tương tác mở khóa tường minh với từng `Xa phu` / `Thuyền Phu`.
   - Giao dịch thật trên Shop Tạp hóa (`Sale(87)` + `buy_shop_item` `0x5D`), mở Dược điếm (`Sale(99, 1)`), Thợ rèn (`Sale(86)`), và gửi/rút thật `500 lượng` (`store_money` `0x73`) tại Bảo rương.
   - Thực thi đầu-cuối toàn bộ chuỗi nhiệm vụ bản địa, nhiệm vụ môn phái/chính tuyến liên đới, chuỗi `Task 32` Khinh Công trên đủ 3 nhóm `Cọc gỗ / Bao cát / Mộc nhân`, quái nhiệm vụ có script (`OnDeath`) và quái dã ngoại.
   - Bước thật qua toàn bộ `3` portal liên bản đồ ở trạng thái mở khóa và hồi quy về tâm thôn.

---

## 3. Lộ trình Khảo sát Từng Phân khu & Tâm phủ Set-Cover

| Phân khu | Tâm `gm_teleport` sạch | Phạm vi & Thực thể kiểm chứng |
| :--- | :--- | :--- |
| **Stage 1 — Vật Thể, Portal & NPC Chặn Cấp `Lv 1`** | Các ô sạch cạnh `7` Objects, `2` Portals có `minLv` & `8` NPC chặn cấp | Kiểm chứng `OpenBox/SetRevPos`, `ShowLadder`, `Say` biển báo, chặn portal `Lv 1` và thoại chặn `Lv 1` |
| **Stage 2 — 4 Cổng Thôn Song Hướng (`0 <-> 1`)** | `137977870` `sample=(1696, 3100)`, `1001729557` `sample=(1727, 3247)`, `1543393726` `sample=(1597, 3102)`, `3993707822` `sample=(1598, 3205)` | Đổi `FightMode` đủ 2 chiều xuất thôn (`0 -> 1`) và nhập thôn (`1 -> 0`) trên toàn bộ `4` cổng |
| **Stage 3 — Set-Cover 41 NPC & Thương Mại (`Lv 20`)** | Hub #01 `(1639,3150)`, Hub #02 `(1639,3127)`, Hub #03 `(1682,3117)`, Hub #04 `(1737,3085)`, Hub #05 `(1728,3154)`, Hub #06 `(1724,3183)`, Hub #07 `(1678,3184)`, Hub #08 `(1692,3241)`, Hub #09 `(1641,3216)`, Hub #10 `(1619,3179)` | Phủ `41/41` Dialog NPCs (`3x3 Region AOI`), mở 3 Shop (`Sale 87 / 99 / 86`), `buy_shop_item` & `store_money` `500 lượng` |
| **Stage 4 — Nhiệm Vụ Thôn, Liên Phái & Luyện Khinh Công** | Các ô sạch cạnh NPC nhiệm vụ, bãi cọc luyện võ & bãi quái ngoài thôn | Thực thi 100% nhiệm vụ thôn, nhánh liên phái/chính tuyến, đánh hạ thật 100% nhóm Scripted Monsters + quái dã ngoại |
| **Stage 5 — 3 Portal Liên Bản Đồ Mở Khóa** | `2569752374 -> Map 323`, `2891399607 -> Map 37`, `4188258588 -> Map 204` | Chuyển bản đồ thật sang toàn bộ `3` bản đồ kết nối rồi quay về tâm thôn |

---

## 4. Ma trận Nhiệm vụ, Học Kỹ năng & Chức năng Liên phái

| Mã / Tên cơ chế | NPC / Vật thể tham gia | Biến `Task` & Vật phẩm Ground Truth | Chuỗi chuyển trạng thái kiểm chứng |
| :--- | :--- | :--- | :--- |
| **Khuyên Lão Chu Về Nhà (`Task 74`)** | Chu Tẩu `(1729,3154)`, Lão Chu `(1680,3236)` | `Task 74`: `0 -> 2 -> 10`; `3x Kim Thương Dược` `(1,0,0,1)` + `Repute +5` | Nhận tại Chu Tẩu (`0 -> 2`) -> gặp Lão Chu chọn `Khuyên can` (`2 -> 10`, nhận 3 bình thuốc + 5 danh vọng) |
| **Khăn Tay Doanh Doanh (`Task 72`)** | Doanh Doanh `(1736,3162)`, Trương Đại Nguyên `(1689,3194)` | `Task 72`: `0 -> 2 -> 4 -> 10` (`Lv >= 2`); `Khăn tay` `(4,192)`; giày `(0,5,0\|2,1)` + `Repute +5` | Nhận khăn tay tại Doanh Doanh (`0 -> 2`) -> gặp Trương Đại Nguyên bị vợ mắng (`2 -> 4`) -> khuyên giải Doanh Doanh nhận giày (`10`) |
| **Chiếc Nhẫn Song Song (`Task 68`)** | Song Song `(1717,3137)`, Tiểu Hùng `(1651,3230)`, Ăn mày `(1648,3163)`, Ông chủ tửu điếm `(1691,3225)` | `Task 68`: `0 -> 1 -> 2 -> 3 -> 4/5` (`Lv >= 6`); `Đậu hũ Ngũ Hương` `(4,187)` giá `100 lượng` -> `Nhẫn` `(4,191)`; `Thắt lưng da sói` `(0,6,0,2)` + `Repute +9` | Nhận tại Song Song (`0 -> 1`) -> hỏi Tiểu Hùng (`1 -> 2`) -> hỏi Ăn mày (`2 -> 3`) -> mua thật Đậu hũ tại Tửu điếm (`100 lượng`) -> đổi nhẫn `191` -> trả Song Song chọn `Từ chối` bạc nhận đai cấp 2 (`5`) |
| **Chính Tuyến Tà Phái (`Task 1003`) & Khinh Công (`Task 32`)** | Vân Nhi `(1721,3179)`, Võ Sư `(1738,3085)` + 3 nhóm Mộc nhân | `Task 1003 == 10/20/50/120/130`; `Task 32`: `0 -> 200` + `AddMagic(210, 1)` | Kiểm chứng thoại nhắc Tà phái trên Vân Nhi + chuỗi đánh thật `Cọc gỗ -> Bao cát -> Mộc nhân` học `Khinh Công (210)` |

---

## 5. Dự báo Sai lệch (`Legacy-vs-Reborn Gap Forecast`)

1. **9 Script NPC trên Map `100` hiện chưa được đăng ký trong `JXReborn.Packs.TieuDaoTu` (`MISSING` -> click no-op nếu chưa port)**:
   - NPC `Công thành quan` (`1628,3216`): `\script\missions\citywar_global\infocenter.lua`
   - NPC `Lệ Quyên` (`1690,3193`): `\script\中原北区\朱仙镇\npc\朱仙镇_丽娟对话.lua`
   - NPC `Tôn Lão đầu` (`1710,3234`): `\script\中原北区\朱仙镇\npc\朱仙镇_孙老头对话.lua`
   - NPC `Chủ tiệm cầm đồ Chu Tiên Trấn` (`1629,3120`): `\script\中原北区\朱仙镇\npc\朱仙镇_当铺老板对话.lua`
   - NPC `Nguyệt Nguyệt` (`1639,3158`): `\script\中原北区\朱仙镇\npc\朱仙镇_月月对话.lua`
   - NPC `Lão Mã` (`1676,3253`): `\script\中原北区\朱仙镇\npc\朱仙镇_老马对话.lua`
   - NPC `A Mao` (`1686,3117`): `\script\中原北区\朱仙镇\npc\朱仙镇_阿毛对话.lua`
   - NPC `A Hắc` (`1636,3202`): `\script\中原北区\朱仙镇\npc\朱仙镇_阿黑对话.lua`
   - NPC `Trần Tẩu` (`1649,3215`): `\script\中原北区\朱仙镇\npc\朱仙镇_陈嫂对话.lua`
2. **Các điểm nhạy cảm về Menu & Cổng Dịch Chuyển cần lưu ý khi chạy Live Proxy**:
   - `Ông chủ / Chủ tiệm tạp hóa` (`Sale(87)`) sử dụng `store_sel_village_extend()` có chèn dòng `"Mua Kim thạch"` (`Sale(165)`) ở `optionIndex = 0`, nên dòng `"Giao dịch"` nằm ở `optionIndex = 1` (trên Map `174` khi `Task 18 == 0` còn ưu tiên hội thoại khởi phát `Task 18` trước menu tiệm).
   - Bản đồ `100` (`inMinimalWorldset = False`) và các bản đồ đích của portal (`Map 323`, `Map 37`, `Map 204`) cần được nạp trong gói cấu hình thế giới khi chạy thực thi Live E2E `WireParityProxy`.

---

## 6. Bằng chứng Kiểm toán Tĩnh (`audit_map_scenario.py --strict`)

- **Lệnh kiểm toán**: `python workbench/.agents/skills/khao-sat-ban-do/scripts/audit_map_scenario.py audit --scenario workbench/qa/JXReborn.WireParityProxy/Scenarios/Maps/100_chu-tien-tran.scenario.json --strict`
- **Kết quả**: **`[PASS]` 100%**
  - `TotalSteps`: **226 bước**
  - `DialogNPCs`: **41/41 (`100%`)**
  - `MapObjects`: **7/7 (`100%`)**
  - `Traps`: **7/7 (`100%`)**
  - `ScriptedMonsterGroups`: **3/3 (`100%`)**
  - `Terrain / Sequence / State & Economy Branch Gaps`: **`0`**
