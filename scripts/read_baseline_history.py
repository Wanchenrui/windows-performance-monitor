"""Read only aggregate coverage from the owned baseline SQLite database."""

import json
import sqlite3
import sys
from collections import Counter, defaultdict
from pathlib import Path


def inspect_history(path: Path) -> dict:
    with sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True) as db:
        schema = db.execute("PRAGMA user_version").fetchone()[0]
        integrity = db.execute("PRAGMA integrity_check(1)").fetchone()[0]
        groups = defaultdict(Counter)
        previous = {}
        missing = 0
        invalid = 0
        count = 0
        max_gap = 0.0
        first_elapsed = None
        last_elapsed = None
        for instance, delivery, raw in db.execute(
            "SELECT instance_id, delivery_sequence, snapshot_json "
            "FROM snapshots_raw ORDER BY persistence_order"
        ):
            count += 1
            snapshot = json.loads(raw)
            elapsed = snapshot.get("elapsedSeconds")
            if not isinstance(delivery, int) or not isinstance(elapsed, (float, int)):
                invalid += 1
                continue
            prior = previous.get(instance)
            if prior is not None:
                missing += max(0, delivery - prior[0] - 1)
                max_gap = max(max_gap, elapsed - prior[1])
            previous[instance] = (delivery, elapsed)
            if first_elapsed is None:
                first_elapsed = elapsed
            last_elapsed = elapsed
            for group_id, group in snapshot.get("groups", {}).items():
                # Do not emit values, process names, device IDs, paths, or subjects.
                groups[group_id]["availability:" + str(group.get("availability"))] += 1
                groups[group_id]["freshness:" + str(group.get("freshness"))] += 1
        return {
            "schemaVersion": schema,
            "integrityCheck": integrity,
            "persistedSnapshots": count,
            "instanceCount": len(previous),
            "missingDeliverySequencesBetweenPersistedRows": missing,
            "invalidTimeOrDeliveryMetadataRows": invalid,
            "firstElapsedSeconds": first_elapsed,
            "lastElapsedSeconds": last_elapsed,
            "maximumPersistedElapsedGapSeconds": max_gap,
            "groupQualityCounts": dict(groups),
            "boundary": "Persisted coverage only; rows before the first or after the last "
            "persisted delivery and all transient fanout/provider events are not proven.",
        }


if __name__ == "__main__":
    result = inspect_history(Path(sys.argv[1]))
    print(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False))
