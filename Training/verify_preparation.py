"""Check the real installed ML-Agents preparation integration and quality gates."""
from pathlib import Path
import json
import tempfile

from mlagents.trainers.learn import parse_command_line
from mlagents.trainers.settings import ConstantSettings
from mlagents.trainers.stats import StatsSummary, StatsAggregationMethod
from prepare_batter import PreparationProgress, PreparationWriter, parameter_manager, PHASE_NAMES, validate_prepared_stage2, validate_camera_checkpoint

ROOT = Path(__file__).resolve().parent.parent
options = parse_command_line([str(ROOT / "Training/config/batter_preparation_camera6.yaml"), "--run-id=verify"])
assert set(options.behaviors) == {"BaseballBatter"}
assert options.behaviors["BaseballBatter"].init_path.endswith("initialization/batter_camera6_100hz/checkpoint.pt")
assert options.behaviors["BaseballBatter"].hyperparameters.learning_rate == 1e-4
assert not options.engine_settings.no_graphics
assert options.behaviors["BaseballBatter"].summary_freq == 20000
assert abs(options.behaviors["BaseballBatter"].hyperparameters.lambd ** 2 - 0.95) < 1e-8
assert abs(next(iter(options.behaviors["BaseballBatter"].reward_signals.values())).gamma ** 2 - 0.99) < 1e-8
prepared = parse_command_line([str(ROOT / "Training/config/stage2_batter_pitcher_camera6_selfplay.yaml"), "--run-id=verify"])
assert prepared.behaviors["BaseballBatter"].init_path.endswith("stage1_batter_camera6_prepare/BaseballBatter/checkpoint.pt")
assert all(b.self_play is not None for b in prepared.behaviors.values())
assert prepared.environment_parameters["batter_prepared"].curriculum[0].value.value == 1.0

with tempfile.TemporaryDirectory(prefix="batter-preparation-handoff-") as temp:
    import torch
    source = Path(temp) / "BaseballBatter/checkpoint.pt"
    source.parent.mkdir()
    prepared.behaviors["BaseballBatter"].init_path = str(source)
    status = source.parent.parent / "preparation-status.json"
    def rejects_handoff(error):
        try: validate_prepared_stage2(prepared)
        except error: pass
        else: raise AssertionError("unsafe preparation handoff was accepted")
    rejects_handoff(RuntimeError)  # Missing preparation status.
    status.write_text(json.dumps({"ready": False, "phase": 15, "latest": {"step": 50000}}))
    rejects_handoff(RuntimeError)
    status.write_text(json.dumps({"ready": True, "phase": 14, "latest": {"step": 50000}}))
    rejects_handoff(RuntimeError)
    status.write_text(json.dumps({"ready": True, "phase": 15, "latest": {"step": 50000}}))
    def saved_camera(step, stack=6):
        return {"global_step": {"_GlobalSteps__global_step": torch.tensor(step)},
                "Policy": {"conv_layers.0.weight": torch.zeros(16, stack, 8, 8)},
                "Optimizer:critic": {"conv_layers.0.weight": torch.zeros(16, stack, 8, 8)}}
    torch.save(saved_camera(49999), source)
    rejects_handoff(RuntimeError)  # Passing status ahead of the saved model.
    torch.save(saved_camera(50000, stack=3), source)
    rejects_handoff(ValueError)
    torch.save(saved_camera(50000), source)
    validate_prepared_stage2(prepared)
    prepared.behaviors["BaseballBatter"].init_path = "unused-default/checkpoint.pt"
    validate_prepared_stage2(prepared, source)  # Custom run/results paths must hand off the model just trained.
    assert prepared.behaviors["BaseballBatter"].init_path == str(source)
    prepared.environment_parameters["batter_prepared"].curriculum[0].value.value = 0
    rejects_handoff(ValueError)  # Changed controls/rewards would invalidate the prepared model.
    prepared.environment_parameters["batter_prepared"].curriculum[0].value.value = 1

def observe(progress, step, fair=.8, quality=.1, phase=None, count=100, zone=.8, chase=.1):
    progress.observe(step, [progress.phase if phase is None else phase] * count, count, fair, quality, zone, chase)

state = PreparationProgress()
for step in (10000, 20000, 30000): observe(state, step, fair=0, quality=0)  # profitable walks cannot pass
assert state.phase == 0
for step in (40000, 50000, 60000): observe(state, step, quality=0)  # weak contact cannot pass
assert state.phase == 0
for step in (70000, 80000, 90000): observe(state, step, count=49)
assert state.phase == 0
observe(state, 100000); observe(state, 110000, phase=.5); observe(state, 120000)
assert state.phase == 0 and len(state.windows) == 1  # mixed phases reset the streak
observe(state, 130000, fair=float("nan")); assert not state.windows
for step in (140000, 150000): observe(state, step)
assert state.phase == 0
observe(state, 160000); assert state.phase == 1
manager = parameter_manager(state)(options.environment_parameters, run_seed=123)
assert isinstance(manager.get_current_samplers()["batter_preparation"], ConstantSettings)
assert manager.get_current_samplers()["batter_preparation"].value == 1
for step in (170000, 180000, 190000): observe(state, step)
assert state.phase == 2
assert manager.update_lessons({}, {}, {}) == (True, False)
assert manager.update_lessons({}, {}, {}) == (False, False)

while state.phase < len(PHASE_NAMES) - 1:
    for delta in (10000, 20000, 30000): observe(state, state.started_at + delta)
assert not state.ready
start = state.started_at
for delta in (10000, 20000, 30000): observe(state, start + delta, chase=.8)
assert not state.ready
for delta in (40000, 50000, 60000): observe(state, start + delta)
assert state.ready
try: manager.update_lessons({}, {}, {})
except KeyboardInterrupt: pass
else: raise AssertionError("ready must use the stock controller's graceful model-saving path")

with tempfile.TemporaryDirectory(prefix="batter-preparation-") as temp:
    state.path = Path(temp) / "preparation-status.json"; state.save()
    restored = PreparationProgress(state.path)
    restored.restore(165000)
    assert restored.phase == 1 and not restored.ready and not restored.windows
    restored.restore(start + 60000)
    assert restored.phase == 15 and not restored.ready
    stopped = PreparationProgress(Path(temp) / "stopped/preparation-status.json")
    stopped.path.parent.mkdir()
    stopped.save()
    early_resume = PreparationProgress(stopped.path)
    early_resume.restore(5000)
    assert early_resume.phase == 0 and not early_resume.ready and not early_resume.windows
    stopped.path.with_name("stop-requested").touch()
    stop_manager = parameter_manager(stopped)(options.environment_parameters, run_seed=123)
    try: stop_manager.update_lessons({}, {}, {})
    except KeyboardInterrupt: pass
    else: raise AssertionError("stop-requested must trigger the normal checkpoint-saving path")

def summary(values): return StatsSummary(values, StatsAggregationMethod.HISTOGRAM)
state = PreparationProgress(); writer = PreparationWriter(state)
values = {"Batter Preparation/Phase": summary([0] * 100),
          "Batter Preparation/Fair Contact": summary([1]*80+[0]*20),
          "Batter Preparation/Qualified Hit": summary([1]*10+[0]*90),
          "Plate Discipline/Zone Swing Rate": summary([1]*80+[0]*20)}
for step in (10000, 20000, 30000): writer.write_stats("BaseballBatter", values, step)
assert state.phase == 1
state = PreparationProgress(minimum_phase_steps=60000)
for step in (10000, 20000, 30000): observe(state, step)
assert state.phase == 0  # 100-Hz preparation preserves the old minimum physical exposure.
for step in (40000, 50000, 60000): observe(state, step)
assert state.phase == 1
print("PASS actual YAML parsing, prepared self-play handoff, metric writer, 16 phases, minimum PAs/steps, mixed/NaN/weak/walk rejection, discipline gate, checkpoint-aligned resume and graceful stop")
