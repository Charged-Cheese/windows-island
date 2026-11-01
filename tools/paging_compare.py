"""Summarize preserved paging traces, including fade timeline and callback distributions.

This reads application diagnostics. It does not capture desktop frames or drive UI input.
"""
import argparse
import json
from pathlib import Path
import statistics

from paging_baseline import summarize


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runs", type=Path, nargs="+", required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    runs = []
    for folder in args.runs:
        source = folder / "summary.json"
        report = json.loads(source.read_text(encoding="utf-8"))
        # Re-read raw CSV so older baselines gain the same analysis without changing them.
        cases = summarize(folder / "paging.csv", report["cases"])
        groups = []
        for name in sorted({case["case"] for case in cases}):
            rows = [case for case in cases if case["case"] == name]
            metrics = {}
            for key in ("page_tail_0_5dip_ms", "logical_tail_ms", "old_page_first_hidden_ms",
                        "opacity_completion_tail_ms", "size_completion_tail_ms",
                        "cpu_240_ms", "cpu_900_ms", "idle_cpu_240_ms", "idle_cpu_900_ms"):
                values = [row[key] for row in rows if row.get(key) is not None]
                metrics[key] = {"median": statistics.median(values), "min": min(values), "max": max(values)} if values else None
            intervals = sorted(value for row in rows for value in row["callback_interval_distribution_ms"])
            groups.append({"case": name, "repeats": len(rows), "metrics": metrics,
                           "callback_interval_ms": {"p50": statistics.median(intervals),
                               "p95": intervals[int((len(intervals) - 1) * 0.95)], "max": max(intervals),
                               "over_33ms": sum(value > 33 for value in intervals)}})
        runs.append({"source": str(source), "assembly_sha256": report["assembly_sha256"],
                     "profile": report.get("profile", "stage5-baseline"), "groups": groups})
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps({"runs": runs, "limits": [
        "Opacity completion is an explicit event only in stage-6 traces; older baselines return null.",
        "Old-page disappearance is a sampled visibility transition; callback precision is not presentation precision.",
        "Actual RGB contrast is tested separately by PagingExperience.SelfTest; opacity sums are not whole-page brightness.",
        "CPU values are process-wide and include probe/background activity; retain idle values and avoid causal attribution."
    ]}, indent=2), encoding="utf-8")
    print(f"Wrote {len(runs)} preserved runs to {args.out}")


if __name__ == "__main__":
    main()
