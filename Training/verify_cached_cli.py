"""Check fresh/resumed model selection and explicit spawned-worker cache setup."""
from pathlib import Path
import sys
from unittest.mock import patch
from mlagents.trainers import learn
import train_cached

root = Path(__file__).resolve().parents[1]
config = root / 'Training/config/stage3_full_team_camera30_direct.yaml'
for resume in (False, True):
    argv = ['train_cached.py', str(config), '--run-id=verify_cached_cli'] + (['--resume'] if resume else [])
    def factory(*args, **kwargs):
        return lambda worker_id, side_channels: ('environment', worker_id)
    def run(options):
        paths = {name: s.init_path for name, s in options.behaviors.items()}
        assert paths['BaseballBatter'] is None if resume else paths['BaseballBatter'].endswith('stage3_direct_camera30/checkpoint.pt')
        assert all(paths[name] is None for name in ('BaseballPitcher', 'BaseballRunner', 'BaseballFielder'))
        with patch('visual_cache.install') as install:
            create = learn.create_environment_factory()
            assert create(7, []) == ('environment', 7)
            install.assert_called_once()
    with patch.object(sys, 'argv', argv), patch.object(learn, 'create_environment_factory', factory), patch.object(learn, 'run_cli', run):
        train_cached.main()
        assert learn.create_environment_factory is factory
print('PASS actual fresh/resume CLI settings, trained batter/fresh opponents, worker cache installation and factory restoration')
