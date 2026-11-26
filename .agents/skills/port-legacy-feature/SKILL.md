---
name: port-legacy-feature
description: >-
  Port complete gameplay features, NPC dialog trees, quests, active items, traps,
  map objects, or sect systems from legacy-server/bin/ into pack-tieudaotu (Tier 1
  UTF-8 JSON + Parametric C# 14 Scripts) and server (clean-room ZoneEngine hooks)
  with 100% behavioral branch parity, mechanical RED->GREEN TDD verification, and
  zero no-op stubs. Use when migrating a missing legacy feature or re-porting an
  entire NPC/item/quest Lua script tree to close parity gaps.
---

# Port Trọn Gói Tính Năng & Script Legacy (`port-legacy-feature`)

Quy trình chuẩn chuyển đổi **trọn vẹn 100%** một thực thể nội dung hoặc cơ chế từ `legacy-server/bin/` sang `pack-tieudaotu/` và `server/` — tuyệt đối không vá lẻ một câu thoại, không đoán hằng số, không để hàm rỗng `{}` và không vi phạm ranh giới Clean-Room của `server/src/`.

**Công cụ liên kết bắt buộc**:
- Đọc Lua/TabFile đa bảng mã & Ghidra: [legacy-explore](../legacy-explore/SKILL.md).
- Kiểm toán dấu vân tay tĩnh `< 1s`: [audit_feature_parity.py](../../../qa/scripts/audit_feature_parity.py).
- Khóa bất biến TDD `RED -> GREEN`: [verify_tdd_gate.py](../../../qa/scripts/verify_tdd_gate.py).
- Bảng ánh xạ Lua C-API sang C# SDK & JSON Tier 1: [parametric-mapping.md](./references/parametric-mapping.md).

---

## 1. Bất biến bắt buộc (Hard Constraints)

1. **Port trọn cây, cấm vá chắp vá**: Khi đụng vào bất kỳ NPC, vật phẩm kích hoạt, bẫy/cổng (`trap`), hay vật thể bản đồ (`0x72`) nào đang thiếu hoặc sai hội thoại, bắt buộc đọc hết file `.lua` gốc **cùng toàn bộ chuỗi `Include(...)` nghiệp vụ** của nó và chuyển đổi đầy đủ mọi nhánh điều kiện (`Level`, `Task`, `GetByte`/`GetBit`, `Faction`, `Series`, `Money`, `BagRoom`, `Item`).
2. **Phân định đúng tầng Kiến trúc (`1 Engine — N Packs`)**:
   - **`server/src/` (Clean-Room Core Engine)**: Chỉ đặt các giao diện/hạ tầng cơ chế dùng chung (`IScriptDialogContext`, `IObjInteractionScript`, `INpcCombatScript`, wire packet serializers/handlers, công thức vật lý/chiến đấu 18-Hz). **Tuyệt đối cấm** ghi địa chỉ `0x08...`, `0x004...`, tên file `.cpp`, tên NPC cốt truyện hay đường dẫn `\script\...` vào `server/src/`.
   - **`pack-tieudaotu/data/` (Tier 1 UTF-8 JSON)**: Đặt dữ liệu tĩnh (thoại thuần `ambient-npcs.dialogs.json`, spawn `*.npcs.json` / `*.map.json`, cửa hàng `*.shops.json`, vật phẩm `*.items.json`).
   - **`pack-tieudaotu/src/` (Layered C# 14 Scripts)**: Đặt các lớp `sealed` thực thi `INpcDialogScript`, `IItemUsageScript`, `IObjInteractionScript`, `ITrapScript`, `INpcCombatScript` gắn `[LegacySource(@"\script\...", "<sha256_64_hex>")]` khớp chính xác byte gốc trên đĩa, và đăng ký trong `TieuDaoTuScriptCodeResolver` / `TieuDaoTuExactScriptPathManifest`.
3. **Ưu tiên Gom nhóm Tham số hóa (Parametric Consolidation)**: Các NPC dịch vụ lặp lại giữa nhiều thành/thôn/phái (Thợ rèn, Hiệu thuốc, Tạp hóa, Xa phu, Rương chứa đồ, Võ sư, Đệ tử tiếp dẫn) phải dùng chung lớp C# tham số hóa theo bảng cấu hình thay vì nhân bản hàng trăm class 1:1.

---

## 2. Quy trình 5 bước thực thi chuẩn

### Bước 1: Trích xuất Dấu vân tay Ground Truth (`Fingerprint & Spec`)
1. Chạy trình trích xuất dấu vân tay tự động trên file `.lua` mục tiêu:
   ```powershell
   python workbench\qa\scripts\audit_feature_parity.py fingerprint --lua "\script\<duong_dan>.lua"
   ```
2. Dùng `read_jx_file.py` và `read_tabfile.py` (từ skill `legacy-explore`) đọc toàn bộ thân hàm `main()`, `OnDeath()`, `OnTimer()`, các hàm callback `Say`/`CreateNewSayEx`/`AskClientForNumber`/`GiveItemUI`, và kiểm tra mọi bảng `.txt` liên quan (lưu ý vật phẩm kích hoạt nằm tại `server1/settings/item/004/`).
3. Nếu phát hiện lệnh Lua C-API chưa có trên `IScriptDialogContext` / `IScriptCombatContext`, kiểm tra binary `jx_linux_y` qua `legacy-server/discovery/` trước khi mở rộng SDK ở `server/src/`.

### Bước 2: Chụp Snapshot & Viết Test Fail-First (`Mechanical RED`)
1. Chụp trạng thái SHA-256 trước khi sửa:
   ```powershell
   python workbench\qa\scripts\verify_tdd_gate.py snapshot --out workbench\artifacts\tdd-locks\pre_port.json
   ```
2. Viết test Ground-Truth trong `pack-tieudaotu/tests/JXReborn.Packs.TieuDaoTu.Tests/` (và `server/tests/JXReborn.ZoneEngine.Tests/` nếu thêm hook Engine):
   - Bao phủ **mọi nhánh chính** (nhánh chưa đủ cấp/tiền/túi đầy, nhánh nhận nhiệm vụ, nhánh hoàn thành nhiệm vụ, kiểm tra thay đổi `Task`, trừ/cộng vật phẩm thực tế, không chỉ kiểm tra mở UI).
   - Nếu duyệt danh sách `[LegacySource]` hoặc catalog, bắt buộc `Assert.NotEmpty(...)` trước vòng lặp.
3. Cưỡng chế kiểm tra `RED` (chỉ sửa `tests/`, dự án build sạch nhưng test mới **bắt buộc FAIL**):
   ```powershell
   python workbench\qa\scripts\verify_tdd_gate.py check-red --snapshot workbench\artifacts\tdd-locks\pre_port.json --project pack-tieudaotu\tests\JXReborn.Packs.TieuDaoTu.Tests\JXReborn.Packs.TieuDaoTu.Tests.csproj --filter "FullyQualifiedName~<TenClassTestMoi>" --save-red-lock workbench\artifacts\tdd-locks\red_lock.json
   ```

### Bước 3: Chuyển đổi Clean-Room sang JSON Tier 1 + C# Script (`Implementation`)
1. Khai báo hằng số `TaskID` mới (nếu có) vào `PlayerTaskConsts.cs` theo thứ tự tăng dần để chống đụng độ ID toàn cục.
2. Ánh xạ chính xác từng cấu trúc Lua sang C# SDK theo [parametric-mapping.md](./references/parametric-mapping.md):
   - Giữ đúng loại khung hội thoại: `Say`/`Talk` (`UI=0` -> `.SendSay()` / `ctx.Talk(...)`) vs `Describe`/`CreateNewSayEx`/`DailogClass:new` (`UI=12` -> `.SendDescribe()`), và `Say(numericStringId, ...)` -> `ctx.SayByStringId(...)`.
   - Giữ đúng chỉ số 1-based khi chuyển `GetByte(val, idx)` / `GetBit(val, idx)`.
   - Gắn đúng `[LegacySource(@"\script\...", "<sha256>")]` với mã băm SHA-256 lấy từ Bước 1.
3. Nếu cập nhật dữ liệu Tier 1 JSON trong `pack-tieudaotu/data/`, biên dịch lại `server.pack`:
   ```powershell
   dotnet run --project server\tools\JXReborn.PackCompiler\JXReborn.PackCompiler.csproj -- --pack pack-tieudaotu
   ```

### Bước 4: Cổng Kiểm Toán Tĩnh Độ Phủ (`Gate 1 — Static Fingerprint Audit`)
Chạy kiểm toán đối chiếu 1–1 giữa file `.lua` gốc và file C#/JSON vừa viết:
```powershell
python workbench\qa\scripts\audit_feature_parity.py audit --lua "\script\<duong_dan>.lua" --target "pack-tieudaotu/src/JXReborn.Packs.TieuDaoTu/Scripts/<FileMoi>.cs" --strict
```
Bắt buộc đạt **`[PASS]`** (`0` thiếu `TaskID`/`Shop`/`Skill`/`Item`/`Modal`/`Hook`, `0` lệch SHA-256, `0` hàm stub `{}`).

### Bước 5: Cổng Kiểm Chứng `Mechanical GREEN` & Kiến trúc
Xác nhận `tests/` hoàn toàn bất biến kể từ pha `RED`, không chạm vào file trọng tài, và toàn bộ test + kiểm toán kiến trúc đều XANH:
```powershell
python workbench\qa\scripts\verify_tdd_gate.py check-green --red-lock workbench\artifacts\tdd-locks\red_lock.json --project pack-tieudaotu\tests\JXReborn.Packs.TieuDaoTu.Tests\JXReborn.Packs.TieuDaoTu.Tests.csproj --filter "FullyQualifiedName~<TenClassTestMoi>" --run-compliance
dotnet run --project workbench\tools\JXReborn.ScriptMigrator\JXReborn.ScriptMigrator.csproj -- audit-coverage --legacy legacy-server\bin --pack pack-tieudaotu
```
