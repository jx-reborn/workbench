---
name: khao-sat-co-che
description: >-
  Survey, mathematically verify against decompiled C++ binaries in legacy-server/discovery/,
  and lock 18-Hz frame-exact systemic gameplay mechanics (movement speed, attack/cast frame
  breakpoints, KMissle flight and collision ticks, 5-element damage and resistances,
  KNpcAttribModify buff stacking, equipment affixes, party/trade/PK state machines) using
  ZoneSimulationHarness (L2) and Offline Wire Capture-Replay (L3A+). Use when verifying or
  fixing combat feel, systemic formulas, sect skills, or cross-map mechanics.
---

# Khảo Sát & Khóa Chuẩn Cơ Chế Động 18-Hz (`khao-sat-co-che`)

Nếu [khao-sat-ban-do](../khao-sat-ban-do/SKILL.md) đảm bảo **chiều rộng không gian thế giới** (không sót thực thể trên bản đồ), thì `khao-sat-co-che` đảm bảo **chiều sâu cảm giác chơi từng frame `55.5ms` (18 Hz)** — bịt kín những điểm mù mà `WireParityProxy` chạy mạng thời gian thực buộc phải dung sai (`±64 mps` vị trí, bỏ qua nhịp tick lẻ của `s2c_npchurt`, `s2c_castskilldirectly`, `s2c_syncstateeffect`).

**Hệ sinh thái liên kết**:
- Nguồn chân lý công thức & máy trạng thái C++: `legacy-server/discovery/` (nếu còn `UNRESOLVED_BINARY_REF`, dùng `/discover-legacy-server` giải mã trực tiếp từ `jx_linux_y`).
- Nguồn chân lý bảng thông số tĩnh: `legacy-server/bin/server1/settings/` (`skills.txt`, `missles.txt`, `npcs.txt`, `magicattrib.txt`, `level_add.txt`) đọc qua [legacy-explore](../legacy-explore/SKILL.md).
- Bảng kiểm tra các ngưỡng frame & làm tròn C++: [frame-breakpoint-checklist.md](./references/frame-breakpoint-checklist.md).
- Thu & khóa gói tin thực tế từ `game.exe`: [legacy-capture-test](../legacy-capture-test/SKILL.md).
- Khóa bất biến TDD: [verify_tdd_gate.py](../../../qa/scripts/verify_tdd_gate.py).

---

## 1. Bất biến bắt buộc (Zero-Tolerance Rules)

1. **100% Toán học Số nguyên & Trình tự C++ Gốc**:
   - Mọi phép chia tốc độ, sát thương, kháng tính, hút máu/mana trong `jx_linux_y` đều là **phép chia số nguyên C++ cắt phần thập phân tại từng bước trung gian** (`(a * b) / 100` khác hẳn `(int)(a * (b / 100.0))`!).
   - Phải đối chiếu từng dòng trong `legacy-server/discovery/` (`combat/`, `attrib_modify/`, `traps/`, `Player/`, `Items/`), giữ nguyên thứ tự cộng/nhân, giới hạn kẹp biên (`clamp` kháng tối đa `75%` hoặc cộng thêm kháng cực đại), và thứ tự sự kiện trong 1 tick `KSubWorld::Activate`.
2. **Sai số Tick = `0` trên `ZoneSimulationHarness` (`L2`)**:
   - Không bao giờ dùng `Task.Delay` hay dung sai xấp xỉ cho cơ chế lõi. Phải bước chính xác từng frame bằng `ZoneSimulationHarness.StepFramesAsync(1)` và kiểm tra sự kiện phát ra ở **đúng chỉ số frame `F`** (sai lệch dù chỉ `±1 frame` = `55.5ms` là cảm giác pk/bơm máu đã khác `jx-linux`!).
3. **Khóa Kép `L2` (Frame-Exact) + `L3A+` (Real `game.exe` Wire Capture)**:
   - Mỗi cơ chế sau khi khớp toán học trên `L2` phải có ít nhất 1 kịch bản thu thật trên sandbox chuẩn **Map 53 (`Ba Lăng Huyện`)** (với `Cọc gỗ` `TemplateId = 413`, trang bị trắng `seed = 0` không dòng ngẫu nhiên, hoặc 2 nhân vật `P1`/`P2`) để đóng băng chuỗi gói tin `S2C` thành `*.capture.json` chạy offline `~1s`.

---

## 2. Quy trình 5 bước Khảo sát & Hội tụ Cơ chế

### Bước 1: Trích xuất Đặc tả Toán học & Máy trạng thái từ Binary + Settings
1. Tra cứu các file giải mã liên quan trong `legacy-server/discovery/` và `server/docs/parity_ledger.json`.
2. Nếu hàm mục tiêu chưa được giải mã hoặc còn dòng `// UNRESOLVED_BINARY_REF`, chạy `ghidra_elf_query.py` (`decompile` / `xrefs`) để trích xuất chính xác biểu thức C++ trước khi viết bất kỳ dòng code/test nào.
3. Đọc dòng dữ liệu thật trong `skills.txt` / `missles.txt` / `npcs.txt` bằng `read_tabfile.py` để lấy bộ tham số thực tế làm vector kiểm thử (cấm tự bịa thông số kỹ năng giả).

### Bước 2: Thiết kế Ma trận Vector Biên & Breakpoints
Dựa theo [frame-breakpoint-checklist.md](./references/frame-breakpoint-checklist.md), lập tối thiểu **4 nhóm vector** từ dữ liệu thật:
1. **Vector Cơ sở (`Baseline`)**: Cấp 1 / Kỹ năng cấp 1 / Trang bị trắng (`0%` tốc đánh, `0%` kháng).
2. **Vector Ranh giới Khung hình (`Frame Breakpoints`)**: Ngay dưới ngưỡng và ngay tại ngưỡng tốc đánh / tốc chạy / tốc xuất chiêu (chứng minh hàm chia nguyên nhảy bậc frame đúng lúc).
3. **Vector Tương tác & Kẹp biên (`Interaction & Clamps`)**: Ngũ hành tương khắc (`+20%` / `-20%`), kháng âm vs kháng vượt trần `75%`, cộng dồn và gỡ bỏ bùa chú (`KNpcAttribModify` hoàn trả đúng `0` sai số tích lũy), miễn sát thương `ProtectTime`.
4. **Vector Hủy / Ngắt quãng (`Fault & Interruption`)**: Đang niệm chiêu bị giật hình (`DoHurt`), đổi vũ khí giữa chừng, bước vào vùng an toàn (`FightMode 1 -> 0`) hủy trạng thái Cừu sát/Đạn bay.

### Bước 3: Thực thi `Mechanical RED -> GREEN` ở Tầng `L1` / `L2`
1. Chụp snapshot bằng `verify_tdd_gate.py snapshot`.
2. Viết test `L1` (`JXReborn.Protocol.Tests` / `GameAssets.Tests`) hoặc `L2` (`JXReborn.ZoneEngine.Tests` / `JXReborn.Workbench.ParityTests`) với hạt giống cố định (`seeded RNG`) và khẳng định chính xác giá trị từng tick.
3. Nếu code hiện tại của `server/` đang lệch với `jx-linux`:
   - Chạy `verify_tdd_gate.py check-red` để xác nhận test mới **FAIL đúng điểm lệch**.
   - Sửa code Clean-Room trong `server/src/` (không sửa test!) và chạy `verify_tdd_gate.py check-green --run-compliance`.
   *(Trường hợp kiểm toán xác nhận cơ chế trong `server/` đã khớp 100% từ trước: chứng minh không vacuous bằng cách chạy thử nghiệm đột biến tạm thời hoặc đối chiếu trực tiếp vector biên thực tế rồi giữ nguyên test bảo vệ).*

### Bước 4: Ghi & Phát lại `L3A+` Wire Capture từ `legacy` + `game.exe` thật
1. Chuẩn bị nhân vật đối xứng qua GM Token `(6, 1, 4258)` trên Map 53 **trước khi** mở cửa sổ thu gói tin (`BeginWireCaptureAsync`).
2. Thu kịch bản 1–3 thao tác cốt lõi qua `run-parity.ps1 -Profile v1 -Mode Record` để tạo `Baselines/<CaptureId>.capture.json` có chữ ký SHA-256.
3. Thêm `[Fact]` vào `LegacyGameExeExperienceReplayIntegrationTests` và chạy offline (`0 Docker`):
   ```powershell
   dotnet test server\tests\JXReborn.Integration.Tests\JXReborn.Integration.Tests.csproj --filter "FullyQualifiedName~WireCaptures"
   ```

### Bước 5: Khóa vào Sổ cái Parity (`parity_ledger.py`)
Sau khi cả `L1/L2`, `L3A+ WireCaptures`, và `SolutionArchitectureComplianceTests` đều xanh 100%, khóa hạng mục vào sổ cái:
```powershell
python workbench\qa\scripts\parity_ledger.py verify-and-lock --domain <engine_mechanics|factions_skills> --id <item_id> --test-project <path_to_csproj> --test-filter "<Filter>" --run-tests
```
