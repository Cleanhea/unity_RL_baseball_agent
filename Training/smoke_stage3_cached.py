"""Short, separate stage-3 training check; always save and stop after 60 seconds."""
from pathlib import Path
import time
from mlagents.trainers import learn
from mlagents.trainers.environment_parameter_manager import EnvironmentParameterManager
from visual_cache import cached_environment_factory


class StopAfterSmoke(EnvironmentParameterManager):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.started = time.monotonic()

    def update_lessons(self, *args, **kwargs):
        if time.monotonic() - self.started > 60:
            raise KeyboardInterrupt
        return super().update_lessons(*args, **kwargs)


def main():
    root = Path(__file__).resolve().parents[1]
    out = root / 'Training/evaluations/visual_cache_stage3_20261009'
    args = [str(root / 'Training/config/stage3_full_team_camera30_direct.yaml'),
            '--run-id=stage3_cache_smoke', '--results-dir=' + str(out / 'smoke_results'),
            '--env=' + str(root / 'Training/builds/Stage3_Camera30/Stage3_Camera30.exe'),
            '--base-port=57383', '--env-args', '-batchmode', '-force-d3d11']
    options = learn.parse_command_line(args)
    original_factory, original_manager = learn.create_environment_factory, learn.EnvironmentParameterManager
    def factory(*a, **k):
        return cached_environment_factory(original_factory(*a, **k))
    learn.create_environment_factory = factory
    learn.EnvironmentParameterManager = StopAfterSmoke
    try:
        learn.run_cli(options)
    finally:
        learn.create_environment_factory = original_factory
        learn.EnvironmentParameterManager = original_manager
    print('STAGE3_CACHE_SMOKE_SAVED', flush=True)


if __name__ == '__main__':
    main()
