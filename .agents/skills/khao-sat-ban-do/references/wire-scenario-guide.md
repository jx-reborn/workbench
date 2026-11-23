# Hướng dẫn viết & chạy `WireParityScenario` (`*.scenario.json`)

Tài liệu tham chiếu cho Bước 5 & Bước 6 của skill [khao-sat-ban-do](../SKILL.md), ánh xạ trực tiếp từ `WireParityScenario.cs`, `WireScenarioStep.cs`, `WireScenarioRunner.cs`, `TestGmTokenDriver.cs` và `JXReborn.ClientDriver`.

---

## 1. Cấu trúc gốc `WireParityScenario`

| Trường JSON | Kiểu | Bắt buộc | Mặc định / Ghi chú |
| :--- | :--- | :---: | :--- |
| `name` | `string` | Có | Tên kịch bản không dấu (ví dụ `"TuongDuongFullGroundTruthSurvey"`) |
| `mapId` | `ushort` | Có | Map ID chính của kịch bản (`53`, `78`, ...) |
| `description` | `string?` | Không | Mô tả phạm vi & nguồn Ground Truth |
| `account` | `string` | Không | `"gm001"` (nên đặt riêng theo map như `"gm_td_01"`) |
| `password` | `string` | Không | `"123456"` |
| `roleName` | `string` | Không | Tên nhân vật tạo ở `T=0` (ví dụ `"KhaoSat78"`, <= 16 ký tự ASCII) |
| `nativePlaceId` | `int` | Không | Starter village khi tạo mới (`53` Ba Lăng Huyện mặc định an toàn; dùng `gm_teleport` ở Bước 2 nếu khảo sát thành thị/môn phái) |
| `autoExportFailingCaptures` | `bool` | Không | `true` — tự động xuất `captures/*.capture.json` cho bước có finding |
| `steps` | `WireScenarioStep[]` | Có | Phần tử đầu tiên (`steps[0]`) bắt buộc có `"type": "login"` |

---

## 2. Danh sách 22 loại Step (`WireScenarioStep.type`)

Các trường chung tùy chọn: `region`, `mapId` (mặc định lấy `scenario.mapId`), `notes`, `maxRadiusTiles`, `resetDrift`.

| `type` | Tham số chính | Hành vi runtime & Lưu ý triển khai |
| :--- | :--- | :--- |
| `login` | *(dùng metadata root)* | Khởi chạy 1 `game.exe` headless qua proxy, tự retry tối đa 3 lần nếu Legacy/Shadow đang warm-up ở `T=0`, đợi tới khi `InWorld && SyncReady`. |
| `gm_teleport` | `mapId?`, `tileX`, `tileY`, `resetDrift?` | Nếu cùng map và cách `<= 60` ô sẽ thử đi bộ ngắn; nếu xa hơn hoặc khác map thì ổn định `doing` rồi gọi GM `[5, 8]` nhập `(MapId, TileX, TileY)`. **Ô `(tileX, tileY)` phải thỏa `srvObs == 0 && cliObs == 0 && trapId == 0`**. |
| `resync_if_drifted` | `mapId?`, `tileX`, `tileY`, `resetDrift?` | Tự dịch chuyển đồng bộ lại nếu `Primary`/`Shadow` lệch `MapId`, drift flag bật hoặc `resetDrift == true`. Nên đặt ngay sau `step_trap` đổi map hoặc cổng chặn có đẩy lùi. |
| `gm_set_level` | `level` (hoặc `value`) | Chỉ tăng cấp khi `Player.Level < targetLevel` bằng GM `[0, 0, 7]` (`1..200`). Không hạ được cấp -> luôn đặt test `< Lv N` lên trước bước này. |
| `gm_set_money` | `value` | `<= 0` xóa tiền qua `[2, 5]`; `> 0` gọi `[2, 5]` rồi cộng `value` qua `[2, 4]` + `input_number`. |
| `gm_set_camp` | `optionIndex` / `value` | Chọn phe qua GM `[1, 0, opt]` (`0`=Tân thủ, `1`=Chính, `2`=Tà, `3`=Trung lập, `4`=Tự do). |
| `gm_set_fight_mode` | `value` | `value == 0` là phi chiến đấu (`[4, 1]`), `!= 0` là chiến đấu (`[4, 0]`). |
| `gm_set_task` | `taskId`, `value` | Gọi GM `[7, 4]` nhập lần lượt `taskId` và `value` (`SetTask(taskId, value)`). |
| `gm_grant_item` | `genre`, `detailType?`, `particularType?`, `level?` | Gọi GM `[3, 5]` nhập 4 số `(genre, detailType, particularType, level)`. Hỗ trợ `Genre = 4` (`AddEventItem` cho QuestKey như `(4,66,0)`, `(4,180,0)`...) và `Genre != 4` (`AddItem`, `Level 1..100`). |
| `gm_clear_bag` | — | Dọn túi qua GM `[3, 4, 0]`. |
| `walk_to` | `mapId?`, `tileX`/`tileY` (hoặc `mpsX`/`mpsY`) | Đi bộ tìm đường tránh `Region_S` + `Region_C` tới sát tọa độ mục tiêu (`stopRadiusCells: 1`). |
| `step_trap` | `mapId?`, `trapId` (hoặc `tileX`/`tileY`), `allowedPassThroughTrapId?`, `expectedMapId?`, `expectedFightMode?` | Tính đường đi tránh NPC/trap khác bằng `ProxyMapPathfinder.FindWaypointsToTrap` và bước thẳng vào tâm ô trap (`arriveRadiusMps: 10.0`). Dùng `expectedMapId == currentMap` khi test cổng chặn Lv thấp, hoặc `expectedMapId == destMap` khi chuyển map. |
| `interact_npc` | `npcName`, `tileX?`, `tileY?`, `maxRadiusTiles?` | Tìm NPC khớp `npcName` trong bộ nhớ client (đi bộ tới `(tileX, tileY)` và chờ tối đa 4s để `c2s_requestnpc` `0x49` tải xong tên nếu vừa teleport xa), có fallback bán kính bờ nước `2..4` ô trước khi gọi `0x6E`. |
| `click_object` | `objectName?` / `dataId?`, `tileX?`, `tileY?`, `maxRadiusTiles?` | Tìm vật thể trên đất (rương, cáo thị, mỏ/vật phẩm nhiệm vụ), đi bộ tới gần (chờ sync tối đa 3s) và nhặt/click. |
| `select_dialog` | `optionIndex` | Chọn nhánh `0`-based trên thoại `Say` / `Describe` hiện hành. |
| `confirm_talk` | — | Nhấn xác nhận / qua trang cho thoại `Talk`. |
| `close_dialog` | — | Tự động đóng sạch chuỗi `Talk`, thoại 0 option `Say(..., 0)` hoặc menu có nút đóng an toàn. |
| `input_number` | `value` | Gửi số nguyên qua RPC `input_number` (`c2s_citywarextend` `0x82`, `SubType=3`) khi NPC mở hộp thoại `AskClientForNumber`. |
| `buy_shop_item` | `value` (hoặc `optionIndex`) | Gửi lệnh mua vật phẩm trong shop đang mở theo chỉ số. |
| `repair_item` | `value` (`itemId`) | Gửi lệnh sửa vật phẩm theo server `itemId`. |
| `box_operate` | `optionIndex` / `value` | Thao tác rương đồ (`BoxOperateAsync`). |
| `crawl_region_npcs` | `maxRadiusTiles?`, `maxBranches?`, `excludeNpcNames?` | Tự động đi qua và mở thoại toàn bộ NPC đối thoại (`Kind == 3`) trong bán kính `maxRadiusTiles` (mặc định `22` ô), bỏ qua `excludeNpcNames` và tránh các option dịch chuyển xa. |

---

## 3. Kiểm tra yêu cầu & Lệnh thực thi chuẩn

1. **Điều kiện tiên quyết của Headless Client**:
   - Đảm bảo `workbench/artifacts/headless-client/winmm.dll` là bản build có RPC `input_number`:
     ```powershell
     Select-String -Path workbench\artifacts\headless-client\winmm.dll -Pattern "input_number" -SimpleMatch -Quiet
     ```
2. **Chạy Unit Test khóa Ground-Truth (sau khi đăng ký vào `WireParityScenarioAndPathfinderTests.cs`)**:
   ```powershell
   dotnet test workbench\qa\JXReborn.WireParityProxy.Tests\JXReborn.WireParityProxy.Tests.csproj
   ```
3. **Dọn sạch Oracle `T=0` trước khi chạy Live E2E**:
   - Dùng `& "C:\Program Files\WSL\wslc.exe"` (lệnh xóa là `remove`, không dùng `rm`), chỉ dọn đúng `legacy-oracle-test` và `legacy-oracle-test-v1`:
     ```powershell
     & "C:\Program Files\WSL\wslc.exe" remove -f legacy-oracle-test
     & "C:\Program Files\WSL\wslc.exe" volume remove legacy-oracle-test-v1
     ```
   - Tuyệt đối không tác động container `jx-server-*`, `jx-mariadb-*` hay client ngoài sandbox.
4. **Chạy Kịch bản Live E2E (`--standalone --headless`)**:
   ```powershell
   dotnet run --project workbench\qa\JXReborn.WireParityProxy\JXReborn.WireParityProxy.csproj -- `
     --standalone --headless --no-browser `
     --report-dir workbench\artifacts\scenario-runs\<mapId> `
     --scenario workbench\qa\JXReborn.WireParityProxy\Scenarios\Maps\<mapId>_<slug>.scenario.json
   ```
5. **Xác minh sau khi chạy**:
   - Đọc `workbench\artifacts\scenario-runs\<mapId>\scenario-report.json`: phải đạt `"StepFailures": 0`.
   - Kiểm tra `workbench\artifacts\scenario-runs\<mapId>\logs\legacy-server.log`: `0` dòng khớp `LSIM|Checksum`.
   - Đối chiếu `findings.jsonl`, `report.md` và `captures/*.capture.json`; dọn xong chạy lại `wslc.exe ps -a` và `Get-CimInstance Win32_Process` xác nhận không còn container/process sandbox sót lại.

---

## 4. Xử lý nhanh các lỗi E2E thường gặp

| Triệu chứng khi chạy `--scenario` | Nguyên nhân gốc hay gặp | Cách xử lý chuẩn |
| :--- | :--- | :--- |
| `No walkable path found ...` tại ô đích của `gm_teleport` hoặc `interact_npc` | Đích `gm_teleport` nằm trên ô `cliObs = 4` (mái nhà/rào), hoặc NPC đứng sát mép nước / quầy cần bán kính `r=3..4`, hoặc cầu thang chéo isometric chưa được `uShape 2..5` phủ | Chuyển tọa độ `gm_teleport` sang ô lân cận thỏa `srvObs == 0 && cliObs == 0 && trapId == 0`; với NPC/địa hình mới, viết test `ProxyMapPathfinder` `RED -> GREEN` trước rồi chạy lại. |
| `Unknown RPC method: input_number` | `workbench\artifacts\headless-client\winmm.dll` là bản build cũ | Copy bản mới từ `legacy-client\src\JXReborn.ClientDriver.Native\build\winmm.dll` (hoặc chạy `build-native.ps1`). |
| Nhân vật dừng di chuyển giữa đường sau `ClickNoticeBoard` / NPC `Say(..., 0)` | Thiếu `close_dialog` nên `Dialog.IsOpen` còn mở | Thêm bước `close_dialog` ngay sau các bước tương tác mở `Say(..., 0)`. |
| `NPC '<tên>' not found` ngay sau `gm_teleport` | Tên trong JSON khác `DisplayName` trong `Region_S.dat` / spawner Lua, hoặc NPC không spawn trên Oracle (`NpcTemplate Error` / `ZERO-SIM`) | Đối chiếu `DisplayName` thực tế trong `Region_S.dat`, `autoexec.lua` và `logs/legacy-server.log`; nếu lệch tên thì sửa lại đúng Ground Truth. |
| `LSIM_*` hoặc `SG_Checksum` error trong `legacy-server.log` | Map mới kích hoạt script gọi hàm Sim/Checksum đã tắt trên Zero-Sim profile | Bổ sung stub an toàn vào `workbench\qa\scripts\pack_legacy_profiles.py`, đóng gói lại `jxser-v1.tar.gz` và thêm assert vào `ZeroSimProfilePackagerTests`. |
