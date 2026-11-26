# Khảo Sát Toàn Diện Bản Đồ 054 — Nam Nhạc Trấn (`legacy-server/bin/` 100% Ground Truth)

Tài liệu đặc tả khảo sát đầu-cuối đồng bộ 1-1 với kịch bản tự động [054_nam-nhac-tran.scenario.json](./054_nam-nhac-tran.scenario.json), được kiểm toán định lượng bằng [audit_map_scenario.py](../../../../.agents/skills/khao-sat-ban-do/scripts/audit_map_scenario.py) đạt **100% độ phủ thực thể & nhánh trạng thái**.

---

## 1. Thông tin Bản đồ & Kiểm kê Thực thể Ground Truth

- **Map ID**: `54` (`Nam Nhạc trấn` — Thị trấn dưới chân núi Hoành Sơn, vùng Lưỡng Hồ)
- **Đường dẫn nhị phân (`maplist.ini`)**: `两湖区\南岳镇` (`Regions: X = 88..110, Y = 91..102`)
- **Điểm hồi sinh (`revivepos.ini`)**:
  - `RevPos = 17` (`SetRevPos(17)` tại Bảo rương 1 quảng trường nhỏ `(1663, 3140)`)
  - `RevPos = 18` (`SetRevPos(18)` tại Bảo rương 2 Nam Nhạc Đại Miếu `(1632, 3017)`)
- **Trạm xe (`station.txt`)**:
  - `CurStation = 11` (`Xa phu` `(1591, 3092)`, tọa độ đích `station.txt`: `(1588, 3098)`). Không áp cổng chặn `Lv < 5`; nhánh đóng `OnCancel` phát `Say("Không tiền không thể ngồi xe!", 0)`.
- **Tổng kiểm kê thực thể Runtime Active trên Legacy Oracle (`v1`)**:
  - **Dialog NPCs (`Kind == 3`)**: **20/20** (`19` tĩnh trong `Region_S.dat` + `1` từ `autoexec.lua` dòng 117: `Hỏa Vân Tà Thần` `(1587, 3236)`).
  - **Map Objects (`OBJ_SECTION`)**: **10/10** (`2` Bảo rương, `1` Bảng cáo thị `ShowLadder(2, 4, 15)`, `4` Bia đá, `3` Cột lộ tiêu).
  - **Traps / Portals (`TRAP_SECTION`)**: **5/5** (`3` cổng đổi `FightMode` song hướng `0 <-> 1` kèm `AddStation(11)` + `SetProtectTime(54)`, `1` portal đi `Map 53` Ba Lăng Huyện, `1` portal đi `Map 56` Hoành Sơn Phái kèm `AddTermini(49)`).
  - **Scripted Monsters (`OnDeath`)**: `0` nhóm.
  - **Ambient Monsters**: `146` cá thể (`Nhím` x79, `Heo rừng` x31, `Gấu` x26, `Linh Miêu` x10).

---

## 2. Chuẩn bị Nhân vật & Lộ trình Mốc Cấp độ / Trạng thái

1. **Mốc `Lv 1` (`T=0` tạo nhân vật tại `nativePlaceId = 53` -> `gm_teleport` sang `Map 54` tại `(1665, 3140)`, `FightMode = 0`)**:
   - Kiểm chứng cả 2 Bảo rương (`RevPos 17` & `RevPos 18`), Bảng cáo thị 1 (`ShowLadder(2, 4, 15)`), 4 Bia đá (`石碑1..4`) và 3 Cột lộ tiêu (`路标1..3`).
   - Kiểm chứng đủ **2 chiều** (`0 -> 1` và `1 -> 0`) trên cả 3 cổng trấn: Tây bài phường (`2527825639`), Cầu gỗ phía Nam (`3072005860`) và Rừng cây phía Nam (`3960808154`).
2. **Mốc `Lv 20` + `50.000 lượng` + `Hàng Long Bất Vũ (ID 14, Lv 5)`**:
   - Quét phủ 100% `20/20` NPC qua các tâm Set-Cover sạch địa hình.
   - Giao dịch thật trên Shop Tạp hóa (`Sale(35)` + `buy_shop_item` `0x5D`), kiểm chứng Dược điếm (`Sale(99, 1)`), Thợ rèn (`Sale(34)`), và gửi/rút thật `500 lượng` (`store_money` `0x73`) tại Bảo rương 1.
   - Kiểm chứng đầy đủ 6 cơ chế/nhiệm vụ đặc thù: `Hỏa Vân Tà Thần` nhận Pháp Bảo `(6, 1, 5133)`, chuỗi dịch bệnh Hoành Sơn `Task 1256` (`1 -> 2` qua `TempTask 111 > 3`), nhiệm vụ Danh câu Bôn Hỏa `Task 76` (`10 -> 20 -> 25 -> 30` cấp thư `(4, 393)`), `Thẩm Câu` xem ngựa 80 (`UI=12` `Describe` -> `Talk` 4 trang -> 6 lựa chọn), `Vạn Sự Thông` (`Nothing()`), `Tĩnh Đốc Thiền Sư` kết bái solo (`11375`) và `Xa phu` (`OnCancel`).
   - Kiểm chứng chuyển map thật qua cả 2 portal ngoại cảnh: đi `Map 53` (Ba Lăng Huyện) và đi `Map 56` (Hoành Sơn Phái).

---

## 3. Lộ trình Khảo sát Từng Phân khu & Tâm phủ Set-Cover

| Phân khu | Tâm `gm_teleport` sạch | Phạm vi & Thực thể kiểm chứng |
| :--- | :--- | :--- |
| **Stage 1 — Vật Thể & 3 Cổng Trấn Song Hướng** | `(1665,3140)`, `(1631,3017)`, `(1611,2967)`, `(1486,2990)`, `(1733,3150)`, `(1709,3210)`, `(1696,3047)`, `(1551,3061)`, `(1599,3239)`, Tây `(1573,3090)`/`(1570,3086)`, Cầu Nam `(1526,3122)`/`(1524,3117)`, Rừng Nam `(1570,3240)`/`(1565,3242)` | Bảo rương 1 & 2, Cáo thị 1 (`ShowLadder(2,4,15)`), 3 Cột lộ tiêu, 4 Bia đá, 3 cổng đổi `FightMode` đủ 2 chiều `0 <-> 1` |
| **Stage 2 — Set-Cover 20 NPC & Thương Mại** | **Hub #1** `(1659,3170)`, **Hub #2** `(1606,3198)`, **Hub #3** `(1587,3236)`, **Hub #4** `(1559,3119)`, **Hub #5** `(1603,3114)`, **Hub #6** `(1629,3139)`, **Hub #7** `(1660,3089)`, **Hub #8** `(1689,3054)`, **Hub #9** `(1640,3058)`, **Hub #10** `(1648,3004)`, Xa phu `(1591,3092)` | Phủ 20/20 Dialog NPCs; mở 3 Shop (`Sale 35 / 99 / 34`), `buy_shop_item` tại Tạp hóa, `store_money` gửi/rút 500 lượng tại Rương 1 |
| **Stage 3 — Chuỗi Nhiệm Vụ & NPC Chức Năng** | `(1587,3236)`, `(1660,3089)`, `(1640,3058)`, `(1606,3198)`, `(1689,3054)`, `(1559,3119)`, `(1648,3004)`, `(1591,3092)` | `Hỏa Vân Tà Thần` nhận Pháp Bảo, `Task 1256` (`1 -> 2`), `Task 76` Bành Đại Gia & Bôn Hỏa (`10 -> 20 -> 25 -> 30`), `Thẩm Câu` ngựa 80, `Vạn Sự Thông`, `Tĩnh Đốc Thiền Sư`, `Xa phu` `OnCancel` |
| **Stage 4 — 2 Portal Liên Bản Đồ** | `(1466,2983)` -> `Map 53`, `(1624,2967)` -> `Map 56` | Chuyển map thật sang Ba Lăng Huyện (`Map 53`) và Hoành Sơn Phái (`Map 56`) |

---

## 4. Ma trận Nhiệm vụ & Chức năng Đặc thù

| Mã / Tên cơ chế | NPC / Vật thể tham gia | Biến `Task` & Vật phẩm Ground Truth | Chuỗi chuyển trạng thái kiểm chứng |
| :--- | :--- | :--- | :--- |
| **Dịch bệnh Hoành Sơn** | 10 NPC dân trấn (Điền Thái Bà, Triệu Bách Niên, A Phúc, Mẫn Tứ Thẩm, ...) | `Task 1256`: `1 -> 2`; `TempTask 111`: `+1` mỗi lần hỏi (`> 3` sang bước 2) | Mồi `Task 1256 = 1` -> hỏi liên tiếp 4 lượt (`TempTask 111 = 1..4`) -> nhận chỉ điểm sư thái, chuyển `Task 1256 = 2` |
| **Danh câu Bôn Hỏa** | Bành Đại Gia `(1606,3198)`, Bôn Hỏa `(1605,3199)` | `Task 76`: `10 -> 20 -> 25 -> 30` (thư `(4,393)`); `50` + `(4,375)` -> `60` (`AddMagic 397`) -> `70` | Mồi `Task 76 = 10` -> hỏi Bành Đại Gia -> khám Bôn Hỏa (`20`) -> nhận thư Bành Đại Gia (`30`) |
| **Thẩm Câu bán ngựa 80** | Thẩm Câu `(1689,3054)` | `Task 97`, `TempTask 17` (`1500` vạn / `1000` vạn Bang chủ), `TempTask 49`, ngựa `(0,10,5,1..5)` | Mở `DailogClass` (`UI=12`) -> chọn xem ngựa (`Talk` 4 trang) -> bảng 6 màu ngựa -> thử mua Ô Vân Đạp Tuyết khi thiếu tiền & chọn hủy |
| **Tĩnh Đốc Thiền Sư** | Tĩnh Đốc Thiền Sư `(1649,3002)` | `SwearBrother`, `Pay(3000)`, StringId `11374..11376` | Đi lẻ phát `Talk` `11375`; đội trưởng tổ đội `> 1` phát `11374` -> quyên 3000 lượng kết nghĩa Kim Lan |
| **Vạn Sự Thông** | Vạn Sự Thông `(1559,3119)` | `QueryWiseMan` (`20.000` / `3.000` lượng) | Mở menu 3 dòng -> chọn `"Không hỏi trước "` hoặc thiếu tiền phát `Talk` `"Ngươi chẳng phải đang chọc ghẹo ta đấy chứ?"` |
| **Hỏa Vân Tà Thần** | Hỏa Vân Tà Thần `(1587,3236)` | `npccapcuu.lua`, vật phẩm `(6, 1, 5133)` | Chọn `"Đây là pháp bảo"` -> kiểm tra ô trống -> nhận Pháp Bảo + `Msg2Player` |

---

## 5. Dự báo Sai lệch (`Legacy-vs-Reborn Gap Forecast`)

1. **4 NPC chưa hạ mã tự động trong `manual-migration-queue.json`**:
   - `Tĩnh Đốc Thiền Sư` (`南岳镇_结拜的和尚.lua` — dùng StringId `11374..11376` + `SwearBrother`),
   - `Thẩm Câu` (`路人_沈驹.lua` — dùng `DailogClass:new` `UI=12` + `GetTongFigure`),
   - `Triệu Bách Niên` (`南岳镇_路人6赵百年对话.lua` — dùng `Task 1256` + `GetExtPoint(7)`),
   - `Vạn Sự Thông` (`职能_万事通.lua` — `QueryWiseMan`).
2. **5 NPC dịch vụ bị gộp chung trong `TieuDaoTuExactScriptPathManifest` lệch cây thoại riêng của Nam Nhạc Trấn**:
   - `Ông chủ tửu điếm` (`南岳镇_酒店老板对话.lua`) có nhánh `Task 1256` và câu `Say(..., 0)` riêng về Độc Cô Đại Hiệp.
   - `Chủ tiệm tạp hóa` (`Sale 35`), `Ông chủ dược điếm` (`Sale 99, 1`), `Thợ rèn` (`Sale 34`) tại Nam Nhạc Trấn đều dùng `Say` (`UI=0`, 2 option) không kèm nhánh Nhiệm vụ Sơ nhập.
   - `Xa phu` (`CurStation = 11`) mở thẳng 4 option ở mọi cấp độ và khi chọn `"Không cần đâu"` phát `Say("Không tiền không thể ngồi xe!", 0)`.

---

## 6. `Open Gaps` & Kết quả Kiểm chứng Live E2E `T=0`

- **Kiểm toán tĩnh (`audit_map_scenario.py audit --strict`)**: `PASS` — `20/20` `DialogNPCs` (+ `Tĩnh Nhạc Sư Thái` từ `partner_head.lua`), `10/10` `MapObjects`, `5/5` `Traps`, `0` lỗ hổng địa hình / nhánh trạng thái & giao dịch.
- **Trạng thái Live E2E `T=0` (`wf_map_convergence.py`, `map_id = 54`)**: **`zero_drift_locked`** — `122/122` bước `OK`, `StepFailures = 0`, `ProtocolViolations = 0`, `CriticalFindings = 0`, `ActionableClusters = 0`, L3A+ `WireCaptures` `100%` PASS. Toàn bộ lỗ hổng dự báo ở Mục 5 đã được đóng hoàn toàn bằng TDD.
