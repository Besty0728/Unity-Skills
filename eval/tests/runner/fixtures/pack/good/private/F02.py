"""Synthetic verifier F02 for the pack loader tests."""
SENTINEL = "ZZ_PRIVATE_SENTINEL_F02"
ASSERTIONS = [("A1", 10.0, True, ["R1", "G1"], "fixture assertion")]
SCENARIO = None


def verify(ctx, card):
    card.check("A1", True)


def verify_runtime(ctx, card):
    card.check("A1", True)

# Producer:Betsy
