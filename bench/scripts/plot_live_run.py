#!/usr/bin/env python3
"""Render a RamPilot metrics CSV (%LOCALAPPDATA%\\RamPilot\\metrics_*.csv) as two static SVG charts
(light + dark) for the README: working set vs. hard cap of one process, and its fault rate below.

No third-party dependencies. Usage:
    python plot_live_run.py metrics.csv out_dir [--pid N] [--title "..."]
Picks the managed process with the largest working-set reduction when --pid is omitted.
"""
import csv
import sys
from collections import defaultdict
from pathlib import Path

# Reference palette (dataviz skill): categorical slot 1 + neutral inks, light and dark steps.
THEMES = {
    "light": dict(surface="#fcfcfb", ink="#0b0b0b", ink2="#52514e", grid="#e4e3df",
                  series="#2a78d6", cap="#52514e", band="#f0efec", fault="#2a78d6"),
    "dark": dict(surface="#1a1a19", ink="#f0efec", ink2="#c3c2b7", grid="#34332f",
                 series="#3987e5", cap="#c3c2b7", band="#252523", fault="#3987e5"),
}
W, H = 960, 470
L, R = 64, 150                    # plot margins (right side holds direct labels)
TOP1, BOT1 = 56, 300              # panel 1: MB
TOP2, BOT2 = 345, 425             # panel 2: faults/s


def secs(t):
    h, m, s = (int(x) for x in t.split(":"))
    return h * 3600 + m * 60 + s


def load(path):
    rows = list(csv.DictReader(open(path, newline="", encoding="utf-8")))
    by_pid = defaultdict(list)
    for r in rows:
        by_pid[r["pid"]].append(r)
    return by_pid


def pick(by_pid):
    best, gain = None, -1
    for pid, rs in by_pid.items():
        capped = [float(r["ws_mb"]) for r in rs if float(r["cap_mb"]) > 0]
        if not capped:
            continue
        g = max(float(r["ws_mb"]) for r in rs) - min(capped)
        if g > gain:
            best, gain = pid, g
    return best


def nice(v):
    for step in (10, 20, 25, 50, 100, 200, 250, 500, 1000):
        if v / step <= 5:
            return step
    return 2000


def svg(rs, theme, title, subtitle):
    c = THEMES[theme]
    t0 = secs(rs[0]["time"])
    pts = []
    for r in rs:
        t = secs(r["time"]) - t0
        if t < 0:
            t += 86400
        pts.append((t / 60.0, float(r["ws_mb"]), float(r["cap_mb"]), float(r["faults_per_s"]), r["state"]))
    xmax = max(p[0] for p in pts) or 1
    ymax_raw = max(max(p[1] for p in pts), max(p[2] for p in pts))
    ystep = nice(ymax_raw)
    ymax = ystep * (int(ymax_raw / ystep) + 1)
    fmax_raw = max(p[3] for p in pts) or 1
    fstep = nice(fmax_raw) if fmax_raw > 5 else 1
    fmax = max(fstep * (int(fmax_raw / fstep) + 1), 5)

    X = lambda m: L + (W - L - R) * m / xmax
    Y = lambda v: BOT1 - (BOT1 - TOP1) * v / ymax
    Yf = lambda v: BOT2 - (BOT2 - TOP2) * min(v, fmax) / fmax

    o = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" '
         f'font-family="Segoe UI, -apple-system, Helvetica, Arial, sans-serif" role="img" '
         f'aria-label="{title}">',
         f'<rect width="{W}" height="{H}" rx="12" fill="{c["surface"]}"/>',
         f'<text x="{L}" y="26" font-size="16" font-weight="600" fill="{c["ink"]}">{title}</text>',
         f'<text x="{L}" y="45" font-size="12" fill="{c["ink2"]}">{subtitle}</text>']

    # Released (uncapped) intervals as recessive bands: the app was focused or active.
    i = 0
    while i < len(pts):
        if pts[i][2] == 0:
            j = i
            while j + 1 < len(pts) and pts[j + 1][2] == 0:
                j += 1
            x0, x1 = X(pts[i][0]), X(pts[min(j + 1, len(pts) - 1)][0])
            if x1 - x0 > 1:
                o.append(f'<rect x="{x0:.1f}" y="{TOP1}" width="{x1 - x0:.1f}" height="{BOT1 - TOP1}" fill="{c["band"]}"/>')
            i = j + 1
        else:
            i += 1

    # Grid + y labels (panel 1)
    v = 0
    while v <= ymax:
        y = Y(v)
        o.append(f'<line x1="{L}" x2="{W - R}" y1="{y:.1f}" y2="{y:.1f}" stroke="{c["grid"]}" stroke-width="1"/>')
        o.append(f'<text x="{L - 8}" y="{y + 4:.1f}" font-size="11" text-anchor="end" fill="{c["ink2"]}">{v} MB</text>')
        v += ystep
    # Panel 2 grid
    for fv in (0, fmax):
        y = Yf(fv)
        o.append(f'<line x1="{L}" x2="{W - R}" y1="{y:.1f}" y2="{y:.1f}" stroke="{c["grid"]}" stroke-width="1"/>')
        o.append(f'<text x="{L - 8}" y="{y + 4:.1f}" font-size="11" text-anchor="end" fill="{c["ink2"]}">{fv:g}/s</text>')
    o.append(f'<text x="{L}" y="{TOP2 - 10}" font-size="12" fill="{c["ink2"]}">Page faults per second (all faults, incl. compression-store reads)</text>')

    # X axis (minutes)
    xstep = 5 if xmax > 20 else (2 if xmax > 8 else 1)
    m = 0
    while m <= xmax + 1e-9:
        x = X(m)
        o.append(f'<text x="{x:.1f}" y="{BOT2 + 18}" font-size="11" text-anchor="middle" fill="{c["ink2"]}">{m:g} min</text>')
        m += xstep

    # Cap: dashed step line, only where a cap is set
    seg = []
    for k, p in enumerate(pts):
        if p[2] > 0:
            x = X(p[0])
            y = Y(p[2])
            if seg:
                seg.append(f"H{x:.1f}V{y:.1f}")
            else:
                seg.append(f"M{x:.1f},{y:.1f}")
        elif seg:
            o.append(f'<path d="{"".join(seg)}" fill="none" stroke="{c["cap"]}" stroke-width="1.5" stroke-dasharray="5 4"/>')
            seg = []
    if seg:
        o.append(f'<path d="{"".join(seg)}" fill="none" stroke="{c["cap"]}" stroke-width="1.5" stroke-dasharray="5 4"/>')

    # Working set: 2 px line
    d = "M" + " L".join(f"{X(p[0]):.1f},{Y(p[1]):.1f}" for p in pts)
    o.append(f'<path d="{d}" fill="none" stroke="{c["series"]}" stroke-width="2" stroke-linejoin="round"/>')

    # Fault rate: thin bars
    bw = max(1.0, (W - L - R) / len(pts) - 1)
    for p in pts:
        if p[3] > 0:
            x = X(p[0]) - bw / 2
            y = Yf(p[3])
            o.append(f'<rect x="{x:.1f}" y="{y:.1f}" width="{bw:.1f}" height="{BOT2 - y:.1f}" fill="{c["fault"]}"/>')

    # Direct labels at the right edge (text in ink, marker carries identity)
    last = pts[-1]
    lastcap = next((p for p in reversed(pts) if p[2] > 0), None)
    ly_ws = Y(last[1])
    o.append(f'<line x1="{W - R + 8}" x2="{W - R + 22}" y1="{ly_ws:.1f}" y2="{ly_ws:.1f}" stroke="{c["series"]}" stroke-width="2"/>')
    o.append(f'<text x="{W - R + 28}" y="{ly_ws + 4:.1f}" font-size="12" fill="{c["ink"]}">Working set</text>')
    if lastcap:
        ly_cap = Y(lastcap[2])
        if abs(ly_cap - ly_ws) < 16:
            ly_cap = ly_ws - 18 if ly_cap <= ly_ws else ly_ws + 18
        o.append(f'<line x1="{W - R + 8}" x2="{W - R + 22}" y1="{ly_cap:.1f}" y2="{ly_cap:.1f}" stroke="{c["cap"]}" stroke-width="1.5" stroke-dasharray="5 4"/>')
        o.append(f'<text x="{W - R + 28}" y="{ly_cap + 4:.1f}" font-size="12" fill="{c["ink"]}">Hard cap</text>')
    # Band legend
    o.append(f'<rect x="{W - R + 8}" y="{BOT1 - 10}" width="14" height="10" fill="{c["band"]}" stroke="{c["grid"]}"/>')
    o.append(f'<text x="{W - R + 28}" y="{BOT1 - 1}" font-size="12" fill="{c["ink2"]}">No cap</text>')
    o.append("</svg>")
    return "\n".join(o)


def main():
    a = sys.argv[1:]
    if len(a) < 2:
        print(__doc__)
        sys.exit(2)
    src, out = Path(a[0]), Path(a[1])
    pid = a[a.index("--pid") + 1] if "--pid" in a else None
    title = a[a.index("--title") + 1] if "--title" in a else None
    by_pid = load(src)
    pid = pid or pick(by_pid)
    if not pid:
        sys.exit("no capped process in this CSV")
    rs = by_pid[pid]
    app = rs[0]["app"].split("#")[0].replace(".exe", "")
    title = title or f"{app} ({rs[0]['role']} process): working set under a RamPilot hard cap"
    sub = (f"Live run, {len(rs)} samples. Cap descends in small steps while refaults stay low; "
           f"focus or activity releases it at once.")
    out.mkdir(parents=True, exist_ok=True)
    for theme in THEMES:
        (out / f"live-run-{theme}.svg").write_text(svg(rs, theme, title, sub), encoding="utf-8")
    print(f"pid {pid}: {out / 'live-run-light.svg'}, {out / 'live-run-dark.svg'}")


if __name__ == "__main__":
    main()
