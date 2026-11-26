"""Batch Parity Campaign Orchestrator (`wf_campaign_batch.py`).

Executes via Antigravity `run_workflow`:
  run_workflow(
      ScriptPath="d:/Projects/jx-reborn/workbench/.agents/workflows/wf_campaign_batch.py",
      Description="Run Starter Villages Parity Campaign Batch",
      Args={"domain": "maps_world", "wave": "wave1_starter_villages", "limit": 3, "survey_only": False}
  )

Reads prioritized unlocked targets from `parity_master_ledger.json`:
  - Stage 1 (`parallel`): Fans out read-only Ground-Truth exploration & Gap Forecast across all selected items concurrently.
  - Stage 2 (`pipeline`): Executes stateful code/pack edits & single-mutex Docker `T=0` verification one item at a time
    so ports `15622`/`16666` and `tests/` snapshots never collide.
"""

import json
import subprocess
import sys
from pathlib import Path

raw_args = globals().get("args") or {}
domain = raw_args.get("domain")
wave = raw_args.get("wave")
limit = int(raw_args.get("limit", 3))
survey_only = bool(raw_args.get("survey_only", False))


def resolve_eco_root() -> Path:
    for ws in globals().get("WORKSPACES") or ():
        p = Path(ws).resolve()
        if (p.parent / "workbench").is_dir() and (p.parent / "server").is_dir():
            return p.parent
        if (p / "workbench").is_dir() and (p / "server").is_dir():
            return p
    cwd = Path.cwd().resolve()
    for anc in (cwd, *cwd.parents):
        if (anc / "workbench").is_dir() and (anc / "server").is_dir():
            return anc
    raise RuntimeError("Could not locate jx-reborn ecosystem root.")


ROOT = resolve_eco_root()
WORKBENCH = ROOT / "workbench"
PY = sys.executable
LEDGER_PY = WORKBENCH / "qa" / "scripts" / "parity_ledger.py"


def run_cmd(cmd: list[str]) -> tuple[int, str]:
    proc = subprocess.run(cmd, cwd=str(ROOT), capture_output=True, text=True, encoding="utf-8", errors="replace")
    return proc.returncode, (proc.stdout + "\n" + proc.stderr).strip()


phase("Stage 0: Sync Parity Master Ledger & Select Batch Queue")
run_cmd([PY, str(LEDGER_PY), "sync"])

next_cmd = [PY, str(LEDGER_PY), "next", "--limit", str(limit), "--json"]
if domain:
    next_cmd.extend(["--domain", str(domain)])
if wave:
    next_cmd.extend(["--wave", str(wave)])

code, out = run_cmd(next_cmd)
if code != 0:
    raise RuntimeError(f"Failed to query parity ledger:\n{out}")

queue = json.loads(out)
log(f"Selected {len(queue)} target(s): {[it['id'] for it in queue]}")

if not queue:
    print("All matching targets in parity_master_ledger.json are already zero_drift_locked!")
else:
    phase("Stage 1: Parallel Read-Only Ground-Truth Gap Forecasts")
    forecasts = await parallel(
        queue,
        lambda item: agent(
            prompt=(
                f"Perform a STRICT READ-ONLY Ground-Truth survey and Gap Forecast (do NOT edit any files).\n"
                f"Item: `{item['domain']}::{item['id']}` — `{item.get('title') or item.get('name')}`\n"
                f"Metadata: {json.dumps(item, ensure_ascii=False)}\n"
                f"Use `legacy-explore` / `audit_map_scenario.py inventory` / `legacy-server/discovery/` as appropriate "
                f"and list exact gaps between `legacy-server` and `pack-tieudaotu` / `server`."
            ),
            schema={
                "type": "object",
                "properties": {
                    "id": {"type": "string"},
                    "entity_or_branch_count": {"type": "integer"},
                    "gaps": {"type": "array", "items": {"type": "string"}},
                    "recommended_test_project": {"type": "string"},
                },
                "required": ["id", "entity_or_branch_count", "gaps", "recommended_test_project"],
            },
            role=f"Survey {item['id']}",
            type_name="research",
        ),
    )

    forecast_by_id = {f["id"]: f for f in forecasts}
    for f in forecasts:
        log(f"[Forecast {f['id']}] count={f['entity_or_branch_count']}, gaps={len(f['gaps'])}")

    if not survey_only:
        phase("Stage 2: Sequential Convergence Execution (Mutex Protected)")

        async def converge_one(item: dict) -> str:
            fc = forecast_by_id.get(item["id"], {})
            return await agent(
                prompt=(
                    f"Converge item `{item['domain']}::{item['id']}` (`{item.get('title') or item.get('name')}`) "
                    f"following `d:/Projects/jx-reborn/workbench/docs/parity-harness-workflows-plan.md`.\n"
                    f"Pre-computed Gap Forecast:\n{json.dumps(fc, indent=2, ensure_ascii=False)}\n\n"
                    f"Enforce all 4 Gates (`audit_map_scenario.py` / `audit_feature_parity.py`, `verify_tdd_gate.py` RED->GREEN, "
                    f"offline `WireCaptures`, and `parity_ledger.py verify-and-lock`)."
                ),
                schema={"type": "string"},
                role=f"Converge {item['id']}",
            )

        results = await pipeline(queue, converge_one)
        for r in results:
            log(r)

    _, final_status = run_cmd([PY, str(LEDGER_PY), "status"])
    print(final_status)
