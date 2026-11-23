---
name: khao-sat-ban-do
description: >-
  Survey, author, and end-to-end verify complete region-by-region map exploration scenarios
  using legacy-server/bin/ as 100% Ground Truth via /legacy-explore, generating synchronized
  Markdown walkthroughs and executable WireParityProxy JSON scenarios verified at T=0 with
  zero step failures for any JX1 map.
---

# Khảo sát bản đồ toàn diện (`legacy-server/bin/` Ground Truth -> E2E)

Workflow xây dựng và kiểm chứng tự động kịch bản khảo sát toàn diện cho bất kỳ bản đồ JX1 nào (tân thủ thôn, thành thị, môn phái, map luyện công, mật thất/phó bản), bảo đảm không bỏ sót NPC, nhiệm vụ, chức năng ẩn, cổng dịch chuyển, vật thể hay bãi quái.

**Hệ sinh thái liên kết**:
- Đọc dữ liệu nhị phân, `.txt`/`.ini` và `.lua` bằng [legacy-explore](../legacy-explore/SKILL.md).
- Tham khảo schema JSON, bảng GM Token `(6, 1, 4258)`, lệnh CLI và xử lý lỗi khi chạy E2E tại [wire-scenario-guide.md](./references/wire-scenario-guide.md).
- Chuyển các sai lệch thu được từ `findings.jsonl` và `captures/*.capture.json` sang [phan-tich-va-sua-loi](../phan-tich-va-sua-loi/SKILL.md) (hoặc trích test bằng [legacy-capture-test](../legacy-capture-test/SKILL.md)).

---

## 1. Bất biến bắt buộc

1. **100% Ground Truth từ `legacy-server/bin/` — CẤM kiểm chứng vòng tròn**:
   - Mọi NPC, `trapId`, tuyến xa phu/bến thuyền, `Sale(shopId, currency)`, điều kiện Task/Level/Item, timer và bãi quái **PHẢI** xuất phát từ `legacy-server/bin/`.
   - **TUYỆT ĐỐI KHÔNG** lấy catalog hoặc script trong `pack-tieudaotu` / `server` làm nguồn chân lý cho kịch bản; chỉ đối chiếu Reborn ở Bước 4 để lập **Gap Forecast**.
2. **Bằng chứng trước, văn bản sau (`No Read, No Claim`)**:
   - Đọc `.lua` bằng [read_jx_file.py](../legacy-explore/scripts/read_jx_file.py) và bảng `.txt`/`.ini` bằng [read_tabfile.py](../legacy-explore/scripts/read_tabfile.py) để giữ đúng song ngữ CP-1258 / CP-936. Không đoán ID, tọa độ hay `DisplayName` từ tên file.
3. **Phân biệt Static Files vs Runtime Active (Zero-Sim Oracle)**:
   - Đối chiếu spawner toàn cục (`autoexec.lua`, `addspreadernpc.lua`, `wlls_autoexec.lua`, `configall.lua`, `cfg_server.lua`) và `NpcTemplate Error` trong log Legacy nếu có. Nếu map mới gọi hàm Sim/Checksum đã bị gỡ trên oracle Zero-Sim, phải bổ sung stub an toàn ở `pack_legacy_profiles.py` và đóng gói lại `jxser-v1.tar.gz`.
4. **Sạch địa hình 2 phía (`Server` + `Client PAK`) & An toàn tiến trình**:
   - Mọi tọa độ `gm_teleport` / `resync_if_drifted` phải thỏa `ServerObstacle == 0 && ClientPakObstacle == 0 && TrapId == 0` (tránh bẫy `cliObs = 4` mái nhà/rào cản trên `Region_C.dat`).
   - Dọn môi trường kiểm chứng chỉ tác động container/volume `legacy-oracle-test` và tiến trình sandbox riêng; tuyệt đối không chạm container khác (`jx-server-*`, `jx-mariadb-*`) hoặc `game.exe` ngoài sandbox.

---

## 2. Quy trình 6 bước triển khai cho mọi bản đồ

### Bước 1: Định danh bản đồ & Đọc cấu hình gốc (`settings/`)
1. Tra cứu tên bản đồ trong `legacy-server/bin/server1/settings/maplist.ini` (xem [directory-map.md](../legacy-explore/references/directory-map.md)) để chốt `MapId`, tên chuẩn và thư mục script.
2. Đọc `revivepos.ini`, `station*`, `wharf*`, và các cờ kích hoạt toàn cục (`autoexec.lua`, `addspreadernpc.lua`, `wlls_autoexec.lua`, `configall.lua`, `cfg_server.lua`).

### Bước 2: Kiểm kê 100% thực thể trên bản đồ
Liệt kê đầy đủ toàn bộ tệp trong thư mục script của bản đồ (`npc/`, `obj/`, `trap/`, script add quái/boss) cộng với mọi NPC/sự kiện được inject từ global/relay/mission scripts.

### Bước 3: Chạy `/legacy-explore` (Tier 2 -> Tier 3) cho từng hạng mục
Áp dụng [extraction-checklists.md](../legacy-explore/references/extraction-checklists.md) để ghi nhận chính xác:
- Loại UI (`Say`/`Talk` `UI=0` hay `Describe`/`CreateTaskSay`/`DailogClass:new` `UI=12`), nguyên văn menu option, và có hay không lời gọi `AskClientForNumber`.
- Chuỗi `GetTask`/`SetTask`, vật phẩm (`HaveItem`/`AddEventItem`/`DelItem`), `SetTimer`, phần thưởng và điều kiện cấp độ.
- Shop `Sale(shopId, currency)`, `ShowLadder(...)`, `SetProtectTime`, `AddSkillState`, `AddStation`/`AddTermini`, và toàn bộ nhánh chặn vs nhánh vượt qua của `trap/*.lua`.
- Đẩy mọi logic phụ thuộc C++ chưa xác minh hết vào mục **Open Gaps**.

### Bước 4: Đối chiếu chéo & Dự báo sai lệch (`Gap Forecast`)
Sau khi hoàn tất Bước 3, mới đối chiếu sang `pack-tieudaotu` và `server` để lập bảng **Legacy-vs-Reborn Gap Forecast** (thiếu NPC/Object, lệch `UI=12` vs `UI=0`, sai `Sale`, thiếu `ShowLadder`, sai ngưỡng gate).

### Bước 5: Đóng gói song song Markdown + `.scenario.json`
1. **Artifact Markdown** (`<ten_map>_survey_scenario.md`) theo 6 phần chuẩn: (1) Thông tin Map & Nguồn Ground Truth, (2) Chuẩn bị nhân vật & Checklist mốc cấp/task, (3) Lộ trình khảo sát từng phân khu, (4) Ma trận nhiệm vụ & chức năng ẩn, (5) Dự báo sai lệch `Gap Forecast`, (6) `Open Gaps`.
2. **File JSON Chạy Tự Động** tại `workbench/qa/JXReborn.WireParityProxy/Scenarios/Maps/<mapId>_<slug>.scenario.json` tuân thủ schema và quy tắc thiết kế trong [wire-scenario-guide.md](./references/wire-scenario-guide.md):
   - Đặt toàn bộ test nhánh chặn cấp thấp (`Lv < N`) **trước** mọi lệnh `gm_set_level`.
   - Sau mọi thoại `Say(..., 0)` không có option, luôn thêm `close_dialog`.
   - Dùng `gm_teleport` ở ô thoáng `srvObs=0 & cliObs=0 & trapId=0`, phối hợp `step_trap`, `crawl_region_npcs`, `gm_set_task`, `gm_grant_item`, `input_number`, `resync_if_drifted`.

### Bước 6: Kiểm chứng Fail-First Unit Test & Chạy Live E2E `T=0`
1. **Khóa kiểm tra tĩnh & địa hình**: Thêm `<mapId>_<slug>.scenario.json` vào test `BuiltInMapSurveyScenarios_...` trong `WireParityScenarioAndPathfinderTests.cs` để xác nhận map active, vùng hợp lệ, mọi `trapId` tồn tại trong `RegionTrapGrid`, và mọi `gm_teleport` có `srvObs == 0 && cliObs == 0 && trapId == 0`. Nếu phát hiện đoạn đường/địa hình đặc thù bị `ProxyMapPathfinder` chặn nhầm, viết test pathfinder Fail-First (`RED -> GREEN`) trước khi sửa.
2. **Chạy Live E2E từ `T=0`**:
   - Xóa container/volume `legacy-oracle-test` + `legacy-oracle-test-v1` (nếu còn), đảm bảo không còn process sandbox dư.
   - Chạy `JXReborn.WireParityProxy --standalone --headless --no-browser --report-dir workbench/artifacts/scenario-runs/<mapId> --scenario ...`.
3. **Tiêu chí hoàn tất & Chuyển giao**:
   - `scenario-report.json` đạt **`StepFailures == 0`** (`100%` bước `OK`).
   - `logs/legacy-server.log` không chứa lỗi `LSIM_Startup` hay `SG_Checksum`.
   - Xuất đủ `captures/*.capture.json`, `findings.jsonl`, `report.md` để bàn giao sang `/phan-tich-va-sua-loi` (nếu người dùng yêu cầu sửa lỗi parity), và xác nhận không còn container/tiến trình chạy ngầm.

---

## 3. Checklist Tự kiểm (Self-Audit)

- [ ] **Phủ 100% thực thể map**: Khớp đầy đủ file trong `npc/`, `obj/`, `trap/`, spawn quái và spawner toàn cục.
- [ ] **Không kiểm chứng vòng tròn**: Toàn bộ kỳ vọng Legacy trích từ `legacy-server/bin/`, Reborn chỉ nằm ở `Gap Forecast`.
- [ ] **Đúng thứ tự cấp độ & đóng thoại**: Test `< Lv N` nằm trước `gm_set_level`; mọi `Say(..., 0)` đều có `close_dialog` theo sau.
- [ ] **Tọa độ & tên NPC Ground Truth**: Mọi `gm_teleport` đáp trên ô `srvObs == 0 && cliObs == 0 && trapId == 0`; `npcName` trùng `DisplayName` thực tế và `trapId` trùng `RegionTrapGrid`.
- [ ] **Unit Test + Live E2E `StepFailures == 0`**: Đã đăng ký vào `WireParityScenarioAndPathfinderTests.cs`, chạy qua cả unit test lẫn `--standalone --headless` với `0` bước fail và `0` lỗi `LSIM`/`Checksum`.
