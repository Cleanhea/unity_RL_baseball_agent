"""Verify the stage-1 curriculum, action mask and unchanged camera policy contract.

Run with the installed ML-Agents Python environment. No training results are changed.
"""
from pathlib import Path
import ast
import importlib.util
import json
import tempfile
import numpy as np
import onnx
from mlagents.torch_utils import torch
from mlagents.trainers.learn import parse_command_line
from mlagents.trainers.policy.torch_policy import TorchPolicy
from mlagents.trainers.torch_entities.networks import SimpleActor
from mlagents.trainers.torch_entities.model_serialization import ModelSerializer
from mlagents_envs.base_env import (
    ActionSpec, BehaviorSpec, DecisionSteps, DimensionProperty, ObservationSpec, ObservationType,
)

ROOT = Path(__file__).resolve().parents[1]
options = parse_command_line([str(ROOT / "Training/config/stage1_batter.yaml"), "--run-id=verify"])
settings = options.behaviors["BaseballBatter"]
lessons = options.environment_parameters["batter_lesson"].curriculum
assert [lesson.value.value for lesson in lessons] == [0, 1, 2, 3]
assert lessons[-1].completion_criteria is None
for i, lesson in enumerate(lessons[:3]):
    criteria = lesson.completion_criteria
    assert criteria.measure.value == "reward" and not criteria.require_reset
    assert not criteria.need_increment(1.0, [-2.77] * 100, -2.77)[0]  # taking-only run
    assert not criteria.need_increment(0.0, [2.8] * 99, 2.8)[0]       # insufficient episodes
    if i > 0:
        assert not criteria.need_increment(1.0, [0.5] * 100, 0.5)[0]  # weak fair hits
        assert not criteria.need_increment(1.0, [1.0] * 100, 1.0)[0]  # walk-only policy
    assert criteria.need_increment(0.0, [2.8] * 100, 2.8)[0]          # actual rewarded success
assert settings.hyperparameters.beta == 0.02
assert settings.hyperparameters.beta_schedule.value == "constant"
assert settings.summary_freq == 10000
for filename in ("stage2_batter_pitcher.yaml", "stage2_batter_pitcher_selfplay.yaml"):
    stage2 = parse_command_line([str(ROOT / "Training/config" / filename), "--run-id=verify"])
    assert "stage1_batter_camera_power/" in stage2.behaviors["BaseballBatter"].init_path
print("PASS YAML curriculum, earned advancement, minimum episodes, constant exploration, stage-2 initialization")

observations = [
    ObservationSpec((6, 256, 256), (DimensionProperty.NONE, DimensionProperty.TRANSLATIONAL_EQUIVARIANCE,
                                  DimensionProperty.TRANSLATIONAL_EQUIVARIANCE), ObservationType.DEFAULT, "CatcherEye"),
    ObservationSpec((16,), (DimensionProperty.NONE,), ObservationType.DEFAULT, "batter"),
]
spec = BehaviorSpec(observations, ActionSpec(7, np.array([2], dtype=np.int32)))
policy = TorchPolicy(123, spec, settings.network_settings, SimpleActor, {})
inputs = [torch.rand((2, 6, 256, 256)), torch.zeros((2, 16))]
encoded = policy.actor.network_body(inputs)[0]
encoded.square().mean().backward()
assert any(p.grad is not None and p.grad.abs().sum() > 0 for p in policy.actor.network_body.parameters())
steps = DecisionSteps([x.detach().cpu().numpy() for x in inputs], np.zeros(2), np.array([1, 2], dtype=np.int32),
                      [np.array([[False, True], [False, True]])], np.zeros(2, dtype=np.int32), np.zeros(2))
action = policy.evaluate(steps, ["0-1", "0-2"])["action"]
assert action.continuous.shape == (2, 7) and np.isfinite(action.continuous).all()
assert (action.discrete == 0).all(), "masked early swings must be taken"
with tempfile.TemporaryDirectory(prefix="baseball-contact-policy-") as artifact:
    output = str(Path(artifact) / "batter")
    ModelSerializer(policy).export_policy_model(output)
    model = onnx.load(output + ".onnx")
    onnx.checker.check_model(model)
    shapes = {p.name: [d.dim_value for d in p.type.tensor_type.shape.dim] for p in model.graph.input}
    assert shapes["obs_0"][1:] == [6, 256, 256] and shapes["obs_1"][1:] == [16]
print("PASS actual CNN forward/backward, early-swing action mask, continuous 7 + [2], ONNX export/checker")
ast.parse((ROOT / "Training/auto_curriculum.py").read_text(encoding="utf-8"))
module_spec = importlib.util.spec_from_file_location("verify_auto_curriculum", ROOT / "Training/auto_curriculum.py")
auto = importlib.util.module_from_spec(module_spec)
module_spec.loader.exec_module(auto)
with tempfile.TemporaryDirectory(prefix="baseball-curriculum-handoff-") as artifact:
    auto.RESULTS = Path(artifact)
    run = auto.RESULTS / auto.RUN_IDS[1]
    (run / "BaseballBatter").mkdir(parents=True)
    (run / "BaseballBatter/checkpoint.pt").touch()
    (run / "BaseballBatter.onnx").touch()
    (run / "run_logs").mkdir()
    training_status = run / "run_logs/training_status.json"
    auto.latest_step = lambda stage, behavior: settings.max_steps
    messages = []
    auto.say = messages.append
    for lesson_num in (0, 1, 2):
        training_status.write_text(json.dumps({"batter_lesson": {"lesson_num": lesson_num}}), encoding="utf-8")
        try:
            auto.check_stage_finished(1)
        except RuntimeError as error:
            assert "FullBatting" in str(error)
        else:
            raise AssertionError("step count alone must not allow a stage-2 handoff")
        assert not messages
    training_status.write_text(json.dumps({"batter_lesson": {"lesson_num": 3}}), encoding="utf-8")
    from tensorboard.summary.writer.event_file_writer import EventFileWriter
    from tensorboard.compat.proto.event_pb2 import Event
    from tensorboard.compat.proto.summary_pb2 import Summary
    import time
    writer = EventFileWriter(str(run / "BaseballBatter"))
    def summary(step, lesson, rate):
        writer.add_event(Event(wall_time=time.time(), step=step, summary=Summary(value=[
            Summary.Value(tag="Batter Training/Lesson", simple_value=lesson),
            Summary.Value(tag="Batter Training/Qualified Hit", simple_value=rate)])))
        writer.flush()
    summary(10000, 2, 1.0)  # earlier, easier lessons cannot qualify
    summary(20000, 2.5, 1.0)  # mixed summary cannot qualify
    for step in (30000, 40000): summary(step, 3, 0.9)
    try:
        auto.check_stage_finished(1)
    except RuntimeError as error:
        assert "at least 3" in str(error)
    else:
        raise AssertionError("fewer than 3 full-control summaries must block stage 2")
    summary(50000, 3, 0.0)
    summary(60000, 3, 0.0)
    summary(70000, 3, 0.0)
    try:
        auto.check_stage_finished(1)
    except RuntimeError as error:
        assert "qualified-hit rate" in str(error)
    else:
        raise AssertionError("recent weak-hit performance must block stage 2")
    for step in (80000, 90000, 100000): summary(step, 3, 0.3)
    writer.close()
    auto.check_stage_finished(1)
    assert len(messages) == 1
print("PASS automatic stage-2 handoff requires full controls and >=25% qualified hits in 3 recent full-control event summaries")
