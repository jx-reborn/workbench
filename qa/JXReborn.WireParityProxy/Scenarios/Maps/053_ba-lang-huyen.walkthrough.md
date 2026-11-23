# Khảo Sát Toàn Diện Bản Đồ 053 — Ba Lăng Huyện (`legacy-server/bin/` 100% Ground Truth)

Tài liệu đặc tả khảo sát đầu-cuối đồng bộ 1-1 với kịch bản tự động [053_ba-lang-huyen.scenario.json](./053_ba-lang-huyen.scenario.json), được kiểm toán định lượng bằng [audit_map_scenario.py](../../../../.agents/skills/khao-sat-ban-do/scripts/audit_map_scenario.py) đạt **100% độ phủ thực thể & nhánh trạng thái**.

---

## 1. Thông tin Bản đồ & Kiểm kê Thực thể Ground Truth

- **Map ID**: `53` (`Ba Lăng huyện` — Tân thủ thôn vùng Lưỡng Hồ)
- **Đường dẫn nhị phân (`maplist.ini`)**: `两湖区\巴陵县` (`Regions: X = 82..114, Y = 89..108`)
- **Điểm hồi sinh (`revivepos.ini`)**: `RevPos = 19` (đặt qua `SetRevPos(19)` tại Bảo rương 1 `(1612, 3167)`)
- **Trạm xe & Bến thuyền (`station.txt`, `wharf.txt`)**:
  - `CurStation = 10` (`Xa phu` `(1578, 3236)`) -> kết nối Tương Dương (`78`), Giang Tân Thôn (`20`), Nam Nhạc Trấn (`54`).
  - `CurWharf = 5` (`Thuyền Phu` bến Trường Giang `(1543, 2923)`) -> kết nối Giang Tân (`20`), Tương Dương (`78`, 100 lượng), Dương Châu (`80`, 400 lượng), Đại Lý (`162`), Lâm An (`176`).
  - Bến Động Đình (`Ngư dân` 1 `(1800, 3154)` & `Ngư dân` 2 `(1778, 3177)`) -> đi Thiên Vương Đảo / Thiên Vương Bang (`Map 59`, `(1425, 3472)`): 100 lượng với khách ngoài, miễn phí với đệ tử `tianwang`.
- **Tổng kiểm kê thực thể Runtime Active trên Legacy Oracle (`v1`)**:
  - **Dialog NPCs (`Kind == 3`)**: **39/39** (`33` tĩnh trong `Region_S.dat` + `4` từ `autoexec.lua` + `2` từ `addspreadernpc.lua`; loại trừ 1 NPC sự kiện Trung Thu `shitouernpc.lua` không kích hoạt).
  - **Map Objects (`OBJ_SECTION`)**: **11/11** (`1` Bảo rương, `2` Bảng cáo thị, `4` Bia đá cổng thành, `3` Cột lộ tiêu ngoại cảnh, `1` Tử Yên Ngọc Bội).
  - **Traps / Portals (`TRAP_SECTION`)**: **8/8** (`4` cổng thành đổi `FightMode` song hướng `0 <-> 1`, `3` portal giới hạn `Lv >= 5` đi Map `54 / 70 / 74`, `1` portal tự do đi Map `199`).
  - **Scripted Monsters (`OnDeath`)**: **3/3 nhóm** (`10` Cọc gỗ, `10` Bao cát, `10` Mộc nhân tại sân tập Bắc cổng thành).
  - **Ambient Monsters**: `782` cá thể (`Heo trắng` x302, `Hươu đốm` x290, `Kim Miêu` x190).

---

## 2. Chuẩn bị Nhân vật & Lộ trình Mốc Cấp độ / Trạng thái

Do `gm_set_level` chỉ tăng cấp một chiều, kịch bản chia tách chặt chẽ theo trình tự đơn điệu cấp độ:

1. **Mốc `Lv 1` (`T=0`, `0 lượng`, `FightMode = 0`)**:
   - Kiểm chứng toàn bộ cổng chặn cấp thấp: `Dã Tẩu` (`Lv < 10`), `A Phương` (`Lv < 2`), `Cổ Lão Thái` (`Lv < 6`), `Xa phu` (`Lv < 5`), 2 `Ngư dân` Động Đình (`Lv < 5`), `Võ Sư` học Khinh công (`Lv < 2`), `Thuyền Phu` Trường Giang (`Lv < 5`), và 3 portal ngoại cảnh (`Map 54 / 70 / 74` chặn `Lv < 5`).
   - Kiểm chứng đủ **2 chiều** (`0 -> 1` và `1 -> 0`) trên cả 4 cổng thành (Nam, Đông, Bắc, Tây) kết hợp mở 4 Bia đá, 2 Bảng cáo thị, 3 Cột lộ tiêu và Bảo rương 1.
2. **Mốc `Lv 20` + `50.000 lượng` + `Hàng Long Bất Vũ (ID 14, Lv 5)`**:
   - Quét phủ 100% NPC nội thị qua 3 tâm Set-Cover + tương tác riêng hàng cá trùng tên `"Ngư dân"`.
   - Giao dịch thật trên Shop (`buy_shop_item` `0x5D`) và Rương chứa đồ (`store_money` gửi/rút `0x73`).
   - Hoàn tất trọn vẹn 3 nhiệm vụ thế giới (`Task 46`, `Task 48` mua khuyên tai thật tại Thẩm Cửu, `Task 47`), chuỗi học Khinh công `Task 32` (hạ gục thật Cọc gỗ, Bao cát, Mộc nhân), các nhánh liên phái Thiên Vương (`Task 38`, `Task 3`), và hạ gục quái ngoại vi (`Hươu đốm`).
   - Mở khóa và chuyển map thật qua `Xa phu`, 2 `Ngư dân` Động Đình (cả trả phí lẫn miễn phí phái Thiên Vương -> `Map 59`), `Thuyền Phu` Trường Giang (-> `Map 78`), và cả 4 portal ngoại cảnh (`Map 54`, `70`, `74`, `199`).

---

## 3. Lộ trình Khảo sát Từng Phân khu & Tâm phủ Set-Cover

| Phân khu | Tâm `gm_teleport` sạch | Phạm vi & Thực thể kiểm chứng |
| :--- | :--- | :--- |
| **Stage 1 — Quảng trường & Ngoại vi `Lv 1`** | Spawn `T=0`, `(1775,3180)`, `(1798,3156)`, `(1668,2982)`, `(1546,2928)`, `(1806,3433)`, `(1350,3301)`, `(1350,3335)` | Bảo rương 1, Cáo thị 1, 3 Cột lộ tiêu (`路标1..3`), chặn `Lv 1` của Dã Tẩu, A Phương, Cổ Lão Thái, Xa phu, 2 Ngư dân Động Đình, Võ Sư, Thuyền Phu Trường Giang, 3 Portal `54/70/74` |
| **Stage 2 — 4 Cổng Thành Song Hướng** | Nam `(1564,3251)`/`(1562,3256)`, Đông `(1650,3236)`/`(1654,3242)`, Bắc `(1624,3102)`/`(1631,3096)`, Tây `(1543,3120)`/`(1539,3112)` | Cáo thị 2 (`ShowLadder`), 4 Bia đá (`石碑1..4`), 4 Trap cổng thành cả chiều ra (`0 -> 1`) lẫn chiều vào (`1 -> 0`) |
| **Stage 3 — Set-Cover Nội Thị & Thương Mại** | **Hub #1** `(1616,3183)` `r=26` (16 NPC), **Hub #2** `(1560,3198)` `r=26` (4 NPC), **Hub #3** `(1585,3133)` `r=26` (7 NPC), Hàng cá `(1628,3213)` | Đủ 10 đệ tử tiếp dẫn môn phái, 6 NPC global (`autoexec`/`spreader`), toàn bộ dân làng, mở 3 Shop (`Sale 99/38/37`), `buy_shop_item` tại Tạp hóa, `store_money` gửi/rút 500 lượng tại Rương 1 |
| **Stage 4 — Nhiệm Vụ, Khinh Công & Ngoại Cảnh** | `(1628,3213)`, `(1508,3174)`, `(1802,3023)`, `(1796,3088)`, `(1668,2982)`, `(1640,2976)`, `(1510,2969)`, `(1603,3122)`, `(1600,3207)` | `Task 46` (Tiểu Ngư), `Task 48` (A Phương <-> Thẩm Cửu), `Task 47` (Cổ Lão Thái <-> Ngọc Bội <-> Du Khách), `Task 32` Khinh công (Cọc gỗ + Bao cát + Mộc nhân), `Task 38` & `Task 3` Thiên Vương, đánh `Hươu đốm` |
| **Stage 5 — Giao Thông & 4 Portal Mở Khóa** | `(1580,3236)`, `(1798,3156)`, `(1775,3180)`, `(1546,2928)`, `(1806,3447)`, `(1340,3298)`, `(1340,3345)`, `(1780,3385)` | Xa phu `Lv 20`, thuyền Động Đình trả phí & miễn phí -> `Map 59`, thuyền Trường Giang -> `Map 78`, 4 Portal -> `Map 54`, `Map 70`, `Map 74`, `Map 199` |

---

## 4. Ma trận Nhiệm vụ, Học Kỹ năng & Chức năng Liên phái

| Mã / Tên cơ chế | NPC / Vật thể tham gia | Biến `Task` & Vật phẩm Ground Truth | Chuỗi chuyển trạng thái kiểm chứng |
| :--- | :--- | :--- | :--- |
| **Cha của Tiểu Ngư** | Tiểu Ngư, Ngô Thần Y, Thiên Vương Tướng Lĩnh | `Task 46`: `0 -> 1 -> 2`; Tỳ Bà Hoàn `(4, 180)` | Nhận nhiệm vụ -> hỏi Ngô Thần Y -> xin thuốc Thiên Vương -> trả Tiểu Ngư nhận giày `(0,5,0/2)` + `5` danh vọng |
| **Hồi môn A Phương** | A Phương (`Lv >= 2`), Thẩm Cửu | `Task 48`: `0 -> 1 -> 3`; Khuyên tai `(4, 181)` giá `200 lượng` | Nhận ở A Phương -> đến Thẩm Cửu chọn nhánh nhiệm vụ mua thật khuyên tai -> trả A Phương nhận khăn/trâm `(0,7,6/8)` + `6` danh vọng |
| **Con trai Cổ Lão Thái** | Cổ Lão Thái (`Lv >= 6`), Tử Yên Ngọc Bội, Du Khách | `Task 47`: `0 -> 1 -> 2 -> 3`; Ngọc bội `(4, 182)`, Thư pháp `(4, 183)` | Nhận ở Cổ Lão Thái -> nhặt Ngọc bội `(1803,3023)` -> đổi Thư pháp (`+4` danh vọng) -> bán cho Du Khách nhận `1000..1500` lượng |
| **Học Khinh Công** | Võ Sư (`Lv >= 2`), Cọc gỗ, Bao cát, Mộc nhân | `Task 32`: `0 -> 20..70 -> 80..130 -> 140..190 -> 200`; `AddMagic(210, 1)` | Đóng 100 lượng (`20`) -> đánh gục thật Cọc gỗ (`69 -> 70`) -> Bao cát (`129 -> 130`) -> Mộc nhân (`189 -> 190`) -> nhận Khinh công (`200`) |
| **Nhập môn Thiên Vương** | Ông chủ tửu điếm `(1605,3120)` | `Task 38` Byte 1: `20 -> 40` (hẹn nấu rượu `tw_ontime.lua` `60 -> 80`, hũ `90`) | Mồi `Task 38 = 20` (từ Map 59) -> hỏi rượu Bồng Lai Xuân -> chuyển trạng thái `40` |
| **Thiên Vương Cấp 40** | Ngô Thần Y `(1598,3209)` | `Task 3`: `40*256+20 (10260) -> 40*256+50 (10290)` | Mồi `Task 3 = 10260` -> thoại 5 trang `L40_tw_talk2` chỉ điểm Phục Lưu Động |

---

## 5. Dự báo Sai lệch (`Legacy-vs-Reborn Gap Forecast`)

1. **Shop Ngô Thần Y (`巴陵县_药店老板对话.lua`)**: Bản custom trong `legacy-server` sửa `Sale(99, 1)` (shop Quân Nhu Tống Kim thu điểm tích lũy), trong khi `pack-tieudaotu` giữ chuẩn `Sale(39)` bằng ngân lượng.
2. **Bảng Cáo Thị 2 (`巴陵县-告示牌2.lua`)**: Legacy gọi `ShowLadder(4, 2, 3, 6, 17)` gửi gói bảng xếp hạng thực, cần đối chiếu đầy đủ payload ladder trên Reborn.
3. **Thẩm Cửu (`巴陵县_路人10商人对话.lua`)**: Legacy dùng StringId `Say(11256, ...)` và `Talk(1, "", 11260)`, trong khi [BaLangVillageQuestDialogScript.cs](../../../../../pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Scripts/Quests/BaLangVillageQuestDialogScript.cs#L314-L381) gửi chuỗi văn bản tường minh.
4. **Hệ thống `DailogClass:new` (`UI=12` vs `UI=0`)**: Chủ tiệm tạp hóa và Võ Sư trên Legacy dùng `DailogClass:new` (`Describe` / `UI=12` kèm portrait spr), dễ phát sinh finding nếu Reborn trả `Say` (`UI=0`).
5. **6 NPC Global Custom (`autoexec.lua` / `addspreadernpc.lua`)**: `Tiêu Dao Tử`, `Thần Tiên Tỷ Tỷ`, `Hỗ Trợ Tân Thủ`, `Thần Tài`, `Long Ngũ`, `Người hướng dẫn bang hội` có cây thoại custom riêng biệt cần đối chiếu chi tiết từ captures xuất ra ở Stage 3.

---

## 6. `Open Gaps` & Kết quả Kiểm chứng Live E2E `T=0`

### Kết quả thực thi Live E2E (`wpp-20261123-104859-3262f3`)
- **Kiểm toán tĩnh (`audit_map_scenario.py audit --strict`)**: `PASS` — `39/39` `DialogNPCs`, `11/11` `MapObjects`, `8/8` `Traps`, `3/3` nhóm `ScriptedMonsters`, `0` lỗ hổng địa hình / biên `3x3 Region AOI` / nhánh trạng thái & giao dịch.
- **Thực thi Live E2E `T=0` ([scenario-report.json](../../../../artifacts/scenario-runs/053/scenario-report.json))**:
  - `TotalSteps`: **216/216 `OK`** (`StepFailures = 0`, `ProtocolViolations = 0`)
  - `LSIM / Checksum Errors`: **0**
  - `Parity Findings`: **2.588** (`2.228` Critical, `360` Warning) cùng **658** gói `captures/*.capture.json` phục vụ phân tích sửa lỗi parity.

### Open Gaps (Ngoài phạm vi chạy E2E thời gian thực)
- **Nhánh chờ Timer 1 giờ của Ông chủ tửu điếm (`Task 38` Byte 1 = `40 -> 60 -> 80` qua `SetTimer` + `tw_ontime.lua`)**: Kịch bản E2E đã kiểm chứng bước chuyển `20 -> 40`; phần nộp 3 dược liệu `(116, 135, 144)` từ Vũ Lăng Sơn và chờ hết bộ đếm thời gian `GetRestSec(1)` phù hợp kiểm chứng ở tầng `ZoneSimulationHarness` (L2) với đồng hồ tua nhanh thay vì chờ 1 giờ thực.
- **Nhánh mở rộng rương phụ tốn 40 Tiền đồng (`(4, 417, 1, 1)`) tại Thẩm Cửu**: Liên quan đến `UseSilver` và ghi log Kim Nguyên Bảo ở C++ engine boundary.
