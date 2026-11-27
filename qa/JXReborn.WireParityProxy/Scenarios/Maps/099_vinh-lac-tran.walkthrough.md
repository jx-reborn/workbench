# Khảo Sát Toàn Diện Bản Đồ 099 — Vĩnh Lạc Trấn (`legacy-server/bin/` 100% Ground Truth)

Tài liệu đặc tả khảo sát đầu-cuối đồng bộ 1-1 với kịch bản tự động [099_vinh-lac-tran.scenario.json](./099_vinh-lac-tran.scenario.json), được kiểm toán định lượng bằng [audit_map_scenario.py](../../../../.agents/skills/khao-sat-ban-do/scripts/audit_map_scenario.py) đạt **`[PASS]` 100% độ phủ thực thể & nhánh trạng thái**.

---

## 1. Thông tin Bản đồ & Kiểm kê Thực thể Ground Truth

- **Map ID**: `99` (`Vĩnh Lạc trấn` — Tân thủ thôn vùng Tây Bắc Nam, nằm dưới chân Hoa Sơn, nối Phượng Tường Phủ (`Map 1`), Hoa Sơn (`Map 2`) và Băng Hà Động (`Map 201`))
- **Đường dẫn nhị phân (`maplist.ini`)**: `西北南区\永乐镇` (`Regions: X = 86..118, Y = 86..115`)
- **Điểm hồi sinh (`revivepos.ini`)**:
  - `RevPos = 43` (`SetRevPos(43)` tại Bảo rương 1 quảng trường `(1638, 3186)`)
  - `RevPos = 44` (`SetRevPos(44)` tại Bảo rương 2 trước lữ quán `(1651, 3252)`)
- **Trạm xe & Bến thuyền (`station.txt`, `wharf.txt`)**:
  - `CurStation = 14` (`2` NPC `Xa phu` tại Bắc môn `(1607, 3161)` và Nam môn `(1630, 3304)` gọi `NewcomerStationCommon("<#> Muốn ngồi xe?")`): chặn `Lv < 5` bằng `Talk`; mở khóa `Lv >= 5` với 4 lựa chọn trạm xe.
- **Tổng kiểm kê thực thể Runtime Active trên Legacy Oracle (`v1`)**:
  - **Dialog NPCs (`Kind == 3`)**: **39/39** có script (tổng `39` thực thể tĩnh + `autoexec.lua`: `Người hướng dẫn bang hội` `(1578, 3209)`, `Long Ngũ` `(1625, 3194)`; loại trừ NPC sự kiện Trung Thu `shitouernpc.lua` không kích hoạt).
  - **Map Objects (`OBJ_SECTION`)**: **6/6** (`bảng cáo thị 3` `dataId=5` `(1583,3136)`, `bảng cáo thị 3` `dataId=5` `(1710,3155)`, `Bảo rương 1` `dataId=1` `(1638,3186)`, `Bảo rương 1` `dataId=1` `(1651,3252)`, `bảng cáo thị 3` `dataId=5` `(1711,3252)`, `bảng cáo thị 3` `dataId=5` `(1624,3342)`).
  - **Traps / Portals (`TRAP_SECTION`)**: **7/7** (`4` cổng thôn đổi `FightMode` song hướng `0 <-> 1` + `3` portal liên bản đồ: `631805625 -> Map 201` (`minLv=None`), `1674145877 -> Map 2` (`minLv=5`), `2945576716 -> Map 1` (`minLv=5`)).
  - **Scripted Monsters (`OnDeath`)**: **3/3 nhóm** (`Mộc nhân` x10 `\script\西北南区\永乐镇\npc\永乐镇木人.lua`, `Cọc gỗ` x9 `\script\西北南区\永乐镇\npc\永乐镇木桩.lua`, `Bao cát` x9 `\script\西北南区\永乐镇\npc\永乐镇沙袋.lua`).
  - **Tổng phân bố quái (`Region_N.dat`)**: `{'Hươu đốm': 326, 'Kim Miêu': 186, 'Heo trắng': 315, 'Mộc nhân': 10, 'Cọc gỗ': 9, 'Bao cát': 9}`.

### Bảng kiểm kê chi tiết Dialog NPCs

| # | Tên NPC | Nguồn | Tpl | Tọa độ `(X,Y)` | Cổng cấp | Shop / Task | Script `.lua` |
| :---: | :--- | :---: | :---: | :---: | :---: | :--- | :--- |
| 1 | `Ngưu Ngưu` | `Region_S` | `360` | `(1651,3131)` | `—` | Task=[65] | `\script\西北南区\永乐镇\npc\永乐镇_牛牛对话.lua` |
| 2 | `Xa phu` | `Region_S` | `393` | `(1607,3161)` | `5` | — | `\script\西北南区\永乐镇\npc\永乐镇_车夫对话.lua` |
| 3 | `Âu Dương` | `Region_S` | `322` | `(1623,3165)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_欧阳对话.lua` |
| 4 | `Ông chủ tửu điếm` | `Region_S` | `375` | `(1602,3188)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_酒店老板对话.lua` |
| 5 | `Lễ Quan` | `Region_S` | `377` | `(1614,3177)` | `—` | Task=[67, 1313] | `\script\global\路人_礼官.lua` |
| 6 | `Hoàng thúc` | `Region_S` | `370` | `(1624,3174)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_黄叔对话.lua` |
| 7 | `Nga My Cẩm Y Ni` | `Region_S` | `83` | `(1627,3188)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_峨嵋弟子对话.lua` |
| 8 | `Thiên Nhẫn Tử sĩ` | `Region_S` | `181` | `(1617,3197)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_天忍教徒对话.lua` |
| 9 | `Đệ tử Cái Bang` | `Region_S` | `103` | `(1631,3180)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_职能_丐帮弟子.lua` |
| 10 | `Dã Tẩu` | `Region_S` | `108` | `(1646,3192)` | `10` | Task=[1027, 1029, 1030, 1032] | `\script\global\seasonnpc.lua` |
| 11 | `Côn Lôn Hộ Pháp` | `Region_S` | `309` | `(1676,3193)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_职能_昆仑护法.lua` |
| 12 | `Ngũ ĐộcTán nhân` | `Region_S` | `186` | `(1690,3180)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_职能_五毒散人.lua` |
| 13 | `Đổng Đại thúc` | `Region_S` | `379` | `(1587,3200)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_董大叔对话.lua` |
| 14 | `Ông chủ dược điếm` | `Region_S` | `389` | `(1595,3227)` | `—` | Sale=[85, 99] | `\script\西北南区\永乐镇\npc\永乐镇_药店老板对话.lua` |
| 15 | `Trình Vô Tâm` | `Region_S` | `329` | `(1629,3221)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_程无心对话.lua` |
| 16 | `Thiên Vương Tướng Lĩnh` | `Region_S` | `184` | `(1616,3206)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_天王帮众对话.lua` |
| 17 | `Thúy Yên Hoa Sứ` | `Region_S` | `171` | `(1618,3218)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_职能_翠烟花使.lua` |
| 18 | `Đường Môn Thị Vệ` | `Region_S` | `177` | `(1636,3208)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_唐门弟子对话.lua` |
| 19 | `Võ Đang Đạo Nhân` | `Region_S` | `188` | `(1642,3203)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_武当弟子对话.lua` |
| 20 | `Phụng Nhi` | `Region_S` | `350` | `(1658,3204)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_凤儿对话.lua` |
| 21 | `Ông chủ cầm đồ Vĩnh lạc` | `Region_S` | `217` | `(1679,3231)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_当铺老板对话.lua` |
| 22 | `Thiếu Lâm La Hán` | `Region_S` | `189` | `(1666,3208)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_职能_少林罗汉.lua` |
| 23 | `Lỗ Gia` | `Region_S` | `313` | `(1693,3205)` | `—` | Task=[70] | `\script\西北南区\永乐镇\npc\永乐镇_鲁爷对话.lua` |
| 24 | `Lý Dự` | `Region_S` | `331` | `(1565,3262)` | `5` | Task=[69] | `\script\西北南区\永乐镇\npc\永乐镇_李豫对话.lua` |
| 25 | `Ngưu Tẩu` | `Region_S` | `381` | `(1613,3243)` | `2` | Task=[65] | `\script\西北南区\永乐镇\npc\永乐镇_牛牛妈对话.lua` |
| 26 | `Ăn mày` | `Region_S` | `321` | `(1661,3233)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_乞丐对话.lua` |
| 27 | `Chủ lữ quán` | `Region_S` | `378` | `(1655,3248)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_客栈老板对话.lua` |
| 28 | `Cúc Nữu` | `Region_S` | `363` | `(1578,3279)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_菊妞对话.lua` |
| 29 | `Ông chủ tạp hóa` | `Region_S` | `215` | `(1606,3282)` | `—` | Sale=[84] | `\script\西北南区\永乐镇\npc\永乐镇_杂货店老板对话.lua` |
| 30 | `Công thành quan` | `Region_S` | `373` | `(1659,3282)` | `—` | — | `\script\missions\citywar_global\infocenter.lua` |
| 31 | `Thợ rèn` | `Region_S` | `387` | `(1669,3268)` | `—` | Sale=[83] | `\script\西北南区\永乐镇\npc\永乐镇_铁匠对话.lua` |
| 32 | `Xuân Hương` | `Region_S` | `339` | `(1685,3288)` | `—` | Task=[69] | `\script\西北南区\永乐镇\npc\永乐镇_春香对话.lua` |
| 33 | `Xa phu` | `Region_S` | `239` | `(1630,3304)` | `5` | — | `\script\西北南区\永乐镇\npc\永乐镇_车夫对话.lua` |
| 34 | `Bà Thư` | `Region_S` | `248` | `(1644,3302)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_舒奶奶对话.lua` |
| 35 | `Đổng Đại Ma` | `Region_S` | `384` | `(1676,3313)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇_董大妈对话.lua` |
| 36 | `Cát Gia` | `Region_S` | `315` | `(1663,3328)` | `—` | Task=[70] | `\script\西北南区\永乐镇\npc\永乐镇_葛爷对话.lua` |
| 37 | `Võ Sư` | `Region_S` | `311` | `(1659,3423)` | `—` | — | `\script\西北南区\永乐镇\npc\永乐镇武师.lua` |
| 38 | `Người hướng dẫn bang hội` | `autoexec` | `1783` | `(1578,3209)` | `—` | — | `\script\global\帮会接引人.lua` |
| 39 | `Long Ngũ` | `autoexec` | `663` | `(1625,3194)` | `—` | — | `\script\task\newtask\education\路人_龙五.lua` |

---

## 2. Chuẩn bị Nhân vật & Lộ trình Mốc Cấp độ / Trạng thái

1. **Mốc `Lv 1` (`T=0` tạo nhân vật tại `nativePlaceId = 99`, `FightMode = 0`)**:
   - Kiểm chứng toàn bộ vật thể tĩnh phi nhiệm vụ trên bản đồ (`OpenBox + SetRevPos`, `ShowLadder`, bảng cáo thị / cột lộ tiêu).
   - Kiểm chứng nhánh chặn cấp thấp tại tất cả portal có điều kiện cấp độ (`1674145877` (`Lv < 5`), `2945576716` (`Lv < 5`)).
   - Kiểm chứng nhánh chặn/thoại fallback ở `Lv 1` trên toàn bộ **5 thực thể NPC có `GetLevel()`** (`Xa phu` `(1607,3161)` (`Lv < 5`), `Dã Tẩu` `(1646,3192)` (`Lv < 10`), `Lý Dự` `(1565,3262)` (`Lv < 5`), `Ngưu Tẩu` `(1613,3243)` (`Lv < 2`), `Xa phu` `(1630,3304)` (`Lv < 5`)) cộng `Võ Sư` (`Lv < 2`).
   - Kiểm chứng đủ **2 chiều** (`0 -> 1` xuất thôn và `1 -> 0` nhập thôn) trên toàn bộ **4 cổng thôn**.
2. **Mốc `Lv 20` + `50.000 lượng` + `Hàng Long Bất Vũ (ID 14, Lv 5)`**:
   - Quét phủ 100% `39/39` Dialog NPCs có script qua `13` tâm phủ Set-Cover (`R=22` tiles, nằm trọn trong biên `3x3 Region AOI`), cộng tương tác mở khóa tường minh với từng `Xa phu` / `Thuyền Phu`.
   - Giao dịch thật trên Shop Tạp hóa (`Sale(84)` + `buy_shop_item` `0x5D`), mở Dược điếm (`Sale(99, 1)`), Thợ rèn (`Sale(83)`), và gửi/rút thật `500 lượng` (`store_money` `0x73`) tại Bảo rương.
   - Thực thi đầu-cuối toàn bộ chuỗi nhiệm vụ bản địa, nhiệm vụ môn phái/chính tuyến liên đới, chuỗi `Task 32` Khinh Công trên đủ 3 nhóm `Cọc gỗ / Bao cát / Mộc nhân`, quái nhiệm vụ có script (`OnDeath`) và quái dã ngoại.
   - Bước thật qua toàn bộ `3` portal liên bản đồ ở trạng thái mở khóa và hồi quy về tâm thôn.

---

## 3. Lộ trình Khảo sát Từng Phân khu & Tâm phủ Set-Cover

| Phân khu | Tâm `gm_teleport` sạch | Phạm vi & Thực thể kiểm chứng |
| :--- | :--- | :--- |
| **Stage 1 — Vật Thể, Portal & NPC Chặn Cấp `Lv 1`** | Các ô sạch cạnh `6` Objects, `2` Portals có `minLv` & `6` NPC chặn cấp | Kiểm chứng `OpenBox/SetRevPos`, `ShowLadder`, `Say` biển báo, chặn portal `Lv 1` và thoại chặn `Lv 1` |
| **Stage 2 — 4 Cổng Thôn Song Hướng (`0 <-> 1`)** | `2469817001` `sample=(1587, 3151)`, `2705573438` `sample=(1623, 3328)`, `3278953784` `sample=(1707, 3171)`, `4069300437` `sample=(1702, 3258)` | Đổi `FightMode` đủ 2 chiều xuất thôn (`0 -> 1`) và nhập thôn (`1 -> 0`) trên toàn bộ `4` cổng |
| **Stage 3 — Set-Cover 39 NPC & Thương Mại (`Lv 20`)** | Hub #01 `(1627,3190)`, Hub #02 `(1615,3228)`, Hub #03 `(1587,3201)`, Hub #04 `(1622,3165)`, Hub #05 `(1650,3131)`, Hub #06 `(1690,3181)`, Hub #07 `(1674,3211)`, Hub #08 `(1656,3243)`, Hub #09 `(1668,3277)`, Hub #10 `(1575,3275)`, Hub #11 `(1605,3282)`, Hub #12 `(1659,3314)`, Hub #13 `(1660,3424)` | Phủ `39/39` Dialog NPCs (`3x3 Region AOI`), mở 3 Shop (`Sale 84 / 99 / 83`), `buy_shop_item` & `store_money` `500 lượng` |
| **Stage 4 — Nhiệm Vụ Thôn, Liên Phái & Luyện Khinh Công** | Các ô sạch cạnh NPC nhiệm vụ, bãi cọc luyện võ & bãi quái ngoài thôn | Thực thi 100% nhiệm vụ thôn, nhánh liên phái/chính tuyến, đánh hạ thật 100% nhóm Scripted Monsters + quái dã ngoại |
| **Stage 5 — 3 Portal Liên Bản Đồ Mở Khóa** | `631805625 -> Map 201`, `1674145877 -> Map 2`, `2945576716 -> Map 1` | Chuyển bản đồ thật sang toàn bộ `3` bản đồ kết nối rồi quay về tâm thôn |

---

## 4. Ma trận Nhiệm vụ, Học Kỹ năng & Chức năng Liên phái

| Mã / Tên cơ chế | NPC / Vật thể tham gia | Biến `Task` & Vật phẩm Ground Truth | Chuỗi chuyển trạng thái kiểm chứng |
| :--- | :--- | :--- | :--- |
| **Tìm Ngưu Ngưu (`Task 65`)** | Ngưu Tẩu `(1613,3243)`, Ngưu Ngưu `(1651,3131)` | `Task 65`: `0 -> 2 -> 9/10/11` (`Lv >= 2`); `3x Kim Thương Dược` `(1,0,0,1)` + `Repute +6` | Nhận tại Ngưu Tẩu (`0 -> 2`) -> gặp Ngưu Ngưu chọn `Khuyên can` (`2 -> 9`, nhận 3 bình thuốc + 6 danh vọng) |
| **Mượn Sách Kê Thần Lục (`Task 70`)** | Lỗ Gia `(1693,3205)`, Cát Gia `(1663,3328)` | `Task 70`: `0 -> 2 -> 4 -> 6 -> 8 -> 10`; `Di Kiên Chí` `(4,189)` <-> `Kê Thần Lục` `(4,188)`; `Ngưu Bì Yêu Đái` `(0,6,0,1)` + `Repute +5` | Nhận tại Lỗ Gia (`0 -> 2`) -> hỏi Cát Gia (`2 -> 4`) -> lấy `Di Kiên Chí` tại Lỗ Gia (`4 -> 6`) -> đổi `Kê Thần Lục` tại Cát Gia (`6 -> 8`) -> trả Lỗ Gia nhận thắt lưng (`10`) |
| **Tâm Ý Lý Dự (`Task 69`)** | Lý Dự `(1565,3262)`, Xuân Hương `(1685,3288)` | `Task 69`: `0 -> 2 -> 4 -> 6 -> 8 -> 10` (`Lv >= 5`); `Quạt lá sen` `(4,190)`; nhẫn `(0,3,0,1\|2)` + `Repute +7` | Nhận quạt tại Lý Dự (`0 -> 2`) -> tặng Xuân Hương nhận câu đố Liễu Vĩnh (`2 -> 4`) -> báo Lý Dự (`4 -> 6`) -> giải đố `Liễu Thất` (`optionIndex=2`, `6 -> 8`) -> báo Lý Dự nhận nhẫn (`10`) |
| **Học Khinh Công (`Task 32`)** | Võ Sư `(1659,3423)`, Cọc gỗ, Bao cát, Mộc nhân | `Task 32`: `0 -> 20 -> 70 -> 80 -> 130 -> 140 -> 190 -> 200` (`Lv >= 2`, `100 lượng`); `AddMagic(210, 1)` | Đóng `100 lượng` (`20`) -> đánh gục thật `Cọc gỗ` (`69 -> 70`) -> `Bao cát` (`129 -> 130`) -> `Mộc nhân` (`189 -> 190`) -> lĩnh hội `Khinh Công (210)` |

---

## 5. Dự báo Sai lệch (`Legacy-vs-Reborn Gap Forecast`)

1. **11 Script NPC trên Map `99` hiện chưa được đăng ký trong `JXReborn.Packs.TieuDaoTu` (`MISSING` -> click no-op nếu chưa port)**:
   - NPC `Công thành quan` (`1659,3282`): `\script\missions\citywar_global\infocenter.lua`
   - NPC `Ăn mày` (`1661,3233`): `\script\西北南区\永乐镇\npc\永乐镇_乞丐对话.lua`
   - NPC `Phụng Nhi` (`1658,3204`): `\script\西北南区\永乐镇\npc\永乐镇_凤儿对话.lua`
   - NPC `Chủ lữ quán` (`1655,3248`): `\script\西北南区\永乐镇\npc\永乐镇_客栈老板对话.lua`
   - NPC `Ông chủ cầm đồ Vĩnh lạc` (`1679,3231`): `\script\西北南区\永乐镇\npc\永乐镇_当铺老板对话.lua`
   - NPC `Âu Dương` (`1623,3165`): `\script\西北南区\永乐镇\npc\永乐镇_欧阳对话.lua`
   - NPC `Trình Vô Tâm` (`1629,3221`): `\script\西北南区\永乐镇\npc\永乐镇_程无心对话.lua`
   - NPC `Cúc Nữu` (`1578,3279`): `\script\西北南区\永乐镇\npc\永乐镇_菊妞对话.lua`
   - NPC `Đổng Đại thúc` (`1587,3200`): `\script\西北南区\永乐镇\npc\永乐镇_董大叔对话.lua`
   - NPC `Đổng Đại Ma` (`1676,3313`): `\script\西北南区\永乐镇\npc\永乐镇_董大妈对话.lua`
   - NPC `Hoàng thúc` (`1624,3174`): `\script\西北南区\永乐镇\npc\永乐镇_黄叔对话.lua`
2. **Các điểm nhạy cảm về Menu & Cổng Dịch Chuyển cần lưu ý khi chạy Live Proxy**:
   - `Ông chủ / Chủ tiệm tạp hóa` (`Sale(84)`) sử dụng `store_sel_village_extend()` có chèn dòng `"Mua Kim thạch"` (`Sale(165)`) ở `optionIndex = 0`, nên dòng `"Giao dịch"` nằm ở `optionIndex = 1` (trên Map `174` khi `Task 18 == 0` còn ưu tiên hội thoại khởi phát `Task 18` trước menu tiệm).
   - Bản đồ `99` (`inMinimalWorldset = False`) và các bản đồ đích của portal (`Map 201`, `Map 2`, `Map 1`) cần được nạp trong gói cấu hình thế giới khi chạy thực thi Live E2E `WireParityProxy`.

---

## 6. Bằng chứng Kiểm toán Tĩnh (`audit_map_scenario.py --strict`)

- **Lệnh kiểm toán**: `python workbench/.agents/skills/khao-sat-ban-do/scripts/audit_map_scenario.py audit --scenario workbench/qa/JXReborn.WireParityProxy/Scenarios/Maps/099_vinh-lac-tran.scenario.json --strict`
- **Kết quả**: **`[PASS]` 100%**
  - `TotalSteps`: **212 bước**
  - `DialogNPCs`: **39/39 (`100%`)**
  - `MapObjects`: **6/6 (`100%`)**
  - `Traps`: **7/7 (`100%`)**
  - `ScriptedMonsterGroups`: **3/3 (`100%`)**
  - `Terrain / Sequence / State & Economy Branch Gaps`: **`0`**
