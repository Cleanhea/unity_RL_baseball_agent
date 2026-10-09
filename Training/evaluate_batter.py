"""Evaluate a frozen camera batter through the real Unity environment.

No trainer/optimizer is created. Deterministic mode uses continuous means and
masked discrete argmax; stochastic mode samples the same unchanged policy.
"""
from __future__ import annotations

import argparse
from collections import defaultdict
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import time

import numpy as np
from mlagents.torch_utils import torch
from mlagents.trainers.learn import parse_command_line
from mlagents.trainers.policy.torch_policy import TorchPolicy
from mlagents.trainers.torch_entities.networks import SimpleActor
from mlagents.trainers.torch_entities.agent_action import AgentAction
from mlagents_envs.environment import UnityEnvironment
from mlagents_envs.side_channel.engine_configuration_channel import EngineConfigurationChannel
from mlagents_envs.side_channel.environment_parameters_channel import EnvironmentParametersChannel
from mlagents_envs.side_channel.stats_side_channel import StatsSideChannel


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest() if hasattr(hashlib, "file_digest") else hashlib.sha256(stream.read()).hexdigest()


def summarize(samples):
    result = {}
    for key, values in samples.items():
        if not values or not all(math.isfinite(v) for v in values):
            raise ValueError(f"Missing/nonfinite samples for {key}")
        result[key] = {"count": len(values), "mean": float(np.mean(values)),
                       "std": float(np.std(values)), "min": min(values), "max": max(values)}
    return result


def evaluate(args, mode, checkpoint, settings, parameters):
    stats, engine, channel = StatsSideChannel(), EngineConfigurationChannel(), EnvironmentParametersChannel()
    engine.set_configuration_parameters(width=84, height=84, quality_level=5,
                                        time_scale=args.time_scale, target_frame_rate=-1)
    for name, value in parameters.items():
        channel.set_float_parameter(name, value)
    log_dir = args.output.parent / (args.output.stem + "_" + mode + "_logs")
    log_dir.mkdir(parents=True, exist_ok=False)
    settings.deterministic = mode == "deterministic"
    samples = defaultdict(list)
    terminal_rewards = []
    steps = decisions = 0
    started = time.monotonic()
    progress_interval = min(100, max(1, args.plate_appearances // 5))
    next_progress = progress_interval
    env = UnityEnvironment(file_name=str(args.env.resolve()), seed=args.seed,
                           base_port=args.base_port, timeout_wait=60, no_graphics=False,
                           side_channels=[engine, channel, stats], additional_args=["-batchmode"] + args.env_args,
                           log_folder=str(log_dir.resolve()))
    try:
        env.reset()
        if len(env.behavior_specs) != 1:
            raise ValueError("Evaluation requires the stage-1 batter-only player")
        behavior, spec = next(iter(env.behavior_specs.items()))
        if behavior.split("?")[0] != "BaseballBatter" or spec.action_spec.continuous_size != 7 or list(spec.action_spec.discrete_branches) != [2]:
            raise ValueError("Unexpected batter behavior/action contract")
        if sorted(tuple(obs.shape) for obs in spec.observation_specs) != sorted([(args.camera_stacks, 256, 256), (16,)]):
            raise ValueError(f"Evaluation requires the {args.camera_stacks}-frame camera and 16 self-state values")
        policy = TorchPolicy(args.seed, spec, settings, SimpleActor, {})
        policy.actor.load_state_dict(checkpoint["Policy"], strict=True)
        policy.actor.eval()
        if mode == "pose_mean":
            # Isolate pose noise without replacing the learned stochastic swing
            # hazard by argmax. This change is confined to this evaluation instance.
            policy.actor.action_model._sample_action = lambda dists: AgentAction(
                dists.continuous.deterministic_sample(), [dist.sample() for dist in dists.discrete])
        if policy.use_recurrent:
            raise ValueError("This evaluator currently supports the feed-forward camera policy")
        while True:
            for key, items in stats.get_and_reset_stats().items():
                samples[key].extend(float(value) for value, _ in items)
            requested, terminal = env.get_steps(behavior)
            terminal_rewards.extend(float(v) for v in terminal.reward)
            count = len(samples["Batter Preparation/Qualified Hit"])
            if count >= next_progress:
                print(f"EVAL {mode}: {count} PA, fair={np.mean(samples['Batter Preparation/Fair Contact']):.3%}, "
                      f"qualified={np.mean(samples['Batter Preparation/Qualified Hit']):.3%}, "
                      f"elapsed={time.monotonic()-started:.1f}s", flush=True)
                next_progress = (count // progress_interval + 1) * progress_interval
            if count >= args.plate_appearances:
                break
            if time.monotonic() - started > args.max_seconds:
                raise TimeoutError("Evaluation time limit reached before the requested completed PAs")
            if len(requested):
                action = policy.get_action(requested).env_action
                if not np.isfinite(action.continuous).all():
                    raise ValueError("Nonfinite action")
                env.set_actions(behavior, action)
                decisions += len(requested)
            env.step()
            steps += 1
        expected = int(parameters["batter_preparation"])
        if set(samples["Batter Preparation/Phase"]) != {float(expected)}:
            raise ValueError("Evaluation mixed preparation phases")
        if parameters.get("batter_skill", -1) >= 0 and set(samples["Batter Skills/Phase"]) != {parameters["batter_skill"]}:
            raise ValueError("Player did not apply the requested skill phase")
        if count != len(samples["Batter Preparation/Fair Contact"]):
            raise ValueError("Completed-PA metrics have different denominators")
        return {"mode": mode, "completed_plate_appearances": count, "environment_steps": steps,
                "decisions": decisions, "elapsed_seconds": time.monotonic()-started,
                "metrics": summarize(samples), "terminal_rewards": summarize({"reward": terminal_rewards})}
    finally:
        env.close()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--camera-stacks", type=int, choices=(6, 30), default=6)
    parser.add_argument("--env", type=Path, default=Path("Training/builds/BatterCamera6/BatterCamera6.exe"))
    parser.add_argument("--config", type=Path, default=Path("Training/config/batter_preparation_camera6.yaml"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--mode", choices=["deterministic", "stochastic", "pose_mean", "both"], default="both")
    parser.add_argument("--plate-appearances", type=int, default=500)
    parser.add_argument("--seed", type=int, default=20261009)
    parser.add_argument("--base-port", type=int, default=57361)
    parser.add_argument("--time-scale", type=float, default=20)
    parser.add_argument("--max-seconds", type=float, default=2400)
    parser.add_argument("--parameter", action="append", default=[], help="Environment parameter name=value")
    parser.add_argument("--env-args", nargs=argparse.REMAINDER, default=[], help="Extra Unity player arguments (place last)")
    args = parser.parse_args(argv)
    if args.output.exists() or args.plate_appearances < 50:
        raise ValueError("Use a new output path and at least 50 completed PAs")
    parameters = {"batter_preparation": 0.0, "batter_prepared": 1.0}
    for pair in args.parameter:
        name, value = pair.split("=", 1)
        parameters[name] = float(value)
    if not all(math.isfinite(v) for v in parameters.values()):
        raise ValueError("Parameters must be finite")
    checkpoint = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    source_hash = digest(args.checkpoint)
    if not all(bool(torch.isfinite(v).all()) for v in checkpoint["Policy"].values() if isinstance(v, torch.Tensor)):
        raise ValueError("Nonfinite checkpoint")
    settings = parse_command_line([str(args.config), "--run-id=evaluate_only"]).behaviors["BaseballBatter"].network_settings
    report = {"version": 1, "started_at_utc": datetime.now(timezone.utc).isoformat(),
              "checkpoint": str(args.checkpoint.resolve()), "checkpoint_sha256": source_hash,
              "checkpoint_step": int(checkpoint["global_step"]["_GlobalSteps__global_step"].item()),
              "env": str(args.env.resolve()), "seed": args.seed, "parameters": parameters, "results": []}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    modes = ["deterministic", "stochastic"] if args.mode == "both" else [args.mode]
    for mode in modes:
        result = evaluate(args, mode, checkpoint, settings, parameters)
        report["results"].append(result)
        if digest(args.checkpoint) != source_hash:
            raise RuntimeError("The evaluated checkpoint changed during evaluation")
        temporary = args.output.with_suffix(".json.tmp")
        temporary.write_text(json.dumps(report, indent=2), encoding="utf-8")
        temporary.replace(args.output)
        print("EVAL_COMPLETE", mode, result["completed_plate_appearances"], flush=True)


if __name__ == "__main__":
    main()
