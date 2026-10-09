"""Check fixed batter stance and retained bat controls in the real Camera30 player."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import numpy as np
from mlagents.torch_utils import torch
from mlagents.trainers.learn import parse_command_line
from mlagents.trainers.policy.torch_policy import TorchPolicy
from mlagents.trainers.torch_entities.networks import SimpleActor
from mlagents_envs.environment import UnityEnvironment
from mlagents_envs.base_env import ActionTuple
from mlagents_envs.side_channel.engine_configuration_channel import EngineConfigurationChannel
from mlagents_envs.side_channel.environment_parameters_channel import EnvironmentParametersChannel
from mlagents_envs.side_channel.stats_side_channel import StatsSideChannel

root = Path(__file__).resolve().parents[1]
out = root / 'Training/evaluations/fixed_stance_20261009'
report_path = out / 'player-probe.json'
if report_path.exists():
    raise FileExistsError('Preserve the previous fixed-stance report')
source = root / 'Training/results/stage1_batter_camera30_prepare/BaseballBatter/checkpoint.pt'
digest = hashlib.sha256(source.read_bytes()).hexdigest()
saved = torch.load(source, map_location='cpu', weights_only=False)
settings = parse_command_line([str(root / 'Training/config/batter_preparation_camera30.yaml'),
                               '--run-id=probe_only']).behaviors['BaseballBatter'].network_settings
engine, parameters, stats = EngineConfigurationChannel(), EnvironmentParametersChannel(), StatsSideChannel()
engine.set_configuration_parameters(time_scale=20, target_frame_rate=-1, quality_level=5)
parameters.set_float_parameter('batter_preparation', 1)
parameters.set_float_parameter('batter_prepared', 1)
(out / 'probe_logs').mkdir(exist_ok=False)
env = UnityEnvironment(file_name=str(out / 'builds/BatterCamera30/BatterCamera30.exe'),
                       base_port=57377, seed=20261009, timeout_wait=90, no_graphics=False,
                       side_channels=[engine, parameters, stats],
                       additional_args=['-batchmode', '-force-d3d11'], log_folder=str(out / 'probe_logs'))
report = {'started_at_utc': datetime.now(timezone.utc).isoformat(), 'checkpoint_sha256': digest,
          'checkpoint_step': int(saved['global_step']['_GlobalSteps__global_step'].item()),
          'observations_checked': 0, 'setups_checked': 0, 'policy_calls': 0, 'max_stance_abs': 0,
          'stats': {}}
expected = {}
try:
    env.reset()
    behavior, spec = next(iter(env.behavior_specs.items()))
    assert len(env.behavior_specs) == 1
    assert sorted(tuple(o.shape) for o in spec.observation_specs) == sorted([(30, 256, 256), (16,)])
    assert spec.action_spec.continuous_size == 7 and list(spec.action_spec.discrete_branches) == [2]
    vector_index = next(i for i, o in enumerate(spec.observation_specs) if tuple(o.shape) == (16,))
    policy = TorchPolicy(20261009, spec, settings, SimpleActor, {})
    policy.actor.load_state_dict(saved['Policy'], strict=True)
    policy.actor.eval()
    for step in range(230):
        requested, terminal = env.get_steps(behavior)
        vectors = requested.obs[vector_index]
        assert all(np.isfinite(o).all() for o in requested.obs)
        if len(requested):
            assert np.max(np.abs(vectors[:, 3:5])) < 1e-6, 'Body position changed'
            report['max_stance_abs'] = max(report['max_stance_abs'], float(np.max(np.abs(vectors[:, 3:5]))))
            report['observations_checked'] += len(requested)
            if step % 50 == 0:
                action = policy.get_action(requested).env_action
                assert np.isfinite(action.continuous).all()
                report['policy_calls'] += 1
            actions = np.zeros((len(requested), 7), dtype=np.float32)
            for i, agent_id in enumerate(requested.agent_id):
                if vectors[i, 0] == 1:
                    sign = 1 if (report['setups_checked'] // 4) % 2 == 0 else -1
                    actions[i, 0:2] = [sign, -sign]
                    actions[i, 2:5] = [sign * 0.5, 0, sign * 0.5]
                    expected[int(agent_id)] = sign * 0.25  # Preparation-1 control scale is 0.5.
                    report['setups_checked'] += 1
                elif vectors[i, 1] == 1 and int(agent_id) in expected:
                    value = expected[int(agent_id)]
                    assert abs(vectors[i, 5] - value) < 1e-5 and abs(vectors[i, 7] - value) < 1e-5, 'Bat controls were frozen'
            env.set_actions(behavior, ActionTuple(actions, np.zeros((len(requested), 1), dtype=np.int32)))
        env.step()
        for key, values in stats.get_and_reset_stats().items():
            if key.startswith(('Batter Observation/', 'Batter Preparation/Phase')):
                report['stats'].setdefault(key, []).extend(float(v) for v, _ in values)
    assert report['setups_checked'] >= 12 and report['policy_calls'] >= 4
    assert set(report['stats']['Batter Preparation/Phase']) == {1}
    assert set(report['stats']['Batter Observation/Stack Count']) == {30}
    assert hashlib.sha256(source.read_bytes()).hexdigest() == digest
    report['checkpoint_unchanged'] = True
    report['status'] = 'PASS'
    report_path.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print('FIXED_STANCE_PLAYER_PASS', report['observations_checked'], 'observations;', report['setups_checked'], 'setups', flush=True)
finally:
    env.close()
