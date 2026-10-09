"""Verify skill-phase stats, saved progress, parameters and guarded entry points."""
from pathlib import Path
import tempfile
from unittest.mock import patch

from mlagents.trainers.learn import parse_command_line
from mlagents.trainers.stats import StatsSummary, StatsAggregationMethod
from prepare_batter import PreparationProgress, PreparationWriter, parameter_manager, SKILL_PHASE_NAMES, main

ROOT = Path(__file__).resolve().parents[1]
config = ROOT / "Training/config/batter_skills_camera6.yaml"
options = parse_command_line([str(config), "--run-id=verify_skills"])
assert set(options.behaviors) == {"BaseballBatter"}
assert options.behaviors["BaseballBatter"].summary_freq == 20000
assert options.environment_parameters["batter_skill"].curriculum[0].value.value == 0
assert options.environment_parameters["batter_preparation"].curriculum[0].value.value == 0
camera30 = parse_command_line([str(ROOT / "Training/config/batter_skills_camera30.yaml"), "--run-id=verify_skills30"])
settings30 = camera30.behaviors["BaseballBatter"]
assert settings30.hyperparameters.batch_size == 32 and settings30.hyperparameters.buffer_size == 256
assert settings30.time_horizon == 64 and settings30.summary_freq == 20000
assert settings30.network_settings == options.behaviors["BaseballBatter"].network_settings
assert camera30.environment_parameters["batter_skill"].curriculum[0].value.value == 0
assert camera30.environment_parameters["batter_prepared"].curriculum[0].value.value == 1

def summary(values):
    return StatsSummary(values, StatsAggregationMethod.HISTOGRAM)

def values(phase, qualified=10):
    return {"Batter Preparation/Phase": summary([0] * 100),
            "Batter Skills/Phase": summary([phase] * 100),
            "Batter Preparation/Fair Contact": summary([1] * 80 + [0] * 20),
            "Batter Preparation/Qualified Hit": summary([1] * qualified + [0] * (100-qualified)),
            "Plate Discipline/Zone Swing Rate": summary([1] * 100)}

with tempfile.TemporaryDirectory(prefix="batter-skills-") as temp:
    state = PreparationProgress(Path(temp) / "skills-status.json", minimum_phase_steps=60000,
                                phase_names=SKILL_PHASE_NAMES)
    writer = PreparationWriter(state, "Batter Skills/Phase")
    old_player_values = values(0); del old_player_values["Batter Skills/Phase"]
    try: writer.write_stats("BaseballBatter", old_player_values, 20000)
    except RuntimeError: pass
    else: raise AssertionError("an old player must not silently train the wrong task")
    manager = parameter_manager(state, skills=True)(options.environment_parameters, run_seed=123)
    for step in (20000, 40000): writer.write_stats("BaseballBatter", values(0), step)
    writer.write_stats("BaseballBatter", values(0, qualified=7), 60000)
    assert state.phase == 0 and not state.windows  # two passes followed by failure
    for step in (80000, 100000, 120000): writer.write_stats("BaseballBatter", values(0), step)
    assert state.phase == 1
    samplers = manager.get_current_samplers()
    assert samplers["batter_skill"].value == 1 and samplers["batter_preparation"].value == 0
    mixed = values(1); mixed["Batter Skills/Phase"] = summary([0] * 20 + [1] * 80)
    writer.write_stats("BaseballBatter", mixed, 140000)
    assert not state.windows
    # Constant legacy phase 0 must not advance skill 1; only the skill histogram governs it.
    for delta in (20000, 40000, 60000): writer.write_stats("BaseballBatter", values(1), state.started_at + delta)
    assert state.phase == 2
    saved = PreparationProgress(state.path, minimum_phase_steps=60000, phase_names=SKILL_PHASE_NAMES)
    saved.restore(125000)
    assert saved.phase == 1 and not saved.ready and not saved.windows
    try: PreparationProgress(state.path).restore(125000)
    except ValueError: pass
    else: raise AssertionError("skill status must not resume as legacy preparation")
    while state.phase < len(SKILL_PHASE_NAMES)-1:
        phase, start = state.phase, state.started_at
        for delta in (20000, 40000, 60000): writer.write_stats("BaseballBatter", values(phase), start + delta)
    assert not state.ready
    phase, start = state.phase, state.started_at
    for delta in (20000, 40000, 60000): writer.write_stats("BaseballBatter", values(phase), start + delta)
    assert state.ready
    assert not state.path.with_name("preparation-status.json").exists()  # direct stage-2 guard cannot accept bootstrap
    try: manager.update_lessons({}, {}, {})
    except KeyboardInterrupt: pass
    else: raise AssertionError("completed skills must take the normal model-saving path")

for arguments in [[str(config), "--run-id=verify_skills"],
                  [str(config), "--skills", "--prepared-stage2"],
                  [str(config), "--skills", "--resume", "--initial-checkpoint=unused"]]:
    try: main(arguments)
    except ValueError: pass
    else: raise AssertionError("invalid skill configuration/entry point was accepted")
assert options.checkpoint_settings.run_id == "verify_skills"
assert hasattr(options.checkpoint_settings, "results_dir")

# Exercise the real CLI's save -> full-preparation command construction without
# running training or launching a child process.
for stacks in (6, 30):
    with tempfile.TemporaryDirectory(prefix="batter-skills-cli-") as temp:
        import torch
        from mlagents.trainers.stats import StatsReporter
        def completed_training(run_options):
            from mlagents.trainers.directory_utils import validate_existing_directories
            validate_existing_directories(run_options.checkpoint_settings.write_path, False, False)
            (Path(run_options.checkpoint_settings.write_path)/"run_logs").mkdir(parents=True)
            from mlagents.trainers import learn
            learn.EnvironmentParameterManager(run_options.environment_parameters, run_seed=123)
            writer = next(w for w in StatsReporter.writers if isinstance(w, PreparationWriter) and w.phase_key == "Batter Skills/Phase")
            state = writer.progress
            assert state.path.is_file()  # saved after stock validation, before summaries
            state.phase = len(SKILL_PHASE_NAMES)-1
            state.ready = True
            state.latest = {"step": 60000}
            state.save()
            source = state.path.parent / "BaseballBatter/checkpoint.pt"
            source.parent.mkdir()
            torch.save({"global_step": {"_GlobalSteps__global_step": torch.tensor([60000])},
                        "Policy": {"conv_layers.0.weight": torch.zeros(16, stacks, 8, 8)},
                        "Optimizer:critic": {"conv_layers.0.weight": torch.zeros(16, stacks, 8, 8)}}, source)
        with patch("mlagents.trainers.learn.run_cli", completed_training), patch("subprocess.run") as launch:
            main([str(ROOT / f"Training/config/batter_skills_camera{stacks}.yaml"), f"--camera-stacks={stacks}",
                  "--skills", "--continue-stage2", "--run-id=skills_handoff", "--results-dir=" + temp])
            command = launch.call_args.args[0]
            assert "--run-id=skills_handoff_prepare" in command and "--continue-stage2" in command
            assert "--prepared-stage2" not in command
            assert "--initial-checkpoint=" + str((Path(temp)/"skills_handoff/BaseballBatter/checkpoint.pt").resolve()) in command
            assert "--results-dir=" + str(Path(temp).resolve()) in command
            assert f"--camera-stacks={stacks}" in command
            assert str(ROOT / f"Training/config/batter_preparation_camera{stacks}.yaml") in command
            if stacks == 30:
                assert "--env=" + str(ROOT / "Training/builds/BatterCamera30/BatterCamera30.exe") in command
print("PASS actual skill YAML, constant-preparation/dynamic-skill parameters, mixed phase and streak rejection, checkpoint-aligned resume, plan mismatch, graceful save and stage-2 separation")
