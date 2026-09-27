#!/usr/bin/env python3
"""Independent reference implementation of the FPS derived-value formulas (docs/SPEC.md
Component 9 / step7-fps acceptance criteria "Deterministic oracle"). Stdlib only, on purpose:
this must never share a bug with the C# FpsAggregator by sharing code with it.

Formulas (exact, from docs/SPEC.md):
  - Frames are bucketed by CPUStartQPCTime into aligned, half-open 1 s buckets [k, k+1000) ms.
  - A row counts only when FrameTime is present (not NA) and > 0.
  - fps.avg[k]           = 1000 * n_k / sum(ft in bucket k)          (null when n_k < 2)
  - frametime.ms[k]      = mean(ft in bucket k)
  - frametime.jitter[k]  = population stddev(ft in bucket k)
  - fps.low1 (as of bucket k) = 1000 / P99(ft pooled over the up-to-60 most recent tracked
    bucket keys at or before k), nearest-rank percentile (null when the pool has < 100 frames)

Usage:
  python fps_oracle.py <presentmon.csv> [--pid PID]

Without --pid, the PID with the most valid (non-NA, positive FrameTime) rows is used --
for tests/fixtures/presentmon/synthetic-game-60s.csv that is the synthetic game PID (see
make_synthetic_game.py). Prints one JSON object to stdout; redirect to build the committed
".expected.json" file.
"""
import csv
import json
import math
import sys
from collections import defaultdict


def bucket_key(cpu_start_qpc_ms):
    return math.floor(cpu_start_qpc_ms / 1000.0) * 1000


def nearest_rank_percentile(sorted_values, percentile):
    n = len(sorted_values)
    if n == 0:
        return None
    rank_index = math.ceil(percentile * n) - 1
    rank_index = max(0, min(n - 1, rank_index))
    return sorted_values[rank_index]


def load_rows(csv_path):
    with open(csv_path, "r", encoding="ascii", newline="") as f:
        reader = csv.DictReader(f)
        for row in reader:
            yield row


def pick_default_pid(rows):
    counts = defaultdict(int)
    for row in rows:
        ft = row.get("FrameTime", "NA")
        if ft in ("NA", "", None):
            continue
        try:
            value = float(ft)
        except ValueError:
            continue
        if value > 0:
            counts[row["ProcessID"]] += 1

    if not counts:
        return None
    return max(counts.items(), key=lambda kv: kv[1])[0]


def compute(csv_path, pid_filter):
    all_rows = list(load_rows(csv_path))

    if pid_filter is None:
        pid_filter = pick_default_pid(all_rows)
        if pid_filter is None:
            return {"pid": None, "buckets": []}

    buckets = defaultdict(list)  # bucketKey -> [frame times]
    for row in all_rows:
        if row["ProcessID"] != str(pid_filter):
            continue

        ft_text = row.get("FrameTime", "NA")
        if ft_text in ("NA", "", None):
            continue

        try:
            ft = float(ft_text)
        except ValueError:
            continue

        if ft <= 0:
            continue

        qpc = float(row["CPUStartQPCTime"])
        buckets[bucket_key(qpc)].append(ft)

    sorted_keys = sorted(buckets.keys())

    results = []
    window_pool = []  # rolling pool of frame times for the trailing-60-bucket window
    window_keys = []  # bucket keys currently represented in window_pool, oldest first

    for key in sorted_keys:
        frames = buckets[key]
        n = len(frames)
        total = sum(frames)
        mean = total / n
        variance = sum((f - mean) ** 2 for f in frames) / n
        jitter = math.sqrt(variance)
        avg_fps = (1000.0 * n / total) if n >= 2 else None

        window_keys.append(key)
        window_pool.append(frames)
        if len(window_keys) > 60:
            window_keys.pop(0)
            window_pool.pop(0)

        pooled = [f for group in window_pool for f in group]
        low1_fps = None
        if len(pooled) >= 100:
            pooled_sorted = sorted(pooled)
            p99 = nearest_rank_percentile(pooled_sorted, 0.99)
            if p99 and p99 > 0:
                low1_fps = 1000.0 / p99

        results.append({
            "bucketKey": key,
            "n": n,
            "avgFps": round(avg_fps, 6) if avg_fps is not None else None,
            "frametimeMs": round(mean, 6),
            "jitterMs": round(jitter, 6),
            "low1Fps": round(low1_fps, 6) if low1_fps is not None else None,
        })

    return {"pid": int(pid_filter), "buckets": results}


def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        sys.exit(1)

    csv_path = args[0]
    pid_filter = None
    if "--pid" in args:
        pid_filter = args[args.index("--pid") + 1]

    output = compute(csv_path, pid_filter)
    print(json.dumps(output, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
