---
name: phan-tich-va-sua-loi
description: >-
  End-to-end workflow to inspect WireParityProxy findings or gameplay bugs, cluster root
  causes from live telemetry and L3A captures, verify exact legacy behavior via discovery
  or binary/script ground truth (zero guessing), enforce Fail-First TDD with immutable
  tests, verify builds safely alongside running daemons, and commit cleanly across repos.
  Use when the user asks to inspect findings, diagnose a parity/gameplay bug, or run the
  full analyze-and-fix pipeline in JX Reborn.
---

# Phân tích và Sửa lỗi chuẩn Parity (JX Reborn)

Quy trình chuẩn từ thu thập Findings/Bug → đối chiếu **Ground-Truth** (cấm đoán mò) → **Fail-First TDD (RED → GREEN)** → kiểm chứng sống qua **WireParityProxy** → commit sạch đa repo.

---

## 1. Bất biến bắt buộc (Non-Negotiable Rules)

1. **Cấm đoán mò (Zero Guessing)**: Không tự suy diễn công thức, kích thước packet, điều kiện trigger hay spawn từ trí nhớ. Mọi chi tiết kỹ thuật phải có bằng chứng từ `legacy-server/discovery/`, `legacy-client/discovery/`, Lua/settings gốc (`legacy-explore`), hoặc decompile bằng `rea` / `/discover-legacy-server`.
2. **Sai hội thoại là port lại TOÀN BỘ script gốc**: Khi gặp finding lệch hội thoại (`Dialog.Prompt`, `Dialog.Options.Count`, `Dialog.IsOpen` hoặc fallback thoại), **tuyệt đối không vá cục bộ một câu thoại đơn lẻ**; phải truy lại toàn bộ file script Lua gốc của NPC đó bên `legacy-server` và cập nhật lại đầy đủ toàn bộ cây hội thoại + logic callback tương ứng.
3. **Fail-First TDD & Bất biến Test**:
   - **Trước khi sửa code sản xuất**: viết test tự động tái hiện đúng lỗi và chạy quan sát **FAIL (RED)** trước.
   - **Trong lúc sửa code**: **tuyệt đối không sửa test** để ép xanh.
   - **Chỉ được sửa test** khi có bằng chứng ground-truth mới (binary decompile hoặc oracle wire capture) chứng minh kỳ vọng cũ của test là sai.
4. **Tuân thủ Hiến pháp dự án (`server/.specify/memory/constitution.md`)**:
   - Không ghi raw disassembly address (`0x08...`, `FUN_00...`), tên file C++ độc quyền vào `server/src/`, `server/tests/` (`SolutionArchitectureComplianceTests`).
   - Mọi công cụ/vật phẩm GM & test hook **chỉ tự bật trong `Development` dưới `Debug`**, luôn tắt ở `Production` và `Release`.
   - Đọc/đóng file theo chuẩn repo (1 top-level type mỗi file, `sealed class`).

---

## 2. Bước 1 — Thu thập & Gom nhóm Findings (Evidence First)

1. Khi có phiên `WireParityProxy` đang chạy:
   - Gọi `get_proxy_status`, `get_proxy_findings`, `get_action_steps` (và `get_world_state` nếu cần đối chiếu NPC/item trong RAM `game.exe`).
   - Dùng Python gom nhóm toàn bộ findings theo **nguyên nhân gốc** (thời điểm step/timestamp, `clusterKey`, `fieldPath`), tách **hiệu ứng dây chuyền** (VD: delay lúc login, straddle ranh giới tick 18 Hz) khỏi **lỗi logic/gói tin thực sự**.
   - Nếu gặp `C2S.EntityTranslation` fail (`could not translate Primary NpcId=...`): tra `dw_id` trong `get_world_state` (`snapshot.npcs`), giải mã hex `name_tcvn3_hex` (TCVN3 hoặc GBK) để biết chính xác tên & vị trí NPC trên Legacy.
2. **Xuất bằng chứng trước khi tắt Proxy**:
   - Nếu chuẩn bị dừng hoặc restart daemon, luôn gọi `export_report` và `export_l3a_capture(step_index=...)` cho các step quan trọng để giữ lại hex frame và server logs.

---

## 3. Bước 2 — Xác minh Ground-Truth (Zero Guessing)

1. Tìm hàm C++ / struct packet / Lua handler trong:
   - `legacy-server/discovery/` và `legacy-client/discovery/` (ưu tiên hàng đầu).
   - `legacy-server/server1/` qua skill [legacy-explore](../legacy-explore/SKILL.md) (khi logic nằm ở Lua / `.txt` / `.ini` / `s3relay`).
   - `rea` MCP hoặc `/discover-legacy-server` (khi discovery chưa phủ tới hàm/opcode cần thiết, VD: tìm layout S2C/C2S theo byte length hoặc `ProcessFunc`).
2. Đối chiếu kết quả tìm được với code hiện tại trong `server/`, `pack-tieudaotu/`, và `workbench/qa/JXReborn.WireParityProxy/`.
3. Phân loại rõ từng nhóm trước khi sửa:
   - **Lỗi thực thi phía `server` / `pack-tieudaotu`**: thiếu packet/handler, sai điều kiện/công thức, sai thời điểm phát (trước vs sau `c2s_syncend`).
   - **Quy trình xử lý riêng khi thiếu NPC (`C2S.EntityTranslation` fail)**:
     - **Nhóm A — Thiếu hẳn trong Content Pack (`pack-tieudaotu`)**: Tra `dw_id`, `name_tcvn3_hex`, `kind`, `level`, `camp`, `(mps_x, mps_y)` trong `get_world_state` → truy nguồn spawn bên `legacy-server`:
       - Nếu là NPC tĩnh trên map (`.dat`) → bổ sung vào `pack-tieudaotu/data/maps/<mapId>_*.map.json`.
       - Nếu là Lua `AddNpc`/`AddNpcEx` (`autoexec.lua`, `global/`, `event/`) → bổ sung vào `pack-tieudaotu/data/npcs/global-spawns.npcs.json` (hoặc `event-spawns.npcs.json`) + `core.npcs.json`, và viết dialog/script trong `pack-tieudaotu/src/.../Scripts/` (**không hardcode NPC pack vào `server/`**).
       - Sau khi sửa Tier 1 JSON, chạy `JXReborn.PackCompiler` để build lại `pack-tieudaotu/dist/server/server.pack`.
     - **Nhóm B — Đã có trong `pack-tieudaotu` nhưng lệch thuộc tính hoặc `EntityIdTranslator` dịch hụt**: Đối chiếu `kind`, `camp`, tên (kể cả dấu cách thừa cuối tên trên Legacy như `"Tiêu Dao Tử  "`) giữa Legacy với `core.npcs.json`/`global-spawns.npcs.json`; chuẩn hoá dữ liệu pack theo gốc và/hoặc bổ sung trim tên + fallback theo khoảng cách `<= 64 mps` trong `EntityIdTranslator` khi Shadow mới có `0x4D` chưa hydrate `0x4C`.
     - **Nhóm C — NPC động tồn đọng do `--reuse-legacy`**: Boot lại `legacy-oracle-test` sạch ở T=0 (không dùng `--reuse-legacy`).
   - **Quy trình xử lý riêng khi sai/thiếu hội thoại (`Dialog.Prompt`, `Dialog.Options.Count`, `Dialog.IsOpen`, fallback dialog)**:
     - **Tuyệt đối không sửa vá mỗi câu thoại bị cảnh báo**.
     - Lập tức dùng [legacy-explore](../legacy-explore/SKILL.md) truy ngược từ tọa độ/tên NPC hoặc đoạn text mẫu (`<#><link=...`, tiêu đề, option) sang **toàn bộ file script Lua gốc** trên `legacy-server` (kèm các file `Include` liên quan).
     - Đọc hết toàn bộ cây hội thoại (`main()`, tất cả hàm con `Say` / `Describe` / `CreateTaskSay` / `Talk`, từng nhánh điều kiện Task/Item/Money/Level/Faction và từng hành động thực thi khi chọn option).
     - Cập nhật/port lại **đầy đủ toàn bộ script hội thoại của NPC đó** vào `pack-tieudaotu` (Tier 1 JSON dialogs/spawns + C# script layer, biên dịch lại `server.pack` / script DLL nếu cần) kèm test Fail-First TDD bao phủ các nhánh chính.
   - **Lỗi/hạn chế ở tầng so sánh `WireParityProxy`**: race lúc login chưa qua `s2c_syncend`, packet bất đồng bộ liên tiến trình (`s3relay` `0xFA`, `0xAC`), hoặc dữ liệu bản địa hóa hợp lệ (`0x4C` Việt hoá TCVN3 vs GBK) cần xử lý ở `GameServerShadowProxy` / `ActionWindowWireDiffer` mà vẫn giữ kiểm tra `ProtocolSafetyGate` + `ProxyShadowClientState`.

---

## 4. Bước 3 — Sửa theo chu kỳ TDD (RED từ MCP → GREEN → Refactor)

Chia thành từng phase/commit nhỏ độc lập theo nguyên nhân gốc, mỗi mục thực hiện đúng thứ tự:

1. **Lấy bằng chứng & Viết Test Fail (RED) trực tiếp từ `jx-wire-parity` MCP**:
   - Đọc `get_proxy_findings` + `get_action_steps` và gọi `export_l3a_capture(step_index=...)` (hoặc `get_world_state`) tại chính step đang lỗi để lấy nguyên C2S/S2C hex, độ dài frame, tọa độ và `expectedS2cContract` từ Oracle thật.
   - Chọn đúng tier theo [legacy-capture-test](../legacy-capture-test/SKILL.md): L1 (Golden Wire bytes/framing), L2 (18-Hz `ZoneSimulationHarness`), L3A+ (`*.capture.json` replay), `JXReborn.Packs.TieuDaoTu.Tests` (cho pack NPC/script), hoặc `JXReborn.WireParityProxy.Tests`.
   - Khởi tạo test bám đúng vector từ MCP (nếu là helper/handler mới thì tạo stub tối thiểu vừa đủ biên dịch).
   - Chạy `dotnet test --filter ...` và **xác nhận test mới FAIL (RED)** tái hiện đúng finding vừa ghi nhận từ MCP.
2. **Sửa Code cho đến khi Xanh (GREEN)**:
   - Implement đúng logic theo ground-truth đã xác minh ở Bước 2 (bao gồm compile lại `server.pack` nếu sửa `pack-tieudaotu`).
   - Tuyệt đối không sửa assertions của test vừa viết (và các test cũ liên quan) để ép xanh.
3. **Kiểm tra Build & Tuân thủ Hiến pháp**:
   - Chạy lại `dotnet test` trên các project bị ảnh hưởng (xanh 100%, 0 warning ở build mới).
   - **Lưu ý tránh lỗi khóa DLL (`MSB3027`)** khi daemon `WireParityProxy` hoặc `JXReborn.Host` đang chạy:
     - Có thể kiểm chứng build `server` bằng `-c Release` hoặc `-c Debug -p:OutDir=<scratch-dir>\` mà không ngắt phiên chơi live của người dùng; khi cần nạp binary mới vào proxy hoặc build đè `JXReborn.WireParityProxy`, hãy dừng daemon cũ sau khi đã export report ở Bước 1.

---

## 5. Bước 4 — Kiểm chứng End-to-End & Commit Đa Repo

1. **Khởi động lại Proxy sạch (khi cần kiểm tra live)**:
   - Nếu xuất hiện NPC/trạng thái dư thừa do tái sử dụng container cũ (`--reuse-legacy`), boot lại không dùng `--reuse-legacy` để đồng bộ T=0 cả Primary và Shadow.
   - Muốn chạy script login tự động qua MCP `perform_action(action: "login")`: dùng `--headless` (vì `InteractiveWindow = true` phục vụ người chơi click tay và không nhận RPC `connect_gateway`). Sau khi kiểm chứng xong, bật lại chế độ tương tác bình thường cho người dùng.
   - Đối chiếu lại `get_proxy_status` & `get_proxy_findings` (số Critical/Warning, `protocolViolations`, `isDrifted`).
2. **Commit sạch từng repo (`git-commit`)**:
   - Kiểm tra `git status` trên từng repo liên quan (`server`, `workbench`, `pack-tieudaotu`, `legacy-server`).
   - Stage đúng các file thuộc phạm vi từng thay đổi và commit theo Conventional Commits (`fix(combat): ...`, `feat(protocol): ...`, `fix(wire-parity-proxy): ...`).
