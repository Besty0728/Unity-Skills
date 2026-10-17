"""Public verification library (contract 5.8): what pack task modules and private verifiers import.

    from usk_eval import verifylib as V

The library reads the editor only through Probe (read-only skills) and the disk; nothing in it writes.
"""
from . import uyaml
from .checks import (REF_RE, address, answer, as_int, close_tuple, floats, go_state, identity_rotation, local_trs,
                     material_color, material_state, new_children, norm_path, object_id, parse_ref, quat_angle_deg,
                     quat_close, quat_from_euler, same, same_id, vec_close, world_from_local)
from .probe import Probe
from .scorecard import Scorecard
from .snapshot import asset_listing, canonical_digest, fingerprint, hierarchy_signature, structure
from ..scope import adopted_paths, scope_violations, under

__all__ = [
    "Scorecard", "Probe", "uyaml",
    "floats", "vec_close", "quat_from_euler", "quat_angle_deg", "quat_close", "parse_ref", "REF_RE",
    "go_state", "material_state", "material_color", "close_tuple", "same", "new_children", "local_trs",
    "identity_rotation", "answer", "as_int", "norm_path", "world_from_local", "object_id", "same_id", "address",
    "canonical_digest", "structure", "hierarchy_signature", "asset_listing", "fingerprint",
    "scope_violations", "adopted_paths", "under",
]

# Producer:Betsy
