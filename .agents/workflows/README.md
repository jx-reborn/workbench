# JXReborn Multi-Agent Parity Workflows (`Harness v2.0`)

Thư mục này chứa **5 kịch bản điều phối đa Agent bằng Python (`run_workflow`)**, vận hành cùng đội hình **6 Binh chủng Agent chuyên biệt (`jx-parity-army`)** và sổ cái [parity_master_ledger.json](../../qa/parity_master_ledger.json).

## Đội Hình 6 Specialist Subagents (`jx-parity-army`)

1. **`jx-migrator-engineer`** — Máy cày Transpiler hàng loạt (`JXReborn.ScriptMigrator` + `Lua4Interpreter` vector equivalence).
2. **`jx-binary-archaeologist`** — Giải mã ELF (`jx_linux_y`, `s3relay_y`, `bishop_y`, `goddess_y`) & PE (`game.exe`) sang `discovery/`.
3. **`jx-contract-architect`** — Pha 0: Xuất Clean-Room Spec + tạo chữ ký Interface / `NotImplementedException` Stub chống kẹt build `CS1061`.
4. **`jx-blind-qa`** (`[HARNESS_ROLE:blind_qa]`) — Viết bài test `RED` từ Ground Truth, bị chặn đọc `src/` sản xuất.
5. **`jx-blind-coder`** (`[HARNESS_ROLE:blind_coder]`) — Viết code Clean-Room từ Spec, bị chặn đọc `tests/` & `legacy-server/` + bài test bị cất trong két sắt ngoài repo, có quyền nộp đơn `test_dispute`.
6. **`jx-dispute-referee`** — Tòa phúc thẩm độc lập khi Coder và Holdout Test mâu thuẫn (`test_bug` | `spec_gap` | `coder_bug`).

## Danh Mục 5 Workflows

1. **[wf_lua_compiler_wave.py](./wf_lua_compiler_wave.py)** *(Tầng 1 của Phễu Quy Mô Lớn — Compiler-First)*:
   Gom cụm `manual-migration-queue.json` và dùng `jx-migrator-engineer` nâng cấp bộ dịch tự động hàng trăm file Lua cùng lúc, khóa bằng `verify-equivalence --strict`.
2. **[wf_map_convergence.py](./wf_map_convergence.py)**:
   Khép kín 1 Bản đồ từ khảo sát Set-Cover → `WireParityProxy` `T=0` → Double-Blind `jx-blind-qa` / `jx-blind-coder` → khóa `zero_drift_locked`.
3. **[wf_mechanic_convergence.py](./wf_mechanic_convergence.py)**:
   Khép kín 1 Cơ chế C++ 18-Hz / Môn phái / `s3relay` / Mission với đầy đủ `jx-contract-architect` → `jx-blind-qa` → Holdout Vault → `jx-blind-coder` → `jx-dispute-referee`.
4. **[wf_journey_calibration.py](./wf_journey_calibration.py)**:
   Hiệu chuẩn tự động `Live -> RED/GREEN -> Record -> Replay` cho các hành trình `J09–J28`.
5. **[wf_campaign_batch.py](./wf_campaign_batch.py)**:
   Chạy chiến dịch hàng loạt theo đợt từ `parity_master_ledger.json`.
