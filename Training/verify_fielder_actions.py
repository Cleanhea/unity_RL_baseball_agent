"""Verify installed ML-Agents fielder collection/export without running training.
Run: python Training/verify_fielder_actions.py
Only a scratch ONNX is written under the system temporary directory.
"""

from pathlib import Path
import sys, ast, tempfile
import numpy as np
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from mlagents.torch_utils import torch
from mlagents_envs.base_env import ActionSpec
from mlagents.trainers.torch_entities.action_model import ActionModel
from baseball_mlagents.actions import FielderActionModel, FielderActor
from baseball_mlagents.trainer import FielderPOCATrainer, TRAINER_NAME
from mlagents.plugins.trainer_type import register_trainer_plugins
from mlagents.trainers.learn import parse_command_line
from mlagents.trainers.torch_entities.networks import SimpleActor
from mlagents.trainers.settings import NetworkSettings
from mlagents.trainers.behavior_id_utils import BehaviorIdentifiers
from mlagents_envs.base_env import BehaviorSpec, ObservationSpec, ObservationType, DimensionProperty

def make(n, branches, hidden=2, model_class=ActionModel):
    return model_class(hidden, ActionSpec(n, np.array(branches, dtype=np.int32)), deterministic=True)

raw = torch.tensor([[4.,2.],[-4.,-2.],[-2.,4.],[2.,-4.],[.3,.4],[0.,0.],[3.,0.]])
mask = torch.ones((len(raw),5))
fielder=make(2,[5])
with torch.no_grad():
    fielder._continuous_distribution.mu.weight.copy_(torch.eye(2))
    fielder._continuous_distribution.mu.bias.zero_()
    fielder._continuous_distribution.log_sigma.fill_(-20.)
baseline=fielder(raw,mask)[0].to_action_tuple(clip=True).continuous.copy()
keys_before=list(fielder.state_dict())
others=[]
for n,branches in [(7,[2]),(1,[5,5,5]),(0,[3])]:
    model=make(n,branches)
    inputs=torch.tensor([[4.,2.],[-2.,4.]])
    masks=torch.ones((2,sum(branches)))
    torch.manual_seed(123)
    action, lp, entropy=model(inputs,masks)
    torch.manual_seed(321)
    out=model.get_action_out(inputs,masks)
    others.append((model,inputs,masks,action.to_action_tuple(clip=True),lp.continuous_tensor,entropy,out))
standard_model = fielder
fielder = make(2, [5], model_class=FielderActionModel)
fielder.load_state_dict(standard_model.state_dict())
action, lp, entropy=fielder(raw,mask)
expected=raw.cpu().numpy()/3.
expected/=np.maximum(1.,np.linalg.norm(expected,axis=1,keepdims=True))
np.testing.assert_allclose(action.to_action_tuple(clip=True).continuous,expected,atol=1e-6)
np.testing.assert_allclose(action.to_action_tuple(clip=False).continuous,raw.cpu().numpy(),atol=1e-6)
out=fielder.get_action_out(raw,mask)
np.testing.assert_allclose(out[0].detach().cpu().numpy(),expected,atol=1e-6)
np.testing.assert_allclose(out[3].detach().cpu().numpy(),expected,atol=1e-6)
assert out[2] is None and fielder.clip_action
assert list(fielder.state_dict())==keys_before
print("PASS fielder environment, stochastic/deterministic export, raw optimizer actions, zero/small/large vectors, checkpoint keys")
print("PASS raw (4,2): old",baseline[0],"new",expected[0])
for model,inputs,masks,before,lp_before,entropy_before,out_before in others:
    torch.manual_seed(123)
    action,lp,entropy=model(inputs,masks)
    after=action.to_action_tuple(clip=True)
    np.testing.assert_array_equal(before.continuous,after.continuous)
    np.testing.assert_array_equal(before.discrete,after.discrete)
    torch.testing.assert_close(lp.continuous_tensor,lp_before)
    torch.testing.assert_close(entropy,entropy_before)
    torch.manual_seed(321)
    outputs=model.get_action_out(inputs,masks)
    for a,b in zip(outputs,out_before):
        if a is None: assert b is None
        else: torch.testing.assert_close(a,b)
print("PASS batter, pitcher and runner environment/export/log-probabilities unchanged; no global ActionModel patch")
assert ActionModel.forward is not FielderActionModel.forward

# Exercise the same plugin discovery and parsing path used by the standard CLI.
trainer_types, _ = register_trainer_plugins()
assert trainer_types[TRAINER_NAME] is FielderPOCATrainer
for filename in ["stage3_full_team.yaml", "stage3_full_team_resume.yaml", "stage3_full_team_selfplay.yaml", "stage3_full_team_refield.yaml"]:
    options = parse_command_line([str(ROOT/"Training/config"/filename), "--run-id=verify", "--results-dir=unused-verify-results"])
    fielder_settings = options.behaviors["BaseballFielder"]
    assert fielder_settings.trainer_type == TRAINER_NAME
    assert fielder_settings.hyperparameters.beta == 0.005
    assert fielder_settings.hyperparameters.batch_size == 128
    assert fielder_settings.hyperparameters.buffer_size == 2048
    assert fielder_settings.max_steps == 3000000
    assert fielder_settings.reward_signals[next(iter(fielder_settings.reward_signals))].gamma == 0.99
    assert options.behaviors["BaseballBatter"].trainer_type == "ppo"
    assert options.behaviors["BaseballRunner"].trainer_type == "poca"
    if filename == "stage3_full_team_refield.yaml":
        # Fielders restart; the camera batter cannot load the archived ray model.
        assert fielder_settings.init_path is None
        assert "stage3_full_team_camera_cf/" in str(options.behaviors["BaseballBatter"].init_path)
        for behavior in ("BaseballPitcher", "BaseballRunner"):
            assert "stage3_full_team_camera_cf" in str(options.behaviors[behavior].init_path), behavior
print("PASS ordinary mlagents-learn plugin discovery and all four stage-3 YAML configs, fielder beta=0.005, refield init paths")

observations = [ObservationSpec((77,), (DimensionProperty.NONE,), ObservationType.DEFAULT, "fielder")]
spec = BehaviorSpec(observations, ActionSpec(2, np.array([5], dtype=np.int32)))
network = NetworkSettings(hidden_units=256)
torch.manual_seed(4321)
standard_actor = SimpleActor(observations, network, spec.action_spec)
torch.manual_seed(4321)
radial_actor = FielderActor(observations, network, spec.action_spec)
assert list(standard_actor.state_dict()) == list(radial_actor.state_dict())
for key, value in standard_actor.state_dict().items():
    torch.testing.assert_close(value, radial_actor.state_dict()[key], rtol=0, atol=0)
radial_actor.load_state_dict(standard_actor.state_dict(), strict=True)
with tempfile.TemporaryDirectory(prefix="baseball-trainer-check-") as artifact:
    trainer = FielderPOCATrainer("BaseballFielder", 10, fielder_settings, True, False, 42, artifact)
    policy = trainer.create_policy(BehaviorIdentifiers.from_name_behavior_id("BaseballFielder"), spec)
    assert isinstance(policy.actor, FielderActor)
    assert isinstance(policy.actor.action_model, FielderActionModel)
    trainer.policy = policy
    assert trainer.create_optimizer() is not None
print("PASS POCA policy/optimizer creation and strict checkpoint compatibility")

class ExportHead(torch.nn.Module):
    def __init__(self,model): super().__init__(); self.model=model
    def forward(self,inputs,masks): return self.model.get_action_out(inputs,masks)[3]
onnx_path=Path(tempfile.mkdtemp(prefix="baseball-radial-check-"))/"fielder-radial-head.onnx"
torch.onnx.export(ExportHead(fielder), (raw,mask),str(onnx_path),opset_version=9,input_names=["inputs","masks"],output_names=["deterministic"],dynamic_axes={"inputs":{0:"batch"},"masks":{0:"batch"},"deterministic":{0:"batch"}})
import onnx
from onnx.reference import ReferenceEvaluator
model=onnx.load(str(onnx_path)); onnx.checker.check_model(model)
result=ReferenceEvaluator(model).run(None,{"inputs":raw.cpu().numpy(),"masks":mask.cpu().numpy()})[0]
np.testing.assert_allclose(result,expected,atol=1e-6)
print("PASS ONNX opset 9 export, checker and evaluated radial directions")
for p in ["Training/train.py","Training/auto_curriculum.py", "Training/mlagents_extensions/baseball_mlagents/actions.py", "Training/mlagents_extensions/baseball_mlagents/trainer.py"]: ast.parse((ROOT/p).read_text(encoding="utf-8"))
print("PASS trainer entry and curriculum syntax")

# New CF action branch must pass the actual custom trainer policy/export path.
cf_spec = BehaviorSpec(observations, ActionSpec(2, np.array([3], dtype=np.int32)))
with tempfile.TemporaryDirectory(prefix="baseball-cf-check-") as artifact:
    trainer = FielderPOCATrainer("BaseballFielder", 10, fielder_settings, True, False, 42, artifact)
    policy = trainer.create_policy(BehaviorIdentifiers.from_name_behavior_id("BaseballFielder"), cf_spec)
    assert isinstance(policy.actor, FielderActor)
    inputs = [torch.zeros((2, 77))]
    outputs = policy.actor(inputs, torch.ones((2, 3)))
    assert outputs[2].shape == (2, 2) and outputs[5].shape == (2, 1)
    assert outputs[6].reshape(-1).tolist() == [3.0]
    trainer.policy = policy
    assert trainer.create_optimizer() is not None
    from mlagents.trainers.torch_entities.model_serialization import ModelSerializer
    ModelSerializer(policy).export_policy_model(artifact + "/cf-policy")
    onnx.checker.check_model(onnx.load(artifact + "/cf-policy.onnx"))
print("PASS sole center-fielder policy: 77 observations, continuous 2 + three base destinations")
