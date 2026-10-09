"""Check exact six-to-thirty transfer, learning, ONNX and guarded continuation."""
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
from prepare_batter import validate_camera_checkpoint, main, parameter_manager
from unittest.mock import patch
import json
from continue_stage3 import validate_stage3_config

ROOT = Path(__file__).resolve().parents[1]
source = ROOT / 'Training/results/stage1_batter_camera6_skills_prepare/BaseballBatter/checkpoint.pt'
original = validate_camera_checkpoint(source)
expanded = copy.deepcopy(original)
changed = expand_checkpoint(expanded, 30)
assert len(changed) == 4 and not expanded['Optimizer:value_optimizer']['state']
for group in ('Policy', 'Optimizer:critic'):
    for key, old in original[group].items():
        new = expanded[group][key]
        if f'{group}/{key}' in changed:
            assert torch.equal(new[:, 24:30], old)
            assert torch.count_nonzero(new[:, :24]) == 0
        else:
            assert torch.equal(old, new), f'Unrelated weight changed: {group}/{key}'
try:
    expand_checkpoint(expanded, 30)
except ValueError:
    pass
else:
    raise AssertionError('An already-expanded policy must be rejected')
initialized = ROOT / 'Training/initialization/batter_camera30_100hz/checkpoint.pt'
validate_camera_checkpoint(initialized, 30)
try:
    validate_camera_checkpoint(source, 30)
except ValueError:
    pass
else:
    raise AssertionError('A six-frame checkpoint cannot directly enter thirty-frame training')

settings = parse_command_line([str(ROOT / 'Training/config/batter_preparation_camera30.yaml'), '--run-id=verify']).behaviors['BaseballBatter']
assert settings.hyperparameters.batch_size == 32 and settings.hyperparameters.buffer_size == 256
assert settings.time_horizon == 64
validate_stage3_config(ROOT / 'Training/config/stage3_full_team_camera30_selfplay.yaml',
                       ROOT / 'Training/config/stage2_batter_pitcher_camera30_selfplay.yaml')
with tempfile.TemporaryDirectory(prefix='camera30-cli-') as folder:
    from mlagents.trainers import learn
    def run_cli(options):
        assert options.behaviors['BaseballBatter'].init_path.endswith('batter_camera30_100hz/checkpoint.pt')
        manager = learn.EnvironmentParameterManager(options.environment_parameters, run_seed=123)
        assert manager.get_current_samplers()['batter_preparation'].value == 1
        status = json.loads((Path(folder) / 'camera30_test/preparation-status.json').read_text())
        assert status['phase'] == 1 and status['history'] == [{'phase': 1, 'start_step': 0}]
    with patch.object(learn, 'run_cli', run_cli):
        main([str(ROOT / 'Training/config/batter_preparation_camera30.yaml'), '--camera-stacks=30',
              '--initial-phase=1', '--run-id=camera30_test', '--results-dir=' + folder])
print('PASS actual Camera30 CLI retains preparation phase and creates independent status')

def make(stack):
    specs = [ObservationSpec((stack, 256, 256),
             (DimensionProperty.NONE, DimensionProperty.TRANSLATIONAL_EQUIVARIANCE, DimensionProperty.TRANSLATIONAL_EQUIVARIANCE),
             ObservationType.DEFAULT, 'CatcherEye'),
             ObservationSpec((16,), (DimensionProperty.NONE,), ObservationType.DEFAULT, 'batter')]
    return TorchPolicy(123, BehaviorSpec(specs, ActionSpec(7, np.array([2], dtype=np.int32))), settings.network_settings, SimpleActor, {})

old_policy, new_policy = make(6), make(30)
old_optimizer, new_optimizer = TorchPPOOptimizer(old_policy, settings), TorchPPOOptimizer(new_policy, settings)
old_policy.actor.load_state_dict(original['Policy'], strict=True)
new_policy.actor.load_state_dict(expanded['Policy'], strict=True)
old_optimizer._critic.load_state_dict(original['Optimizer:critic'], strict=True)
new_optimizer._critic.load_state_dict(expanded['Optimizer:critic'], strict=True)
new_optimizer.optimizer.load_state_dict(expanded['Optimizer:value_optimizer'])
device = next(new_policy.actor.parameters()).device
images, vector = torch.rand((2, 30, 256, 256), device=device), torch.rand((2, 16), device=device)
old_inputs, new_inputs = [images[:, 24:30], vector], [images, vector]
with torch.no_grad():
    assert torch.allclose(old_policy.actor.network_body(old_inputs)[0], new_policy.actor.network_body(new_inputs)[0], atol=3e-5, rtol=3e-5)
    old_values = old_optimizer._critic.critic_pass(old_inputs)[0]
    new_values = new_optimizer._critic.critic_pass(new_inputs)[0]
    assert all(torch.allclose(old_values[k], new_values[k], atol=3e-5, rtol=3e-5) for k in old_values)
print('PASS preserved actor/critic outputs, latest six channels and all unrelated weights')
new_policy.actor.network_body(new_inputs)[0].square().mean().backward()
first = new_policy.actor.network_body.observation_encoder.processors[0].conv_layers[0]
assert first.weight.grad[:, :24].abs().sum() > 0
new_optimizer.optimizer.step()
assert first.weight[:, :24].abs().sum() > 0
assert all(torch.isfinite(p).all() for p in new_policy.actor.parameters())
with tempfile.TemporaryDirectory(prefix='baseball-camera30-') as folder:
    output = str(Path(folder) / 'batter')
    ModelSerializer(new_policy).export_policy_model(output)
    model = onnx.load(output + '.onnx')
    onnx.checker.check_model(model)
    shapes = {v.name: [d.dim_value for d in v.type.tensor_type.shape.dim] for v in model.graph.input}
    assert shapes['obs_0'][1:] == [30, 256, 256] and shapes['obs_1'][1:] == [16]
print('PASS new-channel learning, fresh Adam, thirty-frame ONNX and stage-2/3 configs')
