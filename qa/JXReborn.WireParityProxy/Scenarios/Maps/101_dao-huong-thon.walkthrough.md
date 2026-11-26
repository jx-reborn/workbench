# Khảo Sát Toàn Diện Bản Đồ 101 — Đạo Hương Thôn (`legacy-server/bin/` 100% Ground Truth)

Tài liệu đặc tả khảo sát đầu-cuối đồng bộ 1-1 với kịch bản tự động [101_dao-huong-thon.scenario.json](./101_dao-huong-thon.scenario.json), được kiểm toán định lượng bằng [audit_map_scenario.py](../../../../.agents/skills/khao-sat-ban-do/scripts/audit_map_scenario.py) đạt **100% độ phủ thực thể & nhánh trạng thái**.

---

## 1. Thông tin Bản đồ & Kiểm kê Thực thể Ground Truth

- **Map ID**: `101` (`Đạo Hương thôn` — Tân thủ thôn vùng Trung Nguyên Nam, nối giữa Tương Dương Phủ, Dương Châu Phủ và Dương Trung Động)
- **Đường dẫn nhị phân (`maplist.ini`)**: `中原南区\稻香村` (`Regions: X = 80..120, Y = 84..109`)
- **Điểm hồi sinh (`revivepos.ini`)**:
  - `RevPos = 47` (`SetRevPos(47)` tại Bảo rương 1 quảng trường `(1683, 3133)`, tọa độ hồi sinh `(1689, 3153)`)
  - `RevPos = 48` (`SetRevPos(48)` tại Bảo rương 2 Nam môn `(1588, 3246)`, tọa độ hồi sinh `(1593, 3258)`)
- **Trạm xe (`station.txt`)**:
  - `CurStation = 13` (`Xa phu` `(1623, 3097)` gọi `NewcomerStationCommon("Muốn đi xe không? ")`). Cấp `< 5` chặn bằng `Talk`; cấp `>= 5` mở 4 lựa chọn (`Những nơi đã đi qua`, `Những thành thị đã đi qua`, `Trở lại địa điểm cũ `, `Không cần đâu` -> `Talk("Có tiền thì hãy quay lại nhé! ")`).
- **Tổng kiểm kê thực thể Runtime Active trên Legacy Oracle (`v1`)**:
  - **Dialog NPCs (`Kind == 3`)**: **38/38** (`36` tĩnh trong `Region_S.dat` + `2` từ `autoexec.lua`: `Người hướng dẫn bang hội` `(1694, 3118)` và `Long Ngũ` `(1692, 3140)`).
  - **Map Objects (`OBJ_SECTION`)**: **6/6** (`2` Bảo rương `RevPos 47/48`, `1` Bảng cáo thị `ShowLadder(4, 2, 3, 13, 24)`, `1` Bia thôn `Đạo Hương thôn`, `2` Cột lộ tiêu đi Tương Dương & Dương Châu).
  - **Traps / Portals (`TRAP_SECTION`)**: **12/12** (`9` cổng thôn đổi `FightMode` song hướng `0 <-> 1` kèm `AddStation(13)`: Tây môn + Nam môn + Đông 1..7 môn; `2` portal cấp `>= 5` đi `Map 78` Tương Dương và `Map 80` Dương Châu; `1` portal không chặn cấp đi `Map 205` Dương Trung Động kèm `AddTermini(217)`).
  - **Scripted Monsters (`OnDeath`)**: **3/3** nhóm mộc nhân luyện Khinh Công (`Cọc gỗ` x10, `Bao cát` x10, `Mộc nhân` x10).
  - **Ambient Monsters**: `827` dã thú (`Hươu đốm` x322, `Heo trắng` x319, `Kim Miêu` x186) + `30` cọc/bao cát/mộc nhân.

---

## 2. Chuẩn bị Nhân vật & Lộ trình Mốc Cấp độ / Trạng thái

1. **Mốc `Lv 1` (`T=0` tạo nhân vật tại `nativePlaceId = 101`, `FightMode = 0`)**:
   - Kiểm chứng toàn bộ `6/6` Map Objects: Bảo rương 1 (`RevPos 47`), Bảng cáo thị 2 (`ShowLadder(4, 2, 3, 13, 24)`), Bảo rương 2 (`RevPos 48`), Bia thôn `Đạo Hương thôn`, và 2 Cột lộ tiêu đi Tương Dương / Dương Châu.
   - Kiểm chứng nhánh chặn cấp độ thấp trên cả 5 NPC có điều kiện cấp độ: `Xa phu` (`Lv < 5`), `Tô đại ma` (`Lv < 3`), `Tiểu Lan` (`Lv < 6`), `Dã Tẩu` (`Lv < 10`) và `Võ Sư` (`Lv < 2`).
   - Kiểm chứng nhánh chặn `Lv < 5` đẩy lùi (`SetPos` + `Talk`) tại 2 portal đi Tương Dương (`4061677319`) và Dương Châu (`1860139780`).
   - Kiểm chứng đủ **2 chiều** (`0 -> 1` và `1 -> 0`) trên toàn bộ **9 cổng thôn** (`Tây môn`, `Nam môn`, `Đông 1..7 môn`).
2. **Mốc `Lv 20` + `50.000 lượng` + `Hàng Long Bất Vũ (ID 14, Lv 20)`**:
   - Quét phủ 100% `38/38` Dialog NPCs qua 15 tâm phủ Set-Cover sạch địa hình (nằm gọn trong lân cận AOI `3x3` Region).
   - Giao dịch thật trên Shop Tạp hóa (`Sale(90)` qua lựa chọn `"Giao dịch"` ở vị trí index `1` do `"Mua Kim thạch"` `Sale(165)` chèn đầu), kiểm chứng Dược điếm (`Sale(99, 1)`), Thợ rèn (`Sale(89)`), và gửi/rút thật `500 lượng` (`store_money` `0x73`) tại Bảo rương 1.
   - Thực thi đầu-cuối 3 nhiệm vụ thế giới của Đạo Hương Thôn (`Task 73` Hoa Hoa & Tiểu Long, `Task 71` Tô đại ma & Tô Trung, `Task 66` Tiểu Lan — Chủ dược điếm — Hành Cước thương nhân — Thợ săn), chuỗi Khinh Công `Task 32` đánh hạ thật `Cọc gỗ -> Bao cát -> Mộc nhân` học `Khinh Công (210)`, và hạ gục quái dã ngoại `Hươu đốm`.
   - Kiểm chứng chuyển map thật qua cả 3 portal ngoại cảnh: đi `Map 78` (Tương Dương), đi `Map 80` (Dương Châu) và đi `Map 205` (Dương Trung Động).

---

## 3. Lộ trình Khảo sát Từng Phân khu & Tâm phủ Set-Cover

| Phân khu | Tâm `gm_teleport` sạch | Phạm vi & Thực thể kiểm chứng |
| :--- | :--- | :--- |
| **Stage 1 — Vật Thể, Cổng Chặn Cấp `Lv 1` & Lộ Tiêu** | `(1682,3133)`, `(1627,3206)`, `(1587,3246)`, `(1566,3269)`, `(1623,3099)`, `(1600,3148)`, `(1678,3145)`, `(1654,3305)`, `(1666,2960)`, `(1339,3389)`/`(1331,3387)`, `(1909,3198)`/`(1915,3216)` | 6/6 Map Objects, 5 NPC chặn `Lv 1` (`Xa phu`, `Tô đại ma`, `Dã Tẩu`, `Tiểu Lan`, `Võ Sư`), 2 portal chặn `Lv < 5` (`Map 78`, `Map 80`) |
| **Stage 2 — 9 Cổng Thôn Song Hướng (`0 <-> 1`)** | Tây `(1613,3088)`/`(1611,3082)`, Nam `(1578,3267)`/`(1575,3274)`, Đông 1..7 `(1793,3175)`..`(1790,3215)` | Toàn bộ 9 cổng đổi `FightMode` đủ 2 chiều `0 -> 1` và `1 -> 0` kèm `AddStation(13)` |
| **Stage 3 — Set-Cover 38 NPC & Thương Mại (`Lv 20`)** | **Hub #1..#15**: `(1686,3134)`, `(1654,3156)`, `(1640,3201)`, `(1601,3191)`, `(1598,3148)`, `(1559,3165)`, `(1561,3230)`, `(1617,3279)`, `(1672,3295)`, `(1683,3250)`, `(1691,3175)`, `(1736,3207)`, `(1749,3160)`, `(1721,3135)`, `(1666,2958)` + `Xa phu` `(1623,3099)` | Phủ 38/38 Dialog NPCs; mở 3 Shop (`Sale 90 / 99 / 89`), `buy_shop_item` tại Tạp hóa, `store_money` gửi/rút 500 lượng tại Rương 1, `Xa phu` `StationOnCancel` |
| **Stage 4 — Nhiệm Vụ Thôn, Khinh Công & Dã Ngoại** | `(1707,3249)`/`(1561,3230)`, `(1600,3148)`/`(1721,3135)`, `(1654,3305)`/`(1679,3198)`/`(1640,3201)`/`(1684,3287)`, `(1666,2960)`/`(1644,2967)`/`(1632,2938)`/`(1627,2943)` | `Task 73` (`0 -> 1 -> 10`), `Task 71` (`0 -> 1 -> 2 -> 4`), `Task 66` (`0 -> 1 -> 2 -> 3 -> 5 -> 10`), `Task 32` Khinh Công 3 nhóm cọc/bao cát/mộc nhân + `Hươu đốm` |
| **Stage 5 — 3 Portal Liên Bản Đồ (`Lv 20`)** | `(1331,3387)` -> `Map 78`, `(1915,3216)` -> `Map 80`, `(1769,2800)` -> `Map 205` | Chuyển map thật sang Tương Dương (`Map 78`), Dương Châu (`Map 80`) và Dương Trung Động (`Map 205`) |

---

## 4. Ma trận Nhiệm vụ & Chức năng Đặc thù

| Mã / Tên nhiệm vụ | NPC / Vật thể tham gia | Biến `Task` & Vật phẩm Ground Truth | Chuỗi chuyển trạng thái kiểm chứng |
| :--- | :--- | :--- | :--- |
| **Tìm Tiểu Long (`Task 73`)** | Hoa Hoa `(1707,3249)`, Tiểu Long `(1561,3230)` | `Task 73`: `0 -> 1 -> 10`; trả `10 lượng`, nhận `Ngưu Bì Yêu Đái` `(0,6,0,1)` + `Repute +5` | Nhận tại Hoa Hoa (`0 -> 1`) -> nghe Tiểu Long 4 trang `Talk` -> cho 10 lượng nhận Yêu đái (`10`) -> báo Hoa Hoa |
| **Chí hướng Tô Trung (`Task 71`)** | Tô đại ma `(1600,3146)`, Tô Trung `(1724,3138)` | `Task 71`: `0 -> 1 -> 2/3 -> 4/5` (`Lv >= 3`); nhận `3x Kim Sáng Dược` `(1,0,0,1)` + `Repute +6/+4` | Nhận tại Tô đại ma (`0 -> 1`) -> Khuyên can Tô Trung (`1 -> 2`) -> báo Tô đại ma nhận 3 bình thuốc + 6 danh vọng (`4`) |
| **Thuốc cho Ông Tiểu Lan (`Task 66`)** | Tiểu Lan `(1654,3307)`, Chủ dược điếm `(1679,3196)`, Hành Cước thương nhân `(1640,3201)`, Thợ săn `(1684,3289)` | `Task 66`: `0 -> 1 -> 2 -> 3 -> 4/5 -> 10` (`Lv >= 6`); `Xạ Hương` `(4,142)` `200l`, `Hổ cốt` `(4,185)` `150l`, `Xạ Hương Hổ Cốt Cao` `(4,186)` `750l`, nhẫn `(0,3,0,1\|2)` + `Repute +8\|+9` | Nhận tại Tiểu Lan (`0 -> 1`) -> chọn tìm nguyên liệu (`2`) -> mua Xạ Hương & Hổ cốt -> giao Chủ dược điếm hẹn giờ (`3`, `Timer 6`) -> mua trực tiếp thuốc (`5`) -> giao Tiểu Lan nhận nhẫn (`10`) |
| **Khinh Công Võ Sư (`Task 32`)** | Võ Sư `(1666,2958)`, Cọc gỗ, Bao cát, Mộc nhân | `Task 32`: `0 -> 20 -> 70 -> 80 -> 130 -> 140 -> 190 -> 200`; `AddMagic(210, 1)` | Ghi danh `100 lượng` -> đánh hạ Cọc gỗ (`70`) -> Bao cát (`130`) -> Mộc nhân (`190`) -> lĩnh hội Khinh Công (`200`) |

---

## 5. Dự báo Sai lệch (`Legacy-vs-Reborn Gap Forecast`)

1. **3 NPC nhiệm vụ chưa hạ mã tự động trong `manual-migration-queue.json`**:
   - `Tô Trung` (`稻香村_苏忠对话.lua` — nhánh `Khuyên can` / `Tán đồng` `Task 71`),
   - `Tiểu Long` (`稻香村_小龙对话.lua` — callback `Fpay` trừ `10 lượng` + `RndItemProp(6,10)` cấp `Ngưu Bì Yêu Đái`),
   - `Tiểu Lan` (`稻香村_小兰对话.lua` — chuỗi `Task 66` cấp nhẫn ngẫu nhiên `25%` cấp 2 / `75%` cấp 1 + danh vọng `8/9`).
2. **5 NPC dịch vụ đang dùng chung trong `TieuDaoTuExactScriptPathManifest` lệch thoại/menu riêng của Đạo Hương Thôn**:
   - `Ông chủ tửu điếm` (`稻香村_酒店老板对话.lua`) phát `Say("Mỹ tửu ơi, thế gian có bao loại mỹ tửu?", 0)` thay vì `Talk` hải sản.
   - `Ông chủ tạp hóa` (`稻香村_杂货店老板对话.lua`) gọi `store_sel_village_extend()` phát 5 lựa chọn theo thứ tự: `"Mua Kim thạch"` (`Sale 165`), `"Giao dịch"` (`Sale 90`), `"Hoạt động cửa hàng "` (`Sale 141, 3`), `"Ta đến nhận nhiệm vụ Sơ nhập"`, `"Thôi không cần đâu"`.
   - `Chủ dược điếm Đạo Hương Thôn` (`稻香村_药店老板对话.lua`) có toàn bộ máy trạng thái bào chế thuốc `Task 66` (`1..4`, `Timer 6`) và câu thoại gốc riêng `"Ngươi đã ăn 5 cốc tạp lương..."`.
   - `Thợ rèn` (`稻香村_铁匠对话.lua`) dùng `TIEJIANG_DIALOG = "<dec><npc>Ta đánh rơi ra rất nhiều loại vũ khí vừa ý!"` (`Sale 89`).
   - `Xa phu` (`稻香村_车夫对话.lua`) dùng câu mở đầu `"Muốn đi xe không? "`.

---

## 6. `Open Gaps` & Kết quả Kiểm chứng Live E2E `T=0`

- **Kiểm toán tĩnh (`audit_map_scenario.py audit --strict`)**: **PASS 100%** (`38/38` DialogNPCs, `6/6` MapObjects, `12/12` Traps, `3/3` nhóm bù nhìn Khinh công, `0` lỗi địa hình / chuỗi nhánh).
- **Trạng thái Live E2E `T=0` (`wf_map_convergence.py`, `map_id = 101`)**: **`zero_drift_locked`**
  - **Tổng số bước thực thi**: `238/238` bước `OK` (`StepFailures = 0`, `ProtocolViolations = 0`, `CriticalFindings = 0`, `ActionableClusters = 0`).
  - **Cổng kiểm chứng Ratchet**:
    - `Gate 1 (Static Audit)`: PASS (`sha256 = 176329af9bf247567fddf5da3a21ff706651ad86f3bbe963d8b008d1a4442a2d`)
    - `Gate 2 (Unit & Scenario Audit)`: PASS (`JXReborn.Packs.TieuDaoTu.Tests` `190/190`, `WireParityScenarioAndPathfinderTests` `9/9`, `ProtocolSafetyGateAndWireDifferTests` `12/12`)
    - `Gate 3 (Offline L3A+ WireCaptures)`: PASS (`100%`)
    - `Gate 4 (Live Dual-Run Proxy)`: PASS (`238/238` steps, `report_sha256 = 941a3ffd13f06d1f956045e76862df6456cb3ff76ed162cc8136f97481f32edb`)
- **Open Gaps**: `0` (Toàn bộ 3 nhiệm vụ địa phương `Task 73/71/66`, 5 NPC dịch vụ thôn, `Võ Sư` `Describe` `UiId=12` + `no()`, cơ chế hồi `NormalAdd=1` thể lực khi chạy bộ mỗi `10` frame, và nhận diện gói thời tiết nền `s2c_syncweather` `0x8F` đã hội tụ 100%).
