# Khảo Sát Toàn Diện Bản Đồ 174 — Long Tuyền Thôn (`legacy-server/bin/` 100% Ground Truth)

Tài liệu đặc tả khảo sát đầu-cuối đồng bộ 1-1 với kịch bản tự động [174_long-tuyen-thon.scenario.json](./174_long-tuyen-thon.scenario.json), được kiểm toán định lượng bằng [audit_map_scenario.py](../../../../.agents/skills/khao-sat-ban-do/scripts/audit_map_scenario.py) đạt **`[PASS]` 100% độ phủ thực thể & nhánh trạng thái**.

---

## 1. Thông tin Bản đồ & Kiểm kê Thực thể Ground Truth

- **Map ID**: `174` (`Long Tuyền thôn` — Tân thủ thôn vùng Giang Nam, đầu mối kết nối 5 khu vực chiến đấu: La Tiêu Sơn (`Map 179`), Ngũ Độc Giáo (`Map 183`), Vũ Di Sơn (`Map 193`), Nhạn Đãng Sơn (`Map 195`) và Thanh Khê Động (`Map 198`))
- **Đường dẫn nhị phân (`maplist.ini`)**: `江南区\龙泉` (`Regions: X = 84..115, Y = 90..112`)
- **Điểm hồi sinh (`revivepos.ini`)**:
  - `RevPos = 65` (`SetRevPos(65)` tại Bảo rương 1 phía Nam `(1658, 3294)`)
  - `RevPos = 66` (`SetRevPos(66)` tại Bảo rương 2 phía Bắc `(1616, 3190)`)
- **Trạm xe & Bến thuyền (`station.txt`, `wharf.txt`)**:
  - `CurStation = 12` (`1` NPC `Xa phu` tại `(1634, 3196)` gọi `NewcomerStationCommon`): chặn `Lv < 5`; mở khóa `Lv >= 5`.
- **Tổng kiểm kê thực thể Runtime Active trên Legacy Oracle (`v1`)**:
  - **Dialog NPCs (`Kind == 3`)**: **33/33** có script (tổng `34` thực thể tĩnh + `autoexec.lua`: `Người hướng dẫn bang hội` `(1644, 3308)`, `Long Ngũ` `(1639, 3291)`, `Phó Lôi Thư` `(1593, 3256)`; `1` NPC cảnh nền rỗng script `Học trò thợ rèn` `(1611, 3244)`; loại trừ NPC sự kiện Trung Thu `shitouernpc.lua` không kích hoạt).
  - **Map Objects (`OBJ_SECTION`)**: **7/7** (`dấu hiệu 41` `dataId=294` `(1715,2945)`, `dấu hiệu 44` `dataId=297` `(1816,3008)`, `Bảo rương 1` `dataId=1` `(1616,3190)`, `Bạch Tuyết Bách Hợp` `dataId=331` `(1540,3244)`, `Dương Xuân Linh Chi` `dataId=332` `(1691,3252)`, `Bảo rương 1` `dataId=1` `(1658,3294)`, `dấu hiệu 43` `dataId=296` `(1817,3453)`).
  - **Traps / Portals (`TRAP_SECTION`)**: **9/9** (`4` cổng thôn đổi `FightMode` song hướng `0 <-> 1` + `5` portal liên bản đồ: `123827185 -> Map 183` (`minLv=5`), `1396616935 -> Map 198` (`minLv=5`), `3329974233 -> Map 195` (`minLv=5`), `3407947926 -> Map 193` (`minLv=5`), `3505793327 -> Map 179` (`minLv=5`)).
  - **Scripted Monsters (`OnDeath`)**: **3/3 nhóm** (`Mộc nhân` x4 `\script\江南区\龙泉村\npc\战斗_木人.lua`, `Cọc gỗ` x3 `\script\江南区\龙泉村\npc\战斗_木桩.lua`, `Bao cát` x4 `\script\江南区\龙泉村\npc\战斗_沙袋.lua`).
  - **Tổng phân bố quái (`Region_N.dat`)**: `{'Hươu đốm': 280, 'Heo trắng': 271, 'Kim Miêu': 169, 'Bao cát': 4, 'Cọc gỗ': 3, 'Mộc nhân': 4}`.

### Bảng kiểm kê chi tiết Dialog NPCs

| # | Tên NPC | Nguồn | Tpl | Tọa độ `(X,Y)` | Cổng cấp | Shop / Task | Script `.lua` |
| :---: | :--- | :---: | :---: | :---: | :---: | :--- | :--- |
| 1 | `Quan sai` | `Region_S` | `51` | `(1584,3188)` | `—` | — | `\script\江南区\龙泉村\npc\职能_官差.lua` |
| 2 | `Thúy Yên Hoa Sứ` | `Region_S` | `171` | `(1628,3187)` | `—` | — | `\script\江南区\龙泉村\npc\龙泉村_职能_翠烟花使.lua` |
| 3 | `Xa phu` | `Region_S` | `237` | `(1634,3196)` | `5` | — | `\script\江南区\龙泉村\npc\职能_车夫.lua` |
| 4 | `Ngũ ĐộcTán nhân` | `Region_S` | `186` | `(1551,3213)` | `—` | — | `\script\江南区\龙泉村\npc\龙泉村_职能_五毒散人.lua` |
| 5 | `Chủ tiệm tạp hóa` | `Region_S` | `216` | `(1566,3201)` | `—` | Sale=[29]; Task=[18, 20] | `\script\江南区\龙泉村\npc\职能_杂货店老板.lua` |
| 6 | `Tạ Thanh My` | `Region_S` | `340` | `(1562,3212)` | `—` | — | `\script\江南区\龙泉村\npc\路人_谢青眉.lua` |
| 7 | `Hà Nhi` | `Region_S` | `363` | `(1563,3213)` | `—` | — | `\script\江南区\龙泉村\npc\路人_霞儿.lua` |
| 8 | `Chủ lữ quán` | `Region_S` | `195` | `(1594,3227)` | `—` | — | `\script\江南区\龙泉村\npc\职能_客栈老板.lua` |
| 9 | `Côn Lôn Hộ Pháp` | `Region_S` | `309` | `(1601,3226)` | `—` | — | `\script\江南区\龙泉村\npc\龙泉村_职能_昆仑护法.lua` |
| 10 | `Dã Tẩu` | `Region_S` | `108` | `(1608,3200)` | `10` | Task=[1027, 1029, 1030, 1032] | `\script\Global\seasonnpc.lua` |
| 11 | `Đệ tử Nga Mi` | `Region_S` | `83` | `(1600,3206)` | `—` | — | `\script\江南区\龙泉村\npc\龙泉村_职能_峨嵋弟子.lua` |
| 12 | `Lễ Quan` | `Region_S` | `377` | `(1631,3216)` | `—` | Task=[67, 1313] | `\script\global\路人_礼官.lua` |
| 13 | `Thiếu Lâm La Hán` | `Region_S` | `189` | `(1622,3225)` | `—` | — | `\script\江南区\龙泉村\npc\龙泉村_职能_少林罗汉.lua` |
| 14 | `Chung Cầm` | `Region_S` | `326` | `(1644,3229)` | `5` | Task=[20] | `\script\江南区\龙泉村\npc\路人_钟离.lua` |
| 15 | `Đệ tử Đường Môn` | `Region_S` | `177` | `(1639,3208)` | `—` | — | `\script\江南区\龙泉村\npc\龙泉村_职能_唐门弟子.lua` |
| 16 | `Công thành quan` | `Region_S` | `373` | `(1652,3206)` | `—` | — | `\script\missions\citywar_global\infocenter.lua` |
| 17 | `Tân Gia Gia` | `Region_S` | `245` | `(1563,3261)` | `—` | — | `\script\江南区\龙泉村\npc\路人_章爷爷.lua` |
| 18 | `Ông chủ dược điếm` | `Region_S` | `203` | `(1572,3253)` | `—` | Sale=[30, 99]; Task=[10] | `\script\江南区\龙泉村\npc\职能_药店老板.lua` |
| 19 | `Đệ tử Thiên Vương` | `Region_S` | `183` | `(1583,3244)` | `—` | — | `\script\江南区\龙泉村\npc\龙泉村_职能_天王帮众.lua` |
| 20 | `Học trò thợ rèn` | `Region_S` | `202` | `(1611,3244)` | `—` | — | *(empty)* |
| 21 | `Thợ rèn` | `Region_S` | `387` | `(1606,3251)` | `—` | Sale=[28]; Task=[19] | `\script\江南区\龙泉村\npc\职能_铁匠.lua` |
| 22 | `Lý Giai` | `Region_S` | `323` | `(1627,3263)` | `—` | — | `\script\江南区\龙泉村\npc\路人_李佳.lua` |
| 23 | `Uy Duẫn Chân` | `Region_S` | `327` | `(1652,3260)` | `—` | Task=[10, 37, 75, 124] | `\script\江南区\龙泉村\npc\路人_戚允真.lua` |
| 24 | `Bành Phóng` | `Region_S` | `309` | `(1665,3251)` | `2` | Task=[19] | `\script\江南区\龙泉村\npc\路人_彭放.lua` |
| 25 | `Thiên Nhẫn Giáo đồ` | `Region_S` | `181` | `(1673,3262)` | `—` | — | `\script\江南区\龙泉村\npc\龙泉村_职能_天忍教徒.lua` |
| 26 | `Đệ tử Võ Đang` | `Region_S` | `188` | `(1589,3277)` | `—` | — | `\script\江南区\龙泉村\npc\龙泉村_职能_武当弟子.lua` |
| 27 | `Đệ tử Cái Bang` | `Region_S` | `103` | `(1602,3265)` | `—` | — | `\script\江南区\龙泉村\npc\龙泉村_职能_丐帮弟子.lua` |
| 28 | `Tô Hồng Tụ` | `Region_S` | `345` | `(1630,3264)` | `—` | — | `\script\江南区\龙泉村\npc\路人_苏红袖.lua` |
| 29 | `Khổng Hiểu` | `Region_S` | `361` | `(1630,3292)` | `—` | Task=[18] | `\script\江南区\龙泉村\npc\路人_孔晓.lua` |
| 30 | `Mạnh Bà Bà` | `Region_S` | `247` | `(1656,3277)` | `—` | — | `\script\江南区\龙泉村\npc\路人_孟婆婆.lua` |
| 31 | `Võ Sư` | `Region_S` | `311` | `(1583,3459)` | `—` | — | `\script\江南区\龙泉村\npc\职能_武师.lua` |
| 32 | `Người hướng dẫn bang hội` | `autoexec` | `1783` | `(1644,3308)` | `—` | — | `\script\global\帮会接引人.lua` |
| 33 | `Long Ngũ` | `autoexec` | `663` | `(1639,3291)` | `—` | — | `\script\task\newtask\education\路人_龙五.lua` |
| 34 | `Phó Lôi Thư` | `autoexec` | `715` | `(1593,3256)` | `—` | Task=[1003] | `\script\task\newtask\master\xiepai\路人_傅雷书.lua` |

---

## 2. Chuẩn bị Nhân vật & Lộ trình Mốc Cấp độ / Trạng thái

1. **Mốc `Lv 1` (`T=0` tạo nhân vật tại `nativePlaceId = 174`, `FightMode = 0`)**:
   - Kiểm chứng toàn bộ vật thể tĩnh phi nhiệm vụ trên bản đồ (`OpenBox + SetRevPos`, `ShowLadder`, bảng cáo thị / cột lộ tiêu).
   - Kiểm chứng nhánh chặn cấp thấp tại tất cả portal có điều kiện cấp độ (`123827185` (`Lv < 5`), `1396616935` (`Lv < 5`), `3329974233` (`Lv < 5`), `3407947926` (`Lv < 5`), `3505793327` (`Lv < 5`)).
   - Kiểm chứng nhánh chặn/thoại fallback ở `Lv 1` trên toàn bộ **4 thực thể NPC có `GetLevel()`** (`Xa phu` `(1634,3196)` (`Lv < 5`), `Dã Tẩu` `(1608,3200)` (`Lv < 10`), `Chung Cầm` `(1644,3229)` (`Lv < 5`), `Bành Phóng` `(1665,3251)` (`Lv < 2`)) cộng `Võ Sư` (`Lv < 2`).
   - Kiểm chứng đủ **2 chiều** (`0 -> 1` xuất thôn và `1 -> 0` nhập thôn) trên toàn bộ **4 cổng thôn**.
2. **Mốc `Lv 20` + `50.000 lượng` + `Hàng Long Bất Vũ (ID 14, Lv 5)`**:
   - Quét phủ 100% `33/33` Dialog NPCs có script qua `11` tâm phủ Set-Cover (`R=22` tiles, nằm trọn trong biên `3x3 Region AOI`), cộng tương tác mở khóa tường minh với từng `Xa phu` / `Thuyền Phu`.
   - Giao dịch thật trên Shop Tạp hóa (`Sale(29)` + `buy_shop_item` `0x5D`), mở Dược điếm (`Sale(99, 1)`), Thợ rèn (`Sale(28)`), và gửi/rút thật `500 lượng` (`store_money` `0x73`) tại Bảo rương.
   - Thực thi đầu-cuối toàn bộ chuỗi nhiệm vụ bản địa, nhiệm vụ môn phái/chính tuyến liên đới, chuỗi `Task 32` Khinh Công trên đủ 3 nhóm `Cọc gỗ / Bao cát / Mộc nhân`, quái nhiệm vụ có script (`OnDeath`) và quái dã ngoại.
   - Bước thật qua toàn bộ `5` portal liên bản đồ ở trạng thái mở khóa và hồi quy về tâm thôn.

---

## 3. Lộ trình Khảo sát Từng Phân khu & Tâm phủ Set-Cover

| Phân khu | Tâm `gm_teleport` sạch | Phạm vi & Thực thể kiểm chứng |
| :--- | :--- | :--- |
| **Stage 1 — Vật Thể, Portal & NPC Chặn Cấp `Lv 1`** | Các ô sạch cạnh `7` Objects, `5` Portals có `minLv` & `5` NPC chặn cấp | Kiểm chứng `OpenBox/SetRevPos`, `ShowLadder`, `Say` biển báo, chặn portal `Lv 1` và thoại chặn `Lv 1` |
| **Stage 2 — 4 Cổng Thôn Song Hướng (`0 <-> 1`)** | `2167294813` `sample=(1661, 3196)`, `2219372469` `sample=(1662, 3320)`, `2347433997` `sample=(1557, 3278)`, `2406851941` `sample=(1564, 3177)` | Đổi `FightMode` đủ 2 chiều xuất thôn (`0 -> 1`) và nhập thôn (`1 -> 0`) trên toàn bộ `4` cổng |
| **Stage 3 — Set-Cover 33 NPC & Thương Mại (`Lv 20`)** | Hub #01 `(1591,3257)`, Hub #02 `(1563,3260)`, Hub #03 `(1563,3213)`, Hub #04 `(1585,3189)`, Hub #05 `(1611,3217)`, Hub #06 `(1629,3188)`, Hub #07 `(1645,3212)`, Hub #08 `(1668,3255)`, Hub #09 `(1642,3275)`, Hub #10 `(1643,3307)`, Hub #11 `(1582,3459)` | Phủ `33/33` Dialog NPCs (`3x3 Region AOI`), mở 3 Shop (`Sale 29 / 99 / 28`), `buy_shop_item` & `store_money` `500 lượng` |
| **Stage 4 — Nhiệm Vụ Thôn, Liên Phái & Luyện Khinh Công** | Các ô sạch cạnh NPC nhiệm vụ, bãi cọc luyện võ & bãi quái ngoài thôn | Thực thi 100% nhiệm vụ thôn, nhánh liên phái/chính tuyến, đánh hạ thật 100% nhóm Scripted Monsters + quái dã ngoại |
| **Stage 5 — 5 Portal Liên Bản Đồ Mở Khóa** | `123827185 -> Map 183`, `1396616935 -> Map 198`, `3329974233 -> Map 195`, `3407947926 -> Map 193`, `3505793327 -> Map 179` | Chuyển bản đồ thật sang toàn bộ `5` bản đồ kết nối rồi quay về tâm thôn |

---

## 4. Ma trận Nhiệm vụ, Học Kỹ năng & Chức năng Liên phái

| Mã / Tên cơ chế | NPC / Vật thể tham gia | Biến `Task` & Vật phẩm Ground Truth | Chuỗi chuyển trạng thái kiểm chứng |
| :--- | :--- | :--- | :--- |
| **Tâm Ý Khổng Hiểu (`Task 18`)** | Chủ tiệm tạp hóa `(1566,3201)`, Khổng Hiểu `(1630,3292)` | `Task 18`: `0 -> 3 -> 6 -> 10`; áo cấp 1 `(0,2,6\|13,1)` + `Repute +5` (đồng thời mở khóa menu `Sale(29)` chuẩn và nhánh tặng Linh Chi miễn phí cho `Task 20`) | Nghe Chủ tiệm tạp hóa tâm sự (`0 -> 3`) -> hỏi Khổng Hiểu (`3 -> 6`) -> báo lại Chủ tiệm tạp hóa nhận áo (`10`) |
| **Canh Dương Xuân Bạch Tuyết (`Task 20`)** | Chung Cầm `(1644,3229)`, `Bạch Tuyết Bách Hợp` `(1540,3244)`, `Dương Xuân Linh Chi` `(1691,3252)` | `Task 20`: `0 -> 5 -> 10` (`Lv >= 5`); nhặt vật thể `dataId=331 -> (4,232)` & `dataId=332 -> (4,231)`; ngọc bội/túi hương `(0,9,0\|1,1)` + `Repute +8` | Nhận tại Chung Cầm (`0 -> 5`) -> nhặt thật 2 thảo dược trên bản đồ -> giao Chung Cầm nhận ngọc bội (`10`) |
| **Sửa Bảo Đao Bành Phóng (`Task 19`)** | Bành Phóng `(1665,3251)`, Thợ rèn `(1606,3251)` | `Task 19`: `0 -> 2 -> 4 (Timer 7) -> 6 -> 8 -> 10` (`Lv >= 2`); đao hỏng `(4,229)` + `120 lượng` -> đao tốt `(4,230)`; Hộ uyển `(0,8,0\|1,1)` + `Repute +6` | Nhận đao hỏng tại Bành Phóng (`0 -> 2`) -> đưa Thợ rèn `120 lượng` sửa đao (`2 -> 4`, bật `Timer 7`) -> tua `Task 19 = 6` lấy đao tốt (`8`) -> trả Bành Phóng (`10`) |
| **Ngũ Độc Cấp 10 (`Task 10`), Tà Phái (`Task 1003`) & Khinh Công (`Task 32`)** | Ông chủ dược điếm `(1572,3253)`, Phó Lôi Thư `(1593,3256)`, Uy Duẫn Chân `(1652,3260)`, Võ Sư `(1583,3459)` | `Task 10 = 2570` mua `10 bao Tín thạch` `(4,220)` giá `1000 lượng`; `Task 1003 = 150`; `Task 124` Ngũ Độc 90; `Task 32` Khinh Công | Mồi `Task 10 = 2570` mua thật Tín thạch tại Dược điếm + kiểm chứng Phó Lôi Thư + đánh hạ thật 3 nhóm cọc/bao cát/mộc nhân học `Khinh Công (210)` |

---

## 5. Dự báo Sai lệch (`Legacy-vs-Reborn Gap Forecast`)

1. **8 Script NPC trên Map `174` hiện chưa được đăng ký trong `JXReborn.Packs.TieuDaoTu` (`MISSING` -> click no-op nếu chưa port)**:
   - NPC `Công thành quan` (`1652,3206`): `\script\missions\citywar_global\infocenter.lua`
   - NPC `Quan sai` (`1584,3188`): `\script\江南区\龙泉村\npc\职能_官差.lua`
   - NPC `Chủ lữ quán` (`1594,3227`): `\script\江南区\龙泉村\npc\职能_客栈老板.lua`
   - NPC `Mạnh Bà Bà` (`1656,3277`): `\script\江南区\龙泉村\npc\路人_孟婆婆.lua`
   - NPC `Lý Giai` (`1627,3263`): `\script\江南区\龙泉村\npc\路人_李佳.lua`
   - NPC `Tân Gia Gia` (`1563,3261`): `\script\江南区\龙泉村\npc\路人_章爷爷.lua`
   - NPC `Tạ Thanh My` (`1562,3212`): `\script\江南区\龙泉村\npc\路人_谢青眉.lua`
   - NPC `Hà Nhi` (`1563,3213`): `\script\江南区\龙泉村\npc\路人_霞儿.lua`
2. **Các điểm nhạy cảm về Menu & Cổng Dịch Chuyển cần lưu ý khi chạy Live Proxy**:
   - `Ông chủ / Chủ tiệm tạp hóa` (`Sale(29)`) sử dụng `store_sel_village_extend()` có chèn dòng `"Mua Kim thạch"` (`Sale(165)`) ở `optionIndex = 0`, nên dòng `"Giao dịch"` nằm ở `optionIndex = 1` (trên Map `174` khi `Task 18 == 0` còn ưu tiên hội thoại khởi phát `Task 18` trước menu tiệm).
   - Bản đồ `174` (`inMinimalWorldset = False`) và các bản đồ đích của portal (`Map 183`, `Map 198`, `Map 195`, `Map 193`, `Map 179`) cần được nạp trong gói cấu hình thế giới khi chạy thực thi Live E2E `WireParityProxy`.

---

## 6. Bằng chứng Kiểm toán Tĩnh (`audit_map_scenario.py --strict`)

- **Lệnh kiểm toán**: `python workbench/.agents/skills/khao-sat-ban-do/scripts/audit_map_scenario.py audit --scenario workbench/qa/JXReborn.WireParityProxy/Scenarios/Maps/174_long-tuyen-thon.scenario.json --strict`
- **Kết quả**: **`[PASS]` 100%**
  - `TotalSteps`: **220 bước**
  - `DialogNPCs`: **33/33 (`100%`)**
  - `MapObjects`: **7/7 (`100%`)**
  - `Traps`: **9/9 (`100%`)**
  - `ScriptedMonsterGroups`: **3/3 (`100%`)**
  - `Terrain / Sequence / State & Economy Branch Gaps`: **`0`**
