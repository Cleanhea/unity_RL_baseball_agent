"""Verify real stage-3 Camera30 conversion and actor compatibility without training."""
from datetime import datetime, timezone
from pathlib import Path
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
from visual_cache import install


def main():
    root = Path(__file__).resolve().parents[1]
    out = root / 'Training/evaluations/visual_cache_stage3_20261009'
    report_path = out / 'player-probe.json'
    assert not report_path.exists()
    source = root / 'Training/initialization/stage3_direct_camera30/checkpoint.pt'
    digest = hashlib.sha256(source.read_bytes()).hexdigest()
    saved = torch.load(source, map_location='cpu', weights_only=False)
    options = parse_command_line([str(root / 'Training/config/stage3_full_team_camera30_direct.yaml'), '--run-id=probe_only'])
    cache = install(verify_first=1000000)  # Compare every real visual observation with stock.
    engine, parameters, stats = EngineConfigurationChannel(), EnvironmentParametersChannel(), StatsSideChannel()
    engine.set_configuration_parameters(time_scale=20, target_frame_rate=-1, quality_level=5)
    parameters.set_float_parameter('batter_prepared', 1)
    env = UnityEnvironment(file_name=str(root / 'Training/builds/Stage3_Camera30/Stage3_Camera30.exe'),
                           base_port=57382, seed=20261009, timeout_wait=90, no_graphics=False,
                           side_channels=[engine, parameters, stats], additional_args=['-batchmode', '-force-d3d11'],
                           log_folder=str(out / 'probe_logs'))
    report = {'started_at_utc': datetime.now(timezone.utc).isoformat(), 'checkpoint_sha256': digest,
              'source_step': int(saved['global_step']['_GlobalSteps__global_step'].item()),
              'observations': 0, 'batter_policy_calls': 0, 'behaviors': []}
    policies = {}
    seen = set()
    def register_behaviors():
        for name, spec in env.behavior_specs.items():
            if name in seen:
                continue
            seen.add(name)
            report['behaviors'].append({'name': name, 'shapes': [list(x.shape) for x in spec.observation_specs]})
            if name.split('?')[0] == 'BaseballBatter':
                assert sorted(tuple(o.shape) for o in spec.observation_specs) == sorted([(30, 256, 256), (16,)])
                policy = TorchPolicy(20261009, spec, options.behaviors['BaseballBatter'].network_settings, SimpleActor, {})
                policy.actor.load_state_dict(saved['Policy'], strict=True)
                policy.actor.eval()
                policies[name] = policy
    try:
        env.reset()
        for step in range(750):
            # Unity publishes each behavior when its first actual decision is requested.
            register_behaviors()
            for name, spec in env.behavior_specs.items():
                requested, terminal = env.get_steps(name)
                assert all(np.isfinite(o).all() for group in (requested, terminal) for o in group.obs)
                report['observations'] += len(requested) + len(terminal)
                if not len(requested):
                    continue
                if name in policies:
                    action = policies[name].get_action(requested).env_action
                    assert np.isfinite(action.continuous).all()
                    report['batter_policy_calls'] += 1
                    vi = next(i for i, o in enumerate(spec.observation_specs) if tuple(o.shape) == (16,))
                    assert np.max(np.abs(requested.obs[vi][:, 3:5])) < 1e-6
                else:
                    # Probe only: legal neutral actions, respecting discrete masks.
                    discrete = np.zeros((len(requested), spec.action_spec.discrete_size), dtype=np.int32)
                    for branch, mask in enumerate(requested.action_mask or []):
                        discrete[:, branch] = np.argmin(mask, axis=1)
                    if name.split('?')[0] == 'BaseballPitcher':
                        discrete[:, 1:3] = 2  # Central cells, matching the existing neutral heuristic.
                    action = ActionTuple(np.zeros((len(requested), spec.action_spec.continuous_size), dtype=np.float32), discrete)
                env.set_actions(name, action)
            env.step()
            stats.get_and_reset_stats()
            if {name.split('?')[0] for name in env.behavior_specs} == set(options.behaviors) and cache.verified >= 150:
                break
        register_behaviors()
        assert {name.split('?')[0] for name in seen} == set(options.behaviors), seen
        assert cache.verified >= 100 and cache.hits > cache.misses
        assert report['batter_policy_calls'] >= 20
        assert hashlib.sha256(source.read_bytes()).hexdigest() == digest
        report['cache'] = cache.snapshot()
        report['all_visual_inputs_bit_exact'] = True
        report['checkpoint_unchanged'] = True
        report['status'] = 'PASS'
        report_path.write_text(json.dumps(report, indent=2), encoding='utf-8')
        print('STAGE3_CACHED_PLAYER_PASS', report['observations'], 'observations', cache.snapshot(), flush=True)
    finally:
        env.close()


if __name__ == '__main__':
    main()
