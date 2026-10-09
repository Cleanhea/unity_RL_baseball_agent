"""Attach to the existing preparation/stage-2 chain without changing its training.

After that exact process exits successfully, verify the completed preparation
and both stage-2 models before starting a separate six-frame stage-3 run.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import sys
import threading

import psutil
import yaml

ROOT = Path(__file__).resolve().parents[1]
DUEL = {"BaseballBatter", "BaseballPitcher"}
ALL = DUEL | {"BaseballRunner", "BaseballFielder"}


def load_options(config):
    from mlagents.trainers.learn import parse_command_line
    return parse_command_line([str(config), "--run-id=continuation_validation"])


def validate_stage3_config(config, stage2_config):
    stage3, stage2 = load_options(config), load_options(stage2_config)
    from mlagents.trainers.settings import ConstantSettings
    if set(stage3.behaviors) != ALL or set(stage2.behaviors) != DUEL:
        raise ValueError("Expected two stage-2 and four stage-3 behaviors")
    prepared = stage3.environment_parameters.get("batter_prepared")
    if (prepared is None or len(prepared.curriculum) != 1
            or not isinstance(prepared.curriculum[0].value, ConstantSettings)
            or prepared.curriculum[0].value.value != 1):
        raise ValueError("Stage 3 must retain constant batter_prepared=1")
    if "batter_skill" in stage3.environment_parameters or "batter_preparation" in stage3.environment_parameters:
        raise ValueError("Stage 3 cannot apply a stage-1 preparation lesson")
    for behavior in DUEL:
        current, previous = stage3.behaviors[behavior], stage2.behaviors[behavior]
        if (current.trainer_type != "ppo" or current.self_play is None or previous.self_play is None
                or current.network_settings != previous.network_settings):
            raise ValueError(f"Incompatible stage-2/3 policy settings for {behavior}")
    for behavior in ALL - DUEL:
        settings = stage3.behaviors[behavior]
        if settings.self_play is not None or settings.init_path is not None:
            raise ValueError("Runner and CF must start fresh without self-play")
    if stage3.behaviors["BaseballFielder"].trainer_type != "baseball_fielder_poca":
        raise ValueError("Stage 3 requires the registered CF direction-preserving trainer")
    return stage3, stage2


def checkpoint_step(saved):
    return int(saved["global_step"]["_GlobalSteps__global_step"].item())


def validate_finished(run, options, camera_stacks=6):
    """Require actual saved steps and final models, including after a clean Ctrl+C."""
    import torch
    from prepare_batter import validate_camera_checkpoint
    completed = {}
    for behavior, settings in options.behaviors.items():
        source, model = run / behavior / "checkpoint.pt", run / (behavior + ".onnx")
        if not source.is_file() or not model.is_file() or model.stat().st_size == 0:
            raise RuntimeError(f"Final checkpoint/model is missing for {behavior}: {run}")
        saved = (validate_camera_checkpoint(source, camera_stacks) if behavior == "BaseballBatter"
                 else torch.load(source, map_location="cpu", weights_only=False))
        step = checkpoint_step(saved)
        if step < settings.max_steps:
            raise RuntimeError(f"{behavior} stopped at {step:,}; require {settings.max_steps:,}")
        for group in ("Policy", "Optimizer:critic"):
            weights = saved.get(group)
            if not weights or any(not bool(torch.isfinite(v).all()) for v in weights.values()
                                  if isinstance(v, torch.Tensor)):
                raise ValueError(f"Missing/nonfinite {behavior} weights in {group}")
        completed[behavior] = {"checkpoint": str(source.resolve()), "step": step}
        del saved
    return completed


def resolve_stage3(args):
    """Use the final models from the specified completed run, never YAML's old defaults."""
    from prepare_batter import validate_prepared_stage2
    stage3, expected2 = validate_stage3_config(args.config, args.stage2_config)
    saved_config = args.stage2_run / "configuration.yaml"
    if not saved_config.is_file():
        raise RuntimeError("Stage 2 has not written its final run configuration")
    actual2 = load_options(saved_config)
    if set(actual2.behaviors) != DUEL:
        raise ValueError("The completed run is not batter/pitcher stage 2")
    # Check the recorded producer, not just filenames in a results directory.
    recorded = yaml.safe_load(saved_config.read_text(encoding="utf-8"))
    recorded_run = recorded.get("checkpoint_settings", {})
    if (recorded_run.get("run_id") != args.stage2_run.name
            or Path(recorded_run.get("results_dir", "")).resolve() != args.stage2_run.parent.resolve()):
        raise ValueError("Stage-2 configuration belongs to a different results run")
    prepared_source = (args.preparation_run / "BaseballBatter/checkpoint.pt").resolve()
    if Path(actual2.behaviors["BaseballBatter"].init_path or "").resolve() != prepared_source:
        raise ValueError("Stage 2 did not start from this chain's completed preparation")
    for behavior in DUEL:
        if (actual2.behaviors[behavior].max_steps != expected2.behaviors[behavior].max_steps
                or actual2.behaviors[behavior].network_settings != stage3.behaviors[behavior].network_settings):
            raise ValueError(f"Stage-2 limits/network differ from the planned run: {behavior}")
    camera_stacks = getattr(args, "camera_stacks", 6)
    validate_prepared_stage2(actual2, prepared_source, camera_stacks)
    completed = validate_finished(args.stage2_run, actual2, camera_stacks)
    data = yaml.safe_load(args.config.read_text(encoding="utf-8"))
    for behavior in DUEL:
        data["behaviors"][behavior]["init_path"] = completed[behavior]["checkpoint"]
    return data, completed


def attach_chain(pid, chain_run, camera_stacks=6):
    """Retain a Process identity/handle so PID reuse cannot satisfy the wait."""
    process = psutil.Process(pid)
    command = process.cmdline()
    if (not any(Path(arg).name == "prepare_batter.py" for arg in command)
            or "--continue-stage2" not in command
            or "--run-id=" + chain_run.name not in command):
        raise ValueError("Attach to the existing preparation -> stage-2 root process")
    if Path(process.cwd()).resolve() != ROOT:
        raise ValueError("The attached training chain runs in a different repository")
    results_arguments = [arg.split("=", 1)[1] for arg in command if arg.startswith("--results-dir=")]
    if len(results_arguments) != 1 or Path(results_arguments[0]).resolve() != chain_run.parent.resolve():
        raise ValueError("The attached training chain uses a different results directory")
    stack_arguments = [arg.split("=", 1)[1] for arg in command if arg.startswith("--camera-stacks=")]
    actual_stacks = int(stack_arguments[0]) if len(stack_arguments) == 1 else 6
    if len(stack_arguments) > 1 or actual_stacks != camera_stacks:
        raise ValueError("The attached preparation chain has a different camera stack")
    return process


def write_status(directory, phase, **details):
    data = {"phase": phase, "updated_at_utc": datetime.now(timezone.utc).isoformat(), **details}
    temporary = directory / "continuation-status.json.tmp"
    temporary.write_text(json.dumps(data, indent=2), encoding="utf-8")
    temporary.replace(directory / "continuation-status.json")


def wait_for_chain(process, args):
    # A single Windows wait retains the native process handle across heartbeats;
    # repeatedly opening a PID after timeouts would leave a PID-reuse race.
    finished = threading.Event()
    result = {}
    def wait_once():
        try:
            result["exit_code"] = process.wait()
        except BaseException as error:
            result["error"] = error
        finally:
            finished.set()
    created_at = process.create_time()
    threading.Thread(target=wait_once, daemon=True).start()
    while True:
        if (args.status_dir / "stop-requested").exists():
            write_status(args.status_dir, "cancelled", detail="Stage-3 continuation cancelled; training is untouched")
            return False
        write_status(args.status_dir, "waiting_chain", chain_pid=process.pid,
                     chain_created_at=created_at, chain_run=str(args.chain_run),
                     stage2_run=str(args.stage2_run), stage3_run_id=args.run_id)
        if not finished.wait(10):
            continue
        if "error" in result:
            raise result["error"]
        exit_code = result.get("exit_code")
        if exit_code != 0:
            raise RuntimeError(f"Attached chain did not exit successfully (code {exit_code})")
        return True


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--attach-chain-pid", type=int)
    mode.add_argument("--stage2-finished", action="store_true", help="Validate an already completed stage 2")
    parser.add_argument("--chain-run", type=Path, default=ROOT / "Training/results/stage1_batter_camera6_skills")
    parser.add_argument("--preparation-run", type=Path)
    parser.add_argument("--stage2-run", type=Path, default=ROOT / "Training/results/stage2_batter_pitcher_camera6_selfplay")
    parser.add_argument("--stage2-config", type=Path, default=ROOT / "Training/config/stage2_batter_pitcher_camera6_selfplay.yaml")
    parser.add_argument("--config", type=Path, default=ROOT / "Training/config/stage3_full_team_camera6_selfplay.yaml")
    parser.add_argument("--env", type=Path, default=ROOT / "Training/builds/Stage3_Camera6/Stage3_Camera6.exe")
    parser.add_argument("--run-id", default="stage3_full_team_camera6_selfplay")
    parser.add_argument("--results-dir", type=Path, default=ROOT / "Training/results")
    parser.add_argument("--status-dir", type=Path, default=ROOT / "Training/results/stage23_camera6_chain")
    parser.add_argument("--base-port", type=int, default=57364)
    parser.add_argument("--camera-stacks", type=int, choices=(6, 30), default=6)
    args = parser.parse_args(argv)
    args.chain_run = args.chain_run.resolve()
    args.preparation_run = (args.preparation_run or args.chain_run.with_name(args.chain_run.name + "_prepare")).resolve()
    args.stage2_run, args.results_dir, args.status_dir = args.stage2_run.resolve(), args.results_dir.resolve(), args.status_dir.resolve()
    if args.attach_chain_pid and args.stage2_run.parent != args.chain_run.parent:
        raise ValueError("The existing chain starts stage 2 in its own results directory")
    if Path(args.run_id).name != args.run_id or args.run_id in ("", ".", ".."):
        raise ValueError("Stage-3 run-id must be a single directory name")
    destination = args.results_dir / args.run_id
    if destination.exists():
        raise FileExistsError(f"Stage-3 results already exist; they will not be overwritten: {destination}")
    if not args.env.is_file():
        raise FileNotFoundError(f"Build the {args.camera_stacks}-frame stage-3 player first: {args.env}")
    validate_stage3_config(args.config, args.stage2_config)
    process = attach_chain(args.attach_chain_pid, args.chain_run, args.camera_stacks) if args.attach_chain_pid else None
    args.status_dir.mkdir(parents=True, exist_ok=True)
    lock = args.status_dir / "continuation.lock"
    with lock.open("x", encoding="utf-8") as stream:
        json.dump({"pid": os.getpid(), "created_at": psutil.Process().create_time()}, stream)
    try:
        if process is not None:
            print(f"Attached to chain PID {process.pid}; waiting for full preparation and stage 2. Training is unchanged.", flush=True)
            if not wait_for_chain(process, args):
                return 0
        if (args.status_dir / "stop-requested").exists():
            write_status(args.status_dir, "cancelled")
            return 0
        write_status(args.status_dir, "validating_stage2")
        data, completed = resolve_stage3(args)
        if destination.exists():
            raise FileExistsError("Stage-3 run appeared while waiting; refusing to overwrite it")
        config = args.status_dir / "stage3-resolved.yaml"
        config.write_text(yaml.safe_dump(data, sort_keys=False), encoding="utf-8")
        command = [sys.executable, "-u", "-B", str(ROOT / "Training/train.py"), str(config),
                   "--run-id=" + args.run_id, "--results-dir=" + str(args.results_dir),
                   "--env=" + str(args.env.resolve()), "--base-port=" + str(args.base_port),
                   "--env-args", "-batchmode"]
        write_status(args.status_dir, "training_stage3", stage2_models=completed, command=command)
        print("Stage 2 fully saved. Starting stage 3 from its batter/pitcher models.", flush=True)
        with (args.status_dir / "stage3-training.log").open("x", encoding="utf-8") as log:
            result = subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, check=False)
        if result.returncode:
            raise RuntimeError(f"Stage-3 trainer exited {result.returncode}; see stage3-training.log")
        validate_finished(destination, load_options(config), args.camera_stacks)
        write_status(args.status_dir, "complete", stage3_run=str(destination))
        return 0
    except BaseException as error:
        write_status(args.status_dir, "stopped", detail=str(error))
        raise
    finally:
        lock.unlink()


if __name__ == "__main__":
    raise SystemExit(main())
