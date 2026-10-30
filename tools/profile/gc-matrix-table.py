"""Median table (Markdown) of a gc-matrix.sh results file: one row per configuration, medians over the repetitions."""
import json
import statistics
import sys

COLUMNS = [
    ("peakRssMb", "peak RSS MB"),
    ("steadyRssMb", "steady RSS MB"),
    ("gcPauseP50Ms", "GC pause p50 ms"),
    ("gcPauseP99Ms", "GC pause p99 ms"),
    ("gcPauseMaxMs", "GC pause max ms"),
    ("cpuSeconds", "CPU s"),
    ("wallSeconds", "wall s"),
    ("gen2", "gen2"),
    ("gcPauses", "pauses"),
]


def main(path):
    runs = {}
    order = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            row = json.loads(line)
            if row["name"] not in runs:
                runs[row["name"]] = []
                order.append(row["name"])
            runs[row["name"]].append(row)
    print("| configuration | runs | " + " | ".join(title for _, title in COLUMNS) + " | load |")
    print("|---|---|" + "---|" * len(COLUMNS) + "---|")
    for name in order:
        rows = runs[name]
        cells = []
        for key, _ in COLUMNS:
            values = [r["result"][key] for r in rows if r["result"].get(key) is not None]
            cells.append("%.1f" % statistics.median(values) if values else "-")
        loads = ", ".join(r["load"] for r in rows)
        print("| %s | %d | %s | %s |" % (name, len(rows), " | ".join(cells), loads))


if __name__ == "__main__":
    main(sys.argv[1])
