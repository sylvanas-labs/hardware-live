#!/usr/bin/env python3
"""Generates tests/fixtures/presentmon/synthetic-game-60s.csv (docs/SPEC.md step7-fps): a
deterministic, synthetic 60 s PresentMon v2 (--v2_metrics --qpc_time_ms) capture with one
fullscreen "game" PID at ~144 fps (with jitter, and some FrameTime/DisplayedTime NA rows to
model dropped frames), plus a lower-rate background "msedge.exe" PID for realism (not used by
the oracle comparison -- fps_oracle.py only reports the game PID).

Deterministic: seeded RNG only, no wall-clock/OS entropy, so re-running this script reproduces
byte-identical output. Run from anywhere; paths are relative to this file.
"""
import csv
import os
import random

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
OUTPUT_PATH = os.path.join(SCRIPT_DIR, "..", "fixtures", "presentmon", "synthetic-game-60s.csv")

HEADER = [
    "Application", "ProcessID", "SwapChainAddress", "PresentRuntime", "SyncInterval",
    "PresentFlags", "AllowsTearing", "PresentMode", "CPUStartQPCTime", "FrameTime",
    "CPUBusy", "CPUWait", "GPULatency", "GPUTime", "GPUBusy", "GPUWait", "DisplayLatency",
    "DisplayedTime", "AnimationError", "AnimationTime", "MsFlipDelay",
    "AllInputToPhotonLatency", "ClickToPhotonLatency",
]

GAME_PID = 5000
GAME_APP = "game.exe"
BACKGROUND_PID = 3392
BACKGROUND_APP = "msedge.exe"

QPC_BASE_MS = 1_000_000.0
DURATION_MS = 60_000.0

FRAMETIME_NA_PROBABILITY = 0.02
DISPLAYEDTIME_NA_PROBABILITY = 0.05


def generate_stream(rng, pid, app, avg_frame_time_ms, jitter_stddev_ms, swap_chain, present_mode):
    rows = []
    t = QPC_BASE_MS
    while t < QPC_BASE_MS + DURATION_MS:
        frame_time = max(0.5, rng.gauss(avg_frame_time_ms, jitter_stddev_ms))
        cpu_start_qpc = t

        frame_time_na = rng.random() < FRAMETIME_NA_PROBABILITY
        displayed_time_na = frame_time_na or (rng.random() < DISPLAYEDTIME_NA_PROBABILITY)

        cpu_busy = round(frame_time * 0.6, 4)
        cpu_wait = round(frame_time - cpu_busy, 4)
        gpu_time = round(frame_time * 0.9, 4)
        gpu_busy = round(gpu_time * 0.95, 4)
        gpu_wait = round(gpu_time - gpu_busy, 4)

        rows.append([
            app,
            pid,
            swap_chain,
            "DXGI",
            0,
            0,
            0,
            present_mode,
            f"{cpu_start_qpc:.4f}",
            "NA" if frame_time_na else f"{frame_time:.4f}",
            f"{cpu_busy:.4f}",
            f"{cpu_wait:.4f}",
            f"{gpu_time:.4f}",
            f"{gpu_time:.4f}",
            f"{gpu_busy:.4f}",
            f"{gpu_wait:.4f}",
            f"{(frame_time * 1.5):.4f}",
            "NA" if displayed_time_na else f"{frame_time:.4f}",
            "NA",
            f"{(frame_time * 2):.4f}",
            "NA",
            "NA",
            "NA",
        ])

        t += frame_time

    return rows


def main():
    rng = random.Random(1234)

    game_rows = generate_stream(
        rng, GAME_PID, GAME_APP,
        avg_frame_time_ms=1000.0 / 144.0, jitter_stddev_ms=0.5,
        swap_chain="0x1A2B3C4D5E6F", present_mode="Hardware: Legacy Flip",
    )
    background_rows = generate_stream(
        rng, BACKGROUND_PID, BACKGROUND_APP,
        avg_frame_time_ms=1000.0 / 60.0, jitter_stddev_ms=0.3,
        swap_chain="0x17F95530EE0", present_mode="Hardware Composed: Independent Flip",
    )

    all_rows = game_rows + background_rows
    all_rows.sort(key=lambda row: float(row[8]))

    os.makedirs(os.path.dirname(OUTPUT_PATH), exist_ok=True)
    with open(OUTPUT_PATH, "w", newline="\n", encoding="ascii") as f:
        writer = csv.writer(f, lineterminator="\n")
        writer.writerow(HEADER)
        writer.writerows(all_rows)

    print(f"Wrote {len(all_rows)} rows ({len(game_rows)} game, {len(background_rows)} background) to {OUTPUT_PATH}")
    print(f"Game PID: {GAME_PID}, Background PID: {BACKGROUND_PID}")


if __name__ == "__main__":
    main()
