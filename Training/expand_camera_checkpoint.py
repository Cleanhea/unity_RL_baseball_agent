"""Expand a camera policy without changing its output on matching images.

Unity stacks oldest to newest. At 100 Hz, channels [1, 3, 5] have the same
40/20/0-ms ages as the old three channels. New intervening channels start at
zero weight. The 6 -> 30 transfer keeps channels 24..29 (50..0 ms).
All other actor and critic weights are retained. This preserves
the network's function on matching images, not performance under new physics.
"""
from pathlib import Path
import argparse
import hashlib
import json
import torch


def expand_checkpoint(state, target_stacks=6):
    if target_stacks not in (6, 30):
        raise ValueError("Supported transfers are 3 -> 6 and 6 -> 30")
    source_stacks = 3 if target_stacks == 6 else 6
    retained_channels = [1, 3, 5] if target_stacks == 6 else list(range(24, 30))
    changed = []
    for group in ("Policy", "Optimizer:critic"):
        weights = state[group]
        keys = [k for k in weights if k.endswith("conv_layers.0.weight")]
        if not keys:
            raise ValueError(f"No camera convolution found in {group}")
        for key in keys:
            old = weights[key]
            if tuple(old.shape) != (16, source_stacks, 8, 8):
                raise ValueError(f"Expected the {source_stacks}-frame simple camera encoder: {group}/{key}: {old.shape}")
            if not bool(torch.isfinite(old).all()):
                raise ValueError(f"Nonfinite camera weights: {group}/{key}")
            new = old.new_zeros((16, target_stacks, 8, 8))
            new[:, retained_channels] = old
            weights[key] = new
            changed.append(f"{group}/{key}")
    # Adam's old channel-shaped moments are incompatible. Keep optimizer groups
    # but start fresh moments for every parameter in this new initialization.
    state["Optimizer:value_optimizer"]["state"] = {}
    return changed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--target-stacks", type=int, choices=(6, 30), default=6)
    args = parser.parse_args()
    source, output = args.source.resolve(), args.output.resolve()
    if output.exists() or output.with_suffix(".json").exists():
        raise FileExistsError("Use a new initialization path; existing checkpoints are never overwritten")
    state = torch.load(source, map_location="cpu", weights_only=False)
    changed = expand_checkpoint(state, args.target_stacks)
    output.parent.mkdir(parents=True, exist_ok=True)
    torch.save(state, output)
    metadata = {"source": str(source), "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
                "source_step": int(state["global_step"]["_GlobalSteps__global_step"].item()),
                "old_stack": 3 if args.target_stacks == 6 else 6, "new_stack": args.target_stacks,
                "old_interval_ms": 20 if args.target_stacks == 6 else 10, "new_interval_ms": 10,
                "retained_channels": [1, 3, 5] if args.target_stacks == 6 else list(range(24, 30)),
                "changed_weights": changed, "optimizer_moments_reset": True}
    output.with_suffix(".json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    print(f"Expanded {len(changed)} actor/critic convolution entries; source step {metadata['source_step']:,}; saved {output}")


if __name__ == "__main__":
    main()
