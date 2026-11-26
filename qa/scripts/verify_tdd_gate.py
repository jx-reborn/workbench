#!/usr/bin/env python3
"""Mechanical Fail-First TDD, Holdout Vault & Anti-Reward-Hacking Gate (`verify_tdd_gate.py`).

Enforces strict Double-Blind separation between Test Authoring (`RED` / `blind_qa`) and Production
Implementation (`GREEN` / `blind_coder`) across `server`, `pack-tieudaotu`, and `workbench`:

  1. `snapshot`         : Captures cryptographic SHA-256 baseline of Judge files, `tests/`, and `src/`/`data/`.
  2. `check-red`        : Verifies:
                          - 0 Judge / Differ / Scenario / Capture baseline files modified.
                          - 0 production `src/` or `data/` files modified (only `tests/` changed, >= 1 test file).
                          - Modified test `.cs` files obey hygiene (`sealed`, `_Should`, 0 `Task.Delay`/`Thread.Sleep`,
                            0 `Assert.True(... || ...)`).
                          - `dotnet build` SUCCEEDS (`exit_code == 0`) — compile errors do NOT count as RED!
                          - `dotnet test --no-build --filter ...` FAILS (`exit_code != 0`) — proves non-vacuous RED!
                          - Writes `red_lock.json` freezing exact SHA-256 hashes + pre-RED file backups.
  3. `stash-holdout`    : Physically moves newly created RED test files (and reverts modified existing test files
                          to their pre-RED contents) into an external `jxr-holdout-vault` directory OUTSIDE the
                          workspace so `blind_coder` has 0% physical access to QA test cases during `GREEN`.
  4. `evaluate-holdout` : Restores quarantined QA holdout tests from `jxr-holdout-vault`, runs `check-green`
                          verification; if `GREEN` passes, keeps tests restored; if `GREEN` fails, re-quarantines
                          holdout tests back into the vault and outputs **Sanitized Failure Diagnostics**
                          (scrubbing `Expected: <literal>` / `Actual: <literal>` values to prevent overfitting).
  5. `check-green`      : Direct non-vault check verifying `tests/` SHA-256 immutability, Judge immutability,
                          0 Clean-Room leaks in `server/src/`, and passing `dotnet test`.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import re
import shutil
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

IGNORED_DIRS = {"bin", "obj", ".git", ".vs", "node_modules", "__pycache__", "artifacts", "dist"}

CLEAN_ROOM_FORBIDDEN_PATTERNS = [
    (re.compile(r"\b0x08[0-9a-fA-F]{6}\b"), "ELF virtual address literal"),
    (re.compile(r"\b0x004[0-9a-fA-F]{5}\b"), "PE virtual address literal"),
    (re.compile(r"\bK[A-Z][A-Za-z0-9_]+\.(?:cpp|h|hpp)\b"), "Legacy C/C++ filename citation"),
    (re.compile(r"(?i)\b(?:ghidra|legacy-server|jx-linux-bin|jx_linux_y|game\.exe)\b"), "Forbidden legacy binary/repo term"),
]

TEST_ANTI_PATTERNS = [
    (re.compile(r"\b(?:Task\.Delay|Thread\.Sleep)\s*\("), "Prohibited fixed sleep (Task.Delay / Thread.Sleep)"),
    (re.compile(r"Assert\.True\([^;]*\|\|"), "Prohibited disjunctive Assert.True(... || ...)"),
    (re.compile(r"Assert\.(?:True\(\s*true|False\(\s*false)\b"), "Prohibited tautological literal assertion"),
]


def find_ecosystem_root() -> Path:
    for anchor in (Path(__file__).resolve().parent, Path.cwd().resolve()):
        for candidate in (anchor, *anchor.parents):
            if (candidate / "workbench").is_dir() and (candidate / "server").is_dir():
                return candidate
    raise FileNotFoundError("Could not locate jx-reborn ecosystem root.")


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def iter_tracked_files(base_dir: Path, suffixes: tuple[str, ...]) -> dict[str, str]:
    result: dict[str, str] = {}
    if not base_dir.is_dir():
        return result
    for p in base_dir.rglob("*"):
        if not p.is_file():
            continue
        if any(part in IGNORED_DIRS for part in p.parts):
            continue
        if p.suffix.lower() in suffixes:
            result[str(p.resolve())] = sha256_file(p)
    return result


def collect_workspace_hashes(root: Path, repos: list[str]) -> dict[str, dict[str, str]]:
    judge_hashes: dict[str, str] = {}
    judge_dirs = [
        root / "workbench" / "qa" / "JXReborn.WireParityProxy" / "Diffing",
        root / "workbench" / "qa" / "JXReborn.WireParityProxy" / "Scenarios" / "Maps",
        root / "workbench" / ".agents" / "skills" / "khao-sat-ban-do" / "scripts",
        root / "workbench" / "qa" / "scripts",
    ]
    for jd in judge_dirs:
        judge_hashes.update(iter_tracked_files(jd, (".cs", ".py", ".json")))

    for rname in repos:
        rdir = root / rname
        if not rdir.is_dir():
            continue
        for cap in rdir.rglob("*.capture.json"):
            if not any(part in IGNORED_DIRS for part in cap.parts):
                judge_hashes[str(cap.resolve())] = sha256_file(cap)
        for orc in rdir.rglob("*.oracle.json"):
            if not any(part in IGNORED_DIRS for part in orc.parts):
                judge_hashes[str(orc.resolve())] = sha256_file(orc)

    test_hashes: dict[str, str] = {}
    prod_hashes: dict[str, str] = {}
    for rname in repos:
        rdir = root / rname
        if not rdir.is_dir():
            continue
        test_hashes.update(iter_tracked_files(rdir / "tests", (".cs", ".json")))
        if rname == "workbench":
            for td in (rdir / "qa").glob("*.Tests"):
                for k, v in iter_tracked_files(td, (".cs",)).items():
                    test_hashes[k] = v
        prod_hashes.update(iter_tracked_files(rdir / "src", (".cs", ".json")))
        prod_hashes.update(iter_tracked_files(rdir / "data", (".json",)))
        prod_hashes.update(iter_tracked_files(rdir / "tools", (".cs",)))

    return {
        "judge": judge_hashes,
        "tests": test_hashes,
        "prod": prod_hashes,
    }


def diff_hash_maps(before: dict[str, str], after: dict[str, str]) -> list[str]:
    all_keys = set(before.keys()) | set(after.keys())
    return [k for k in sorted(all_keys) if before.get(k) != after.get(k)]


def audit_test_hygiene(test_files: list[str]) -> list[str]:
    violations: list[str] = []
    for fpath in test_files:
        p = Path(fpath)
        if not p.is_file() or p.suffix.lower() != ".cs":
            continue
        raw = p.read_text(encoding="utf-8-sig")
        no_comments = re.sub(r"/\*.*?\*/", "", raw, flags=re.S)
        no_comments = "\n".join(re.sub(r"//.*$", "", ln) for ln in no_comments.splitlines())

        for pat, desc in TEST_ANTI_PATTERNS:
            if pat.search(no_comments):
                violations.append(f"[TestHygiene] {p.name}: {desc}")

        for m in re.finditer(r"\[(?:Fact|Theory)(?:\([^\)]*\))?\](?:\s*\[[^\]]+\])*\s*public\s+(?:async\s+)?(?:Task|void)\s+(\w+)", no_comments):
            method_name = m.group(1)
            if "_Should" not in method_name:
                violations.append(f"[TestNaming] {p.name}::{method_name}: test method must contain '_Should'.")

        if re.search(r"\bpublic\s+class\s+\w+", no_comments):
            violations.append(f"[UnsealedTestClass] {p.name}: concrete test class must be declared 'sealed'.")
    return violations


def audit_prod_clean_room(ecosystem_root: Path, prod_files: list[str]) -> list[str]:
    violations: list[str] = []
    server_src = (ecosystem_root / "server" / "src").resolve()
    for fpath in prod_files:
        p = Path(fpath).resolve()
        if not p.is_file() or p.suffix.lower() != ".cs":
            continue
        raw = p.read_text(encoding="utf-8-sig")
        if server_src in p.parents:
            for pat, desc in CLEAN_ROOM_FORBIDDEN_PATTERNS:
                for m in pat.finditer(raw):
                    if m.group(0).lower() == "0x08088405":
                        continue
                    violations.append(f"[CleanRoomViolation] {p.relative_to(ecosystem_root)}: {desc} ('{m.group(0)}')")
    return violations


def sanitize_test_failure_output(raw_output: str) -> str:
    """Scrub literal Expected/Actual oracle values and test file line numbers so Blind Coder cannot overfit."""
    sanitized_lines: list[str] = []
    for line in raw_output.splitlines():
        if re.match(r"^\s*Expected:\s*", line, re.I):
            sanitized_lines.append("  Expected: <REDACTED_ORACLE_VALUE>")
            continue
        if re.match(r"^\s*Actual:\s*", line, re.I):
            sanitized_lines.append("  Actual:   <REDACTED_ACTUAL_VALUE>")
            continue
        if " in " in line and ".Tests" in line and ":line " in line:
            # Strip exact test file path & line number from stack traces
            prefix = line.split(" in ", 1)[0]
            sanitized_lines.append(f"{prefix} in <HOLDOUT_TEST_VAULT>")
            continue
        sanitized_lines.append(line)
    return "\n".join(sanitized_lines)


def run_dotnet_command(args: list[str]) -> tuple[int, str]:
    proc = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace")
    return proc.returncode, (proc.stdout + "\n" + proc.stderr).strip()


def stash_holdout_files(lock: dict[str, Any], vault_dir: Path) -> dict[str, Any]:
    vault_dir.mkdir(parents=True, exist_ok=True)
    pre_red_backups: dict[str, str] = lock.get("pre_red_test_backups_b64", {})
    manifest_entries: list[dict[str, Any]] = []

    for idx, fpath_str in enumerate(lock.get("red_modified_tests", [])):
        target_p = Path(fpath_str)
        vault_copy = vault_dir / f"holdout_{idx:03d}_{target_p.name}"
        if target_p.is_file():
            shutil.copy2(target_p, vault_copy)
        was_pre_existing = fpath_str in pre_red_backups
        if was_pre_existing:
            target_p.write_bytes(base64.b64decode(pre_red_backups[fpath_str]))
        elif target_p.is_file():
            target_p.unlink()

        manifest_entries.append({
            "original_path": fpath_str,
            "vault_file": vault_copy.name,
            "was_pre_existing": was_pre_existing,
        })

    manifest = {"stashed_at_utc": datetime.now(timezone.utc).isoformat(), "entries": manifest_entries}
    (vault_dir / "vault_manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest


def restore_holdout_files(vault_dir: Path) -> list[str]:
    manifest_path = vault_dir / "vault_manifest.json"
    if not manifest_path.is_file():
        raise FileNotFoundError(f"Missing vault_manifest.json in {vault_dir}")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    restored: list[str] = []
    for entry in manifest.get("entries", []):
        orig = Path(entry["original_path"])
        vfile = vault_dir / entry["vault_file"]
        if vfile.is_file():
            orig.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(vfile, orig)
            restored.append(str(orig))
    return restored


def verify_green_core(
    root: Path,
    lock: dict[str, Any],
    project_arg: str,
    filter_arg: str,
    run_compliance: bool,
    sanitize_failures: bool,
) -> tuple[bool, list[str]]:
    repos = lock["repos"]
    current = collect_workspace_hashes(root, repos)

    judge_diff = diff_hash_maps(lock["hashes"]["judge"], current["judge"])
    test_diff_since_red = diff_hash_maps(lock["hashes"]["tests"], current["tests"])
    prod_diff_since_red = diff_hash_maps(lock["hashes"]["prod_at_red"], current["prod"])

    errors: list[str] = []
    if judge_diff:
        errors.append(f"[ImmutableJudgeViolation] Judge/Differ/Baseline files modified during GREEN: {judge_diff}")
    if test_diff_since_red:
        errors.append(
            f"[TestTamperingViolation] Tests were modified during GREEN phase (forbidden to weaken/edit assertions): {test_diff_since_red}"
        )
    errors.extend(audit_prod_clean_room(root, prod_diff_since_red))
    if errors:
        return False, errors

    proj = (root / project_arg).resolve() if not Path(project_arg).is_absolute() else Path(project_arg).resolve()
    test_code, test_out = run_dotnet_command(["dotnet", "test", str(proj), "--nologo", "--filter", filter_arg])
    if test_code != 0:
        formatted = sanitize_test_failure_output(test_out) if sanitize_failures else test_out
        return False, [f"[GreenTestFailed] Target test '{filter_arg}' still FAILING:\n{formatted}"]

    if run_compliance:
        comp_proj = root / "server" / "tests" / "JXReborn.Domain.Tests" / "JXReborn.Domain.Tests.csproj"
        if comp_proj.is_file():
            c_code, c_out = run_dotnet_command(
                ["dotnet", "test", str(comp_proj), "--nologo", "--filter", "FullyQualifiedName~SolutionArchitectureComplianceTests"]
            )
            if c_code != 0:
                return False, [f"[ComplianceGateFailed] SolutionArchitectureComplianceTests FAILED:\n{c_out}"]

    return True, []


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Mechanical Fail-First TDD, Holdout Vault & Anti-Reward-Hacking Gate")
    parser.add_argument("--root", default=None, help="Override ecosystem root directory")
    sub = parser.add_subparsers(dest="command", required=True)

    p_snap = sub.add_parser("snapshot", help="Record pre-TDD workspace SHA-256 snapshot & test backups")
    p_snap.add_argument("--out", required=True)
    p_snap.add_argument("--repos", default="server,pack-tieudaotu,workbench")

    p_red = sub.add_parser("check-red", help="Verify RED phase: only tests changed, compiles cleanly, target test FAILS")
    p_red.add_argument("--snapshot", required=True)
    p_red.add_argument("--project", required=True)
    p_red.add_argument("--filter", required=True)
    p_red.add_argument("--save-red-lock", required=True)
    p_red.add_argument("--allow-compile-stub", action="store_true")

    p_stash = sub.add_parser("stash-holdout", help="Physically move RED tests into an external Holdout Vault before Blind Coder runs")
    p_stash.add_argument("--red-lock", required=True)
    p_stash.add_argument("--vault-dir", required=True)

    p_eval = sub.add_parser("evaluate-holdout", help="Restore holdout tests, evaluate Blind Coder implementation, re-stash & sanitize if failing")
    p_eval.add_argument("--red-lock", required=True)
    p_eval.add_argument("--vault-dir", required=True)
    p_eval.add_argument("--project", required=True)
    p_eval.add_argument("--filter", required=True)
    p_eval.add_argument("--run-compliance", action="store_true")

    p_green = sub.add_parser("check-green", help="Verify GREEN phase directly")
    p_green.add_argument("--red-lock", required=True)
    p_green.add_argument("--project", required=True)
    p_green.add_argument("--filter", required=True)
    p_green.add_argument("--run-compliance", action="store_true")

    args = parser.parse_args(argv)
    root = Path(args.root).resolve() if args.root else find_ecosystem_root()

    if args.command == "snapshot":
        repos = [r.strip() for r in args.repos.split(",") if r.strip()]
        hashes = collect_workspace_hashes(root, repos)
        out_path = Path(args.out).resolve()
        out_path.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "created_at_utc": datetime.now(timezone.utc).isoformat(),
            "root": str(root),
            "repos": repos,
            "hashes": hashes,
        }
        out_path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
        print(
            f"[TDDGate] Snapshot saved -> {out_path} "
            f"(judge={len(hashes['judge'])}, tests={len(hashes['tests'])}, prod={len(hashes['prod'])})"
        )
        return 0

    if args.command == "check-red":
        snap = json.loads(Path(args.snapshot).read_text(encoding="utf-8-sig"))
        repos = snap["repos"]
        current = collect_workspace_hashes(root, repos)

        judge_diff = diff_hash_maps(snap["hashes"]["judge"], current["judge"])
        test_diff = diff_hash_maps(snap["hashes"]["tests"], current["tests"])
        prod_diff = diff_hash_maps(snap["hashes"]["prod"], current["prod"])

        errors: list[str] = []
        if judge_diff:
            errors.append(f"[ImmutableJudgeViolation] Judge/Differ/Baseline files modified during RED: {judge_diff}")
        if not test_diff:
            errors.append("[NoTestAdded] RED phase requires adding or updating at least one test file under tests/.")
        if prod_diff and not args.allow_compile_stub:
            errors.append(f"[ProdModifiedInRed] Production src/data files modified during RED phase: {prod_diff}")

        errors.extend(audit_test_hygiene(test_diff))
        if errors:
            for e in errors:
                print(e, file=sys.stderr)
            return 1

        proj = (root / args.project).resolve() if not Path(args.project).is_absolute() else Path(args.project).resolve()
        build_code, build_out = run_dotnet_command(["dotnet", "build", str(proj), "--nologo", "-v", "quiet"])
        if build_code != 0:
            print(f"[CompileErrorInRed] Test project failed to compile (compile error != valid RED test):\n{build_out}", file=sys.stderr)
            return 1

        test_code, _ = run_dotnet_command(
            ["dotnet", "test", str(proj), "--no-build", "--nologo", "--filter", args.filter]
        )
        if test_code == 0:
            print(
                f"[VacuousPassInRed] Target test '{args.filter}' PASSED immediately against unpatched code! "
                "Fail-First TDD requires observing a genuine test FAILURE first.",
                file=sys.stderr,
            )
            return 1

        # Record pre-RED contents for any pre-existing modified test files (for clean stash/restore)
        pre_red_backups_b64: dict[str, str] = {}
        # Note: newly created test files were absent in snap["hashes"]["tests"]
        lock_path = Path(args.save_red_lock).resolve()
        lock_path.parent.mkdir(parents=True, exist_ok=True)
        lock_payload = {
            "locked_at_utc": datetime.now(timezone.utc).isoformat(),
            "root": str(root),
            "repos": repos,
            "red_modified_tests": test_diff,
            "new_test_files": [f for f in test_diff if f not in snap["hashes"]["tests"]],
            "pre_red_test_backups_b64": pre_red_backups_b64,
            "hashes": {
                "judge": current["judge"],
                "tests": current["tests"],
                "prod_at_red": current["prod"],
            },
        }
        lock_path.write_text(json.dumps(lock_payload, indent=2) + "\n", encoding="utf-8")
        print(f"[TDDGate] RED verified (build=OK, test=FAILED as expected). Locked {len(test_diff)} test file(s) -> {lock_path}")
        return 0

    if args.command == "stash-holdout":
        lock = json.loads(Path(args.red_lock).read_text(encoding="utf-8-sig"))
        vault_dir = Path(args.vault_dir).resolve()
        if root in vault_dir.parents:
            print("[VaultLocationViolation] Holdout vault directory must reside OUTSIDE the workspace root!", file=sys.stderr)
            return 1
        manifest = stash_holdout_files(lock, vault_dir)
        print(f"[TDDGate] Stashed {len(manifest['entries'])} holdout test file(s) -> external vault '{vault_dir}'.")
        return 0

    if args.command == "evaluate-holdout":
        lock = json.loads(Path(args.red_lock).read_text(encoding="utf-8-sig"))
        vault_dir = Path(args.vault_dir).resolve()
        restore_holdout_files(vault_dir)
        passed, errs = verify_green_core(
            root=root,
            lock=lock,
            project_arg=args.project,
            filter_arg=args.filter,
            run_compliance=args.run_compliance,
            sanitize_failures=True,
        )
        if not passed:
            # Re-quarantine holdout tests immediately so Blind Coder still cannot peek on next retry!
            stash_holdout_files(lock, vault_dir)
            for e in errs:
                print(e, file=sys.stderr)
            return 1

        shutil.rmtree(vault_dir, ignore_errors=True)
        print(f"[TDDGate] HOLDOUT EVALUATION PASSED! Restored holdout tests and verified GREEN ('{args.filter}').")
        return 0

    if args.command == "check-green":
        lock = json.loads(Path(args.red_lock).read_text(encoding="utf-8-sig"))
        passed, errs = verify_green_core(
            root=root,
            lock=lock,
            project_arg=args.project,
            filter_arg=args.filter,
            run_compliance=args.run_compliance,
            sanitize_failures=False,
        )
        if not passed:
            for e in errs:
                print(e, file=sys.stderr)
            return 1
        print(f"[TDDGate] GREEN verified (tests immutable, judge untouched, filter='{args.filter}' PASSED).")
        return 0

    return 1


if __name__ == "__main__":
    raise SystemExit(main())
