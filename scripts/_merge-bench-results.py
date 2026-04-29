#!/usr/bin/env python3
"""Merge hyperfine JSON outputs into a z42 baseline-schema.json document.

Used by scripts/bench-run.sh. Not intended for direct invocation by users.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--commit", required=True, help="git commit short SHA")
    p.add_argument("--branch", required=True, help="git branch name")
    p.add_argument("--os", required=True, help="os tag, e.g. darwin-arm64")
    p.add_argument("--timestamp", required=True, help="ISO8601 UTC timestamp")
    p.add_argument("--output", required=True, help="output JSON path")
    p.add_argument("input_jsons", nargs="+", help="hyperfine JSON files")
    args = p.parse_args()

    benchmarks: list[dict] = []
    for f in args.input_jsons:
        path = Path(f)
        try:
            data = json.loads(path.read_text())
        except (json.JSONDecodeError, OSError) as e:
            print(f"error: cannot read {f}: {e}", file=sys.stderr)
            return 2

        for bench in data.get("results", []):
            # hyperfine exports: command, mean, stddev, median, user, system, min, max, times[]
            # All time values are in seconds.
            name = bench.get("command", path.stem.replace("-bench", ""))
            mean_s = bench.get("mean")
            min_s = bench.get("min")
            max_s = bench.get("max")
            times = bench.get("times", [])

            if mean_s is None:
                print(f"error: {f} missing 'mean'", file=sys.stderr)
                return 2

            benchmarks.append({
                "name": name,
                "tier": "z42-e2e",
                "metric": "time",
                "value": round(mean_s * 1000.0, 3),
                "unit": "ms",
                "ci_lower": round(min_s * 1000.0, 3) if min_s is not None else None,
                "ci_upper": round(max_s * 1000.0, 3) if max_s is not None else None,
                "samples": len(times) if times else None,
            })

    output = {
        "schema_version": 1,
        "commit": args.commit,
        "branch": args.branch,
        "os": args.os,
        "timestamp": args.timestamp,
        "benchmarks": benchmarks,
    }

    Path(args.output).write_text(json.dumps(output, indent=2) + "\n")
    print(f"  wrote {len(benchmarks)} benchmark result(s) to {args.output}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
