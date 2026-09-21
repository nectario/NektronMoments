"""Server-owned scene model allowlist and conservative standard-tier rates.

Source: https://developers.openai.com/api/docs/pricing (2026-09-21).
Flex can cost less. These rates preserve conservative budget accounting.
"""
from decimal import Decimal

SCENE_MODEL_RATES = {
    # input, cached input, output USD / million tokens; per-call reservation USD
    "gpt-5.6-terra": tuple(map(Decimal, ("2", "0.2", "12", "0.01"))),
    "gpt-5.6-luna": tuple(map(Decimal, ("0.2", "0.02", "1.2", "0.01"))),
    "gpt-5.6-sol": tuple(map(Decimal, ("4", "0.4", "20", "0.02"))),
}
