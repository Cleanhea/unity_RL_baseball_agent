"""Warm-start a 192x192, 12-frame policy from a 256x256, 30-frame policy.

This is an approximate transfer, not a claim of identical actions or quality.
The latest eleven channels retain their weights; older channels are folded
into the oldest available frame. Dense spatial weights are resized with their
integral scale retained. All other learned tensors are preserved.
"""
import argparse
import hashlib
import json
from pathlib import Path
import torch
from torch.nn import functional as F


def resize_checkpoint(state):
    changed = []
    for group in ('Policy', 'Optimizer:critic'):
        weights = state[group]
        conv_keys = [key for key in weights if key.endswith('conv_layers.0.weight')]
        if not conv_keys:
            raise ValueError(f'Missing visual encoder: {group}')
        for key in conv_keys:
            old = weights[key]
            if tuple(old.shape) != (16, 30, 8, 8):
                raise ValueError(f'Expected Camera30 encoder: {group}/{key}: {old.shape}')
            # Unity's temporal order is oldest -> newest, 10 ms apart.
            weights[key] = torch.cat((old[:, :19].sum(dim=1, keepdim=True), old[:, 19:]), dim=1)
            dense_key = key.replace('conv_layers.0.weight', 'dense.0.weight')
            dense = weights[dense_key]
            if dense.ndim != 2 or dense.shape[1] != 32 * 30 * 30:
                raise ValueError(f'Expected 256px simple encoder dense weights: {dense_key}')
            spatial = dense.reshape(dense.shape[0], 32, 30, 30)
            resized = F.interpolate(spatial, size=(22, 22), mode='bilinear', align_corners=False)
            resized = resized * (30 * 30 / (22 * 22))
            weights[dense_key] = resized.reshape(dense.shape[0], 32 * 22 * 22)
            changed.extend([f'{group}/{key}', f'{group}/{dense_key}'])
        if not all(bool(torch.isfinite(v).all()) for v in weights.values() if isinstance(v, torch.Tensor)):
            raise ValueError(f'Nonfinite checkpoint tensor: {group}')
    # Changed tensor dimensions invalidate the old Adam moments. Init resets steps.
    state['Optimizer:value_optimizer']['state'] = {}
    return changed


def convert(source, output):
    source, output = Path(source).resolve(), Path(output).resolve()
    metadata_path = output.with_suffix('.json')
    if output.exists() or metadata_path.exists():
        raise FileExistsError('Choose a new initialization path; preserve existing files')
    digest = hashlib.sha256(source.read_bytes()).hexdigest()
    state = torch.load(source, map_location='cpu', weights_only=False)
    changed = resize_checkpoint(state)
    output.parent.mkdir(parents=True, exist_ok=True)
    torch.save(state, output)
    if hashlib.sha256(source.read_bytes()).hexdigest() != digest:
        raise RuntimeError('Source changed during conversion')
    metadata = {'source': str(source), 'source_sha256': digest,
                'source_step': int(state['global_step']['_GlobalSteps__global_step'].item()),
                'source_shape': [30, 256, 256], 'target_shape': [12, 192, 192],
                'output_sha256': hashlib.sha256(output.read_bytes()).hexdigest(),
                'changed': changed, 'transfer': 'approximate_warm_start',
                'temporal_mapping': 'old 0..18 sum -> new 0; old 19..29 -> new 1..11',
                'spatial_mapping': 'bilinear 30x30 -> 22x22 dense weights, area scale',
                'optimizer_moments_reset': True, 'equal_policy_outputs_claimed': False}
    metadata_path.write_text(json.dumps(metadata, indent=2), encoding='utf-8')
    return state, metadata


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    _, info = convert(args.source, args.output)
    print(json.dumps(info, indent=2))
