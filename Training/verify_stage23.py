"""Verify continuation gates and command construction without starting training."""
from pathlib import Path
from types import SimpleNamespace
import json
import tempfile
from unittest.mock import patch

import torch
import yaml

from continue_stage3 import (ROOT, DUEL, ALL, attach_chain, load_options, main,
                             resolve_stage3, validate_stage3_config, wait_for_chain)

CONFIG2 = ROOT / "Training/config/stage2_batter_pitcher_camera6_selfplay.yaml"
CONFIG3 = ROOT / "Training/config/stage3_full_team_camera6_selfplay.yaml"
options3, options2 = validate_stage3_config(CONFIG3, CONFIG2)
assert options3.behaviors["BaseballBatter"].max_steps == 12000000
assert options3.behaviors["BaseballPitcher"].max_steps == 260000
assert options3.behaviors["BaseballRunner"].init_path is None
assert options3.behaviors["BaseballFielder"].init_path is None
assert options3.behaviors["BaseballBatter"].hyperparameters.lambd == options2.behaviors["BaseballBatter"].hyperparameters.lambd
assert options3.behaviors["BaseballBatter"].self_play == options2.behaviors["BaseballBatter"].self_play


def rejects(operation, error):
    try:
        operation()
    except error:
        return
    raise AssertionError("An incomplete/incompatible transition was accepted")


def checkpoint(path, step, stacks=6, camera=True):
    path.parent.mkdir(parents=True, exist_ok=True)
    # Synthetic weights exercise persistence/shape/finite/step gates; no claims
    # of trained performance or Unity inference are made by these fixtures.
    weights = ({"conv_layers.0.weight": torch.zeros(16, stacks, 8, 8)}
               if camera else {"vector.weight": torch.ones(4, 11)})
    torch.save({"global_step": {"_GlobalSteps__global_step": torch.tensor(step)},
                "Policy": weights, "Optimizer:critic": weights}, path)


with tempfile.TemporaryDirectory(prefix="baseball-stage23-") as temp:
    directory = Path(temp)
    preparation, stage2 = directory / "chain_prepare", directory / "finished_stage2"
    source = preparation / "BaseballBatter/checkpoint.pt"
    checkpoint(source, 150000)
    status = preparation / "preparation-status.json"
    state = {"ready": True, "phase": 15, "latest": {"step": 150000}}
    status.write_text(json.dumps(state))
    actual2 = load_options(CONFIG2)
    actual2.behaviors["BaseballBatter"].init_path = str(source)
    recorded = actual2.as_dict()
    recorded["checkpoint_settings"].update(run_id=stage2.name, results_dir=str(directory), resume=False)
    stage2.mkdir()
    saved_config = stage2 / "configuration.yaml"
    saved_config.write_text(yaml.safe_dump(recorded, sort_keys=False))
    for behavior in DUEL:
        checkpoint(stage2 / behavior / "checkpoint.pt", options2.behaviors[behavior].max_steps,
                   camera=behavior == "BaseballBatter")
        (stage2 / (behavior + ".onnx")).write_bytes(b"synthetic final-save marker")
    args = SimpleNamespace(config=CONFIG3, stage2_config=CONFIG2, stage2_run=stage2, preparation_run=preparation)
    data, completed = resolve_stage3(args)
    for behavior in DUEL:
        assert data["behaviors"][behavior]["init_path"] == str((stage2/behavior/"checkpoint.pt").resolve())
        assert completed[behavior]["step"] == options2.behaviors[behavior].max_steps
    for behavior in ALL - DUEL:
        assert data["behaviors"][behavior].get("init_path") is None

    state["ready"] = False; status.write_text(json.dumps(state))
    rejects(lambda: resolve_stage3(args), RuntimeError)
    state["ready"] = True; state["phase"] = 14; status.write_text(json.dumps(state))
    rejects(lambda: resolve_stage3(args), RuntimeError)
    state["phase"] = 15; status.write_text(json.dumps(state))
    checkpoint(source, 149999)
    rejects(lambda: resolve_stage3(args), RuntimeError)
    checkpoint(source, 150000)
    batter = stage2 / "BaseballBatter/checkpoint.pt"
    checkpoint(batter, 12000000, stacks=3)
    rejects(lambda: resolve_stage3(args), ValueError)
    checkpoint(batter, 11999999)
    rejects(lambda: resolve_stage3(args), RuntimeError)
    checkpoint(batter, 12000000)
    pitcher = stage2 / "BaseballPitcher/checkpoint.pt"
    checkpoint(pitcher, 259999, camera=False)
    rejects(lambda: resolve_stage3(args), RuntimeError)
    checkpoint(pitcher, 260000, camera=False)
    final_model = stage2 / "BaseballPitcher.onnx"
    final_model.unlink()
    rejects(lambda: resolve_stage3(args), RuntimeError)
    final_model.write_bytes(b"synthetic final-save marker")
    weights = torch.load(pitcher, weights_only=False)
    weights["Policy"]["vector.weight"][0, 0] = float("nan")
    torch.save(weights, pitcher)
    rejects(lambda: resolve_stage3(args), ValueError)
    checkpoint(pitcher, 260000, camera=False)

    recorded["checkpoint_settings"]["run_id"] = "other_run"
    saved_config.write_text(yaml.safe_dump(recorded))
    rejects(lambda: resolve_stage3(args), ValueError)
    recorded["checkpoint_settings"]["run_id"] = stage2.name
    recorded["behaviors"]["BaseballBatter"]["init_path"] = "old-model/checkpoint.pt"
    saved_config.write_text(yaml.safe_dump(recorded))
    rejects(lambda: resolve_stage3(args), ValueError)
    recorded["behaviors"]["BaseballBatter"]["init_path"] = str(source)
    saved_config.write_text(yaml.safe_dump(recorded))

    invalid = directory / "invalid-stage3.yaml"
    old = yaml.safe_load(CONFIG3.read_text())
    old["environment_parameters"]["batter_prepared"] = 0
    invalid.write_text(yaml.safe_dump(old))
    rejects(lambda: validate_stage3_config(invalid, CONFIG2), ValueError)
    old["environment_parameters"]["batter_prepared"] = 1
    old["behaviors"]["BaseballFielder"]["init_path"] = "legacy/CF/checkpoint.pt"
    invalid.write_text(yaml.safe_dump(old))
    rejects(lambda: validate_stage3_config(invalid, CONFIG2), ValueError)

    # Exercise main -> resolved YAML -> ordinary registered CLI -> completion.
    env, status_dir = directory / "Stage3_Camera6.exe", directory / "watcher"
    env.touch()
    def completed_stage3(command, **kwargs):
        assert command[3] == str(ROOT/"Training/train.py")
        assert "--env-args" in command and "-batchmode" in command
        assert "--force" not in command and "--no-graphics" not in command
        effective = load_options(command[4])
        for behavior in DUEL:
            assert effective.behaviors[behavior].init_path == str((stage2/behavior/"checkpoint.pt").resolve())
        destination = directory / "fresh_stage3"
        for behavior, settings in effective.behaviors.items():
            checkpoint(destination/behavior/"checkpoint.pt", settings.max_steps, camera=behavior=="BaseballBatter")
            (destination/(behavior+".onnx")).write_bytes(b"synthetic final-save marker")
        return SimpleNamespace(returncode=0)
    cli = ["--stage2-finished", "--stage2-run="+str(stage2), "--preparation-run="+str(preparation),
           "--run-id=fresh_stage3", "--results-dir="+str(directory), "--status-dir="+str(status_dir), "--env="+str(env)]
    with patch("continue_stage3.subprocess.run", completed_stage3):
        assert main(cli) == 0
    assert json.loads((status_dir/"continuation-status.json").read_text())["phase"] == "complete"
    assert not (status_dir/"continuation.lock").exists()
    rejects(lambda: main(cli), FileExistsError)  # completed/user results cannot be overwritten

    fake_chain = SimpleNamespace(pid=123, create_time=lambda: 456, wait=lambda: 1)
    wait_args = SimpleNamespace(status_dir=status_dir, chain_run=directory/"chain", stage2_run=stage2, run_id="new_stage3")
    rejects(lambda: wait_for_chain(fake_chain, wait_args), RuntimeError)
    fake_chain.wait = lambda: 0
    assert wait_for_chain(fake_chain, wait_args)
    (status_dir/"stop-requested").touch()
    assert not wait_for_chain(fake_chain, wait_args)

    process = SimpleNamespace(cmdline=lambda: ["python", "prepare_batter.py", "--skills", "--continue-stage2", "--run-id=chain", "--results-dir="+str(directory)], cwd=lambda: str(ROOT))
    with patch("continue_stage3.psutil.Process", return_value=process):
        assert attach_chain(123, directory/"chain") is process
        rejects(lambda: attach_chain(123, directory/"other_chain"), ValueError)

print("PASS actual Camera6/100Hz configs and plugin, full preparation gate, both saved step limits, wrong-run/3-frame/nonfinite/missing-model rejection, exact source paths, fresh runner/CF, CLI launch and no overwrite, process exit and watcher-only cancellation")
