# Khảo Sát Toàn Diện Bản Đồ 020 — Giang Tân Thôn (`legacy-server/bin/` 100% Ground Truth)

Tài liệu đặc tả khảo sát đầu-cuối đồng bộ 1-1 với kịch bản tự động [020_giang-tan-thon.scenario.json](./020_giang-tan-thon.scenario.json), được kiểm toán định lượng bằng [audit_map_scenario.py](../../../../.agents/skills/khao-sat-ban-do/scripts/audit_map_scenario.py) đạt **`[PASS]` 100% độ phủ thực thể & nhánh trạng thái**.

---

## 1. Thông tin Bản đồ & Kiểm kê Thực thể Ground Truth

- **Map ID**: `20` (`Giang Tân Thôn` — Tân thủ thôn vùng Tây Nam Bắc, nối giữa Thành Đô Phủ, Phù Dung Động và các bến thuyền Trường Giang)
- **Đường dẫn nhị phân (`maplist.ini`)**: `西南北区\江津村` (`Regions: X = 208..241, Y = 174..203`)
- **Điểm hồi sinh (`revivepos.ini`)**:
  - `RevPos = 10` (`SetRevPos(10)` tại Bảo rương 1 quảng trường nhỏ `(3551, 6216)`, tọa độ hồi sinh `(3546, 6226)`)
  - `RevPos = 11` (`SetRevPos(11)` tại Bảo rương 2 ngã tư `(3456, 6172)`, tọa độ hồi sinh `(3458, 6184)`)
  - `RevPos = 12` (`SetRevPos(12)` tại Bảo rương 3 bến thuyền `(3388, 6272)`, tọa độ hồi sinh `(3377, 6266)`)
- **Trạm xe & Bến thuyền (`station.txt`, `wharf.txt`, `wharfprice.txt`)**:
  - `CurStation = 8` (`Xa phu` `(3458, 6110)` gọi `NewcomerStationCommon("Muốn ngồi xe đi đâu? ")`, tọa độ đến `(3450, 6106)`): chặn `Lv < 5` bằng `Talk`; mở khóa `Lv >= 5` với 4 lựa chọn (`Những nơi đã đi qua`, `Những thành thị đã đi qua`, `Trở lại địa điểm cũ `, `Không cần đâu` -> `Talk("Có tiền thì hãy quay lại nhé! ")`).
  - `CurWharf = 2` (`Thuyền Phu` `(3360, 6282)`, tọa độ đến `(3364, 6277)`): chặn `Lv < 5` bằng `Talk`; mở khóa `Lv >= 5` kết nối 5 bến (`Tương Dương Phủ` `400 lượng`, `Ba Lăng huyện` `400 lượng`, `Dương Châu Phủ` `800 lượng`, `Đại Lý` `100 lượng`, `Lâm An Phủ` `800 lượng`).
- **Tổng kiểm kê thực thể Runtime Active trên Legacy Oracle (`v1`)**:
  - **Dialog NPCs (`Kind == 3`)**: **35/35** (`33` tĩnh trong `Region_S.dat` + `2` từ `autoexec.lua`: `Người hướng dẫn bang hội` `(3534, 6197)` và `Long Ngũ` `(3545, 6223)`; loại trừ `1` NPC sự kiện Trung Thu `shitouernpc.lua` không kích hoạt).
  - **Map Objects (`OBJ_SECTION`)**: **12/12** (`3` Bảo rương `RevPos 10/11/12`, `1` Bảng cáo thị 4 `ShowLadder(6, 2, 3, 7, 9, 18, 20)`, `1` Cáo thị quảng trường nhỏ, `2` Cáo thị trùng tọa độ `(3368, 6266)` tại bến thuyền, `1` Cột lộ tiêu 7 `Đi Thành Đô `, `2` `Chạc cây` `(4, 177)` và `2` `Da trâu` `(4, 178)`).
  - **Traps / Portals (`TRAP_SECTION` — `\script\西南北区\江津村\连接trap\`)**: **8/8** (`6` cổng thôn đổi `FightMode` song hướng `0 <-> 1`: Xuất khẩu 1 & 2 kèm `AddStation(8)`, Xuất khẩu 3..6; `1` portal cấp `>= 5` đi `Map 11` Thành Đô `(3407, 5295)`; `1` portal tự do đi `Map 202` Phù Dung Động `(1786, 2823)` kèm `AddTermini(214)`).
  - **Scripted Monsters (`OnDeath`)**: **3/3 nhóm** mộc nhân luyện Khinh Công (`Mộc nhân` x10, `Cọc gỗ` x10, `Bao cát` x10).
  - **Ambient Monsters**: `606` dã thú (`Heo trắng` x231, `Hươu đốm` x205, `Kim Miêu` x170) + `30` mộc nhân/cọc gỗ/bao cát.

---

## 2. Chuẩn bị Nhân vật & Lộ trình Mốc Cấp độ / Trạng thái

1. **Mốc `Lv 1` (`T=0` tạo nhân vật tại `nativePlaceId = 20`, `FightMode = 0`)**:
   - Kiểm chứng toàn bộ `8` vật thể phi nhiệm vụ: 3 Bảo rương (`RevPos 10/11/12`), Bảng cáo thị 4 (`ShowLadder(6, 2, 3, 7, 9, 18, 20)`), Cáo thị quảng trường nhỏ, 2 Cáo thị bến thuyền và Cột lộ tiêu 7 đi Thành Đô.
   - Kiểm chứng nhánh chặn `Lv < 5` đẩy lùi về `(3402, 6019)` + `Talk` tại portal đi Thành Đô (`2753492350`).
   - Kiểm chứng nhánh chặn cấp độ thấp trên cả **6 NPC có điều kiện cấp độ**: `Xa phu` (`Lv < 5`), `Thuyền Phu` (`Lv < 5`), `Ngô Lão gia` (`Lv < 3`), `Hổ Tử` (`Lv < 5`), `Dã Tẩu` (`Lv < 10`) và `Võ Sư` (`Lv < 2`).
   - Kiểm chứng đủ **2 chiều** (`0 -> 1` và `1 -> 0`) trên toàn bộ **6 cổng thôn** (`江津村出口1..6.lua`).
2. **Mốc `Lv 20` + `50.000 lượng` + `Hàng Long Bất Vũ (ID 14, Lv 5)`**:
   - Quét phủ 100% `35/35` Dialog NPCs qua 12 tâm phủ Set-Cover sạch địa hình nằm trọn trong biên `3x3 Region AOI`, cộng tương tác tường minh với `Xa phu` & `Thuyền Phu` ở trạng thái mở khóa.
   - Giao dịch thật trên Shop Tạp hóa (`Sale(23)` tại `optionIndex = 1` sau `"Mua Kim thạch"` + `buy_shop_item` `0x5D`), kiểm chứng Dược điếm (`Sale(99, 1)`), Thợ rèn (`Sale(22)` `UI=12`), `La Tiên Sinh` (`PlayMusic`), `Nguyệt Hạ lão nhân` (`UI=12` `Qmarry`), và gửi/rút thật `500 lượng` (`store_money` `0x73`) tại Bảo rương 1.
   - Thực thi đầu-cuối 3 nhiệm vụ thôn Giang Tân (`Task 44` Hổ Tử bái sư, `Task 49` mua thật `Xuyên Bối Hoàn` `200 lượng` giao Ngô Lão gia -> Ngô Hồng Mai, `Task 45` nhặt thật cả 4 vật thể `Chạc cây` & `Da trâu` rèn `Giàn ná` cho Hổ Tử), nhánh Đường Môn cấp 50 (`Task 2`: `12840 -> 12860`), chuỗi Khinh Công `Task 32` đánh hạ thật `Cọc gỗ -> Bao cát -> Mộc nhân` học `Khinh Công (210)`, và đánh gục `Hươu đốm` dã ngoại.
   - Kiểm chứng chuyển map thật qua cả 2 portal ngoại cảnh: đi `Map 11` (Thành Đô) và đi `Map 202` (Phù Dung Động).

---

## 3. Lộ trình Khảo sát Từng Phân khu & Tâm phủ Set-Cover

| Phân khu | Tâm `gm_teleport` sạch | Phạm vi & Thực thể kiểm chứng |
| :--- | :--- | :--- |
| **Stage 1 — Vật Thể, Portal & 6 NPC Chặn Cấp `Lv 1`** | `(3551,6217)`, `(3555,6220)`, `(3516,6153)`, `(3456,6173)`, `(3388,6273)`, `(3370,6267)`, `(3395,6029)`, `(3402,6019)`, `(3457,6110)`, `(3360,6281)`, `(3510,6217)`, `(3572,6192)`, `(3538,6230)`, `(3636,6262)` | 8/12 Map Objects phi nhiệm vụ, portal Thành Đô chặn `Lv < 5`, 6 NPC chặn `Lv 1` (`Xa phu`, `Thuyền Phu`, `Ngô Lão gia`, `Hổ Tử`, `Dã Tẩu`, `Võ Sư`) |
| **Stage 2 — 6 Cổng Thôn Song Hướng (`0 <-> 1`)** | Cổng 1 `(3490,6243)`/`(3486,6248)`, Cổng 2 `(3447,6239)`/`(3451,6244)`, Cổng 3 `(3406,6263)`/`(3410,6266)`, Cổng 4 `(3594,6232)`/`(3596,6237)`, Cổng 5 `(3434,6090)`/`(3429,6085)`, Cổng 6 `(3370,6271)`/`(3367,6274)` | Toàn bộ 6 cổng `江津村出口1..6.lua` đổi `FightMode` đủ cả chiều xuất thôn (`0 -> 1`) lẫn nhập thôn (`1 -> 0`) kèm `AddStation(8)` |
| **Stage 3 — Set-Cover 35 NPC & Thương Mại (`Lv 20`)** | **Hub #01..#12**: `(3477,6129)`, `(3510,6105)`, `(3459,6176)`, `(3563,6173)`, `(3426,6203)`, `(3489,6215)`, `(3560,6215)`, `(3572,6193)`, `(3533,6210)`, `(3361,6243)`, `(3539,6232)`, `(3625,6250)` + `Xa phu` `(3457,6110)` + `Thuyền Phu` `(3360,6281)` | Phủ 35/35 Dialog NPCs (`3x3 Region AOI`); mở 3 Shop (`Sale 23 / 99 / 22`), `buy_shop_item` tại Tạp hóa, `store_money` gửi/rút `500 lượng` tại Rương 1, `La Tiên Sinh` `PlayMusic`, `Nguyệt Lão` `Qmarry` |
| **Stage 4 — Nhiệm Vụ Thôn, Đường Môn 50 & Khinh Công** | `(3412,6220)`/`(3636,6262)`, `(3510,6217)`/`(3468,6164)`/`(3482,6150)`, `(3572,6192)`/`(3438,6220)`/`(3511,6098)`/`(3463,6241)`/`(3644,6086)`/`(3727,6346)`, `(3445,6168)`, `(3643,6266)`/`(3642,6252)`/`(3613,6258)`, `(3428,5754)` | `Task 44` (`0 -> 10 -> 20 -> 100`), `Task 49` (`0 -> 1 -> 3 -> 10`), `Task 45` (`0 -> 10 -> 20 -> 100` + nhặt đủ 4/4 `Chạc cây` & `Da trâu`), Đường Môn `Task 2` (`12840 -> 12860`), `Task 32` Khinh Công 3 nhóm cọc/bao cát/mộc nhân + `Hươu đốm` |
| **Stage 5 — 2 Portal Liên Bản Đồ (`Lv 20`)** | `(3403,6014)` -> `Map 11`, `(3703,6226)` -> `Map 202` | Chuyển map thật sang Thành Đô (`Map 11`) và Phù Dung Động (`Map 202`) |

---

## 4. Ma trận Nhiệm vụ, Học Kỹ năng & Chức năng Liên phái

| Mã / Tên cơ chế | NPC / Vật thể tham gia | Biến `Task` & Vật phẩm Ground Truth | Chuỗi chuyển trạng thái kiểm chứng |
| :--- | :--- | :--- | :--- |
| **Hổ Tử Bái Sư (`Task 44`)** | Phụ Thân Hổ Tử `(3413,6220)`, Võ Sư `(3637,6262)` | `Task 44`: `0 -> 10 -> 20 -> 100`; mũ `(0,7,6\|8,1)` + `Repute +5` | Nhận tại Phụ Thân Hổ Tử (`0 -> 10`) -> xin Võ Sư nhận học trò miễn phí (`10 -> 20`) -> báo Phụ Thân Hổ Tử nhận mũ (`100`) |
| **Thuốc Ngô Lão Gia (`Task 49`)** | Ngô Lão gia `(3510,6216)`, Ông chủ dược điếm `(3469,6164)`, Ngô Hồng Mai `(3483,6150)` | `Task 49`: `0 -> 1 -> 3 -> 10` (`Lv >= 3`); `Xuyên Bối Hoàn` `(4,179)` giá `200 lượng`; giày `(0,5,0\|2,1)` + `Repute +6` | Nhận tại Ngô Lão gia (`0 -> 1`) -> mua thật Xuyên Bối Hoàn tại Dược điếm (`200 lượng`) -> giao Ngô Lão gia (`1 -> 3`) -> gặp Ngô Hồng Mai nhận giày (`10`) |
| **Giàn Ná Hổ Tử (`Task 45`)** | Hổ Tử `(3573,6192)`, Thợ rèn `(3439,6220)`, Chạc cây, Da trâu | `Task 45`: `0 -> 10 -> 20 -> 100` (`Lv >= 5`); `Chạc cây` `(4,177)` + `Da trâu` `(4,178)` -> `Giàn ná` `(4,176)`; vũ khí cấp 2 `(0,0,0..5,2)` + `Repute +8` | Nhận tại Hổ Tử (`0 -> 10`) -> hỏi Thợ rèn (`10 -> 20`) -> nhặt thật Chạc cây & Da trâu trên bản đồ -> Thợ rèn chế Giàn ná (`176`) -> trả Hổ Tử nhận binh khí cấp 2 (`100`) |
| **Đường Môn Cấp 50 (`Task 2`)** | Cậu của Đường Nguyên `(3448,6169)`, Đường Nguyên `(3446,6168)` | `Task 2`: `50*256+40 (12840) -> 50*256+60 (12860)` | Mồi `Task 2 = 12840` (từ Map 25 Đường Môn) -> hỏi thăm Cậu -> nghe Đường Nguyên 7 trang `Talk` chỉ điểm Hưởng Thủy Động (`12860`) |
| **Học Khinh Công (`Task 32`)** | Võ Sư `(3637,6262)`, Cọc gỗ, Bao cát, Mộc nhân | `Task 32`: `0 -> 20 -> 70 -> 80 -> 130 -> 140 -> 190 -> 200`; `AddMagic(210, 1)` | Đóng `100 lượng` (`20`) -> đánh gục thật Cọc gỗ (`69 -> 70`) -> Bao cát (`129 -> 130`) -> Mộc nhân (`189 -> 190`) -> lĩnh hội Khinh Công (`200`) |

---

## 5. Dự báo Sai lệch (`Legacy-vs-Reborn Gap Forecast`)

1. **7 NPC chưa đăng ký trong `TieuDaoTuScriptCodeResolver` (`MISSING` -> click no-op)**:
   - `Phụ Thân Hổ Tử` (`西南北区--江津村-路人5虎子爹对话.lua` — khởi phát & trả thưởng `Task 44`),
   - `Võ Sư` (`江津村武师.lua` — chưa đưa vào `TieuDaoTuExactScriptPathManifest` và có nhánh riêng `Task 44 == 10 -> 20` trước khi gọi `learn()`),
   - `Hổ Tử` (`西南北区--江津村-路人3虎子对话.lua` — khởi phát & trả thưởng `Task 45`),
   - `Thiên Thiên` (`西南北区--江津村-路人4天天对话.lua` — thoại phản ứng theo tiến trình ná cao su),
   - `Ngô Hồng Mai` (`西南北区--江津村-路人8吴红梅对话.lua` — trả thưởng giày cuối chuỗi `Task 49`),
   - `La Tiên Sinh` (`说书先生.lua` — phát nhạc `PlayMusic` `\Music\Music901..903.mp3`),
   - `Nguyệt Hạ lão nhân` (`月老.lua` — `DailogClass:new` `UI=12` 6 dòng cầu hôn / ly hôn / đơn phương ly hôn `Task 67, 1779`).
2. **5 NPC dịch vụ đang dùng chung trong `TieuDaoTuExactScriptPathManifest` bị lệch hành vi / menu**:
   - `Thuyền Phu` (`西南北区--江津村-码头船夫对话.lua`, `CurWharf = 2`): `FerryBoatmanDialogScript.cs` hiện lái nhầm sang bến Động Đình Hồ -> Thiên Vương Đảo (`Map 59`) thay vì mở 5 tuyến thuyền Trường Giang từ Giang Tân (`wharfprice.txt` dòng 3).
   - `Thợ rèn` (`西南北区--江津村-铁匠对话.lua`, `Sale(22)`): `TownBlacksmithDialogScript.cs` chưa có máy trạng thái `Task 45 == 10/20` chế tạo `Giàn ná` `(4, 176)` từ `Chạc cây` `(4, 177)` + `Da trâu` `(4, 178)` và chưa khớp `TIEJIANG_DIALOG` riêng của Giang Tân.
   - `Chủ tiệm tạp hóa` (`西南北区--江津村-杂货店老板对话.lua`, `Sale(23)`): `TownGrocerDialogScript.cs` cho `MapId == 20` đang thiếu nút `"Mua Kim thạch"` (`Sale(165)`) ở `optionIndex = 0` từ `store_sel_village_extend()` (khiến `"Giao dịch"` lệch vị trí `0` vs `1`).
   - `Ông chủ dược điếm` (`西南北区--江津村-药店老板对话.lua`, `Sale(99, 1)`): Đã có nhánh bán `Xuyên Bối Hoàn` nhưng thiếu `AddNote("Mua được Xuyên Bối hoàn")` và câu mở đầu gốc `"Ngươi đã ăn 5 cốc tạp lương..."`.
   - `Xa phu` (`西南北区--江津村-驿站车夫对话.lua`, `CurStation = 8`): Câu chào gốc là `"Muốn ngồi xe đi đâu? "`.
3. **Cấu hình Gói Zero-Sim (`MINIMAL_TEST_MAPS`) & Trình kiểm toán Trap**:
   - `Map 20` (Giang Tân Thôn), `Map 11` (Thành Đô) và `Map 202` (Phù Dung Động) hiện chưa nằm trong `MINIMAL_TEST_MAPS` của `pack_legacy_profiles.py` — cần bổ sung và đóng gói lại `jxser-v1.tar.gz` trước khi chạy Live E2E ở Bước 6.
   - Thư mục trap của Giang Tân đặt tên là `\script\西南北区\江津村\连接trap\` (không có dấu `\` ngay trước chữ `trap`), nên `scan_trap_scripts()` trong `audit_map_scenario.py` cần nới điều kiện lọc thư mục từ `"\\trap"` sang `"trap"` để kiểm toán tự động cả `biGate` và `minLv` cho `Map 20`, `Map 1` và `Map 11`.

---

## 6. `Open Gaps` & Trạng thái Kiểm toán Tĩnh

- **Kiểm toán tĩnh (`audit_map_scenario.py audit --strict`)**: **`[PASS]` 100%**
  - `TotalSteps`: **235 bước**
  - `DialogNPCs`: **35/35 (`100%`)**
  - `MapObjects`: **12/12 (`100%`)**
  - `Traps`: **8/8 (`100%`)**
  - `ScriptedMonsterGroups`: **3/3 (`100%`)**
  - `Terrain / Sequence / State & Economy Branch Gaps`: **`0`**
- **Trạng thái Live E2E `T=0`**: Sẵn sàng thực thi ngay trong Đợt 1 sau khi mở `Map 20 / 11 / 202` trong `MINIMAL_TEST_MAPS` và đóng 7 NPC `MISSING` + 5 lệch Shared Manifest ở Mục 5.
