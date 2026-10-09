"""Ordinary ML-Agents training with exact stacked-grayscale frame reuse."""
from visual_cache import cached_environment_factory


def main():
    from mlagents.trainers import learn
    options = learn.parse_command_line()
    if options.checkpoint_settings.resume:
        # ML-Agents otherwise prefers init_path even when --resume was requested.
        for settings in options.behaviors.values():
            settings.init_path = None
    original = learn.create_environment_factory
    def factory(*args, **kwargs):
        return cached_environment_factory(original(*args, **kwargs))
    learn.create_environment_factory = factory
    try:
        learn.run_cli(options)
    finally:
        learn.create_environment_factory = original


if __name__ == '__main__':
    main()
