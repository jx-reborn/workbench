---
name: tao-kich-ban
description: Build complete, region-by-region map exploration and QA verification scenarios using legacy-server/bin/ as 100% Ground Truth via /legacy-explore. Use when asked to create a survey or exploration script covering all NPCs, quests, special features, town gates, cross-map portals, objects, and monster zones for any JX1 map.
---

# Tạo kịch bản khảo sát bản đồ (`legacy-server/bin/` Ground Truth)

Workflow xây dựng kịch bản khảo sát toàn diện cho một bản đồ JX1 (thành thị, tân thủ thôn, map luyện công, khu vực đặc biệt) để người dùng hoặc QA chạy kiểm tra thực tế (client / `WireParityProxy`), đảm bảo không bỏ sót NPC, nhiệm vụ, chức năng ẩn, cổng dịch chuyển hay bãi quái.

**Yêu cầu cốt lõi**: Sử dụng [legacy-explore](../legacy-explore/SKILL.md) cho mọi bước đọc dữ liệu nhị phân, `.txt`/`.ini` và `.lua` từ `legacy-server/bin/`. Khi phát hiện sai lệch giữa kịch bản Ground Truth và `pack-tieudaotu` / `server`, chuyển sang [phan-tich-va-sua-loi](../phan-tich-va-sua-loi/SKILL.md) để chẩn đoán và sửa theo TDD, hoặc [legacy-capture-test](../legacy-capture-test/SKILL.md) nếu cần ghi capture thành test tự động.

---

## 1. Bất biến bắt buộc

1. **100% Ground Truth từ `legacy-server/bin/` — CẤM kiểm chứng vòng tròn**:
   - Mọi danh sách NPC, tọa độ cổng/trap, xe phu/bến thuyền, shop ID, điều kiện nhiệm vụ, timer và bãi quái **PHẢI** xuất phát từ `legacy-server/bin/`.
   - **TUYỆT ĐỐI KHÔNG** dùng catalog/script trong `pack-tieudaotu` hay `server` để suy ra "map này có những gì" rồi viết thành checklist khảo sát. Reborn chỉ được đọc ở Bước 4 để lập mục **Cảnh báo sai lệch (Gap Forecast)**.
2. **Bằng chứng trước, văn bản sau (`No Read, No Claim`)**:
   - Phải tuân thủ quy tắc của [legacy-explore](../legacy-explore/SKILL.md): đọc script bằng [read_jx_file.py](../legacy-explore/scripts/read_jx_file.py) và bảng `.txt`/`.ini` bằng [read_tabfile.py](../legacy-explore/scripts/read_tabfile.py) để xử lý đúng song ngữ CP-1258 / CP-936.
   - Tuyệt đối không ghi vào kịch bản bất kỳ ID, cấp độ, tọa độ, phần thưởng hay nhánh hội thoại nào chỉ đoán từ tên file hoặc trí nhớ.
3. **Phân biệt Tồn tại tĩnh (Static Files) vs Tồn tại thực tế (Runtime Active)**:
   - Một NPC/event có file `.lua` trong thư mục map vẫn có thể bị tắt hoặc chỉ spawn có điều kiện bởi `autoexec.lua`, `addspreadernpc.lua`, `configall.lua`, `cfg_server.lua`, hoặc fail template khi nạp map (xem log `gameserver.log` nếu có oracle đang chạy). Kịch bản phải gắn nhãn rõ nguồn spawn/điều kiện kích hoạt.
4. **Minh bạch khoảng trống (`Open Gaps`)**:
   - Bất kỳ cơ chế nào phụ thuộc engine C++ chưa decompile hết hoặc script gọi sâu chưa truy được, phải ghi rõ vào mục **Open Gaps** thay vì điền nội dung hợp lý hóa.

---

## 2. Quy trình 5 bước dựng kịch bản khảo sát

### Bước 1: Định danh bản đồ & Đọc bảng cấu hình gốc (`settings/`)

1. Đối chiếu tên map người dùng yêu cầu với `legacy-server/bin/server1/settings/maplist.ini` (tham khảo [directory-map.md](../legacy-explore/references/directory-map.md)) để chốt:
   - `MapId`, tên chuẩn (tiếng Việt / Hán tự / đường dẫn thư mục script tương ứng).
   - Loại map (Tân thủ thôn, Thành thị, Môn phái, Luyện công, Động/Phó bản).
2. Dùng `read_tabfile.py` / `read_jx_file.py` đọc các file cấu hình liên quan tới map mục tiêu:
   - Điểm hồi sinh / thành thị: `settings/revivepos.ini`.
   - Mạng lưới Xa phu & Bến thuyền: `settings/station*`, `settings/wharf*`.
   - Cấu hình global bật/tắt tính năng map: `autoexec.lua`, `addspreadernpc.lua`, `configall.lua`, `cfg_server.lua` (ví dụ cờ level tối thiểu, sự kiện theo giờ/ngày).
   - Nếu đang có `legacy-oracle-test` (hoặc server Legacy log), kiểm tra log nạp map xem có lỗi `NpcTemplate Error` hoặc script tắt NPC (như `remove_sims.py`) hay không để tránh đưa NPC chết vào luồng test chính mà không cảnh báo.

### Bước 2: Kiểm kê 100% thực thể trên bản đồ

Liệt kê toàn bộ file thuộc thư mục bản đồ dưới `legacy-server/bin/server1/script/` (thường chia thành các nhóm `{npc, obj, trap}` và các file add NPC/quái):

1. **NPC đối thoại & dịch vụ (`npc/`)**: Tiệm thuốc, Tiệm tạp hóa, Thợ rèn, Thủ khố, Xa phu, Thuyền phu, Nha môn, Võ sư, Môn phái, Dã Tẩu, Lễ Quan, Tín Sứ, NPC nhiệm vụ/hỗ trợ tân thủ.
2. **Vật thể tương tác (`obj/`)**: Rương chứa đồ, bảng cáo thị (`告示牌`), bia đá, vật phẩm nhiệm vụ nhặt trên map.
3. **Cổng làng, Cổng chuyển map & Vùng dịch chuyển (`trap/`)**: Đọc 100% file trong `trap/` để lấy map đích, tọa độ đến, điều kiện cấp độ (`GetLevel()`), buff bảo vệ (`SetProtectTime`, `AddSkillState`), và trạng thái mở trạm (`AddStation`, `AddTermini`).
4. **Bãi quái & Boss dã ngoại**: Đọc script spawn quái / file `.txt` / `.lua` add fight NPC của map đó để xác định từng bãi theo hướng (Đông/Tây/Nam/Bắc), tên quái, level quái, quái tinh anh/thủ lĩnh và vật phẩm rơi nhiệm vụ (nếu có).
5. **Global/Relay Spawner**: Kiểm tra các file global (`autoexec.lua`, `addspreadernpc.lua`, mission/event scripts) xem có NPC hoặc sự kiện nào inject thêm vào `MapId` này ngoài thư mục map hay không.

### Bước 3: Chạy `/legacy-explore` (Tier 2 → Tier 3) cho từng hạng mục

Áp dụng quy trình của [legacy-explore](../legacy-explore/SKILL.md) và checklist theo loại thực thể trong [extraction-checklists.md](../legacy-explore/references/extraction-checklists.md) trên toàn bộ danh sách ở Bước 2:

- **Hội thoại & UI Packet**: Phân biệt rõ NPC dùng `Say` (`UI=0`), `Talk` (`UI=0`), hay Rich `Describe` / `CreateTaskSay` / `DailogClass:new` (`<dec>`, `UI=12`). Ghi nguyên văn các lựa chọn menu chính xác.
- **Nhiệm vụ & State (`Task` ID)**: Truy đủ chuỗi điều kiện `GetTask(...)` / `SetTask(...)`, vật phẩm yêu cầu (`HaveItem`, `DelItem`), timer (`SetTimer`, file `*_ontime.lua`), và chuỗi phần thưởng (EXP, tiền, vật phẩm, danh vọng). Không bỏ qua NPC trông giống dân làng thường nhưng ẩn nhiệm vụ chuỗi.
- **Chức năng đặc biệt & Kinh tế**: Shop ID trong `Sale(shopId, currencyType)`, chế đồ/khảm nạm, rửa PK, học khinh công, đổi điểm, bảng xếp hạng `ShowLadder(...)`.
- **Cổng & Di chuyển**: Từng tuyến đi/về của Xa phu, Bến thuyền, và toàn bộ `trap/*.lua` (kèm thông báo chặn khi thiếu cấp hoặc chưa đủ điều kiện).
- **Nếu Lua gọi C++ / Engine chưa rõ**: Ghi chú đúng cấp độ cần kiểm chứng tiếp ở Tier 3 hoặc đẩy vào **Open Gaps**.

### Bước 4: Đối chiếu chéo & Dự báo sai lệch (`Gap Forecast`) — Chỉ làm SAU KHI xong Bước 3

Sau khi đã có bức tranh 100% Ground Truth từ Legacy, mới đối chiếu sang `pack-tieudaotu` và `server` để bổ sung cột **Legacy-vs-Reborn Gap Forecast** cho người đi khảo sát:

- NPC/Object nào Legacy có mà Reborn thiếu (hoặc ngược lại: có ở Reborn nhưng Legacy lỗi template / bị script tắt).
- NPC nào khác loại UI (`Describe` `UI=12` vs `Say` `UI=0`), khác `Sale(id, type)`, thiếu `ShowLadder`, thiếu `SetTimer`, hoặc khác ngưỡng level gate ở cổng/dịch vụ.
- Mục đích: giúp người test biết trước những điểm **nghi ngờ lệch** để chú ý quan sát trên `WireParityProxy` / client thực tế, hoàn toàn không sửa ngược lại Ground Truth của Legacy.

### Bước 5: Đóng gói Kịch bản Khảo sát Chuẩn hóa (Markdown + JSON Chạy Tự Động)

Xuất **đồng thời 2 đầu ra** khớp nhau 100% từ Ground Truth đã duyệt:

1. **Artifact Markdown** (`<ten_map>_survey_scenario.md`, hoặc đường dẫn người dùng chỉ định) theo cấu trúc 6 phần cố định:
   1. **Thông tin Map & Ground Truth Sources**: `MapId`, tên chuẩn, thư mục `script/` đã đọc, danh sách `settings/*.ini/*.txt` và runtime logs đã đối chiếu (ghi rõ tổng số file script đã duyệt).
   2. **Chuẩn bị nhân vật & Checklist trạng thái test**: Các mốc level/task cần chuẩn bị để test được 100% nhánh cổng/nhiệm vụ (ví dụ: char `Lv 1` để test gate chặn, char đạt ngưỡng `Lv >= 2 / 5 / 6 / 10` để qua cổng & mở khóa nhiệm vụ, trạng thái task chưa nhận / đang làm / hoàn thành).
   3. **Lộ trình khảo sát theo từng phân khu (Region-by-Region Walkthrough)**:
      - Chia lộ trình đi bộ hợp lý (Quảng trường trung tâm → Khu dịch vụ & Bang hội → Bến tàu / Ngoại ô → 4 cổng & Trap → Các bãi quái theo hướng).
      - Mỗi điểm dừng ghi thành bảng/thẻ gồm: **Tên & Vị trí**, **File Ground Truth**, **Thao tác test từng nhánh**, **Kết quả kỳ vọng 100% Legacy** (UI type, text/menu chính, Task ID/Timer/Shop/Teleport đích).
   4. **Ma trận Nhiệm vụ, Chức năng ẩn & Điều kiện rẽ nhánh**: Tổng hợp toàn bộ quest line, NPC liên kết đa bước, và các event phụ thuộc giờ/trạng thái.
   5. **Dự báo sai lệch Legacy vs Reborn (Gap Forecast)**: Bảng các sai lệch đã phát hiện sẵn từ Bước 4 để QA xác minh trên thực tế hoặc chuyển sang `/phan-tich-va-sua-loi`.
   6. **Open Gaps**: Mọi nhánh chưa xác minh được đến tận cùng (ví dụ hàm C++ `engine`/`gameserver`, logic phụ thuộc relay `s3relay`).
2. **File Kịch bản Chạy Tự Động** tại `workbench/qa/JXReborn.WireParityProxy/Scenarios/Maps/<mapId>_<slug>.scenario.json` (`WireParityScenario`):
   - Mỗi điểm dừng quan trọng (mốc cấp, nhận/trả nhiệm vụ qua `gm_set_task`/`gm_grant_item`, quét `crawl_region_npcs`, cổng/trap qua `step_trap`, `input_number`, `resync_if_drifted`) ánh xạ thành step chạy trực tiếp qua:
     - CLI: `dotnet run --project workbench/qa/JXReborn.WireParityProxy -- --standalone --headless --scenario <path>`
     - Hoặc MCP `WireParityProxy`: `run_scenario` / `crawl_nearby_npcs`.

---

## 3. Tự kiểm (Self-Audit) trước khi bàn giao kịch bản

- [ ] **Phủ 100% file script của map**: Số lượng NPC / Object / Trap trong kịch bản khớp hoàn toàn với danh sách file đã kiểm kê trong `legacy-server/bin/server1/` (không bỏ sót file vì thấy tên lạ).
- [ ] **Không kiểm chứng vòng tròn**: Không có bất kỳ mục nào trong phần **Kết quả kỳ vọng Legacy** lấy từ code C# của `pack-tieudaotu` hay `server`.
- [ ] **Đủ nhánh chặn & biên điều kiện**: Với mỗi cổng, xa phu, bến thuyền và nhiệm vụ có điều kiện cấp/task, kịch bản đều có cả test case **bị chặn** lẫn **vượt qua** (chú ý đặt toàn bộ test nhánh `< Lv N` trước lệnh `gm_set_level` tăng cấp).
- [ ] **Có dấu vết kiểm chứng**: Mỗi mục đều trích nguồn file `.lua` / `.ini` / `.txt` cụ thể đã đọc trong `legacy-server/bin/` và tách biệt rõ **Open Gaps**.
- [ ] **Tọa độ JSON sạch & không kẹt vật cản**: Mọi bước `gm_teleport` / `walk_to` / `resync_if_drifted` đều đáp trên ô `Obstacle == 0` & `TrapId == 0`, và mọi `npcName` khớp đúng tên thực thể spawn thực tế trong `Region_S.dat` / `autoexec.lua`.
