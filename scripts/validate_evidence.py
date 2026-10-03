"""Validate generated differential and soak evidence against contract v1."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker


ROOT = Path(__file__).resolve().parents[1]
CONTRACTS = ROOT / "contracts" / "v1"


def validate_evidence(schema_name: str, evidence_path: Path) -> None:
    schema_path = CONTRACTS / schema_name
    schema = json.loads(schema_path.read_text(encoding="utf-8"))
    evidence = json.loads(evidence_path.read_text(encoding="utf-8-sig"))
    validator = Draft202012Validator(
        schema,
        format_checker=FormatChecker(),
    )
    errors = sorted(
        validator.iter_errors(evidence),
        key=lambda error: list(error.path),
    )
    if errors:
        details = "\n".join(
            f"{'/'.join(map(str, error.path))}: {error.message}"
            for error in errors
        )
        raise ValueError(f"{evidence_path} evidence is invalid:\n{details}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("schema_name")
    parser.add_argument("evidence_path", type=Path)
    arguments = parser.parse_args()
    validate_evidence(arguments.schema_name, arguments.evidence_path)
    print(f"evidence valid: {arguments.evidence_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
