# JXReborn Multi-Agent Parity Workflows (`run_workflow`)

Thư mục này chứa 4 kịch bản điều phối đa Agent bằng Python cho công cụ `run_workflow`, kết nối trực tiếp với sổ cái [parity_master_ledger.json](../../qa/parity_master_ledger.json) và bộ kiểm toán cơ học tại `workbench/qa/scripts/`.

## Danh mục Workflows

1. **[wf_map_convergence.py](./wf_map_convergence.py)** — Khép kín 1 Bản đồ từ khảo sát Set-Cover → chạy `WireParityProxy` `T=0` → tách pha `RED -> GREEN` → khóa `zero_drift_locked`:
   ```python
   run_workflow(
       ScriptPath="d:/Projects/jx-reborn/workbench/.agents/workflows/wf_map_convergence.py",
       Description="Converge Map 53 to Zero Parity Drift",
       Args={"map_id": 53, "max_fix_iterations": 3, "reuse_existing_clean_run": False}
   )
   ```

2. **[wf_mechanic_convergence.py](./wf_mechanic_convergence.py)** — Khép kín 1 Cơ chế C++ 18-Hz / Môn phái / Kỹ năng / Spec:
   ```python
   run_workflow(
       ScriptPath="d:/Projects/jx-reborn/workbench/.agents/workflows/wf_mechanic_convergence.py",
       Description="Verify and lock KNpc::CheckTrap latching parity",
       Args={
           "domain": "engine_mechanics",
           "target_id": "traps.knpc_checktrap_latching",
           "goal_brief": "Verify KNpc::CheckTrap latching against discovery/traps/KNpc_And_KObj_Traps.cpp and lock L2 frame-exact tests"
       }
   )
   ```

3. **[wf_journey_calibration.py](./wf_journey_calibration.py)** — Hiệu chuẩn tự động `Live -> RED/GREEN -> Record -> Replay` cho các hành trình `J09–J28`:
   ```python
   run_workflow(
       ScriptPath="d:/Projects/jx-reborn/workbench/.agents/workflows/wf_journey_calibration.py",
       Description="Calibrate and lock Journey J09",
       Args={"journey_id": "J09", "test_class": "DifferentialParityNpcShopTests", "max_attempts": 3}
   )
   ```

4. **[wf_campaign_batch.py](./wf_campaign_batch.py)** — Chạy chiến dịch hàng loạt theo đợt từ hàng đợi ưu tiên của `parity_master_ledger.json` (`parallel` cho khảo sát tĩnh, `pipeline` tuần tự có khóa Mutex cho Docker `T=0`):
   ```python
   run_workflow(
       ScriptPath="d:/Projects/jx-reborn/workbench/.agents/workflows/wf_campaign_batch.py",
       Description="Survey Wave 1 Starter Villages",
       Args={"domain": "maps_world", "wave": "wave1_starter_villages", "limit": 3, "survey_only": True}
   )
   ```
