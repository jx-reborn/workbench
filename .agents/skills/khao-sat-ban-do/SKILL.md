---
name: khao-sat-ban-do
description: >-
  Survey, author, mathematically verify 100% entity/state coverage, and end-to-end execute
  region-by-region map exploration scenarios using legacy-server/bin/ as 100% Ground Truth
  via /legacy-explore and audit_map_scenario.py, generating synchronized Markdown walkthroughs
  and executable WireParityProxy JSON scenarios verified at T=0 with zero step failures.
---

# Khảo sát bản đồ toàn diện (`legacy-server/bin/` Ground Truth -> Deterministic Set-Cover -> E2E)

Workflow xây dựng, kiểm toán toán học độ phủ thực thể/nhánh trạng thái và kiểm chứng tự động E2E `T=0` cho bất kỳ bản đồ JX1 nào (tân thủ thôn, thành thị, môn phái, map luyện công, mật thất/phó bản), khóa cứng việc không bỏ sót NPC, vật thể, cổng dịch chuyển, nhiệm vụ hay hành động giao dịch.

**Hệ sinh thái liên kết**:
- Trích xuất tự động 100% thực thể nhị phân (`Region_S.dat` + `Region_C.dat`), tính **Greedy Disk Set Cover + 2-opt TSP** và kiểm toán tĩnh `.scenario.json` bằng [audit_map_scenario.py](./scripts/audit_map_scenario.py).
- Đọc chi tiết cây thoại `.lua` và bảng `.txt`/`.ini` bằng [legacy-explore](../legacy-explore/SKILL.md).
- Tham khảo schema JSON, 26 loại step, bảng GM Token `(6, 1, 4258)`, lệnh CLI và xử lý lỗi E2E tại [wire-scenario-guide.md](./references/wire-scenario-guide.md).
- Chuyển các sai lệch thu được từ `findings.jsonl` và `captures/*.capture.json` sang [phan-tich-va-sua-loi](../phan-tich-va-sua-loi/SKILL.md) (hoặc trích test bằng [legacy-capture-test](../legacy-capture-test/SKILL.md)).

---

## 1. Bất biến bắt buộc

1. **100% Ground Truth từ `legacy-server/bin/` — CẤM kiểm chứng vòng tròn**:
   - Mọi NPC, `trapId`, vật thể, tuyến xa phu/bến thuyền, `Sale(shopId, currency)`, điều kiện Task/Level/Item, timer và quái gắn script **PHẢI** xuất phát trực tiếp từ `legacy-server/bin/` (`server1/pak/maps.pak`, `settings/`, `lang/vn/replacename_npc.txt`, `script/`).
   - **TUYỆT ĐỐI KHÔNG** lấy catalog hoặc script trong `pack-tieudaotu` / `server` làm nguồn chân lý cho kịch bản; chỉ đối chiếu Reborn ở Bước 4 để lập **Gap Forecast**.
2. **Cấm nhẩm hình học / đếm tay bằng mắt — Bắt buộc qua Verifier định lượng**:
   - Luôn chạy `audit_map_scenario.py inventory` và `plan-cover` trước khi viết kịch bản, và bắt buộc đạt `audit_map_scenario.py audit --strict` (`PASS`, `0` thực thể sót, `0` lỗ hổng nhánh) **trước khi** khởi chạy Live E2E tốn nhiều phút.
3. **5 Luật Phủ Đồ Thị Trạng Thái (State-Transition Coverage Rules)**:
   - **Luật Phủ Tập Thực Thể & Biên AOI 3x3 (`100% Entity Set-Cover + 3x3 Region AOI`)**: Phủ đủ `100%` `DialogNpc` active (`Region_S` + `autoexec.lua` + `addspreadernpc.lua` + `wlls_autoexec.lua`), `100%` `MapObject` có script (rương, cáo thị, bia đá, cột lộ tiêu, mỏ/vật phẩm nhiệm vụ), `100%` `Trap/Portal`, và `100%` nhóm `ScriptedMonster` (`OnDeath` như Cọc gỗ, Bao cát, Mộc nhân, Boss nhiệm vụ). Lưu ý Engine JX1 chỉ đồng bộ NPC trong lưới `3x3 Region` quanh người chơi (`|rx_npc - rx_center| <= 1 && |ry_npc - ry_center| <= 1`), đã được mô hình hóa chính xác trong `audit_map_scenario.py`; đồng thời cảnh giác các NPC trùng `DisplayName` với `excludeNpcNames` (như hàng cá tên `"Ngư dân"` trùng thuyền phu -> phải `interact_npc` tường minh theo tọa độ).
   - **Luật Đối Ngẫu Cấp Độ (`Blocked vs Unlocked`)**: Mọi cổng dịch chuyển, xa phu, thuyền phu, Dã Tẩu, Võ Sư, NPC nhiệm vụ có điều kiện `Lv < K` bắt buộc có **cặp kiểm chứng** ở cả 2 phía: nhánh chặn lúc `Lv 1` (trước `gm_set_level`) **và** nhánh mở khóa khi `Lv >= K` (cấm cho vào `excludeNpcNames` rồi bỏ quên sau khi tăng cấp). Nếu map đích của portal/bến thuyền chưa nằm trong `MINIMAL_TEST_MAPS` của `pack_legacy_profiles.py`, bổ sung map đích vào `MINIMAL_TEST_MAPS` và đóng gói lại `jxser-v1.tar.gz` để test chuyển map thật.
   - **Luật Song Hướng Cổng Thành (`Bidirectional Fight-Toggle`)**: Với các cổng `toggleFightMode` (`SetFightState(1)` <-> `SetFightState(0)`), phải kiểm chứng đủ cả chiều xuất thành (`0 -> 1`) lẫn chiều nhập thành (`1 -> 0`).
   - **Luật Cấm Đi Tắt Nội Bản Đồ (`No Intra-Map GM Shortcut`)**: Cấm dùng `gm_grant_item` / `gm_set_task` để bỏ qua một NPC hoặc vật thể nằm ngay trên chính bản đồ đang khảo sát (ví dụ: phải đến Thương nhân mua khuyên tai thật thay vì cấp thẳng bằng GM). Chỉ dùng GM để mồi tiền điều kiện thuộc bản đồ khác hoặc rút ngắn bộ đếm lặp lớn sau khi giữ lại ít nhất **1 bước thực thi thật trên biên chuyển trạng thái** (ví dụ: đặt `SetTask(32, 69)` rồi dùng `attack_npc` đánh gục thật 1 Cọc gỗ để kích hoạt `69 -> 70`).
   - **Luật Giao Dịch Wire Thực (`Real Economy Wire Action`)**: Nếu bản đồ có Shop (`Sale`) hoặc Rương chứa đồ (`OpenBox`), bắt buộc thực hiện ít nhất 1 bước giao dịch gói tin thật (`buy_shop_item` trên ô trống hành trang và `store_money` / `box_operate`), không dừng ở mức mở khung UI rồi đóng.
4. **Sạch địa hình 2 phía (`Server` + `Client PAK`) & An toàn tiến trình**:
   - Mọi tọa độ `gm_teleport` phải thỏa `ServerObstacle == 0 && ClientPakObstacle == 0 && TrapId == 0` (lấy trực tiếp từ `audit_map_scenario.py plan-cover` / `inventory`).
   - Dọn môi trường kiểm chứng chỉ tác động container/volume `legacy-oracle-test` + `legacy-oracle-test-v1` và tiến trình sandbox riêng; tuyệt đối không chạm container khác (`jx-server-*`, `jx-mariadb-*`) hoặc `game.exe` ngoài sandbox.

---

## 2. Quy trình 6 bước triển khai cho mọi bản đồ

### Bước 1: Kiểm kê tự động 100% Ground Truth & Quy hoạch tâm phủ (`Set-Cover`)
1. Chạy trình kiểm kê nhị phân trực tiếp trên `legacy-server/bin/` + `maps.pak`:
   ```powershell
   python workbench\.agents\skills\khao-sat-ban-do\scripts\audit_map_scenario.py inventory --map <mapId>
   python workbench\.agents\skills\khao-sat-ban-do\scripts\audit_map_scenario.py plan-cover --map <mapId> --radius 26
   ```
2. Kiểm tra cột `inMinWorld` của các portal/tuyến thuyền/xa phu và `skippedSpawners`. Nếu cần mở map lân cận cho portal chuyển map thật hoặc thêm stub `LSIM`/`SG_*` cho Zero-Sim, cập nhật `workbench/qa/scripts/pack_legacy_profiles.py`, thêm test vào `ZeroSimProfilePackagerTests.cs`, và đóng gói lại `workbench/artifacts/packages/jxser-v1.tar.gz`.

### Bước 2: Phân tích chi tiết cây thoại & máy trạng thái (`/legacy-explore` Tier 2)
Đọc từng script `.lua` và bảng `.txt`/`.ini` bằng [read_jx_file.py](../legacy-explore/scripts/read_jx_file.py) và [read_tabfile.py](../legacy-explore/scripts/read_tabfile.py) theo [extraction-checklists.md](../legacy-explore/references/extraction-checklists.md):
- Loại UI (`Say`/`Talk` `UI=0` hay `Describe`/`CreateTaskSay`/`DailogClass:new` `UI=12`), thứ tự `optionIndex` chính xác, và lời gọi `AskClientForNumber`.
- Chuỗi `GetTask`/`SetTask` (kể cả nhiệm vụ thế giới tại chỗ lẫn nhánh nhiệm vụ môn phái/sơ nhập đi ngang qua bản đồ), vật phẩm `(G, D, P)`, `SetTimer`, phần thưởng và điều kiện cấp độ/môn phái.
- Shop `Sale(shopId, currency)`, `OpenBox()` + `SetRevPos(...)`, `ShowLadder(...)`, `AddStation`/`AddTermini`, và toàn bộ nhánh chặn vs nhánh vượt qua của `trap/*.lua`.

### Bước 3: Đối chiếu chéo & Dự báo sai lệch (`Gap Forecast`)
Sau khi chốt xong Ground Truth Legacy ở Bước 1–2, đối chiếu sang `pack-tieudaotu` và `server` để lập bảng **Legacy-vs-Reborn Gap Forecast** (thiếu NPC/Object, lệch `UI=12` vs `UI=0`, sai `Sale`, thiếu `ShowLadder`, sai ngưỡng cấp độ, thiếu script `OnDeath`).

### Bước 4: Đóng gói đồng bộ Walkthrough Markdown + `.scenario.json`
1. **Tài liệu Markdown chuẩn hóa** tại `workbench/qa/JXReborn.WireParityProxy/Scenarios/Maps/<mapId>_<slug>.walkthrough.md` gồm đủ 6 phần:
   - (1) Thông tin Map & Kiểm kê thực thể Ground Truth,
   - (2) Chuẩn bị nhân vật & Lộ trình mốc cấp độ / môn phái / task,
   - (3) Lộ trình khảo sát từng phân khu & Tâm phủ Set-Cover,
   - (4) Ma trận nhiệm vụ, học kỹ năng & chức năng liên phái,
   - (5) Bảng dự báo sai lệch `Gap Forecast`,
   - (6) `Open Gaps` & Kết quả kiểm chứng E2E.
2. **File JSON Chạy Tự Động** tại `workbench/qa/JXReborn.WireParityProxy/Scenarios/Maps/<mapId>_<slug>.scenario.json` tuân thủ [wire-scenario-guide.md](./references/wire-scenario-guide.md):
   - Đặt toàn bộ bước kiểm tra chặn cấp thấp (`Lv 1`) **trước** mọi lệnh `gm_set_level`.
   - Sau mọi thoại `Say(..., 0)` (cáo thị, bia đá, lộ tiêu, NPC thoại 0 option), luôn thêm `close_dialog`.
   - Áp dụng đúng 5 Luật Phủ Đồ Thị Trạng Thái ở Mục 1.

### Bước 5: Chạy Kiểm Toán Độ Phủ Tĩnh (`audit --strict`) & Fail-First Unit Tests
1. Chạy kiểm toán độ phủ `< 1s`:
   ```powershell
   python workbench\.agents\skills\khao-sat-ban-do\scripts\audit_map_scenario.py audit --scenario workbench\qa\JXReborn.WireParityProxy\Scenarios\Maps\<mapId>_<slug>.scenario.json --strict
   ```
   Bắt buộc đạt **`[PASS]`** (`100%` `DialogNPCs`, `100%` `Objects`, `100%` `Traps`, `100%` `ScriptedMonsters`, `0` `Terrain/Sequence/Branch Gaps`).
2. Đăng ký kịch bản vào `WireParityScenarioAndPathfinderTests.cs` (kiểm tra cả địa hình lẫn cổng kiểm toán độ phủ Ground Truth) và chạy `dotnet test` xanh 100%. Nếu phát hiện địa hình đặc thù cần mở rộng `ProxyMapPathfinder` hoặc step runner, viết test Fail-First (`RED -> GREEN`) trước khi sửa code sản xuất.

### Bước 6: Thực thi Live E2E từ `T=0` & Nghiệm thu
1. Dọn sạch container/volume `legacy-oracle-test` + `legacy-oracle-test-v1` (không chạm container khác).
2. Chạy `JXReborn.WireParityProxy --standalone --headless --no-browser --report-dir workbench\artifacts\scenario-runs\<mapId> --scenario ...`.
3. Xác nhận nghiệm thu:
   - `scenario-report.json` đạt **`StepFailures == 0`** (`100%` bước `OK`) và **`ProtocolViolations == 0`**.
   - `logs/legacy-server.log` không chứa lỗi `LSIM_Startup`, `SG_Checksum` hay `ScriptError`.
   - Cập nhật số liệu thực chạy vào `<mapId>_<slug>.walkthrough.md` và xác nhận không còn container/tiến trình sandbox chạy ngầm.

---

## 3. Checklist Tự kiểm (Self-Audit)

- [ ] **`audit_map_scenario.py audit --strict` báo `PASS`**: Đủ 100% `DialogNPCs`, `Objects`, `Traps`, `ScriptedMonsters`, và `0` lỗ hổng nhánh trạng thái / giao dịch.
- [ ] **Không kiểm chứng vòng tròn & Không đi tắt nội bản đồ**: Dữ liệu trích 100% từ `legacy-server/bin/`; mọi NPC/vật phẩm nhiệm vụ thuộc cùng bản đồ đều được tương tác thật.
- [ ] **Đủ cặp đối ngẫu & song hướng**: Mọi cổng/NPC giới hạn cấp đều có cặp `(Blocked @ Lv 1, Unlocked @ Lv >= K)`; cổng thành test đủ cả `0 -> 1` và `1 -> 0`; Shop và Rương có bước `buy_shop_item` / `store_money` thật.
- [ ] **Đồng bộ Markdown + JSON + Unit Test**: Có đủ `<mapId>_<slug>.walkthrough.md` (6 phần) + `<mapId>_<slug>.scenario.json` + khóa kiểm tra tự động trong `WireParityScenarioAndPathfinderTests.cs`.
- [ ] **Live E2E `T=0` sạch tuyệt đối**: `StepFailures == 0`, `ProtocolViolations == 0`, `0` lỗi `LSIM`/`Checksum`, và dọn sạch container/sandbox sau khi chạy xong.
