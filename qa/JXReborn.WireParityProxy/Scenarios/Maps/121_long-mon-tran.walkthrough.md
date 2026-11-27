# Khảo Sát Toàn Diện Bản Đồ 121 — Long Môn Trấn (`legacy-server/bin/` 100% Ground Truth)

Tài liệu đặc tả khảo sát đầu-cuối đồng bộ 1-1 với kịch bản tự động [121_long-mon-tran.scenario.json](./121_long-mon-tran.scenario.json), được kiểm toán định lượng bằng [audit_map_scenario.py](../../../../.agents/skills/khao-sat-ban-do/scripts/audit_map_scenario.py) đạt **`[PASS]` 100% độ phủ thực thể & nhánh trạng thái**.

---

## 1. Thông tin Bản đồ & Kiểm kê Thực thể Ground Truth

- **Map ID**: `121` (`Long Môn trấn` — Tân thủ thôn vùng Tây Bắc Bắc, đầu mối giao thông lớn nối Côn Lôn Phái (`Map 131`), Hoàng Hà Nguyên Đầu (`Map 122`), Dược Vương Cốc (`Map 140`), Cổ Dương Động (`Map 200`), Phong Lăng Độ (`Map 336`) và Mạc Cao Quật (`Map 340`))
- **Đường dẫn nhị phân (`maplist.ini`)**: `西北北区\龙门镇` (`Regions: X = 105..145, Y = 123..149`)
- **Điểm hồi sinh (`revivepos.ini`)**:
  - `RevPos = 55` (`SetRevPos(55)` tại Bảo rương 1 Đông Bắc `(2000, 4470)`)
  - `RevPos = 56` (`SetRevPos(56)` tại Bảo rương 2 Trung tâm `(1933, 4512)`)
- **Trạm xe & Bến thuyền (`station.txt`, `wharf.txt`)**:
  - `CurStation = 9` (`2` NPC `Xa phu` tại Bắc môn `(1924, 4426)` và Nam môn `(1980, 4593)` gọi `NewcomerStationCommon("<#> Khách quan muốn đi đâu?")`): chặn `Lv < 5`; mở khóa `Lv >= 5`.
  - `CurWharf = 10` (`Thuyền Phu` bến Hoàng Hà `(1837, 4633)`): chặn `Lv < 5` bằng `Talk`; mở khóa `Lv >= 5` kết nối Phượng Tường & Biện Kinh.
- **Tổng kiểm kê thực thể Runtime Active trên Legacy Oracle (`v1`)**:
  - **Dialog NPCs (`Kind == 3`)**: **36/36** có script (tổng `36` thực thể tĩnh + `autoexec.lua`: `Người hướng dẫn bang hội` `(2036, 4507)`, `Long Ngũ` `(2010, 4484)`, `Đoan Mộc Thanh` `(2016, 4490)`; loại trừ NPC sự kiện Trung Thu `shitouernpc.lua` không kích hoạt).
  - **Map Objects (`OBJ_SECTION`)**: **7/7** (`dấu hiệu 44` `dataId=297` `(1843,4100)`, `dấu hiệu 44` `dataId=297` `(2207,4170)`, `dấu hiệu 41` `dataId=294` `(1726,4264)`, `dấu hiệu 42` `dataId=295` `(1716,4378)`, `dấu hiệu 43` `dataId=296` `(2287,4394)`, `Bảo rương 1` `dataId=1` `(2000,4470)`, `Bảo rương 1` `dataId=1` `(1933,4512)`).
  - **Traps / Portals (`TRAP_SECTION`)**: **10/10** (`4` cổng thôn đổi `FightMode` song hướng `0 <-> 1` + `6` portal liên bản đồ: `341062816 -> Map 200` (`minLv=None`), `1870138638 -> Map 336` (`minLv=5`), `3127090509 -> Map 140` (`minLv=5`), `3551699588 -> Map 340` (`minLv=5`), `3685519343 -> Map 122` (`minLv=5`), `3688459730 -> Map 131` (`minLv=5`)).
  - **Scripted Monsters (`OnDeath`)**: **4/4 nhóm** (`Mộc nhân` x9 `\script\西北北区\龙门镇\npc\战斗_木人.lua`, `Cọc gỗ` x3 `\script\西北北区\龙门镇\npc\战斗_木桩.lua`, `Bao cát` x7 `\script\西北北区\龙门镇\npc\战斗_沙袋.lua`, `Heo rừng` x4 `\script\西北北区\龙门镇\npc\战斗_野猪.lua`).
  - **Tổng phân bố quái (`Region_N.dat`)**: `{'Kim Miêu': 216, 'Hươu đốm': 493, 'Heo trắng': 488, 'Heo rừng': 4, 'Bao cát': 7, 'Mộc nhân': 9, 'Cọc gỗ': 3}`.

### Bảng kiểm kê chi tiết Dialog NPCs

| # | Tên NPC | Nguồn | Tpl | Tọa độ `(X,Y)` | Cổng cấp | Shop / Task | Script `.lua` |
| :---: | :--- | :---: | :---: | :---: | :---: | :--- | :--- |
| 1 | `Xa phu` | `Region_S` | `237` | `(1924,4426)` | `5` | — | `\script\西北北区\龙门镇\npc\职能_车夫.lua` |
| 2 | `Trình Đại Vi` | `Region_S` | `330` | `(1942,4458)` | `—` | — | `\script\西北北区\龙门镇\npc\路人_程大为.lua` |
| 3 | `Triệu Mi Nhi` | `Region_S` | `344` | `(1993,4477)` | `—` | Task=[26] | `\script\西北北区\龙门镇\npc\路人_赵眉儿.lua` |
| 4 | `Triệu gia Bà Bà` | `Region_S` | `318` | `(1985,4479)` | `—` | — | `\script\西北北区\龙门镇\npc\路人_赵家婆婆.lua` |
| 5 | `Thiên Nhẫn Giáo đồ` | `Region_S` | `181` | `(2006,4465)` | `—` | — | `\script\西北北区\龙门镇\npc\龙门镇_职能_天忍教徒.lua` |
| 6 | `Ăn mày` | `Region_S` | `321` | `(1872,4495)` | `2` | Task=[26] | `\script\西北北区\龙门镇\npc\路人_乞丐.lua` |
| 7 | `Ngũ ĐộcTán nhân` | `Region_S` | `186` | `(1895,4489)` | `—` | — | `\script\西北北区\龙门镇\npc\龙门镇_职能_五毒散人.lua` |
| 8 | `Quan sai` | `Region_S` | `52` | `(1940,4502)` | `—` | — | `\script\西北北区\龙门镇\npc\职能_官差.lua` |
| 9 | `Quan sai` | `Region_S` | `52` | `(1945,4497)` | `—` | — | `\script\西北北区\龙门镇\npc\职能_官差.lua` |
| 10 | `Côn Lôn Hộ Pháp` | `Region_S` | `309` | `(1938,4508)` | `—` | — | `\script\西北北区\龙门镇\npc\龙门镇_职能_昆仑护法.lua` |
| 11 | `Đệ tử Nga Mi` | `Region_S` | `84` | `(1956,4490)` | `—` | — | `\script\西北北区\龙门镇\npc\龙门镇_职能_峨嵋弟子.lua` |
| 12 | `Dã Tẩu` | `Region_S` | `108` | `(1959,4500)` | `10` | Task=[1027, 1029, 1030, 1032] | `\script\Global\seasonnpc.lua` |
| 13 | `Thúy Yên Hoa Sứ` | `Region_S` | `171` | `(1976,4486)` | `—` | — | `\script\西北北区\龙门镇\npc\龙门镇_职能_翠烟花使.lua` |
| 14 | `Công thành quan` | `Region_S` | `373` | `(1982,4497)` | `—` | — | `\script\missions\citywar_global\infocenter.lua` |
| 15 | `Chủ tiệm tạp hóa` | `Region_S` | `243` | `(1914,4524)` | `—` | Sale=[26] | `\script\西北北区\龙门镇\npc\职能_杂货店老板.lua` |
| 16 | `Đệ tử Cái Bang` | `Region_S` | `103` | `(1904,4537)` | `—` | — | `\script\西北北区\龙门镇\npc\龙门镇_职能_丐帮弟子.lua` |
| 17 | `Thượng Quan Thu` | `Region_S` | `327` | `(1925,4516)` | `—` | Task=[25] | `\script\西北北区\龙门镇\npc\路人_上官秋.lua` |
| 18 | `Thiếu Lâm La Hán` | `Region_S` | `189` | `(1948,4527)` | `—` | — | `\script\西北北区\龙门镇\npc\龙门镇_职能_少林罗汉.lua` |
| 19 | `Lễ Quan` | `Region_S` | `377` | `(1957,4518)` | `—` | Task=[67, 1313] | `\script\global\路人_礼官.lua` |
| 20 | `Bà chủ` | `Region_S` | `356` | `(1975,4537)` | `5` | Task=[25, 27] | `\script\西北北区\龙门镇\npc\职能_客栈老板娘.lua` |
| 21 | `Đệ tử Đường Môn` | `Region_S` | `177` | `(1984,4515)` | `—` | — | `\script\西北北区\龙门镇\npc\龙门镇_职能_唐门弟子.lua` |
| 22 | `Thợ rèn` | `Region_S` | `199` | `(2000,4512)` | `—` | Sale=[25] | `\script\西北北区\龙门镇\npc\职能_铁匠.lua` |
| 23 | `Ông chủ dược điếm` | `Region_S` | `203` | `(1934,4551)` | `—` | Sale=[27, 99] | `\script\西北北区\龙门镇\npc\职能_药店老板.lua` |
| 24 | `Đệ tử Võ Đang` | `Region_S` | `188` | `(1922,4557)` | `—` | — | `\script\西北北区\龙门镇\npc\龙门镇_职能_武当弟子.lua` |
| 25 | `Tê Hương` | `Region_S` | `338` | `(1950,4564)` | `—` | — | `\script\西北北区\龙门镇\npc\路人_樨香.lua` |
| 26 | `Đệ tử Thiên Vương` | `Region_S` | `184` | `(1957,4557)` | `—` | — | `\script\西北北区\龙门镇\npc\龙门镇_职能_天王帮众.lua` |
| 27 | `Tôn Nhị Lang` | `Region_S` | `311` | `(2011,4551)` | `40` | Task=[41, 86] | `\script\西北北区\龙门镇\npc\路人_孙二郎.lua` |
| 28 | `Tôn Tiểu Tiểu` | `Region_S` | `360` | `(2019,4551)` | `—` | — | `\script\西北北区\龙门镇\npc\路人_孙小小.lua` |
| 29 | `Tôn gia TẩuTử` | `Region_S` | `207` | `(2023,4548)` | `40` | Task=[41] | `\script\西北北区\龙门镇\npc\路人_孙家嫂子.lua` |
| 30 | `Mai Hương` | `Region_S` | `355` | `(1927,4582)` | `—` | — | `\script\西北北区\龙门镇\npc\路人_梅香.lua` |
| 31 | `Xa phu` | `Region_S` | `393` | `(1980,4593)` | `5` | — | `\script\西北北区\龙门镇\npc\职能_车夫.lua` |
| 32 | `Thuyền Phu` | `Region_S` | `240` | `(1837,4633)` | `5` | — | `\script\西北北区\龙门镇\npc\职能_船夫.lua` |
| 33 | `Võ Sư` | `Region_S` | `311` | `(1904,4633)` | `—` | — | `\script\西北北区\龙门镇\npc\职能_武师.lua` |
| 34 | `Người hướng dẫn bang hội` | `autoexec` | `1783` | `(2036,4507)` | `—` | — | `\script\global\帮会接引人.lua` |
| 35 | `Long Ngũ` | `autoexec` | `663` | `(2010,4484)` | `—` | — | `\script\task\newtask\education\路人_龙五.lua` |
| 36 | `Đoan Mộc Thanh` | `autoexec` | `718` | `(2016,4490)` | `—` | Task=[1003] | `\script\task\newtask\master\xiepai\路人_端木清.lua` |

---

## 2. Chuẩn bị Nhân vật & Lộ trình Mốc Cấp độ / Trạng thái

1. **Mốc `Lv 1` (`T=0` tạo nhân vật tại `nativePlaceId = 121`, `FightMode = 0`)**:
   - Kiểm chứng toàn bộ vật thể tĩnh phi nhiệm vụ trên bản đồ (`OpenBox + SetRevPos`, `ShowLadder`, bảng cáo thị / cột lộ tiêu).
   - Kiểm chứng nhánh chặn cấp thấp tại tất cả portal có điều kiện cấp độ (`1870138638` (`Lv < 5`), `3127090509` (`Lv < 5`), `3551699588` (`Lv < 5`), `3685519343` (`Lv < 5`), `3688459730` (`Lv < 5`)).
   - Kiểm chứng nhánh chặn/thoại fallback ở `Lv 1` trên toàn bộ **8 thực thể NPC có `GetLevel()`** (`Xa phu` `(1924,4426)` (`Lv < 5`), `Ăn mày` `(1872,4495)` (`Lv < 2`), `Dã Tẩu` `(1959,4500)` (`Lv < 10`), `Bà chủ` `(1975,4537)` (`Lv < 5`), `Tôn Nhị Lang` `(2011,4551)` (`Lv < 40`), `Tôn gia TẩuTử` `(2023,4548)` (`Lv < 40`), `Xa phu` `(1980,4593)` (`Lv < 5`), `Thuyền Phu` `(1837,4633)` (`Lv < 5`)) cộng `Võ Sư` (`Lv < 2`).
   - Kiểm chứng đủ **2 chiều** (`0 -> 1` xuất thôn và `1 -> 0` nhập thôn) trên toàn bộ **4 cổng thôn**.
2. **Mốc `Lv 40` + `50.000 lượng` + `Hàng Long Bất Vũ (ID 14, Lv 5)`**:
   - Quét phủ 100% `36/36` Dialog NPCs có script qua `11` tâm phủ Set-Cover (`R=22` tiles, nằm trọn trong biên `3x3 Region AOI`), cộng tương tác mở khóa tường minh với từng `Xa phu` / `Thuyền Phu`.
   - Giao dịch thật trên Shop Tạp hóa (`Sale(26)` + `buy_shop_item` `0x5D`), mở Dược điếm (`Sale(99, 1)`), Thợ rèn (`Sale(25)`), và gửi/rút thật `500 lượng` (`store_money` `0x73`) tại Bảo rương.
   - Thực thi đầu-cuối toàn bộ chuỗi nhiệm vụ bản địa, nhiệm vụ môn phái/chính tuyến liên đới, chuỗi `Task 32` Khinh Công trên đủ 3 nhóm `Cọc gỗ / Bao cát / Mộc nhân`, quái nhiệm vụ có script (`OnDeath`) và quái dã ngoại.
   - Bước thật qua toàn bộ `6` portal liên bản đồ ở trạng thái mở khóa và hồi quy về tâm thôn.

---

## 3. Lộ trình Khảo sát Từng Phân khu & Tâm phủ Set-Cover

| Phân khu | Tâm `gm_teleport` sạch | Phạm vi & Thực thể kiểm chứng |
| :--- | :--- | :--- |
| **Stage 1 — Vật Thể, Portal & NPC Chặn Cấp `Lv 1`** | Các ô sạch cạnh `7` Objects, `5` Portals có `minLv` & `9` NPC chặn cấp | Kiểm chứng `OpenBox/SetRevPos`, `ShowLadder`, `Say` biển báo, chặn portal `Lv 1` và thoại chặn `Lv 1` |
| **Stage 2 — 4 Cổng Thôn Song Hướng (`0 <-> 1`)** | `2195735038` `sample=(1980, 4621)`, `2248335958` `sample=(1892, 4563)`, `2553972814` `sample=(1905, 4418)`, `2672110758` `sample=(2037, 4484)` | Đổi `FightMode` đủ 2 chiều xuất thôn (`0 -> 1`) và nhập thôn (`1 -> 0`) trên toàn bộ `4` cổng |
| **Stage 3 — Set-Cover 36 NPC & Thương Mại (`Lv 40`)** | Hub #01 `(1948,4507)`, Hub #02 `(1985,4520)`, Hub #03 `(2020,4551)`, Hub #04 `(2020,4494)`, Hub #05 `(1992,4480)`, Hub #06 `(1941,4458)`, Hub #07 `(1881,4493)`, Hub #08 `(1915,4525)`, Hub #09 `(1937,4555)`, Hub #10 `(1928,4583)`, Hub #11 `(1903,4633)` | Phủ `36/36` Dialog NPCs (`3x3 Region AOI`), mở 3 Shop (`Sale 26 / 99 / 25`), `buy_shop_item` & `store_money` `500 lượng` |
| **Stage 4 — Nhiệm Vụ Thôn, Liên Phái & Luyện Khinh Công** | Các ô sạch cạnh NPC nhiệm vụ, bãi cọc luyện võ & bãi quái ngoài thôn | Thực thi 100% nhiệm vụ thôn, nhánh liên phái/chính tuyến, đánh hạ thật 100% nhóm Scripted Monsters + quái dã ngoại |
| **Stage 5 — 6 Portal Liên Bản Đồ Mở Khóa** | `341062816 -> Map 200`, `1870138638 -> Map 336`, `3127090509 -> Map 140`, `3551699588 -> Map 340`, `3685519343 -> Map 122`, `3688459730 -> Map 131` | Chuyển bản đồ thật sang toàn bộ `6` bản đồ kết nối rồi quay về tâm thôn |

---

## 4. Ma trận Nhiệm vụ, Học Kỹ năng & Chức năng Liên phái

| Mã / Tên cơ chế | NPC / Vật thể tham gia | Biến `Task` & Vật phẩm Ground Truth | Chuỗi chuyển trạng thái kiểm chứng |
| :--- | :--- | :--- | :--- |
| **Đưa Tin Triệu Mi Nhi (`Task 26`)** | Ăn mày `(1872,4495)`, Triệu Mi Nhi `(1993,4477)` | `Task 26`: `0 -> 3 -> 6 -> 10` (`Lv >= 2`); Hộ uyển `(0,8,0\|1,1)` + `Repute +5` | Nhận lời nhắn tại Ăn mày (`0 -> 3`) -> báo tin cho Triệu Mi Nhi (`3 -> 6`) -> quay lại hỏi Ăn mày nhận hộ uyển (`10`) |
| **Giới Thiệu Việc Làm (`Task 25`)** | Thượng Quan Thu `(1925,4516)`, Bà chủ `(1975,4537)` | `Task 25`: `0 -> 3 -> 6 -> 10`; vũ khí cấp 1 `(0,0\|1,i,1)` + `Repute +3` | Nhận tại Thượng Quan Thu (`0 -> 3`) -> xin việc tại Bà chủ lữ quán (`3 -> 6`) -> báo tin vui cho Thượng Quan Thu nhận binh khí (`10`) |
| **Săn Thịt Heo Rừng (`Task 27`)** | Bà chủ `(1975,4537)`, 4 `Heo rừng` có script `战斗_野猪.lua` | `Task 27`: `0 -> 5 -> 10` (`Lv >= 5`); `3x Thịt heo rừng` `(4,228)` rớt `80%` khi hạ `Heo rừng`; mũ cấp 2 `(0,7,6\|10,2)` + `Repute +9` | Sau `Task 25`, nhận tại Bà chủ (`0 -> 5`) -> ra rừng đánh hạ thật cả 4 `Heo rừng` scripted -> quay về báo Bà chủ (`10`) |
| **Cứu Tiểu Quyên (`Task 41`, `Lv >= 40`) & Khinh Công (`Task 32`)** | Tôn gia TẩuTử `(2023,4548)`, Tôn Nhị Lang `(2011,4551)`, Đoan Mộc Thanh `(2016,4490)`, Võ Sư `(1904,4633)` | `Task 41` (`Lv >= 40`, `ReputeLv >= 5`), `Task 1003` Tà phái cấp 60, `Task 32` Khinh Công | Thăng `Lv 40` mở khóa kiểm chứng cặp NPC Tôn gia + Đoan Mộc Thanh + chuỗi đánh thật 3 nhóm cọc/bao cát/mộc nhân học `Khinh Công (210)` |

---

## 5. Dự báo Sai lệch (`Legacy-vs-Reborn Gap Forecast`)

1. **8 Script NPC trên Map `121` hiện chưa được đăng ký trong `JXReborn.Packs.TieuDaoTu` (`MISSING` -> click no-op nếu chưa port)**:
   - NPC `Công thành quan` (`1982,4497`): `\script\missions\citywar_global\infocenter.lua`
   - NPC `Quan sai` (`1945,4497`): `\script\西北北区\龙门镇\npc\职能_官差.lua`
   - NPC `Quan sai` (`1940,4502`): `\script\西北北区\龙门镇\npc\职能_官差.lua`
   - NPC `Tôn Tiểu Tiểu` (`2019,4551`): `\script\西北北区\龙门镇\npc\路人_孙小小.lua`
   - NPC `Mai Hương` (`1927,4582`): `\script\西北北区\龙门镇\npc\路人_梅香.lua`
   - NPC `Tê Hương` (`1950,4564`): `\script\西北北区\龙门镇\npc\路人_樨香.lua`
   - NPC `Trình Đại Vi` (`1942,4458`): `\script\西北北区\龙门镇\npc\路人_程大为.lua`
   - NPC `Triệu gia Bà Bà` (`1985,4479`): `\script\西北北区\龙门镇\npc\路人_赵家婆婆.lua`
2. **Các điểm nhạy cảm về Menu & Cổng Dịch Chuyển cần lưu ý khi chạy Live Proxy**:
   - `Ông chủ / Chủ tiệm tạp hóa` (`Sale(26)`) sử dụng `store_sel_village_extend()` có chèn dòng `"Mua Kim thạch"` (`Sale(165)`) ở `optionIndex = 0`, nên dòng `"Giao dịch"` nằm ở `optionIndex = 1` (trên Map `174` khi `Task 18 == 0` còn ưu tiên hội thoại khởi phát `Task 18` trước menu tiệm).
   - Bản đồ `121` (`inMinimalWorldset = False`) và các bản đồ đích của portal (`Map 200`, `Map 336`, `Map 140`, `Map 340`, `Map 122`, `Map 131`) cần được nạp trong gói cấu hình thế giới khi chạy thực thi Live E2E `WireParityProxy`.

---

## 6. Bằng chứng Kiểm toán Tĩnh (`audit_map_scenario.py --strict`)

- **Lệnh kiểm toán**: `python workbench/.agents/skills/khao-sat-ban-do/scripts/audit_map_scenario.py audit --scenario workbench/qa/JXReborn.WireParityProxy/Scenarios/Maps/121_long-mon-tran.scenario.json --strict`
- **Kết quả**: **`[PASS]` 100%**
  - `TotalSteps`: **248 bước**
  - `DialogNPCs`: **36/36 (`100%`)**
  - `MapObjects`: **7/7 (`100%`)**
  - `Traps`: **10/10 (`100%`)**
  - `ScriptedMonsterGroups`: **4/4 (`100%`)**
  - `Terrain / Sequence / State & Economy Branch Gaps`: **`0`**
