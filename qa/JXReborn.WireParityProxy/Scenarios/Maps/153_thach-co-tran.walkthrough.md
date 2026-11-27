# Khảo Sát Toàn Diện Bản Đồ 153 — Thạch Cổ Trấn (`legacy-server/bin/` 100% Ground Truth)

Tài liệu đặc tả khảo sát đầu-cuối đồng bộ 1-1 với kịch bản tự động [153_thach-co-tran.scenario.json](./153_thach-co-tran.scenario.json), được kiểm toán định lượng bằng [audit_map_scenario.py](../../../../.agents/skills/khao-sat-ban-do/scripts/audit_map_scenario.py) đạt **`[PASS]` 100% độ phủ thực thể & nhánh trạng thái**.

---

## 1. Thông tin Bản đồ & Kiểm kê Thực thể Ground Truth

- **Map ID**: `153` (`Thạch Cổ trấn` — Tân thủ thôn vùng Tây Nam Nam bên bờ Kim Sa Giang, nối Đại Lý Phủ (`Map 162`) và Vô Danh Động (`Map 203`))
- **Đường dẫn nhị phân (`maplist.ini`)**: `西南南区\石鼓镇` (`Regions: X = 84..118, Y = 87..112`)
- **Điểm hồi sinh (`revivepos.ini`)**:
  - `RevPos = 59` (`SetRevPos(59)` tại Bảo rương 1 trung tâm thôn `(1664, 3225)`)
- **Trạm xe & Bến thuyền (`station.txt`, `wharf.txt`)**:
  - `CurStation = 16` (`2` NPC `Xa phu` tại Bắc môn `(1630, 3177)` và Đông Nam `(1685, 3244)` gọi `NewcomerStationCommon`): chặn `Lv < 5`; mở khóa `Lv >= 5`.
- **Tổng kiểm kê thực thể Runtime Active trên Legacy Oracle (`v1`)**:
  - **Dialog NPCs (`Kind == 3`)**: **29/29** có script (tổng `29` thực thể tĩnh + `autoexec.lua`: `Người hướng dẫn bang hội` `(1609, 3257)`, `Long Ngũ` `(1622, 3242)`; loại trừ NPC sự kiện Trung Thu `shitouernpc.lua` không kích hoạt).
  - **Map Objects (`OBJ_SECTION`)**: **2/2** (`dấu hiệu 43` `dataId=296` `(1869,3027)`, `Bảo rương 1` `dataId=1` `(1664,3225)`).
  - **Traps / Portals (`TRAP_SECTION`)**: **4/4** (`2` cổng thôn đổi `FightMode` song hướng `0 <-> 1` + `2` portal liên bản đồ: `927159190 -> Map 203` (`minLv=None`), `3667243001 -> Map 162` (`minLv=5`)).
  - **Scripted Monsters (`OnDeath`)**: **4/4 nhóm** (`Mộc nhân` x11 `\script\西南南区\石鼓镇\npc\战斗_木人.lua`, `Cọc gỗ` x10 `\script\西南南区\石鼓镇\npc\战斗_木桩.lua`, `Bao cát` x9 `\script\西南南区\石鼓镇\npc\战斗_沙袋.lua`, `Linh Miêu` x4 `\script\西南南区\石鼓镇\npc\战斗_灵猫.lua`).
  - **Tổng phân bố quái (`Region_N.dat`)**: `{'Hươu đốm': 783, 'Heo trắng': 800, 'Kim Miêu': 399, 'Linh Miêu': 4, 'Bao cát': 9, 'Cọc gỗ': 10, 'Mộc nhân': 11}`.

### Bảng kiểm kê chi tiết Dialog NPCs

| # | Tên NPC | Nguồn | Tpl | Tọa độ `(X,Y)` | Cổng cấp | Shop / Task | Script `.lua` |
| :---: | :--- | :---: | :---: | :---: | :---: | :--- | :--- |
| 1 | `Xa phu` | `Region_S` | `239` | `(1630,3177)` | `5` | — | `\script\西南南区\石鼓镇\npc\职能_车夫.lua` |
| 2 | `Chủ lữ quán` | `Region_S` | `195` | `(1618,3189)` | `—` | — | `\script\西南南区\石鼓镇\npc\职能_客栈老板.lua` |
| 3 | `Thúy Yên Hoa Sứ` | `Region_S` | `171` | `(1623,3184)` | `—` | — | `\script\西南南区\石鼓镇\npc\石鼓镇_职能_翠烟花使.lua` |
| 4 | `A Kinh` | `Region_S` | `357` | `(1650,3192)` | `5` | Task=[24] | `\script\西南南区\石鼓镇\npc\路人_阿琼.lua` |
| 5 | `Ngũ ĐộcTán nhân` | `Region_S` | `186` | `(1579,3211)` | `—` | — | `\script\西南南区\石鼓镇\npc\石鼓镇_职能_五毒散人.lua` |
| 6 | `Đệ tử Nga Mi` | `Region_S` | `84` | `(1592,3217)` | `—` | — | `\script\西南南区\石鼓镇\npc\石鼓镇_职能_峨嵋弟子.lua` |
| 7 | `Ông chủ dược điếm` | `Region_S` | `203` | `(1600,3209)` | `—` | Sale=[27, 99] | `\script\西南南区\石鼓镇\npc\职能_药店老板.lua` |
| 8 | `Thẩm Gia` | `Region_S` | `324` | `(1610,3202)` | `—` | Task=[23] | `\script\西南南区\石鼓镇\npc\路人_沈爷.lua` |
| 9 | `Lễ Quan` | `Region_S` | `377` | `(1606,3227)` | `—` | Task=[67, 1313] | `\script\global\路人_礼官.lua` |
| 10 | `Thiếu Lâm La Hán` | `Region_S` | `189` | `(1623,3228)` | `—` | — | `\script\西南南区\石鼓镇\npc\石鼓镇_职能_少林罗汉.lua` |
| 11 | `Công thành quan` | `Region_S` | `373` | `(1629,3201)` | `—` | — | `\script\missions\citywar_global\infocenter.lua` |
| 12 | `Chủ tiệm tạp hóa` | `Region_S` | `216` | `(1634,3223)` | `—` | Sale=[26] | `\script\西南南区\石鼓镇\npc\职能_杂货店老板.lua` |
| 13 | `Thiên Nhẫn Giáo đồ` | `Region_S` | `181` | `(1639,3213)` | `—` | — | `\script\西南南区\石鼓镇\npc\石鼓镇_职能_天忍教徒.lua` |
| 14 | `Đệ tử Cái Bang` | `Region_S` | `103` | `(1632,3229)` | `—` | — | `\script\西南南区\石鼓镇\npc\石鼓镇_职能_丐帮弟子.lua` |
| 15 | `Liễu Diệp Nhi` | `Region_S` | `363` | `(1656,3221)` | `—` | Task=[22] | `\script\西南南区\石鼓镇\npc\路人_柳叶儿.lua` |
| 16 | `Đệ tử Đường Môn` | `Region_S` | `177` | `(1650,3212)` | `—` | — | `\script\西南南区\石鼓镇\npc\石鼓镇_职能_唐门弟子.lua` |
| 17 | `Côn Lôn Hộ Pháp` | `Region_S` | `309` | `(1590,3237)` | `—` | — | `\script\西南南区\石鼓镇\npc\石鼓镇_职能_昆仑护法.lua` |
| 18 | `Thợ rèn` | `Region_S` | `199` | `(1608,3249)` | `—` | Sale=[25] | `\script\西南南区\石鼓镇\npc\职能_铁匠.lua` |
| 19 | `Đệ tử Thiên Vương` | `Region_S` | `183` | `(1631,3255)` | `—` | — | `\script\西南南区\石鼓镇\npc\石鼓镇_职能_天王帮众.lua` |
| 20 | `Dã Tẩu` | `Region_S` | `108` | `(1643,3235)` | `10` | Task=[1027, 1029, 1030, 1032] | `\script\Global\seasonnpc.lua` |
| 21 | `Võ Sư` | `Region_S` | `379` | `(1660,3257)` | `—` | Task=[22] | `\script\西南南区\石鼓镇\npc\职能_武师.lua` |
| 22 | `Đệ tử Võ Đang` | `Region_S` | `188` | `(1658,3236)` | `—` | — | `\script\西南南区\石鼓镇\npc\石鼓镇_职能_武当弟子.lua` |
| 23 | `A Toàn` | `Region_S` | `332` | `(1671,3234)` | `3` | Task=[23] | `\script\西南南区\石鼓镇\npc\路人_阿玛&阿全.lua` |
| 24 | `A Mã` | `Region_S` | `347` | `(1672,3235)` | `3` | Task=[23] | `\script\西南南区\石鼓镇\npc\路人_阿玛&阿全.lua` |
| 25 | `Xa phu` | `Region_S` | `235` | `(1685,3244)` | `5` | — | `\script\西南南区\石鼓镇\npc\职能_车夫.lua` |
| 26 | `A Nguyên` | `Region_S` | `359` | `(1694,3256)` | `—` | — | `\script\西南南区\石鼓镇\npc\路人_阿源.lua` |
| 27 | `Ngô Nghiễn Nhân` | `Region_S` | `327` | `(1697,3257)` | `—` | — | `\script\西南南区\石鼓镇\npc\路人_吴趼人.lua` |
| 28 | `Người hướng dẫn bang hội` | `autoexec` | `1783` | `(1609,3257)` | `—` | — | `\script\global\帮会接引人.lua` |
| 29 | `Long Ngũ` | `autoexec` | `663` | `(1622,3242)` | `—` | — | `\script\task\newtask\education\路人_龙五.lua` |

---

## 2. Chuẩn bị Nhân vật & Lộ trình Mốc Cấp độ / Trạng thái

1. **Mốc `Lv 1` (`T=0` tạo nhân vật tại `nativePlaceId = 153`, `FightMode = 0`)**:
   - Kiểm chứng toàn bộ vật thể tĩnh phi nhiệm vụ trên bản đồ (`OpenBox + SetRevPos`, `ShowLadder`, bảng cáo thị / cột lộ tiêu).
   - Kiểm chứng nhánh chặn cấp thấp tại tất cả portal có điều kiện cấp độ (`3667243001` (`Lv < 5`)).
   - Kiểm chứng nhánh chặn/thoại fallback ở `Lv 1` trên toàn bộ **6 thực thể NPC có `GetLevel()`** (`Xa phu` `(1630,3177)` (`Lv < 5`), `A Kinh` `(1650,3192)` (`Lv < 5`), `Dã Tẩu` `(1643,3235)` (`Lv < 10`), `A Toàn` `(1671,3234)` (`Lv < 3`), `A Mã` `(1672,3235)` (`Lv < 3`), `Xa phu` `(1685,3244)` (`Lv < 5`)) cộng `Võ Sư` (`Lv < 2`).
   - Kiểm chứng đủ **2 chiều** (`0 -> 1` xuất thôn và `1 -> 0` nhập thôn) trên toàn bộ **2 cổng thôn**.
2. **Mốc `Lv 20` + `50.000 lượng` + `Hàng Long Bất Vũ (ID 14, Lv 5)`**:
   - Quét phủ 100% `29/29` Dialog NPCs có script qua `7` tâm phủ Set-Cover (`R=22` tiles, nằm trọn trong biên `3x3 Region AOI`), cộng tương tác mở khóa tường minh với từng `Xa phu` / `Thuyền Phu`.
   - Giao dịch thật trên Shop Tạp hóa (`Sale(26)` + `buy_shop_item` `0x5D`), mở Dược điếm (`Sale(99, 1)`), Thợ rèn (`Sale(25)`), và gửi/rút thật `500 lượng` (`store_money` `0x73`) tại Bảo rương.
   - Thực thi đầu-cuối toàn bộ chuỗi nhiệm vụ bản địa, nhiệm vụ môn phái/chính tuyến liên đới, chuỗi `Task 32` Khinh Công trên đủ 3 nhóm `Cọc gỗ / Bao cát / Mộc nhân`, quái nhiệm vụ có script (`OnDeath`) và quái dã ngoại.
   - Bước thật qua toàn bộ `2` portal liên bản đồ ở trạng thái mở khóa và hồi quy về tâm thôn.

---

## 3. Lộ trình Khảo sát Từng Phân khu & Tâm phủ Set-Cover

| Phân khu | Tâm `gm_teleport` sạch | Phạm vi & Thực thể kiểm chứng |
| :--- | :--- | :--- |
| **Stage 1 — Vật Thể, Portal & NPC Chặn Cấp `Lv 1`** | Các ô sạch cạnh `2` Objects, `1` Portals có `minLv` & `7` NPC chặn cấp | Kiểm chứng `OpenBox/SetRevPos`, `ShowLadder`, `Say` biển báo, chặn portal `Lv 1` và thoại chặn `Lv 1` |
| **Stage 2 — 2 Cổng Thôn Song Hướng (`0 <-> 1`)** | `3896736772` `sample=(1697, 3267)`, `3962654142` `sample=(1645, 3172)` | Đổi `FightMode` đủ 2 chiều xuất thôn (`0 -> 1`) và nhập thôn (`1 -> 0`) trên toàn bộ `2` cổng |
| **Stage 3 — Set-Cover 29 NPC & Thương Mại (`Lv 20`)** | Hub #01 `(1641,3225)`, Hub #02 `(1633,3191)`, Hub #03 `(1599,3212)`, Hub #04 `(1590,3236)`, Hub #05 `(1614,3250)`, Hub #06 `(1659,3257)`, Hub #07 `(1683,3245)` | Phủ `29/29` Dialog NPCs (`3x3 Region AOI`), mở 3 Shop (`Sale 26 / 99 / 25`), `buy_shop_item` & `store_money` `500 lượng` |
| **Stage 4 — Nhiệm Vụ Thôn, Liên Phái & Luyện Khinh Công** | Các ô sạch cạnh NPC nhiệm vụ, bãi cọc luyện võ & bãi quái ngoài thôn | Thực thi 100% nhiệm vụ thôn, nhánh liên phái/chính tuyến, đánh hạ thật 100% nhóm Scripted Monsters + quái dã ngoại |
| **Stage 5 — 2 Portal Liên Bản Đồ Mở Khóa** | `927159190 -> Map 203`, `3667243001 -> Map 162` | Chuyển bản đồ thật sang toàn bộ `2` bản đồ kết nối rồi quay về tâm thôn |

---

## 4. Ma trận Nhiệm vụ, Học Kỹ năng & Chức năng Liên phái

| Mã / Tên cơ chế | NPC / Vật thể tham gia | Biến `Task` & Vật phẩm Ground Truth | Chuỗi chuyển trạng thái kiểm chứng |
| :--- | :--- | :--- | :--- |
| **Đưa Cơm Cho Võ Sư (`Task 22`)** | Liễu Diệp Nhi `(1656,3221)`, Võ Sư `(1660,3257)` | `Task 22`: `0 -> 5 -> 10`; `Cơm` `(4,233)`; binh khí cấp 1 `(0,0,0..5,1)` + `Repute +3` | Nhận hộp cơm tại Liễu Diệp Nhi (`0 -> 5`) -> giao cho Võ Sư ngoài thôn nhận vũ khí cấp 1 (`10`) |
| **Trả Nợ Thẩm Gia (`Task 23`)** | A Toàn `(1671,3234)`, A Mã `(1672,3235)`, Thẩm Gia `(1610,3202)` | `Task 23`: `0 -> 3 -> 6 -> 10` (`Lv >= 3`); trả `300 lượng` xóa nợ; áo cấp 1 `(0,2,2\|10,1)` + `Repute +7` | Nhận tại A Toàn (`0 -> 3`) -> trả thật `300 lượng` cho Thẩm Gia (`3 -> 6`) -> báo tin mừng cho A Mã nhận áo (`10`) |
| **Chiếc Nhẫn A Kinh (`Task 24`)** | A Kinh `(1650,3192)`, 4 `Linh Miêu` có script `战斗_灵猫.lua` | `Task 24`: `0 -> 5 -> 10` (`Lv >= 5`); `Nhẫn` `(4,234)` rớt `66%` từ `Linh Miêu`; bùa/dây chuyền `(0,4,0\|1,1)` + `Repute +7` | Nhận tại A Kinh (`0 -> 5`) -> đánh hạ thật cả 4 `Linh Miêu` scripted ngoài thôn thu nhẫn `234` -> trả A Kinh (`10`) |
| **Học Khinh Công (`Task 32`)** | Võ Sư `(1660,3257)`, Cọc gỗ, Bao cát, Mộc nhân | `Task 32`: `0 -> 20 -> 70 -> 80 -> 130 -> 140 -> 190 -> 200`; `AddMagic(210, 1)` | Đóng `100 lượng` (`20`) -> đánh gục thật `Cọc gỗ` (`69 -> 70`) -> `Bao cát` (`129 -> 130`) -> `Mộc nhân` (`189 -> 190`) -> học `Khinh Công (210)` |

---

## 5. Dự báo Sai lệch (`Legacy-vs-Reborn Gap Forecast`)

1. **4 Script NPC trên Map `153` hiện chưa được đăng ký trong `JXReborn.Packs.TieuDaoTu` (`MISSING` -> click no-op nếu chưa port)**:
   - NPC `Công thành quan` (`1629,3201`): `\script\missions\citywar_global\infocenter.lua`
   - NPC `Chủ lữ quán` (`1618,3189`): `\script\西南南区\石鼓镇\npc\职能_客栈老板.lua`
   - NPC `Ngô Nghiễn Nhân` (`1697,3257`): `\script\西南南区\石鼓镇\npc\路人_吴趼人.lua`
   - NPC `A Nguyên` (`1694,3256`): `\script\西南南区\石鼓镇\npc\路人_阿源.lua`
2. **Các điểm nhạy cảm về Menu & Cổng Dịch Chuyển cần lưu ý khi chạy Live Proxy**:
   - `Ông chủ / Chủ tiệm tạp hóa` (`Sale(26)`) sử dụng `store_sel_village_extend()` có chèn dòng `"Mua Kim thạch"` (`Sale(165)`) ở `optionIndex = 0`, nên dòng `"Giao dịch"` nằm ở `optionIndex = 1` (trên Map `174` khi `Task 18 == 0` còn ưu tiên hội thoại khởi phát `Task 18` trước menu tiệm).
   - Bản đồ `153` (`inMinimalWorldset = False`) và các bản đồ đích của portal (`Map 203`, `Map 162`) cần được nạp trong gói cấu hình thế giới khi chạy thực thi Live E2E `WireParityProxy`.

---

## 6. Bằng chứng Kiểm toán Tĩnh (`audit_map_scenario.py --strict`)

- **Lệnh kiểm toán**: `python workbench/.agents/skills/khao-sat-ban-do/scripts/audit_map_scenario.py audit --scenario workbench/qa/JXReborn.WireParityProxy/Scenarios/Maps/153_thach-co-tran.scenario.json --strict`
- **Kết quả**: **`[PASS]` 100%**
  - `TotalSteps`: **171 bước**
  - `DialogNPCs`: **29/29 (`100%`)**
  - `MapObjects`: **2/2 (`100%`)**
  - `Traps`: **4/4 (`100%`)**
  - `ScriptedMonsterGroups`: **4/4 (`100%`)**
  - `Terrain / Sequence / State & Economy Branch Gaps`: **`0`**
