"""Preserve all four stopped stage-3 policies and prepare manual Camera12_192."""
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import shutil
import numpy as np
import onnx
import torch
import yaml
from mlagents.trainers.learn import parse_command_line
from mlagents.trainers.policy.torch_policy import TorchPolicy
from mlagents.trainers.ppo.optimizer_torch import TorchPPOOptimizer
from mlagents.trainers.torch_entities.networks import SimpleActor
from mlagents.trainers.torch_entities.model_serialization import ModelSerializer
from mlagents_envs.base_env import ActionSpec, BehaviorSpec, ObservationSpec, ObservationType, DimensionProperty
from resize_camera_checkpoint import convert


def batter_spec():
    return BehaviorSpec([
        ObservationSpec((12, 192, 192), (DimensionProperty.NONE, DimensionProperty.TRANSLATIONAL_EQUIVARIANCE,
                         DimensionProperty.TRANSLATIONAL_EQUIVARIANCE), ObservationType.DEFAULT, 'CatcherEye'),
        ObservationSpec((16,), (DimensionProperty.NONE,), ObservationType.DEFAULT, 'batter')],
        ActionSpec(7, np.array([2], dtype=np.int32)))


def verify_and_export(state, settings, output):
    policy = TorchPolicy(20261009, batter_spec(), settings.network_settings, SimpleActor, {})
    optimizer = TorchPPOOptimizer(policy, settings)
    policy.actor.load_state_dict(state['Policy'], strict=True)
    optimizer._critic.load_state_dict(state['Optimizer:critic'], strict=True)
    optimizer.optimizer.load_state_dict(state['Optimizer:value_optimizer'])
    device = next(policy.actor.parameters()).device
    inputs = [torch.rand((2, 12, 192, 192), device=device), torch.zeros((2, 16), device=device)]
    features = policy.actor.network_body(inputs)[0]
    assert bool(torch.isfinite(features).all())
    assert all(bool(torch.isfinite(v).all()) for v in optimizer._critic.critic_pass(inputs)[0].values())
    features.square().mean().backward()
    assert policy.actor.network_body.observation_encoder.processors[0].conv_layers[0].weight.grad.abs().sum() > 0
    # Export the unmodified transferred weights; no synthetic optimizer update.
    policy.actor.zero_grad()
    ModelSerializer(policy).export_policy_model(str(output))
    model = onnx.load(str(output) + '.onnx')
    onnx.checker.check_model(model)
    shapes = {v.name: [d.dim_value for d in v.type.tensor_type.shape.dim] for v in model.graph.input}
    assert shapes['obs_0'][1:] == [12, 192, 192] and shapes['obs_1'][1:] == [16]
    return shapes


def main():
    root = Path(__file__).resolve().parents[1]
    output = root / 'Training/evaluations/camera12_192_20261009'
    output.mkdir(parents=True, exist_ok=True)
    init = root / 'Training/initialization/stage3_direct_camera12_192'
    config = root / 'Training/config/stage3_full_team_camera12_192_direct.yaml'
    if init.exists() or config.exists():
        raise FileExistsError('Preserve existing Camera12 initialization/config')
    source_run = root / 'Training/results/stage3_full_team_camera30_direct'
    data = yaml.safe_load((root / 'Training/config/stage3_full_team_camera30_direct.yaml').read_text(encoding='utf-8'))
    settings = parse_command_line([str(root / 'Training/config/stage3_full_team_camera30_direct.yaml'), '--run-id=verify']).behaviors['BaseballBatter']
    report = {'prepared_at_utc': datetime.now(timezone.utc).isoformat(), 'policies': {}}
    for name in data['behaviors']:
        source = source_run / name / 'checkpoint.pt'
        destination = init / name / 'checkpoint.pt'
        if name == 'BaseballBatter':
            state, metadata = convert(source, destination)
            report['actor_critic_strict_load_gradient_onnx'] = verify_and_export(state, settings, output / 'transferred_batter')
        else:
            state = torch.load(source, map_location='cpu', weights_only=False)
            destination.parent.mkdir(parents=True, exist_ok=False)
            shutil.copy2(source, destination)
            assert destination.read_bytes() == source.read_bytes()
            metadata = {'source_step': int(state['global_step']['_GlobalSteps__global_step'].item()),
                        'source_sha256': hashlib.sha256(source.read_bytes()).hexdigest(), 'unchanged_copy': True}
        data['behaviors'][name]['init_path'] = destination.relative_to(root).as_posix()
        report['policies'][name] = metadata
    # A fixed stage-1 benchmark remains stage-1, separate from live stage-3 weights.
    benchmark_state, benchmark_info = convert(root / 'Training/initialization/stage3_direct_camera30/checkpoint.pt',
                                             output / 'benchmark_stage1/checkpoint.pt')
    model_path = root / 'Assets/BaseballSimulation/Models/BenchmarkBatter_Stage1_Camera12_192'
    assert not model_path.with_suffix('.onnx').exists()
    report['benchmark_shapes'] = verify_and_export(benchmark_state, settings, model_path)
    report['benchmark_source'] = benchmark_info
    config.write_text('# Camera12_192: 192x192 grayscale, 12 frames, 100Hz (110ms history).\n'
                      '# Warm start from normally stopped Camera30 stage 3; use a NEW run-id.\n'
                      + yaml.safe_dump(data, sort_keys=False), encoding='utf-8')
    report['status'] = 'PASS'
    (output / 'transfer-report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print('CAMERA12_192_PREPARED', {k: v['source_step'] for k, v in report['policies'].items()}, flush=True)


if __name__ == '__main__':
    main()
