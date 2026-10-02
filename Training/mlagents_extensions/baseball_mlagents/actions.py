"""Keep fielder action direction in environment collection and ONNX export."""

from mlagents.torch_utils import torch
from mlagents.trainers.torch_entities.action_model import ActionModel
from mlagents.trainers.torch_entities.agent_action import AgentAction
from mlagents.trainers.torch_entities.networks import SimpleActor


def radial_output(values):
    # Preserve the standard Gaussian /3 scale; limit length instead of each axis.
    scaled = values / 3.0
    length = torch.sqrt(torch.sum(scaled * scaled, dim=-1, keepdim=True))
    return scaled / torch.clamp(length, min=1.0)


class FielderAction(AgentAction):
    def to_action_tuple(self, clip=False):
        if not clip:
            return super().to_action_tuple(clip=False)
        return AgentAction(radial_output(self.continuous_tensor), self.discrete_list).to_action_tuple(clip=False)


class FielderActionModel(ActionModel):
    def forward(self, inputs, masks):
        action, log_probs, entropy = super().forward(inputs, masks)
        if self.clip_action:
            action = FielderAction(action.continuous_tensor, action.discrete_list)
        # Raw actions and their log probabilities remain in optimizer buffers.
        return action, log_probs, entropy

    def get_action_out(self, inputs, masks):
        if not self.clip_action:
            return super().get_action_out(inputs, masks)
        previous_clip = self.clip_action
        self.clip_action = False
        try:
            outputs = list(super().get_action_out(inputs, masks))
        finally:
            self.clip_action = previous_clip
        outputs[0] = radial_output(outputs[0])
        outputs[3] = radial_output(outputs[3])
        return tuple(outputs)


class FielderActor(SimpleActor):
    def __init__(self, observation_specs, network_settings, action_spec, **kwargs):
        super().__init__(observation_specs, network_settings, action_spec, **kwargs)
        # Reuse the standard actor's initialized weights and checkpoint keys.
        # Do not consume additional RNG values while constructing the replacement.
        with torch.random.fork_rng(devices=[]):
            replacement = FielderActionModel(
                self.encoding_size, action_spec,
                deterministic=network_settings.deterministic, **kwargs,
            )
        replacement.load_state_dict(self.action_model.state_dict())
        self.action_model = replacement
