"""Synthetic verifier F01 for the pack loader tests."""
SENTINEL = "ZZ_PRIVATE_SENTINEL_F01"
ASSERTIONS = [("A1", 10.0, False, ["R1"], "fixture assertion")]
HINTS = {"A1": {"zh-CN": "fixture hint", "en": "fixture hint"}}


def verify(ctx, card):
    card.check("A1", True)

# Producer:Betsy
