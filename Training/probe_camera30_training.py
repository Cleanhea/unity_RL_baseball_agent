"""Run a bounded real Camera30 training probe and record memory and learned channels."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import subprocess
import sys
import time
import argparse
import psutil
import yaml

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, default=ROOT / 'Training/evaluations/camera30_20261009')
OUT = parser.parse_args().output.resolve()
OUT.mkdir(parents=True, exist_ok=True)
run = OUT / 'smoke_results/camera30_smoke'
if run.exists():
    raise FileExistsError('The probe results already exist; preserve them')
data = yaml.safe_load((ROOT / 'Training/config/batter_preparation_camera30.yaml').read_text())
batter = data['behaviors']['BaseballBatter']
batter.update(max_steps=1024, summary_freq=512, checkpoint_interval=512, keep_checkpoints=2)
config = OUT / 'smoke.yaml'
config.write_text(yaml.safe_dump(data, sort_keys=False), encoding='utf-8')
command = [sys.executable, '-u', '-B', str(ROOT / 'Training/prepare_batter.py'), str(config),
           '--camera-stacks=30', '--initial-phase=1', '--run-id=camera30_smoke',
           '--results-dir=' + str(run.parent),
           '--env=' + str(ROOT / 'Training/builds/BatterCamera30/BatterCamera30.exe'),
           '--base-port=57371', '--env-args', '-batchmode', '-force-d3d11']
source = ROOT / 'Training/results/stage1_batter_camera6_skills_prepare/BaseballBatter/checkpoint.pt'
source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
report = {'started_at_utc': datetime.now(timezone.utc).isoformat(), 'command': command,
          'baseline_available_ram_gib': psutil.virtual_memory().available / 2**30,
          'minimum_available_ram_gib': psutil.virtual_memory().available / 2**30,
          'peak_process_working_set_gib': 0, 'peak_system_gpu_memory_mib': 0}
started = time.monotonic()
with (OUT / 'smoke-training.log').open('x', encoding='utf-8') as log:
    process = subprocess.Popen(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT,
                               creationflags=subprocess.CREATE_NO_WINDOW)
    tracked = psutil.Process(process.pid)
    while process.poll() is None:
        report['minimum_available_ram_gib'] = min(report['minimum_available_ram_gib'], psutil.virtual_memory().available / 2**30)
        try:
            processes = [tracked] + tracked.children(recursive=True)
            working_set = sum(p.memory_info().rss for p in processes if p.is_running()) / 2**30
            report['peak_process_working_set_gib'] = max(report['peak_process_working_set_gib'], working_set)
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            pass
        gpu = subprocess.run(['nvidia-smi', '--query-gpu=memory.used', '--format=csv,noheader,nounits'],
                              capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW)
        if gpu.returncode == 0:
            report['peak_system_gpu_memory_mib'] = max(report['peak_system_gpu_memory_mib'], int(gpu.stdout.strip()))
        time.sleep(2)
    report['exit_code'] = process.returncode
report['elapsed_seconds'] = time.monotonic() - started
if process.returncode:
    (OUT / 'smoke-report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    raise RuntimeError('Real Camera30 training failed; inspect smoke-training.log')

import torch
from tensorboard.backend.event_processing.event_accumulator import EventAccumulator
saved = torch.load(run / 'BaseballBatter/checkpoint.pt', map_location='cpu', weights_only=False)
report['saved_step'] = int(saved['global_step']['_GlobalSteps__global_step'].item())
report['decisions_per_second_including_startup'] = report['saved_step'] / report['elapsed_seconds']
for group in ('Policy', 'Optimizer:critic'):
    assert all(torch.isfinite(v).all() for v in saved[group].values() if isinstance(v, torch.Tensor))
    camera = next(v for k, v in saved[group].items() if k.endswith('conv_layers.0.weight'))
    assert tuple(camera.shape) == (16, 30, 8, 8)
    report[group + '_new_channels_abs_sum'] = float(camera[:, :24].abs().sum())
    assert report[group + '_new_channels_abs_sum'] > 0
events = EventAccumulator(str(run / 'BaseballBatter'), size_guidance={'scalars': 0})
events.Reload()
for tag in ('Batter Observation/Stack Count', 'Batter Observation/Decision Interval (ms)',
            'Batter Observation/History Span (ms)', 'Batter Preparation/Phase', 'Losses/Policy Loss', 'Losses/Value Loss'):
    values = events.Scalars(tag)
    assert values, tag
    report[tag] = values[-1].value
assert report['Batter Observation/Stack Count'] == 30
assert abs(report['Batter Observation/Decision Interval (ms)'] - 10) < 0.01
assert abs(report['Batter Observation/History Span (ms)'] - 290) < 0.01
assert report['Batter Preparation/Phase'] == 1
assert hashlib.sha256(source.read_bytes()).hexdigest() == source_hash
report['source_sha256_unchanged'] = source_hash
(OUT / 'smoke-report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report, indent=2))
