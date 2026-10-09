"""Preserve a stage-1 Camera30 checkpoint for explicitly requested direct stage 3.

Does not forge preparation/2nd-stage completion and never starts training.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import shutil
import torch
import yaml
from prepare_batter import validate_camera_checkpoint


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--checkpoint', type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    source = args.checkpoint.resolve()
    destination = root / 'Training/initialization/stage3_direct_camera30/checkpoint.pt'
    config_path = root / 'Training/config/stage3_full_team_camera30_direct.yaml'
    if destination.exists() or config_path.exists():
        raise FileExistsError('Preserve the existing direct-stage-3 initialization/configuration')
    digest = hashlib.sha256(source.read_bytes()).hexdigest()
    saved = validate_camera_checkpoint(source, 30)
    for group in ('Policy', 'Optimizer:critic'):
        assert saved[group] and all(bool(torch.isfinite(v).all()) for v in saved[group].values() if isinstance(v, torch.Tensor))
    step = int(saved['global_step']['_GlobalSteps__global_step'].item())
    data = yaml.safe_load((root / 'Training/config/stage3_full_team_camera30_selfplay.yaml').read_text(encoding='utf-8'))
    assert set(data['behaviors']) == {'BaseballBatter', 'BaseballPitcher', 'BaseballRunner', 'BaseballFielder'}
    assert data['environment_parameters'] == {'batter_prepared': 1.0}
    for name, settings in data['behaviors'].items():
        settings.pop('init_path', None)
    data['behaviors']['BaseballBatter']['init_path'] = 'Training/initialization/stage3_direct_camera30/checkpoint.pt'
    # The trained actor is Camera30-compatible; untrained opponents/fielding start fresh.
    destination.parent.mkdir(parents=True, exist_ok=False)
    shutil.copy2(source, destination)
    if hashlib.sha256(source.read_bytes()).hexdigest() != digest or hashlib.sha256(destination.read_bytes()).hexdigest() != digest:
        raise RuntimeError('Source checkpoint changed while making the initialization copy')
    header = '# User-requested direct stage 3; preparation and stage 2 were deliberately skipped.\n# Only the stage-1 batter is initialized. Use Training/train_cached.py.\n'
    config_path.write_text(header + yaml.safe_dump(data, sort_keys=False), encoding='utf-8')
    skills = source.parent.parent / 'skills-status.json'
    metadata = {'prepared_at_utc': datetime.now(timezone.utc).isoformat(), 'manual_direct_stage3': True,
                'preparation_completed': False, 'stage2_completed': False,
                'source': str(source), 'source_step': step, 'checkpoint_sha256': digest,
                'initialization': str(destination), 'config': str(config_path),
                'fresh_behaviors': ['BaseballPitcher', 'BaseballRunner', 'BaseballFielder'],
                'skills_at_source': json.loads(skills.read_text()) if skills.is_file() else None}
    (destination.parent / 'source.json').write_text(json.dumps(metadata, indent=2), encoding='utf-8')
    print('Prepared manual direct stage 3 from preserved batter step', step, digest)


if __name__ == '__main__':
    main()
