"""Registered POCA extension, loaded by the ordinary mlagents-learn CLI."""

from mlagents.trainers.poca.trainer import POCATrainer
from mlagents.trainers.poca.optimizer_torch import POCASettings
from mlagents.trainers.policy.torch_policy import TorchPolicy
from mlagents_envs.logging_util import get_logger
from .actions import FielderActor

TRAINER_NAME = "baseball_fielder_poca"
logger = get_logger(__name__)


class FielderPOCATrainer(POCATrainer):
    @staticmethod
    def get_trainer_name():
        return TRAINER_NAME

    def create_policy(self, parsed_behavior_id, behavior_spec):
        spec = behavior_spec.action_spec
        if spec.continuous_size != 2 or tuple(spec.discrete_branches) not in ((3,), (5,)):
            raise ValueError("baseball_fielder_poca requires continuous 2 + discrete [3] (CF) or legacy [5] actions")
        logger.info("Baseball fielder: direction-preserving movement enabled (collection + ONNX)")
        return TorchPolicy(
            self.seed, behavior_spec, self.trainer_settings.network_settings,
            FielderActor, {"conditional_sigma": False, "tanh_squash": False},
        )


def register_trainers():
    return {TRAINER_NAME: FielderPOCATrainer}, {TRAINER_NAME: POCASettings}
