"""Build separate Unity players and run the three ML-Agents stages in order.

The --attach-stage1-pid option continues an already running Editor-based stage 1
without touching that Editor. Run this script with the Python environment that
contains mlagents-learn, tensorboard and psutil.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time


ROOT = Path(__file__).resolve().parent.parent
TRAINING = ROOT / "Training"
RESULTS = TRAINING / "results"
BUILDS = TRAINING / "builds"
# A short path avoids Windows MAX_PATH failures inside Unity's PackageCache (Burst).
# resolve() expands an 8.3 TEMP such as C:\Users\MRHONG~1 to its long name: Unity opened
# through the 8.3 path cannot resolve project MonoScripts, so every scene script is missing
# in the player and mlagents-learn times out waiting for agents.
WORK = Path(tempfile.gettempdir()).resolve() / "baseball-curriculum-build"
NAMES = {1: "Stage1_Batter", 2: "Stage2_BatterPitcher", 3: "Stage3_FullTeam"}
RUN_IDS = {1: "stage1_batter", 2: "stage2_batter_pitcher", 3: "stage3_full_team"}
CONFIGS = {stage: TRAINING / "config" / (run_id + ".yaml") for stage, run_id in RUN_IDS.items()}
BEHAVIORS = {1: ("BaseballBatter",), 2: ("BaseballBatter", "BaseballPitcher"),
             3: ("BaseballBatter", "BaseballPitcher", "BaseballRunner", "BaseballFielder")}
# Self-play variants of stages 2-3 keep their own results so they never mix with simultaneous training.
SELF_PLAY_RUN_IDS = {2: "stage2_batter_pitcher_selfplay", 3: "stage3_full_team_selfplay"}


def use_self_play() -> None:
    """Switch stages 2-3 to the self-play configs and run ids. Stage 1 has no opponent and is shared."""
    RUN_IDS.update(SELF_PLAY_RUN_IDS)
    CONFIGS.update({stage: TRAINING / "config" / (run_id + ".yaml") for stage, run_id in SELF_PLAY_RUN_IDS.items()})


def say(message: str) -> None:
    print(f"[{dt.datetime.now().astimezone().isoformat(timespec='seconds')}] {message}", flush=True)


def status(phase: str, detail: str = "") -> None:
    RESULTS.mkdir(parents=True, exist_ok=True)
    destination = RESULTS / "curriculum-status.json"
    temporary = destination.with_suffix(".json.tmp")
    temporary.write_text(json.dumps({"phase": phase, "detail": detail,
                                     "updated": dt.datetime.now().astimezone().isoformat(timespec="seconds")},
                                    ensure_ascii=False, indent=2), encoding="utf-8")
    temporary.replace(destination)
    say(f"{phase}: {detail}")


def unity_editor() -> Path:
    version_file = ROOT / "ProjectSettings" / "ProjectVersion.txt"
    version = next(line.split(":", 1)[1].strip() for line in version_file.read_text(encoding="utf-8").splitlines()
                   if line.startswith("m_EditorVersion:"))
    path = Path(os.environ.get("PROGRAMFILES", r"C:\Program Files")) / "Unity" / "Hub" / "Editor" / version / "Editor" / "Unity.exe"
    if not path.is_file():
        raise FileNotFoundError(f"Unity Editor {version} is missing: {path}")
    return path


def player(stage: int) -> Path:
    name = NAMES[stage]
    return BUILDS / name / (name + ".exe")


def check_empty_runs(first_stage: int) -> None:
    for stage in range(first_stage, 4):
        run = RESULTS / RUN_IDS[stage]
        if run.exists() and any(run.iterdir()):
            raise FileExistsError(f"Run already has results; archive it before restarting: {run}")


def copy_project(destination: Path) -> None:
    for folder in ("Assets", "Packages", "ProjectSettings"):
        shutil.copytree(ROOT / folder, destination / folder)
    editor_folder = destination / "Assets" / "Editor"
    editor_folder.mkdir(exist_ok=True)
    shutil.copy2(TRAINING / "UnityBuild" / "Editor" / "CurriculumPlayerBuild.cs", editor_folder)


def build_players(first_stage: int) -> None:
    WORK.mkdir(parents=True, exist_ok=True)
    clone = WORK / f"project-{int(time.time())}-{os.getpid()}"
    clone.mkdir()
    try:
        status("building", f"Creating isolated Unity project at {clone}")
        copy_project(clone)
        BUILDS.mkdir(parents=True, exist_ok=True)
        import_log = RESULTS / "curriculum-import.log"
        import_command = [str(unity_editor()), "-batchmode", "-nographics", "-quit",
                          "-projectPath", str(clone), "-logFile", str(import_log)]
        say("Importing and compiling isolated Unity project; Unity log: " + str(import_log))
        imported = subprocess.run(import_command, cwd=ROOT, check=False)
        if imported.returncode:
            raise RuntimeError(f"Unity import exited {imported.returncode}; see {import_log}")
        log_path = RESULTS / "curriculum-build.log"
        command = [str(unity_editor()), "-batchmode", "-nographics", "-quit", "-buildTarget", "win64",
                   "-projectPath", str(clone), "-executeMethod", "CurriculumPlayerBuild.Build",
                   f"--curriculum-build-dir={BUILDS}", f"--curriculum-first-stage={first_stage}",
                   "-logFile", str(log_path)]
        say("Building stage players; Unity log: " + str(log_path))
        result = subprocess.run(command, cwd=ROOT, check=False)
        if result.returncode:
            raise RuntimeError(f"Unity build exited {result.returncode}; see {log_path}")
        for stage in range(first_stage, 4):
            if not player(stage).is_file():
                raise FileNotFoundError(f"Stage {stage} executable was not built: {player(stage)}")
    finally:
        # Only remove the clone that this invocation created inside the known work directory.
        if clone.resolve().parent != WORK.resolve():
            raise RuntimeError(f"Refusing to remove an unexpected build path: {clone}")
        shutil.rmtree(clone)


def latest_step(stage: int, behavior: str) -> int:
    from tensorboard.backend.event_processing import event_accumulator

    directory = RESULTS / RUN_IDS[stage] / behavior
    events = sorted(directory.glob("events.out.tfevents*"), key=lambda path: path.stat().st_mtime)
    if not events:
        raise FileNotFoundError(f"No TensorBoard events for {behavior}: {directory}")
    accumulator = event_accumulator.EventAccumulator(str(events[-1]))
    accumulator.Reload()
    tag = "Environment/Cumulative Reward"
    samples = accumulator.Scalars(tag)
    if not samples:
        raise RuntimeError(f"No {tag} summaries for {behavior}: {events[-1]}")
    return samples[-1].step


def check_stage_finished(stage: int) -> None:
    # The final model can also be written on interruption. Require progress near
    # the configured max_steps before continuing to the next stage.
    import yaml

    config = yaml.safe_load(CONFIGS[stage].read_text(encoding="utf-8"))
    for behavior in BEHAVIORS[stage]:
        directory = RESULTS / RUN_IDS[stage]
        checkpoint = directory / behavior / "checkpoint.pt"
        model = directory / (behavior + ".onnx")
        if not checkpoint.is_file() or not model.is_file():
            raise FileNotFoundError(f"Stage {stage} has no final {behavior} checkpoint/model: {directory}")
        settings = config["behaviors"][behavior]
        required = settings["max_steps"] - settings["summary_freq"]
        step = latest_step(stage, behavior)
        if step < required:
            raise RuntimeError(f"Stage {stage} {behavior} ended at {step:,} steps; expected at least {required:,}")
        say(f"Stage {stage} {behavior} finished at {step:,} steps")


def wait_for_existing_stage1(pid: int) -> None:
    import psutil

    try:
        process = psutil.Process(pid)
        command = " ".join(process.cmdline()).lower()
        if "mlagents-learn" not in process.name().lower() or "stage1_batter" not in command:
            raise ValueError(f"PID {pid} is not the stage 1 mlagents-learn process")
    except psutil.NoSuchProcess:
        # The run may have ended while players were building. Final artifacts
        # and recorded steps are checked below before starting stage 2.
        process = None
    status("waiting_stage1", f"Waiting for existing stage 1 trainer PID {pid}")
    if process is not None:
        while process.is_running() and process.status() != psutil.STATUS_ZOMBIE:
            time.sleep(5)
    check_stage_finished(1)


def check_initialize_from(run_id: str) -> None:
    """Stage 1 can start from an archived stage 1 run (same batter contract) instead of random weights."""
    for behavior in BEHAVIORS[1]:
        checkpoint = RESULTS / run_id / behavior / "checkpoint.pt"
        if not checkpoint.is_file():
            raise FileNotFoundError(f"No {behavior} checkpoint to initialize stage 1 from: {checkpoint}")


def run_stage(stage: int, trainer: Path, torch_device: str | None, initialize_from: str | None = None) -> None:
    if stage > 1:
        check_stage_finished(stage - 1)
    if not player(stage).is_file():
        raise FileNotFoundError(f"Missing Unity player: {player(stage)}")
    # The registered fielder trainer is selected by the Stage 3 YAML.
    command = [str(trainer), str(CONFIGS[stage]), f"--run-id={RUN_IDS[stage]}",
               f"--results-dir={RESULTS}", f"--env={player(stage)}",
               "--no-graphics", "--base-port=5010"]
    if initialize_from:
        command.append(f"--initialize-from={initialize_from}")
    if torch_device:
        command.append(f"--torch-device={torch_device}")
    status(f"training_stage{stage}", " ".join(command))
    log_path = RESULTS / f"{RUN_IDS[stage]}.log"
    with log_path.open("w", encoding="utf-8") as log:
        result = subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, check=False)
    if result.returncode:
        raise RuntimeError(f"Stage {stage} trainer exited {result.returncode}; see {log_path}")
    check_stage_finished(stage)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    start = parser.add_mutually_exclusive_group()
    start.add_argument("--attach-stage1-pid", type=int, help="Wait for an existing Editor stage 1 run")
    start.add_argument("--start-stage", type=int, choices=(1, 2, 3), default=1,
                       help="Resume from a stage after checking the preceding stage is complete")
    parser.add_argument("--skip-build", action="store_true", help="Use previously built players")
    parser.add_argument("--build-only", action="store_true", help="Build players but do not train")
    parser.add_argument("--torch-device", help="Optional ML-Agents torch device, e.g. cpu or cuda")
    parser.add_argument("--self-play", action="store_true",
                        help="Train stages 2-3 with ML-Agents self-play (config/*_selfplay.yaml, separate run ids)")
    parser.add_argument("--stage1-initialize-from", metavar="RUN_ID",
                        help="Start stage 1 from the weights of an archived run in Training/results (mlagents --initialize-from)")
    args = parser.parse_args()
    if args.self_play:
        use_self_play()
    first_stage = 2 if args.attach_stage1_pid else args.start_stage
    if args.stage1_initialize_from and first_stage != 1:
        parser.error("--stage1-initialize-from only applies when stage 1 is trained by this script")

    try:
        if not args.build_only:
            check_empty_runs(first_stage)
            if first_stage > 1 and not args.attach_stage1_pid:
                check_stage_finished(first_stage - 1)
            if args.stage1_initialize_from:
                check_initialize_from(args.stage1_initialize_from)
        trainer = Path(sys.executable).parent / "Scripts" / "mlagents-learn.exe"
        if not trainer.is_file() and not args.build_only:
            raise FileNotFoundError(f"Run this with the mlagents Python environment: {trainer}")
        if not args.skip_build:
            build_players(first_stage)
        else:
            for stage in range(first_stage, 4):
                if not player(stage).is_file():
                    raise FileNotFoundError(f"Missing Unity player: {player(stage)}")
        if args.build_only:
            status("players_ready", f"Stages {first_stage}-3 built in {BUILDS}")
            return 0
        if args.attach_stage1_pid:
            wait_for_existing_stage1(args.attach_stage1_pid)
        for stage in range(first_stage, 4):
            run_stage(stage, trainer, args.torch_device, args.stage1_initialize_from if stage == 1 else None)
        status("complete", "All three stages finished")
        return 0
    except Exception as error:
        status("failed", str(error))
        raise


if __name__ == "__main__":
    raise SystemExit(main())
