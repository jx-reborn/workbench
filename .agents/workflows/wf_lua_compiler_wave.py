"""Compiler-First Lua Corpus Wave Workflow (`wf_lua_compiler_wave.py` — Harness v2.0).

Executes the 2-Stage Industrial Funnel across the remaining ~3,615 Legacy Lua scripts so we NEVER
burn millions of LLM tokens hand-porting repetitive scripts:

  Stage 1: Run `JXReborn.ScriptMigrator audit-coverage` & cluster `manual-migration-queue.json`
           by AST failure pattern / directory family (`activitysys/`, `event/`, `item/`, `npc/`, `trap/`).
  Stage 2: Dispatch `jx-migrator-engineer` on the highest-ROI pattern cluster to extend
           `JXReborn.ScriptMigrator`, regenerate fail-closed C# / JSON catalogs, compile `server.pack`,
           and enforce `JXReborn.ScriptMigrator verify-equivalence --strict` (`0` vector mismatches
           between `Lua4Interpreter` and C# runtime!).
  Stage 3: Sync updated metrics into `parity_master_ledger.json`.
"""

import asyncio
import json
import subprocess
import sys
from collections import Counter
from pathlib import Path

raw_args = globals().get("args") or {}
scope = str(raw_args.get("scope", "all"))
kind = str(raw_args.get("kind", "all"))
max_compiler_waves = int(raw_args.get("max_compiler_waves", 1))
audit_only = bool(raw_args.get("audit_only", False))
worker_timeout_sec = int(raw_args.get("worker_timeout_sec", 900))


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
SERVER = ROOT / "server"
PACK = ROOT / "pack-tieudaotu"
LEGACY_BIN = ROOT / "legacy-server" / "bin"
PY = sys.executable

MIGRATOR_CSPROJ = WORKBENCH / "tools" / "JXReborn.ScriptMigrator" / "JXReborn.ScriptMigrator.csproj"
MIGRATOR_TESTS = WORKBENCH / "tests" / "JXReborn.ScriptMigrator.Tests" / "JXReborn.ScriptMigrator.Tests.csproj"
PACK_COMPILER = SERVER / "tools" / "JXReborn.PackCompiler" / "JXReborn.PackCompiler.csproj"
PACK_TESTS = PACK / "tests" / "JXReborn.Packs.TieuDaoTu.Tests" / "JXReborn.Packs.TieuDaoTu.Tests.csproj"
LEDGER_PY = WORKBENCH / "qa" / "scripts" / "parity_ledger.py"


def run_cmd(cmd: list[str], cwd: Path | None = None) -> tuple[int, str]:
    proc = subprocess.run(cmd, cwd=str(cwd or WORKBENCH), capture_output=True, text=True, encoding="utf-8", errors="replace")
    return proc.returncode, (proc.stdout + "\n" + proc.stderr).strip()


def summarize_manual_queue() -> dict:
    q_path = WORKBENCH / "artifacts" / "migration" / "manual-migration-queue.json"
    cov_path = WORKBENCH / "artifacts" / "migration" / "coverage-report.json"
    summary: dict = {"coverage": {}, "buckets": [], "top_reasons": [], "top_prefixes": []}
    if cov_path.is_file():
        cov = json.loads(cov_path.read_text(encoding="utf-8-sig"))
        summary["coverage"] = {
            "accounted": cov.get("AccountedLuaFiles"),
            "unaccounted": cov.get("UnaccountedLuaFiles"),
            "broken_paths": cov.get("BrokenScriptPaths"),
        }
    if q_path.is_file():
        items = json.loads(q_path.read_text(encoding="utf-8-sig"))
        if isinstance(items, dict):
            items = items.get("Items") or items.get("Entries") or []
        buckets: Counter[str] = Counter()
        reasons: Counter[str] = Counter()
        prefixes: Counter[str] = Counter()
        for it in items:
            if not isinstance(it, dict):
                continue
            buckets[str(it.get("BucketName") or it.get("Bucket") or "unknown")] += 1
            blocking = it.get("BlockingReasons")
            if isinstance(blocking, list) and blocking:
                for r in blocking:
                    reasons[str(r)] += 1
            else:
                reasons[str(it.get("Reason") or it.get("FailureReason") or "unknown")] += 1
            raw_path = str(it.get("Utf8ScriptPath") or it.get("RelativePath") or it.get("ScriptPath") or "")
            prefixes["/".join(raw_path.replace("\\", "/").strip("/").split("/")[:3])] += 1
        summary["buckets"] = buckets.most_common(10)
        summary["top_reasons"] = reasons.most_common(12)
        summary["top_prefixes"] = prefixes.most_common(10)
    return summary


phase("Stage 1: Baseline Compiler Coverage & Queue Clustering")
_, cov_out = run_cmd([
    "dotnet",
    "run",
    "--project",
    str(MIGRATOR_CSPROJ),
    "--",
    "audit-coverage",
    "--legacy",
    str(LEGACY_BIN),
    "--pack",
    str(PACK),
    "--out",
    str(WORKBENCH / "artifacts"),
    "--scope",
    scope,
])
log(cov_out)
before_stats = summarize_manual_queue()
log(f"Queue summary before wave: {json.dumps(before_stats, ensure_ascii=False)}")

if not audit_only:
    for wave_idx in range(1, max_compiler_waves + 1):
        phase(f"Stage 2 (Wave {wave_idx}): jx-migrator-engineer Batch Transpiler Upgrade")
        mig_note = await asyncio.wait_for(
            agent(
                prompt=(
                    f"Target Scope: `{scope}` | Kind: `{kind}`\n"
                    f"Current Migration Queue Clusters:\n{json.dumps(before_stats, indent=2, ensure_ascii=False)}\n\n"
                    f"1. Pick the highest-ROI repeating AST/library pattern in `manual-migration-queue.json`.\n"
                    f"2. Write a Ground-Truth test in `JXReborn.ScriptMigrator.Tests` first, extend `JXReborn.ScriptMigrator` fail-closed lowering, "
                    f"regenerate scripts (`generate-scripts --kind {kind}`), compile `pack-tieudaotu` (`JXReborn.PackCompiler`), "
                    f"and confirm `verify-equivalence --strict` has 0 mismatches!"
                ),
                schema={
                    "type": "object",
                    "properties": {
                        "pattern_targeted": {"type": "string"},
                        "newly_migrated_script_count": {"type": "integer"},
                    },
                    "required": ["pattern_targeted", "newly_migrated_script_count"],
                },
                role=f"Migrator Wave {wave_idx}",
                type_name="jx-migrator-engineer",
            ),
            timeout=worker_timeout_sec,
        )
        log(f"Wave {wave_idx} completed: {mig_note}")

        phase(f"Stage 3 (Wave {wave_idx}): Mechanical Equivalence & Pack Gates")
        eq_code, eq_out = run_cmd([
            "dotnet",
            "run",
            "--project",
            str(MIGRATOR_CSPROJ),
            "--",
            "verify-equivalence",
            "--legacy",
            str(LEGACY_BIN),
            "--pack",
            str(PACK),
            "--out",
            str(WORKBENCH / "artifacts"),
            "--strict",
        ])
        if eq_code != 0:
            raise RuntimeError(f"Lua 4.0 vs C# Differential Equivalence Gate FAILED:\n{eq_out}")
        log(f"[Equivalence PASS] {eq_out}")

        mt_code, mt_out = run_cmd(["dotnet", "test", str(MIGRATOR_TESTS), "--nologo", "--verbosity", "minimal"])
        if mt_code != 0:
            raise RuntimeError(f"JXReborn.ScriptMigrator.Tests FAILED:\n{mt_out}")

        pc_code, pc_out = run_cmd(["dotnet", "run", "--project", str(PACK_COMPILER), "--", "--pack", str(PACK)])
        if pc_code != 0:
            raise RuntimeError(f"PackCompiler FAILED:\n{pc_out}")

        pt_code, pt_out = run_cmd(["dotnet", "test", str(PACK_TESTS), "--nologo", "--verbosity", "minimal"])
        if pt_code != 0:
            raise RuntimeError(f"JXReborn.Packs.TieuDaoTu.Tests FAILED:\n{pt_out}")

        run_cmd([
            "dotnet",
            "run",
            "--project",
            str(MIGRATOR_CSPROJ),
            "--",
            "audit-coverage",
            "--legacy",
            str(LEGACY_BIN),
            "--pack",
            str(PACK),
            "--out",
            str(WORKBENCH / "artifacts"),
            "--scope",
            scope,
        ])
        before_stats = summarize_manual_queue()
        log(f"Queue summary after Wave {wave_idx}: {json.dumps(before_stats, ensure_ascii=False)}")

run_cmd([PY, str(LEDGER_PY), "sync"])
print(json.dumps(before_stats, indent=2, ensure_ascii=False))
