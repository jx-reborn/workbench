# Hướng dẫn viết, kiểm toán độ phủ & chạy `WireParityScenario` (`*.scenario.json`)

Tài liệu tham chiếu cho Bước 1, Bước 4, Bước 5 & Bước 6 của skill [khao-sat-ban-do](../SKILL.md), ánh xạ trực tiếp từ `audit_map_scenario.py`, `WireParityScenario.cs`, `WireScenarioStep.cs`, `WireScenarioRunner.cs`, `TestGmTokenItemScript.cs` và `JXReborn.ClientDriver`.

---

## 1. Cấu trúc gốc `WireParityScenario`

| Trường JSON | Kiểu | Bắt buộc | Mặc định / Ghi chú |
| :--- | :--- | :---: | :--- |
| `name` | `string` | Có | Tên kịch bản không dấu (ví dụ `"BaLangHuyenFullGroundTruthSurvey"`) |
| `mapId` | `ushort` | Có | Map ID chính của kịch bản (`53`, `78`, ...) |
| `description` | `string?` | Không | Mô tả phạm vi & nguồn Ground Truth |
| `account` | `string` | Không | `"gm001"` (nên đặt riêng theo map như `"gm_bl_01"`, `"gm_td_01"`) |
| `password` | `string` | Không | `"123456"` |
| `roleName` | `string` | Không | Tên nhân vật tạo ở `T=0` (ví dụ `"KhaoSat53"`, <= 16 ký tự ASCII) |
| `nativePlaceId` | `int` | Không | Starter village khi tạo mới (`53` Ba Lăng Huyện mặc định an toàn; dùng `gm_teleport` ở Bước 2 nếu khảo sát thành thị/môn phái) |
| `autoExportFailingCaptures` | `bool` | Không | `true` — tự động xuất `captures/*.capture.json` cho bước có finding Critical |
| `steps` | `WireScenarioStep[]` | Có | Phần tử đầu tiên (`steps[0]`) bắt buộc có `"type": "login"` |

---

## 2. Danh sách 26 loại Step (`WireScenarioStep.type`)

Các trường chung tùy chọn: `region`, `mapId` (mặc định lấy `scenario.mapId`), `notes`, `maxRadiusTiles`, `resetDrift`.

| `type` | Tham số chính | Hành vi runtime & Lưu ý triển khai |
| :--- | :--- | :--- |
| `login` | *(dùng metadata root)* | Khởi chạy 1 `game.exe` headless qua proxy, tự retry tối đa 3 lần nếu Legacy/Shadow đang warm-up ở `T=0`, đợi tới khi `InWorld && SyncReady`. |
| `gm_teleport` | `mapId?`, `tileX`, `tileY`, `resetDrift?` | Nếu cùng map và cách `<= 60` ô sẽ thử đi bộ ngắn; nếu xa hơn hoặc khác map thì ổn định `doing` rồi gọi GM `[5, 8]` nhập `(MapId, TileX, TileY)`. **Ô `(tileX, tileY)` bắt buộc thỏa `srvObs == 0 && cliObs == 0 && trapId == 0`**. |
| `resync_if_drifted` | `mapId?`, `tileX`, `tileY`, `resetDrift?` | Tự dịch chuyển đồng bộ lại nếu `Primary`/`Shadow` lệch `MapId`, drift flag bật hoặc `resetDrift == true`. Đặt ngay sau `step_trap` đổi map hoặc cổng chặn có đẩy lùi. |
| `gm_set_level` | `level` (hoặc `value`) | Chỉ tăng cấp khi `Player.Level < targetLevel` bằng GM `[0, 0, 7]` (`1..200`). Không hạ được cấp -> luôn đặt toàn bộ test `< Lv N` lên trước bước này. |
| `gm_set_money` | `value` | `<= 0` xóa tiền qua `[2, 5]`; `> 0` gọi `[2, 5]` rồi cộng `value` qua `[2, 4]` + `input_number`. |
| `gm_set_camp` | `optionIndex` / `value` | Chọn phe qua GM `[1, 0, opt]` (`0`=Tân thủ, `1`=Chính, `2`=Tà, `3`=Trung lập, `4`=Tự do). |
| `gm_learn_skill` | `value` (`skillId`), `level?` | Học kỹ năng qua GM Token: nếu `skillId == 14 && (level ?? 5) == 5` gọi nhanh `[1, 2, 0]` (Hàng Long Bất Vũ cấp 5), ngược lại gọi `[1, 2, 2]` nhập `(skillId, level)` và hồi đầy Sinh/Nội/Thể lực qua `[0, 2, 0]`. |
| `gm_join_faction` | `optionIndex` / `value` | Gia nhập môn phái qua GM `[1, 5, idx]` (`0`=Thiếu Lâm, `1`=Thiên Vương, `2`=Đường Môn, `3`=Ngũ Độc, `4`=Nga My, `5`=Thúy Yên, `6`=Cái Bang, `7`=Thiên Nhẫn, `8`=Võ Đang, `9`=Côn Lôn, `10`=Hoa Sơn, `11`=Xuất sư). |
| `gm_set_fight_mode` | `value` | `value == 0` là phi chiến đấu (`[4, 1]`), `!= 0` là chiến đấu (`[4, 0]`). |
| `gm_set_task` | `taskId`, `value` | Gọi GM `[7, 4]` nhập lần lượt `taskId` và `value` (`SetTask(taskId, value)`). |
| `gm_grant_item` | `genre`, `detailType?`, `particularType?`, `level?` | Gọi GM `[3, 5]` nhập 4 số `(genre, detailType, particularType, level)`. Hỗ trợ `Genre = 4` (`AddEventItem` cho QuestKey như `(4,90,0)`, `(4,116,0)`...) và `Genre != 4` (`AddItem`, `Level 1..100`). |
| `gm_clear_bag` | — | Dọn túi qua GM `[3, 4, 0]` (giữ lại GM Token `(6,1,4258)`). |
| `walk_to` | `mapId?`, `tileX`/`tileY` (hoặc `mpsX`/`mpsY`) | Đi bộ tìm đường tránh `Region_S` + `Region_C` tới sát tọa độ mục tiêu (`stopRadiusCells: 1`). |
| `step_trap` | `mapId?`, `trapId` (hoặc `tileX`/`tileY`), `allowedPassThroughTrapId?`, `expectedMapId?`, `expectedFightMode?` | Tính đường đi tránh NPC/trap khác bằng `ProxyMapPathfinder.FindWaypointsToTrap` và bước thẳng vào tâm ô trap (`arriveRadiusMps: 10.0`). Dùng `expectedMapId == currentMap` khi test cổng chặn Lv thấp, hoặc `expectedMapId == destMap` khi chuyển map. |
| `interact_npc` | `npcName`, `tileX?`, `tileY?`, `maxRadiusTiles?` | Tìm NPC khớp `npcName` trong bộ nhớ client (đi bộ tới `(tileX, tileY)` và chờ `c2s_requestnpc` `0x49` tải xong tên nếu vừa teleport xa), có fallback bán kính bờ nước `2..4` ô trước khi gọi `0x6E`. |
| `attack_npc` | `npcName` (hoặc `templateId`), `tileX?`, `tileY?`, `value?` (`skillId`, mặc định `14` nếu đã học, fallback `53`), `optionIndex?` (`0`=hạ gục hoàn toàn để kích hoạt `OnDeath`, `1`=đánh trúng hạ máu) | Đi bộ tới tầm đánh, tự bật chiến đấu nếu đang ở `FightMode == 0`, và tung kỹ năng vào mục tiêu cho tới khi gục (`OnDeath` chạy xong) hoặc tụt máu. Dùng để kiểm chứng Cọc gỗ / Bao cát / Mộc nhân / Quái & Boss nhiệm vụ. |
| `click_object` | `objectName?` / `dataId?`, `tileX?`, `tileY?`, `maxRadiusTiles?` | Tìm vật thể trên đất (rương, cáo thị, bia đá, lộ tiêu, mỏ/vật phẩm nhiệm vụ), đi bộ tới gần và nhặt/click. |
| `select_dialog` | `optionIndex` | Chọn nhánh `0`-based trên thoại `Say` / `Describe` hiện hành. |
| `confirm_talk` | — | Nhấn xác nhận / qua trang cho thoại `Talk`. |
| `close_dialog` | — | Tự động đóng sạch chuỗi `Talk`, thoại 0 option `Say(..., 0)` hoặc menu có nút đóng an toàn. |
| `input_number` | `value` | Gửi số nguyên qua RPC `input_number` (`c2s_citywarextend` `0x82`, `SubType=3`) khi NPC mở hộp thoại `AskClientForNumber`. |
| `buy_shop_item` | `value` (hoặc `optionIndex`) | Tự động tìm ô trống an toàn trong hành trang (không đè lên GM Token `(6,1,4258)`) và gửi lệnh mua vật phẩm `0x5D` theo `buyIndex`. |
| `repair_item` | `value` (`itemId`) | Gửi lệnh sửa vật phẩm `0x7A` theo server `itemId`. |
| `store_money` | `optionIndex` (`0`=gửi vào rương, `1`=rút ra túi), `value` (`amount`) | Gửi/rút tiền rương chứa đồ thật qua C2S `0x73` (`SendStoreMoney`) khi đang mở `OpenBox()`. |
| `box_operate` | `optionIndex` / `value` | Thao tác mật khẩu/khóa rương đồ (`BoxOperateAsync`, C2S `0x84`). |
| `crawl_region_npcs` | `maxRadiusTiles?`, `maxBranches?`, `excludeNpcNames?` | Tự động đi qua và mở thoại toàn bộ NPC đối thoại (`Kind == 3`) trong bán kính `maxRadiusTiles` tính từ tâm đứng đầu bước, bỏ qua `excludeNpcNames` và tránh các option dịch chuyển xa. |

---

## 3. Công cụ Kiểm kê, Quy hoạch Set-Cover & Kiểm toán Tĩnh (`audit_map_scenario.py`)

Trước khi viết hoặc chạy bất kỳ kịch bản bản đồ nào, dùng trực tiếp `audit_map_scenario.py`:

```powershell
# 1. Xuất 100% thực thể Ground Truth từ legacy-server/bin/ + kiểm tra MINIMAL_TEST_MAPS
python workbench\.agents\skills\khao-sat-ban-do\scripts\audit_map_scenario.py inventory --map 53

# 2. Giải Greedy Disk Set Cover + 2-opt TSP tìm các tâm gm_teleport sạch phủ 100% DialogNpc
python workbench\.agents\skills\khao-sat-ban-do\scripts\audit_map_scenario.py plan-cover --map 53 --radius 26

# 3. Kiểm toán tĩnh độ phủ 100% thực thể, cổng 2 chiều, nhánh cấp độ và giao dịch (< 1 giây)
python workbench\.agents\skills\khao-sat-ban-do\scripts\audit_map_scenario.py audit `
  --scenario workbench\qa\JXReborn.WireParityProxy\Scenarios\Maps\053_ba-lang-huyen.scenario.json --strict
```

---

## 4. Kiểm tra yêu cầu & Lệnh thực thi Live E2E `T=0`

1. **Điều kiện tiên quyết của Headless Client & Gói Zero-Sim**:
   - Đảm bảo `workbench/artifacts/headless-client/winmm.dll` là bản build có RPC `input_number`:
     ```powershell
     Select-String -Path workbench\artifacts\headless-client\winmm.dll -Pattern "input_number" -SimpleMatch -Quiet
     ```
   - Nếu mới bổ sung map đích vào `MINIMAL_TEST_MAPS` hoặc stub vào `workbench\qa\scripts\pack_legacy_profiles.py`, đóng gói lại `jxser-v1.tar.gz`:
     ```powershell
     python workbench\qa\scripts\pack_legacy_profiles.py `
       --profile v1 `
       --source-dir d:\Projects\jx-reborn\legacy-server\bin `
       --output-dir d:\Projects\jx-reborn\workbench\artifacts\packages
     ```
2. **Chạy Unit Test khóa Ground-Truth & Độ phủ**:
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
   - Đọc `workbench\artifacts\scenario-runs\<mapId>\scenario-report.json`: phải đạt `"StepFailures": 0` và `"ProtocolViolations": 0`.
   - Kiểm tra `workbench\artifacts\scenario-runs\<mapId>\logs\legacy-server.log`: `0` dòng khớp `LSIM|Checksum`.
   - Đối chiếu `findings.jsonl`, `report.md` và `captures/*.capture.json`; dọn xong kiểm tra không còn container/process sandbox sót lại.

---

## 5. Xử lý nhanh các lỗi E2E thường gặp

| Triệu chứng khi chạy `--scenario` | Nguyên nhân gốc hay gặp | Cách xử lý chuẩn |
| :--- | :--- | :--- |
| `audit --strict` báo `Uncovered Dialog NPCs` dù đã đặt `crawl_region_npcs` | NPC đứng ngoài bán kính `maxRadiusTiles * 32` tính từ tâm đứng lúc bắt đầu bước, hoặc trùng tên với `excludeNpcNames` (ví dụ hàng tôm cá cũng tên `"Ngư dân"`) | Dùng các tâm `gm_teleport` từ `audit_map_scenario.py plan-cover`, và thêm `interact_npc` theo tọa độ `(tileX, tileY)` cho các NPC trùng tên bị exclude. |
| `step_trap` sang map lân cận đứng yên trên Legacy nhưng Reborn đổi map (gây drift) | Map đích chưa được khai báo trong `MINIMAL_TEST_MAPS` của `pack_legacy_profiles.py` nên `SubWorldID2Idx(destMap) < 0` trên Oracle | Thêm `destMap` vào `MINIMAL_TEST_MAPS`, cập nhật `ZeroSimProfilePackagerTests.cs`, và đóng gói lại `jxser-v1.tar.gz`. |
| `No walkable path found ...` tại ô đích của `gm_teleport` hoặc `interact_npc` | Đích `gm_teleport` nằm trên ô `cliObs = 4` (mái nhà/rào), hoặc NPC đứng sát mép nước / quầy cần bán kính `r=3..4` | Lấy `cleanTp` từ `audit_map_scenario.py inventory` (`srvObs == 0 && cliObs == 0 && trapId == 0`). |
| Nhân vật dừng di chuyển giữa đường sau `ClickNoticeBoard` / Bia đá / Lộ tiêu | Thiếu `close_dialog` nên `Dialog.IsOpen` còn mở sau `Say(..., 0)` | Thêm bước `close_dialog` ngay sau mọi vật thể/NPC gọi `Say(..., 0)`. |
| `LSIM_*` hoặc `SG_Checksum` error trong `legacy-server.log` | Map mới kích hoạt script gọi hàm Sim/Checksum đã tắt trên Zero-Sim profile | Bổ sung stub an toàn vào `workbench\qa\scripts\pack_legacy_profiles.py`, đóng gói lại `jxser-v1.tar.gz` và thêm assert vào `ZeroSimProfilePackagerTests`. |
