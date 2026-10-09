"""Prepare Camera30 configs and migrate scenes in the cached isolated Unity project."""
from pathlib import Path
import hashlib
import json
import re
import shutil
import psutil

root = Path(__file__).resolve().parents[1]
artifacts = root / 'Training/evaluations/camera30_20261009'
artifacts.mkdir(parents=True, exist_ok=True)
clone = Path((root / '.utmp/camera-project.txt').read_text(encoding='utf-8').strip()).resolve()
if not clone.name.startswith('baseball-camera-') or not (clone / 'Library').is_dir():
    raise RuntimeError('Expected the existing isolated camera verification project')
for process in psutil.process_iter(['name', 'cmdline']):
    if process.info['name'] != 'Unity.exe':
        continue
    args = process.info['cmdline'] or []
    for i, arg in enumerate(args[:-1]):
        if arg.lower() == '-projectpath' and Path(args[i+1]).resolve() == clone:
            raise RuntimeError('Verification clone is already open; do not change it')

config = root / 'Training/config'
for old, new in [('batter_preparation_camera6.yaml', 'batter_preparation_camera30.yaml'),
                 ('batter_skills_camera6.yaml', 'batter_skills_camera30.yaml'),
                 ('stage2_batter_pitcher_camera6_selfplay.yaml', 'stage2_batter_pitcher_camera30_selfplay.yaml'),
                 ('stage3_full_team_camera6_selfplay.yaml', 'stage3_full_team_camera30_selfplay.yaml')]:
    output = config / new
    if output.exists():
        raise FileExistsError(output)
    content = (config / old).read_text(encoding='utf-8')
    content = content.replace('camera6', 'camera30').replace('Camera6', 'Camera30')
    content = content.replace('six-frame', 'thirty-frame').replace('x6,', 'x30,')
    content = content.replace('batch_size: 128', 'batch_size: 32', 1)
    content = content.replace('buffer_size: 2048', 'buffer_size: 256', 1)
    content = content.replace('time_horizon: 128', 'time_horizon: 64', 1)
    if new == 'batter_skills_camera30.yaml':
        content = content.replace('Training/evaluations/batter_camera30_20261009_771410/checkpoint.pt',
                                  'Training/initialization/batter_camera30_100hz/checkpoint.pt')
    output.write_text('# Camera30: 256x256 grayscale, 100 Hz, 290 ms history. Batch/buffer bound image memory.\n' + content, encoding='utf-8')

scenes = {}
for scene in (root / 'Assets/BaseballSimulation/Scenes/Training').glob('*.unity'):
    raw = scene.read_bytes()
    scenes[str(scene.relative_to(root))] = {
        'sha256': hashlib.sha256(raw).hexdigest(),
        'meta_sha256': hashlib.sha256(scene.with_suffix('.unity.meta').read_bytes()).hexdigest(),
        'ids': [v.decode() for v in re.findall(rb'^--- !u!\d+ &(-?\d+)', raw, re.M)]}
(artifacts / 'scene-baseline.json').write_text(json.dumps(scenes, indent=2), encoding='utf-8')
for name in ('Assets', 'Packages', 'ProjectSettings'):
    shutil.copytree(root / name, clone / name, dirs_exist_ok=True)
helper = (root / '.utmp/Camera6Batch.cs').read_text(encoding='utf-8').replace('Camera6', 'Camera30').replace('CAMERA6', 'CAMERA30').replace('camera6', 'camera30')
(clone / 'Assets/Editor/Camera30Batch.cs').write_text(helper, encoding='utf-8')
(artifacts / 'project.txt').write_text(str(clone), encoding='utf-8')
print('Camera30 configs and isolated Unity project ready:', clone)
