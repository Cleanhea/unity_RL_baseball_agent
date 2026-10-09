"""Train the stage-1 camera batter through a measured transition to stage 2.

Uses the installed ML-Agents training loop and environment-parameter channel.
Only this entry point installs the preparation parameter manager; ordinary
mlagents-learn runs are unaffected. No reward-based lesson advancement.
"""
from __future__ import annotations

import json
import math
from pathlib import Path
import sys

from mlagents.trainers.environment_parameter_manager import EnvironmentParameterManager
from mlagents.trainers.settings import ConstantSettings
from mlagents.trainers.stats import StatsWriter, StatsReporter

PHASE_NAMES = [f"SwingGate_{max(0, 300 - 50 * i)}ms" for i in range(7)] + [
    "Controls_62_5", "Controls_75", "Controls_87_5", "Controls_100",
    "Location_Medium", "Location_Full", "PitchMix_25", "PitchMix_50", "PitchMix_Full",
]
SKILL_PHASE_NAMES = ["Timing", "Height_25", "Height_50", "Height_100", "Angles_25",
                     "Angles_50", "Angles_100", "Position_25", "Position_50", "Position_100"]


class PreparationProgress:
    def __init__(self, path: Path | None = None, minimum_phase_steps=30000, phase_names=None):
        self.phase_names = list(PHASE_NAMES if phase_names is None else phase_names)
        self.path = path
        self.phase = 0
        self.started_at = 0
        self.windows = []
        self.history = [{"phase": 0, "start_step": 0}]
        self.ready = False
        self.latest = {}
        self.minimum_phase_steps = minimum_phase_steps

    def restore(self, checkpoint_step: int):
        data = json.loads(self.path.read_text(encoding="utf-8"))
        if data.get("version") != 1:
            raise ValueError("Unsupported preparation status version")
        if not 0 <= data["phase"] < len(self.phase_names) or data["name"] != self.phase_names[data["phase"]]:
            raise ValueError("Resume status belongs to a different preparation plan")
        history = [h for h in data["history"] if h["start_step"] <= checkpoint_step]
        if not history:
            raise ValueError("No preparation phase matches the saved checkpoint")
        self.history = history
        self.phase = history[-1]["phase"]
        self.started_at = history[-1]["start_step"]
        # Re-earn the 3 windows after resuming; status may be ahead of the checkpoint.
        self.windows = []
        self.ready = False

    def save(self):
        if self.path is None:
            return
        self.path.parent.mkdir(parents=True, exist_ok=True)
        data = {"version": 1, "phase": self.phase, "name": self.phase_names[self.phase],
                "ready": self.ready, "history": self.history, "latest": self.latest,
                "passing_windows": len(self.windows), "minimum_phase_steps": self.minimum_phase_steps}
        temporary = self.path.with_suffix(".json.tmp")
        temporary.write_text(json.dumps(data, indent=2), encoding="utf-8")
        temporary.replace(self.path)

    def observe(self, step: int, phase_samples, count: int, fair: float, qualified: float,
                zone_swing: float, chase: float | None):
        self.latest = {"step": step, "plate_appearances": count, "fair_contact": fair,
                       "qualified_hit": qualified, "zone_swing": zone_swing, "chase": chase}
        valid = (phase_samples and all(p == self.phase for p in phase_samples) and count >= 50
                 and all(math.isfinite(v) for v in (fair, qualified, zone_swing))
                 and (chase is None or math.isfinite(chase)))
        # Initial experiment thresholds, not claims of achieved batting skill.
        fair_floor, quality_floor = (0.65, 0.08) if self.phase < 11 else (0.40, 0.05) if self.phase < 13 else (0.30, 0.05)
        discipline = self.phase < 11 or (zone_swing >= 0.40 and chase is not None and chase <= 0.35)
        passed = valid and fair >= fair_floor and qualified >= quality_floor and discipline
        if not passed:
            self.windows.clear()
        else:
            self.windows.append(step)
            self.windows = self.windows[-3:]
        if len(self.windows) == 3 and step - self.started_at >= self.minimum_phase_steps:
            if self.phase == len(self.phase_names) - 1:
                self.ready = True
                print(f"PREPARATION PLAN READY at {step:,} steps: {self.phase_names[-1]}", flush=True)
            else:
                self.phase += 1
                self.started_at = step
                self.history.append({"phase": self.phase, "start_step": step})
                self.windows.clear()
                print(f"PREPARATION -> {self.phase}: {self.phase_names[self.phase]} at {step:,} steps", flush=True)
        self.save()


class PreparationWriter(StatsWriter):
    def __init__(self, progress, phase_key="Batter Preparation/Phase"):
        self.progress = progress
        self.phase_key = phase_key

    def write_stats(self, category, values, step):
        if category != "BaseballBatter":
            return
        if self.phase_key not in values:
            if self.phase_key == "Batter Skills/Phase" and "Batter Preparation/Phase" in values:
                raise RuntimeError("This player does not publish skill phases. Use the rebuilt BatterSkillsCamera6 player")
            return
        required = ["Batter Preparation/Fair Contact", "Batter Preparation/Qualified Hit", "Plate Discipline/Zone Swing Rate"]
        if any(key not in values for key in required):
            self.progress.windows.clear()
            return
        fair, quality, zone = (values[key] for key in required)
        chase = values.get("Plate Discipline/Chase Rate")
        self.progress.observe(step, values[self.phase_key].full_dist,
                              quality.num, float(fair.mean), float(quality.mean), float(zone.mean),
                              None if chase is None else float(chase.mean))


def parameter_manager(progress, skills=False):
    class PreparationParameterManager(EnvironmentParameterManager):
        def __init__(self, *args, **kwargs):
            super().__init__(*args, **kwargs)
            if "batter_preparation" not in self._dict_settings:
                raise ValueError("Preparation requires the batter_preparation environment parameter")
            self.sent_phase = progress.phase
            # The stock learner has now validated/created its output directory.
            # Save before the first summary, without making a fresh run look old.
            progress.save()

        def get_current_samplers(self):
            values = super().get_current_samplers()
            values["batter_preparation"] = ConstantSettings(value=0.0 if skills else float(progress.phase))
            if skills:
                values["batter_skill"] = ConstantSettings(value=float(progress.phase))
            return values

        def get_current_lesson_number(self):
            values = super().get_current_lesson_number()
            values["batter_preparation"] = 0 if skills else progress.phase
            if skills:
                values["batter_skill"] = progress.phase
            return values

        def update_lessons(self, *args, **kwargs):
            if progress.ready or (progress.path is not None and progress.path.with_name("stop-requested").exists()):
                # The stock controller catches this and saves both checkpoint and ONNX.
                raise KeyboardInterrupt
            changed = self.sent_phase != progress.phase
            self.sent_phase = progress.phase
            return changed, False
    return PreparationParameterManager


def validate_camera_checkpoint(source, expected_stacks=6):
    import torch
    saved = torch.load(source, map_location="cpu", weights_only=False)
    for group in ("Policy", "Optimizer:critic"):
        weights = [v for k, v in saved[group].items() if k.endswith("conv_layers.0.weight")]
        if not weights or any(tuple(w.shape) != (16, expected_stacks, 8, 8) for w in weights):
            raise ValueError(f"This run requires a {expected_stacks}-frame checkpoint. Expand into a new initialization file; do not resume a different-stack run")
    return saved


def validate_prepared_stage2(options, checkpoint_override=None, expected_stacks=6):
    if set(options.behaviors) != {"BaseballBatter", "BaseballPitcher"} or any(b.self_play is None for b in options.behaviors.values()):
        raise ValueError("Prepared stage 2 requires batter/pitcher self-play")
    prepared = options.environment_parameters.get("batter_prepared")
    if prepared is None or len(prepared.curriculum) != 1 or not isinstance(prepared.curriculum[0].value, ConstantSettings) or prepared.curriculum[0].value.value != 1:
        raise ValueError("Prepared stage 2 requires constant batter_prepared=1 to preserve the trained controls and rewards")
    if options.checkpoint_settings.resume:
        raise ValueError("Use a resume config without init_path for an existing stage-2 run")
    source = Path(checkpoint_override or options.behaviors["BaseballBatter"].init_path or "")
    status_path = source.parent.parent / "preparation-status.json"
    if not status_path.is_file():
        raise RuntimeError("Preparation status is missing; stage 2 is not ready")
    state = json.loads(status_path.read_text(encoding="utf-8"))
    if not state.get("ready") or state.get("phase") != len(PHASE_NAMES) - 1:
        raise RuntimeError("Full preparation quality gate has not passed; stage 2 is blocked")
    saved = validate_camera_checkpoint(source, expected_stacks)
    if int(saved["global_step"]["_GlobalSteps__global_step"].item()) < state["latest"]["step"]:
        raise RuntimeError("Preparation checkpoint is older than the passing evaluation")
    options.behaviors["BaseballBatter"].init_path = str(source)


def main(argv=None):
    from mlagents.trainers import learn
    arguments = list(sys.argv[1:] if argv is None else argv)
    continue_stage2 = "--continue-stage2" in arguments
    stage2 = "--prepared-stage2" in arguments
    skills = "--skills" in arguments
    stack_args = [arg.split("=", 1)[1] for arg in arguments if arg.startswith("--camera-stacks=")]
    phase_args = [arg.split("=", 1)[1] for arg in arguments if arg.startswith("--initial-phase=")]
    if len(stack_args) > 1 or len(phase_args) > 1:
        raise ValueError("Specify camera stacks and initial phase at most once")
    camera_stacks = int(stack_args[0]) if stack_args else 6
    if camera_stacks not in (6, 30):
        raise ValueError("Supported camera stacks are 6 and 30")
    initial_phase = int(phase_args[0]) if phase_args else 0
    phase_names = SKILL_PHASE_NAMES if skills else PHASE_NAMES
    if not 0 <= initial_phase < len(phase_names) or phase_args and (stage2 or "--resume" in arguments):
        raise ValueError("Initial phase must belong to a fresh preparation plan")
    initial = [arg.split("=", 1)[1] for arg in arguments if arg.startswith("--initial-checkpoint=")]
    if len(initial) > 1 or initial and (stage2 or "--resume" in arguments):
        raise ValueError("--initial-checkpoint is only allowed for a fresh preparation run")
    if skills and stage2:
        raise ValueError("Skills must finish the full preparation before stage 2")
    checkpoint_arguments = [arg.split("=", 1)[1] for arg in arguments if arg.startswith("--prepared-checkpoint=")]
    if checkpoint_arguments and (not stage2 or len(checkpoint_arguments) != 1):
        raise ValueError("--prepared-checkpoint requires --prepared-stage2 and exactly one path")
    arguments = [arg for arg in arguments if arg not in ("--continue-stage2", "--prepared-stage2", "--skills")
                 and not arg.startswith(("--prepared-checkpoint=", "--initial-checkpoint=", "--camera-stacks=", "--initial-phase="))]
    options = learn.parse_command_line(arguments)
    if options.engine_settings.no_graphics:
        raise ValueError("Catcher camera training requires graphics")
    if options.checkpoint_settings.force:
        raise ValueError("Use a new run-id; preparation does not overwrite results with --force")
    if stage2:
        validate_prepared_stage2(options, checkpoint_arguments[0] if checkpoint_arguments else None, camera_stacks)
        learn.run_cli(options)
        return
    if set(options.behaviors) != {"BaseballBatter"}:
        raise ValueError("Preparation trains only BaseballBatter in Stage1_Batter")
    if initial:
        options.behaviors["BaseballBatter"].init_path = initial[0]
    if skills and "batter_skill" not in options.environment_parameters:
        raise ValueError("Skills mode requires the opt-in batter_skill parameter")
    if not skills and "batter_skill" in options.environment_parameters:
        raise ValueError("The skills configuration requires --skills")
    checkpoint = options.checkpoint_settings
    run = Path(checkpoint.write_path)
    if not checkpoint.resume and run.exists() and any(run.iterdir()):
        raise FileExistsError("The preparation run already exists. Resume it or use a new run-id; its status is preserved")
    if (run / "stop-requested").exists():
        raise ValueError("Remove stop-requested before starting or resuming preparation")
    progress = PreparationProgress(run / ("skills-status.json" if skills else "preparation-status.json"),
                                   minimum_phase_steps=max(30000, 3 * options.behaviors["BaseballBatter"].summary_freq),
                                   phase_names=SKILL_PHASE_NAMES if skills else None)
    if not checkpoint.resume:
        progress.phase = initial_phase
        progress.history = [{"phase": initial_phase, "start_step": 0}]
    if checkpoint.resume:
        saved = validate_camera_checkpoint(run / "BaseballBatter/checkpoint.pt", camera_stacks)
        step = int(saved["global_step"]["_GlobalSteps__global_step"].item())
        progress.restore(step)
        # ML-Agents 1.1 otherwise prioritizes init_path over the run being resumed.
        options.behaviors["BaseballBatter"].init_path = None
    elif not Path(options.behaviors["BaseballBatter"].init_path or "").is_file():
        raise FileNotFoundError("The stage-1 starting checkpoint is missing")
    else:
        validate_camera_checkpoint(options.behaviors["BaseballBatter"].init_path, camera_stacks)
    original = learn.EnvironmentParameterManager
    learn.EnvironmentParameterManager = parameter_manager(progress, skills=skills)
    writer = PreparationWriter(progress, "Batter Skills/Phase" if skills else "Batter Preparation/Phase")
    StatsReporter.add_writer(writer)
    try:
        learn.run_cli(options)
    finally:
        learn.EnvironmentParameterManager = original
        StatsReporter.writers.remove(writer)
    if progress.path.with_name("stop-requested").exists():
        print("Preparation saved after stop request; stage 2 was not started.", flush=True)
        return
    if not progress.ready:
        print("Preparation saved; quality gate has not passed. Do not start stage 2 yet.", flush=True)
    elif continue_stage2:
        import subprocess
        root = Path(__file__).resolve().parent.parent
        if skills:
            source = (run / "BaseballBatter/checkpoint.pt").resolve()
            saved = validate_camera_checkpoint(source, camera_stacks)
            if int(saved["global_step"]["_GlobalSteps__global_step"].item()) < progress.latest["step"]:
                raise RuntimeError("Skills checkpoint is older than the passing evaluation")
            command = [sys.executable, "-u", "-B", str(Path(__file__).resolve()),
                       str(root / f"Training/config/batter_preparation_camera{camera_stacks}.yaml"),
                       "--continue-stage2", "--run-id=" + checkpoint.run_id + "_prepare",
                       "--initial-checkpoint=" + str(source), "--results-dir=" + str(Path(checkpoint.results_dir).resolve()),
                       f"--camera-stacks={camera_stacks}",
                       "--env=" + str(root / ("Training/builds/BatterSkillsCamera6/BatterSkillsCamera6.exe" if camera_stacks == 6
                                              else "Training/builds/BatterCamera30/BatterCamera30.exe")),
                       "--base-port=57363", "--env-args", "-batchmode"]
            print("Skills passed and saved. Starting full guarded preparation from this model.", flush=True)
            subprocess.run(command, cwd=root, check=True)
            return
        command = [sys.executable, "-u", "-B", str(Path(__file__).resolve()),
                   str(root / f"Training/config/stage2_batter_pitcher_camera{camera_stacks}_selfplay.yaml"),
                   "--prepared-stage2", f"--run-id=stage2_batter_pitcher_camera{camera_stacks}_selfplay",
                   f"--camera-stacks={camera_stacks}",
                   "--prepared-checkpoint=" + str((run / "BaseballBatter/checkpoint.pt").resolve()),
                   "--results-dir=" + str(Path(checkpoint.results_dir).resolve()),
                   "--env=" + str(root / f"Training/builds/Stage2_Camera{camera_stacks}/Stage2_Camera{camera_stacks}.exe"),
                   "--base-port=57352", "--env-args", "-batchmode"]
        print("Preparation passed and saved. Starting guarded stage-2 self-play.", flush=True)
        subprocess.run(command, cwd=root, check=True)


if __name__ == "__main__":
    main()
