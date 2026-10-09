"""Read-only trained-policy and fixed-pose checks in the staged Camera30 player."""
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
out = root / 'Training/evaluations/timing_progress_20261009'
report_path = out / 'player-probe.json'
if report_path.exists():
    raise FileExistsError('Preserve the previous timing-progress report')
source = out / 'checkpoint.pt'
digest = hashlib.sha256(source.read_bytes()).hexdigest()
saved = torch.load(source, map_location='cpu', weights_only=False)
settings = parse_command_line([str(root / 'Training/config/batter_skills_camera30.yaml'),
                               '--run-id=probe_only']).behaviors['BaseballBatter'].network_settings
engine, parameters, stats = EngineConfigurationChannel(), EnvironmentParametersChannel(), StatsSideChannel()
engine.set_configuration_parameters(time_scale=20, target_frame_rate=-1, quality_level=5)
for key, value in [('batter_preparation', 0), ('batter_prepared', 1), ('batter_skill', 0)]:
    parameters.set_float_parameter(key, value)
(out / 'probe_logs').mkdir(exist_ok=False)
env = UnityEnvironment(file_name=str(out / 'builds/BatterCamera30/BatterCamera30.exe'),
                       base_port=57378, seed=20261009, timeout_wait=90, no_graphics=False,
                       side_channels=[engine, parameters, stats],
                       additional_args=['-batchmode', '-force-d3d11'], log_folder=str(out / 'probe_logs'))
report = {'started_at_utc': datetime.now(timezone.utc).isoformat(), 'checkpoint_sha256': digest,
          'source_checkpoint_step': int(saved['global_step']['_GlobalSteps__global_step'].item()),
          'observations_checked': 0, 'setups_checked': 0, 'policy_calls': 0, 'stats': {}}
try:
    env.reset()
    behavior, spec = next(iter(env.behavior_specs.items()))
    assert len(env.behavior_specs) == 1
    assert sorted(tuple(o.shape) for o in spec.observation_specs) == sorted([(30, 256, 256), (16,)])
    assert spec.action_spec.continuous_size == 7 and list(spec.action_spec.discrete_branches) == [2]
    vi = next(i for i, o in enumerate(spec.observation_specs) if tuple(o.shape) == (16,))
    policy = TorchPolicy(20261009, spec, settings, SimpleActor, {})
    policy.actor.load_state_dict(saved['Policy'], strict=True)
    policy.actor.eval()
    for step in range(300):
        requested, terminal = env.get_steps(behavior)
        assert all(np.isfinite(o).all() for group in (requested, terminal) for o in group.obs)
        vectors = requested.obs[vi]
        if len(requested):
            assert np.max(np.abs(vectors[:, 3:5])) < 1e-6, 'Body moved'
            report['observations_checked'] += len(requested)
            inflight = vectors[:, 1] == 1
            if inflight.any():
                assert np.max(np.abs(vectors[inflight][:, [5, 7]])) < 1e-6, 'Timing phase unlocked bat X/Z'
                assert np.max(np.abs(vectors[inflight][:, 6] + 0.6125)) < 1e-5, 'Central bat height changed'
            if step % 50 == 0:
                sampled = policy.get_action(requested).env_action
                assert np.isfinite(sampled.continuous).all()
                report['policy_calls'] += 1
            # Deliberately extreme body/grip/angle actions: the timing phase ignores them.
            actions = np.full((len(requested), 7), 1 if (step // 50) % 2 == 0 else -1, dtype=np.float32)
            discrete = np.zeros((len(requested), 1), dtype=np.int32)
            report['setups_checked'] += int(np.count_nonzero(vectors[:, 0] == 1))
            # Only this verification script chooses a fixed probe time; training uses policy decisions.
            for i in range(len(requested)):
                if inflight[i] and vectors[i, 2] * 2 >= 0.40 and not requested.action_mask[0][i, 1]:
                    discrete[i, 0] = 1
            env.set_actions(behavior, ActionTuple(actions, discrete))
        env.step()
        for key, values in stats.get_and_reset_stats().items():
            if key.startswith(('Batter Observation/', 'Batter Skills/', 'Batter Preparation/', 'Batted Ball/')):
                report['stats'].setdefault(key, []).extend(float(v) for v, _ in values)
    assert report['setups_checked'] >= 12 and report['policy_calls'] >= 5
    assert set(report['stats']['Batter Skills/Phase']) == {0}
    assert set(report['stats']['Batter Observation/Stack Count']) == {30}
    assert hashlib.sha256(source.read_bytes()).hexdigest() == digest
    report['checkpoint_unchanged'] = True
    report['status'] = 'PASS'
    report_path.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print('TIMING_PROGRESS_PLAYER_PASS', report['observations_checked'], 'observations;', report['setups_checked'], 'setups', flush=True)
finally:
    env.close()
