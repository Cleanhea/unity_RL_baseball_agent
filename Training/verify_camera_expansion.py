"""Verify exact actor/critic transfer and learnable new temporal channels."""
from pathlib import Path
import copy
import tempfile
import numpy as np
import onnx
from mlagents.torch_utils import torch
from mlagents.trainers.learn import parse_command_line
from mlagents.trainers.policy.torch_policy import TorchPolicy
from mlagents.trainers.ppo.optimizer_torch import TorchPPOOptimizer
from mlagents.trainers.torch_entities.networks import SimpleActor
from mlagents.trainers.torch_entities.model_serialization import ModelSerializer
from mlagents_envs.base_env import ActionSpec, BehaviorSpec, ObservationSpec, ObservationType, DimensionProperty
from expand_camera_checkpoint import expand_checkpoint

ROOT = Path(__file__).resolve().parents[1]
source = ROOT / "Training/results/stage1_batter_camera_power/BaseballBatter/checkpoint.pt"
original = torch.load(source, map_location="cpu", weights_only=False)
expanded = copy.deepcopy(original)
changed = expand_checkpoint(expanded)
assert len(changed) == 4
assert not expanded["Optimizer:value_optimizer"]["state"]
for group in ("Policy", "Optimizer:critic"):
    for key, old in original[group].items():
        new = expanded[group][key]
        if f"{group}/{key}" in changed:
            assert torch.equal(new[:, [1, 3, 5]], old)
            assert torch.count_nonzero(new[:, [0, 2, 4]]) == 0
        else:
            assert torch.equal(old, new), f"unrelated weight changed: {group}/{key}"
try: expand_checkpoint(expanded)
except ValueError: pass
else: raise AssertionError("expanding a six-frame policy again must fail")
settings = parse_command_line([str(ROOT / "Training/config/batter_preparation_camera6.yaml"), "--run-id=verify"]).behaviors["BaseballBatter"]
def make(stack):
    observations = [ObservationSpec((stack, 256, 256),
                    (DimensionProperty.NONE, DimensionProperty.TRANSLATIONAL_EQUIVARIANCE, DimensionProperty.TRANSLATIONAL_EQUIVARIANCE),
                    ObservationType.DEFAULT, "CatcherEye"),
                    ObservationSpec((16,), (DimensionProperty.NONE,), ObservationType.DEFAULT, "batter")]
    return TorchPolicy(123, BehaviorSpec(observations, ActionSpec(7, np.array([2], dtype=np.int32))), settings.network_settings, SimpleActor, {})
old_policy, new_policy = make(3), make(6)
old_optimizer, new_optimizer = TorchPPOOptimizer(old_policy, settings), TorchPPOOptimizer(new_policy, settings)
old_policy.actor.load_state_dict(original["Policy"], strict=True)
new_policy.actor.load_state_dict(expanded["Policy"], strict=True)
old_optimizer._critic.load_state_dict(original["Optimizer:critic"], strict=True)
new_optimizer._critic.load_state_dict(expanded["Optimizer:critic"], strict=True)
new_optimizer.optimizer.load_state_dict(expanded["Optimizer:value_optimizer"])
device = next(new_policy.actor.parameters()).device
images = torch.rand((2, 6, 256, 256), device=device)
vector = torch.rand((2, 16), device=device)
old_inputs, new_inputs = [images[:, [1, 3, 5]], vector], [images, vector]
with torch.no_grad():
    old_features = old_policy.actor.network_body(old_inputs)[0]
    new_features = new_policy.actor.network_body(new_inputs)[0]
    assert torch.allclose(old_features, new_features, atol=3e-5, rtol=3e-5)
    old_values = old_optimizer._critic.critic_pass(old_inputs)[0]
    new_values = new_optimizer._critic.critic_pass(new_inputs)[0]
    for key in old_values:
        assert torch.allclose(old_values[key], new_values[key], atol=3e-5, rtol=3e-5)
print("PASS exact retained weights, actor features and critic outputs on matching 40/20/0-ms images")
new_policy.actor.network_body(new_inputs)[0].square().mean().backward()
first = new_policy.actor.network_body.observation_encoder.processors[0].conv_layers[0]
assert first.weight.grad[:, [0, 2, 4]].abs().sum() > 0
new_optimizer.optimizer.step()
assert first.weight[:, [0, 2, 4]].abs().sum() > 0
assert all(torch.isfinite(p).all() for p in new_policy.actor.parameters())
with tempfile.TemporaryDirectory(prefix="baseball-camera6-") as folder:
    output = str(Path(folder) / "batter")
    ModelSerializer(new_policy).export_policy_model(output)
    model = onnx.load(output + ".onnx")
    onnx.checker.check_model(model)
    shapes = {v.name: [d.dim_value for d in v.type.tensor_type.shape.dim] for v in model.graph.input}
    assert shapes["obs_0"][1:] == [6, 256, 256]
print("PASS fresh optimizer loads, new temporal channels receive gradients and learn, six-frame ONNX export/checker")
