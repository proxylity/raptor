#!/usr/bin/env python3
"""Draws SVG charts from a bench/benchmark.sh results.csv. No dependencies beyond Python 3.

Usage: bench/charts.py results.csv out_dir
       bench/charts.py --loss loss-results.csv out.svg   (from bench/loss-benchmark.sh: time against packet loss)

Writes:
  time-old-vs-new.svg   time until every file is in S3: raptor (Lambda + DynamoDB) vs raptor (MicroVM)
  time-all.svg          the same, with the AWS CLI native preview and the Python AWS CLI
  cpu-all.svg           client CPU time (user + sys)
  files-per-second.svg  files per second until in S3, old vs new
Each value is the median of the runs that succeeded and verified.
"""
import csv
import math
import os
import statistics
import sys
from collections import defaultdict

TOOLS = {
    "raptor1": ("raptor, Lambda + DynamoDB", "#9aa5b1"),
    "raptor2": ("raptor, Lambda MicroVMs", "#e8590c"),
    "awscli": ("AWS CLI native preview", "#1c7ed6"),
    "awspy": ("AWS CLI (Python)", "#74c0fc"),
}


def load(path):
    runs = defaultdict(list)
    tried = set()
    order = []
    with open(path, newline="") as f:
        for row in csv.DictReader(f):
            if row["workload"] not in order:
                order.append(row["workload"])
            tried.add((row["workload"], row["tool"]))
            if row["verify"] != "ok" or row.get("durable_s", "-") in ("", "-"):
                continue
            runs[(row["workload"], row["tool"])].append(
                {"time": float(row["durable_s"]), "cpu": float(row["user_s"]) + float(row["sys_s"]),
                 "files": int(row["files"])})
    return order, runs, tried


def median(runs, key):
    return statistics.median(r[key] for r in runs) if runs else None


def label(workload):
    count, _, size = workload.partition("x")
    return f"{int(count):,} × {size}" if size else workload


def fmt(v):
    if v >= 100:
        return f"{v:,.0f}"
    if v >= 10:
        return f"{v:.1f}"
    return f"{v:.2f}"


def tick(v):
    return f"{v:,.0f}" if v >= 1 else f"{v:g}"


def chart(path, title, subtitle, unit, workloads, tools, values, log, tried=frozenset()):
    """values[(workload, tool)] -> number or None."""
    width, height = 920, 460
    left, right, top, bottom = 70, 20, 78, 92
    plot_w, plot_h = width - left - right, height - top - bottom
    present = [v for v in values.values() if v]
    vmax = max(present)
    if log:
        vmin = min(present)
        lo = 10 ** math.floor(math.log10(vmin))
        hi = 10 ** math.ceil(math.log10(vmax))
        ticks = [10 ** e for e in range(int(math.log10(lo)), int(math.log10(hi)) + 1)]
        y = lambda v: top + plot_h * (1 - (math.log10(v) - math.log10(lo)) / (math.log10(hi) - math.log10(lo)))
    else:
        step = 10 ** math.floor(math.log10(vmax))
        for m in (1, 2, 5, 10):
            if vmax / (step * m) <= 6:
                step *= m
                break
        hi = step * math.ceil(vmax / step)
        ticks = [step * i for i in range(int(round(hi / step)) + 1)]
        lo = 0
        y = lambda v: top + plot_h * (1 - v / hi)
    base = top + plot_h

    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" '
           f'font-family="Segoe UI, Helvetica, Arial, sans-serif">',
           f'<rect width="{width}" height="{height}" fill="white"/>',
           f'<text x="{left}" y="30" font-size="19" font-weight="600" fill="#212529">{title}</text>',
           f'<text x="{left}" y="52" font-size="13" fill="#495057">{subtitle}</text>']
    for t in ticks:
        ty = y(t)
        out.append(f'<line x1="{left}" y1="{ty:.1f}" x2="{width - right}" y2="{ty:.1f}" stroke="#e9ecef"/>')
        out.append(f'<text x="{left - 8}" y="{ty + 4:.1f}" font-size="12" text-anchor="end" fill="#868e96">{tick(t)}</text>')
    out.append(f'<text x="18" y="{top + plot_h / 2:.0f}" font-size="12" fill="#495057" '
               f'transform="rotate(-90 18 {top + plot_h / 2:.0f})" text-anchor="middle">{unit}{" (log scale)" if log else ""}</text>')

    group_w = plot_w / len(workloads)
    bar_w = min(34, group_w * 0.8 / len(tools))
    for i, w in enumerate(workloads):
        gx = left + group_w * i + (group_w - bar_w * len(tools)) / 2
        for j, tool in enumerate(tools):
            v = values.get((w, tool))
            x = gx + bar_w * j
            if not v:
                note = "incomplete" if (w, tool) in tried else "n/a"
                out.append(f'<text x="{x + bar_w / 2 + 4:.1f}" y="{base - 6}" font-size="11" text-anchor="start" fill="#868e96" '
                           f'transform="rotate(-90 {x + bar_w / 2:.1f} {base - 6})">{note}</text>')
                continue
            ty = y(v)
            out.append(f'<rect x="{x + 1:.1f}" y="{ty:.1f}" width="{bar_w - 2:.1f}" height="{base - ty:.1f}" fill="{TOOLS[tool][1]}"/>')
            out.append(f'<text x="{x + bar_w / 2:.1f}" y="{ty - 4:.1f}" font-size="10.5" text-anchor="middle" fill="#343a40">{fmt(v)}</text>')
        out.append(f'<text x="{left + group_w * (i + 0.5):.1f}" y="{base + 20}" font-size="13" text-anchor="middle" fill="#212529">{label(w)}</text>')
    out.append(f'<line x1="{left}" y1="{base}" x2="{width - right}" y2="{base}" stroke="#adb5bd"/>')

    lx = left
    for tool in tools:
        name, color = TOOLS[tool]
        out.append(f'<rect x="{lx}" y="{height - 34}" width="12" height="12" fill="{color}"/>')
        out.append(f'<text x="{lx + 18}" y="{height - 24}" font-size="13" fill="#212529">{name}</text>')
        lx += 30 + 7.4 * len(name)
    out.append("</svg>")
    with open(path, "w") as f:
        f.write("\n".join(out) + "\n")
    print("wrote", path)


def loss_chart(csv_path, path):
    """Time until in S3 against packet loss (the loss_pct column from bench/loss-benchmark.sh): median line, run range."""
    data = defaultdict(list)
    with open(csv_path, newline="") as f:
        for row in csv.DictReader(f):
            if row["verify"] == "ok" and row.get("durable_s", "-") not in ("", "-"):
                data[(row["tool"], float(row["loss_pct"]))].append(float(row["durable_s"]))
    losses = sorted({loss for _, loss in data})
    tools = [t for t in TOOLS if any((t, l) in data for l in losses)]
    workload = next(csv.DictReader(open(csv_path, newline="")))["workload"]

    width, height = 920, 460
    left, right, top, bottom = 70, 30, 78, 92
    plot_w, plot_h = width - left - right, height - top - bottom
    vmax = max(max(v) for v in data.values())
    step = 10 ** math.floor(math.log10(vmax))
    for m in (1, 2, 5, 10):
        if vmax / (step * m) <= 6:
            step *= m
            break
    hi = step * math.ceil(vmax / step)
    x = lambda loss: left + plot_w * (losses.index(loss) + 0.5) / len(losses)
    y = lambda v: top + plot_h * (1 - v / hi)
    base = top + plot_h

    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" '
           f'font-family="Segoe UI, Helvetica, Arial, sans-serif">',
           f'<rect width="{width}" height="{height}" fill="white"/>',
           f'<text x="{left}" y="30" font-size="19" font-weight="600" fill="#212529">Upload time with packet loss: {label(workload)}</text>',
           f'<text x="{left}" y="52" font-size="13" fill="#495057">Outbound loss from tc netem. Line: median of 3 runs; '
           f'bar: fastest to slowest run. Lower is better.</text>']
    for i in range(int(round(hi / step)) + 1):
        t = step * i
        out.append(f'<line x1="{left}" y1="{y(t):.1f}" x2="{width - right}" y2="{y(t):.1f}" stroke="#e9ecef"/>')
        out.append(f'<text x="{left - 8}" y="{y(t) + 4:.1f}" font-size="12" text-anchor="end" fill="#868e96">{tick(t)}</text>')
    out.append(f'<text x="18" y="{top + plot_h / 2:.0f}" font-size="12" fill="#495057" '
               f'transform="rotate(-90 18 {top + plot_h / 2:.0f})" text-anchor="middle">seconds until in S3</text>')
    for loss in losses:
        out.append(f'<text x="{x(loss):.1f}" y="{base + 20}" font-size="13" text-anchor="middle" fill="#212529">{loss:g}% loss</text>')
    out.append(f'<line x1="{left}" y1="{base}" x2="{width - right}" y2="{base}" stroke="#adb5bd"/>')
    for j, tool in enumerate(tools):
        color = TOOLS[tool][1]
        dx = (j - (len(tools) - 1) / 2) * 10
        pts = []
        for loss in losses:
            runs = data.get((tool, loss))
            if not runs:
                continue
            px, med = x(loss) + dx, statistics.median(runs)
            out.append(f'<line x1="{px:.1f}" y1="{y(min(runs)):.1f}" x2="{px:.1f}" y2="{y(max(runs)):.1f}" '
                       f'stroke="{color}" stroke-width="6" stroke-opacity="0.35" stroke-linecap="round"/>')
            pts.append((px, y(med), med))
        out.append(f'<polyline fill="none" stroke="{color}" stroke-width="2.5" points="'
                   + " ".join(f"{px:.1f},{py:.1f}" for px, py, _ in pts) + '"/>')
        for px, py, med in pts:
            out.append(f'<circle cx="{px:.1f}" cy="{py:.1f}" r="4" fill="{color}"/>')
            # First tool's labels below its points, the others' above, so they don't collide where lines meet.
            ly = py + 18 if j == 0 else py - 10
            out.append(f'<text x="{px:.1f}" y="{ly:.1f}" font-size="11" font-weight="600" text-anchor="middle" '
                       f'fill="{color}">{fmt(med)}</text>')
    lx = left
    for tool in tools:
        name, color = TOOLS[tool]
        out.append(f'<rect x="{lx}" y="{height - 34}" width="12" height="12" fill="{color}"/>')
        out.append(f'<text x="{lx + 18}" y="{height - 24}" font-size="13" fill="#212529">{name}</text>')
        lx += 30 + 7.4 * len(name)
    out.append("</svg>")
    with open(path, "w") as f:
        f.write("\n".join(out) + "\n")
    print("wrote", path)


def main():
    if len(sys.argv) == 4 and sys.argv[1] == "--loss":
        loss_chart(sys.argv[2], sys.argv[3])
        return
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    order, runs, tried = load(sys.argv[1])
    out = sys.argv[2]
    os.makedirs(out, exist_ok=True)
    tools_all = [t for t in TOOLS if any((w, t) in runs for w in order)]
    old_new = [t for t in ("raptor1", "raptor2") if t in tools_all]
    time = {(w, t): median(runs.get((w, t), []), "time") for w in order for t in tools_all}
    cpu = {(w, t): median(runs.get((w, t), []), "cpu") for w in order for t in tools_all}
    fps = {(w, t): (runs[(w, t)][0]["files"] / time[(w, t)]) if time[(w, t)] else None for w in order for t in old_new}
    sub = "Median of the verified runs; time from start until every file is in S3. Lower is better."
    chart(os.path.join(out, "time-old-vs-new.svg"), "Upload time: Lambda + DynamoDB vs. Lambda MicroVMs", sub,
          "seconds", order, old_new, time, log=True, tried=tried)
    chart(os.path.join(out, "time-all.svg"), "Upload time, small files, against the AWS CLI", sub,
          "seconds", order, tools_all, time, log=True, tried=tried)
    chart(os.path.join(out, "cpu-all.svg"), "Client CPU time (user + sys)", "Median of the verified runs. Lower is better.",
          "CPU seconds", order, tools_all, cpu, log=True, tried=tried)
    chart(os.path.join(out, "files-per-second.svg"), "Files per second until durable in S3",
          "Median of the verified runs. Higher is better.", "files / second", order, old_new, fps, log=True, tried=tried)


if __name__ == "__main__":
    main()
