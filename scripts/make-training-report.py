"""Builds a standalone HTML training report: three annotated TensorBoard screenshots,
graded results, and plain-English explanations at three reading levels.

    .venv\\Scripts\\python.exe scripts\\make-training-report.py --shots <dir> --out docs\\reports\\<name>.html

--shots is a directory of tb_<key>.png files written by scripts\\capture-tensorboard.ps1.
The results table, the bar charts and the 2M-step band tables are read from
results\\eval\\*.json and the runs' TensorBoard event files. The prose around the three
charts is written for the p32pass / p33team runs and needs editing for a different pair.
The file embeds its images and needs no network.
"""
import argparse
import base64
import glob
import html
import io
import json
import os
from datetime import date

from PIL import Image
from tensorboard.backend.event_processing.event_accumulator import EventAccumulator

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
# The chart card inside a 3000x1640 TensorBoard capture (1500x820 at scale 2).
CARD_BOX = (640, 340, 1540, 1190)

RUNS = [
    ("p31", "soccer_p31_standard", "Before: trained 1v1 only"),
    ("p32pass", "soccer_p32pass_standard", "Team run 1: pass reward"),
    ("p33team", "soccer_p33team_standard", "Team run 2: shared goal reward"),
]


def load_eval(name):
    path = os.path.join(ROOT, "results", "eval", name + ".json")
    if not os.path.exists(path):
        return None
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def bands(run_id, tag, width=2_000_000):
    files = sorted(glob.glob(os.path.join(ROOT, "results", run_id, "STANDARD", "events.out.tfevents.*")))
    if not files:
        return []
    acc = EventAccumulator(files[-1])
    acc.Reload()
    if tag not in acc.Tags().get("scalars", []):
        return []
    points = acc.Scalars(tag)
    last = max(p.step for p in points)
    out = []
    start = 0
    while start < last:
        vals = [p.value for p in points if start <= p.step < start + width]
        if vals:
            out.append((start, start + width, sum(vals) / len(vals), len(vals)))
        start += width
    return out


def shot_data_uri(shots_dir, key):
    path = os.path.join(shots_dir, f"tb_{key}.png")
    if not os.path.exists(path):
        return None, (0, 0)
    im = Image.open(path).convert("RGB")
    if im.size == (3000, 1640):
        im = im.crop(CARD_BOX)
    buf = io.BytesIO()
    im.save(buf, format="PNG", optimize=True)
    return "data:image/png;base64," + base64.b64encode(buf.getvalue()).decode(), im.size


def esc(s):
    return html.escape(str(s))


def pct(n, d):
    return f"{100.0 * n / d:.1f}%" if d else "n/a"


def bar_chart(title, rows, unit, note):
    """rows: (label, value, css class). Horizontal bars, value printed at the end."""
    top = max((v for _, v, _ in rows), default=1) or 1
    h = 34 * len(rows) + 16
    parts = [f'<figure class="viz"><figcaption>{esc(title)}</figcaption>',
             f'<svg viewBox="0 0 360 {h}" role="img" aria-label="{esc(title)}">']
    for i, (label, value, cls) in enumerate(rows):
        y = 8 + i * 34
        w = max(2, 170 * value / top)
        parts.append(f'<text x="102" y="{y + 17}" text-anchor="end" class="lbl">{esc(label)}</text>')
        parts.append(f'<rect x="110" y="{y}" width="{w:.1f}" height="24" rx="4" class="{cls}"/>')
        parts.append(f'<text x="{110 + w + 8:.1f}" y="{y + 17}" class="val">{esc(value if isinstance(value, int) else f"{value:.1f}")}{esc(unit)}</text>')
    parts.append("</svg>")
    parts.append(f'<p class="note">{esc(note)}</p></figure>')
    return "\n".join(parts)


def band_table(tag, label, fmt):
    rows = []
    for key, run_id, _ in RUNS[1:]:
        b = bands(run_id, tag)
        if b:
            cells = "".join(f"<td>{fmt(v)}</td>" for _, _, v, _ in b)
            rows.append(f"<tr><th>{esc(key)}</th>{cells}</tr>")
    if not rows:
        return ""
    head = "".join(f"<th>{a // 1_000_000}–{b // 1_000_000}M</th>" for a, b, _, _ in bands(RUNS[1][1], tag))
    return (f'<table class="bands"><caption>{esc(label)}, averaged over each 2M training steps</caption>'
            f"<thead><tr><th>Run</th>{head}</tr></thead><tbody>{''.join(rows)}</tbody></table>")


def annotated(uri, size, marks, alt):
    """marks: (x%, y%, number). Numbered pins over the screenshot, explained in the list beside it."""
    if not uri:
        return '<p class="note">Screenshot not captured.</p>'
    w, h = size
    pins = "".join(
        f'<g transform="translate({w * x / 100:.0f},{h * y / 100:.0f})"><circle r="26" class="pin"/>'
        f'<text y="10" text-anchor="middle" class="pin-n">{n}</text></g>' for x, y, n in marks)
    return (f'<div class="shot"><img src="{uri}" alt="{esc(alt)}" width="{w}" height="{h}">'
            f'<svg viewBox="0 0 {w} {h}" aria-hidden="true">{pins}</svg></div>')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--shots", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    ev2 = {k: load_eval(rid + "_squad2") for k, rid, _ in RUNS}
    ev1 = {k: load_eval(rid) for k, rid, _ in RUNS}
    bot2 = load_eval("baseline_squad2")
    done = [k for k, _, _ in RUNS if ev2[k]]
    latest = done[-1]
    first = "p31"

    def row(label, e2, e1):
        n = e2["episodes"]
        one = f'{e1["blueWins"]} ({pct(e1["blueWins"], e1["episodes"])})' if e1 and e1.get("squad", "1") in ("1", "") else "not graded"
        return (f"<tr><th>{esc(label)}</th><td>{e2['blueWins']} ({pct(e2['blueWins'], n)})</td>"
                f"<td>{e2['bluePasses']}</td><td>{e2['bluePassProgressM']:.0f} m</td>"
                f"<td>{e2['blueAssistedGoals']} of {e2['blueWins']}</td><td>{one}</td></tr>")

    table_rows = [row(f"{k}: {desc}", ev2[k], ev1[k]) for k, _, desc in RUNS if ev2[k]]
    if bot2:
        table_rows.append(row("Scripted bots, for scale", bot2, None).replace("not graded", "—"))

    pass_rows = [(f"{k}", ev2[k]["bluePasses"], "b-run" if k != first else "b-before") for k, _, _ in RUNS if ev2[k]]
    assist_rows = [(f"{k}", ev2[k]["blueAssistedGoals"], "b-run" if k != first else "b-before") for k, _, _ in RUNS if ev2[k]]
    win_rows = [(f"{k}", round(100.0 * ev2[k]["blueWins"] / ev2[k]["episodes"], 1), "b-run" if k != first else "b-before") for k, _, _ in RUNS if ev2[k]]
    if bot2:
        pass_rows.append(("scripted bots", bot2["bluePasses"], "b-bot"))
        assist_rows.append(("scripted bots", bot2["blueAssistedGoals"], "b-bot"))
        win_rows.append(("scripted bots", round(100.0 * bot2["blueWins"] / bot2["episodes"], 1), "b-bot"))

    a, z = ev2[first], ev2[latest]
    n = z["episodes"]
    pass_x = (z["bluePasses"] / a["bluePasses"]) if a["bluePasses"] else float("inf")
    headline = (f"After team training the brain completes {z['bluePasses']} passes per {n} two-a-side matches, "
                f"up from {a['bluePasses']}, and wins {pct(z['blueWins'], n)} of them, up from {pct(a['blueWins'], a['episodes'])}.")
    short = ""
    o1 = ev1[latest]
    if o1 and ev1[first]:
        short = (f"At one-a-side, the benchmark, it wins {pct(o1['blueWins'], o1['episodes'])}, against "
                 f"{pct(ev1[first]['blueWins'], ev1[first]['episodes'])} before. ")
    if bot2:
        short += (f"It still passes less than the scripted bots ({bot2['bluePasses']}), and "
                 f"{z['blueAssistedGoals']} of its {z['blueWins']} goals came from a teammate's pass.")

    s_pass, sz_pass = shot_data_uri(args.shots, "passes")
    s_ast, sz_ast = shot_data_uri(args.shots, "assisted")
    s_gap, sz_gap = shot_data_uri(args.shots, "gap")

    css = """
:root{--bg:#f6f7f9;--card:#fff;--ink:#16202b;--mute:#5b6876;--line:#dfe4ea;--accent:#1f6feb;--before:#9aa7b5;--run:#1f6feb;--bot:#d1492e;--pin:#111827;--pinink:#fff;--good:#1a7f4b;--warn:#a15c00}
@media (prefers-color-scheme:dark){:root:not([data-theme=light]){--bg:#0f141a;--card:#171e27;--ink:#e7edf3;--mute:#9aa7b5;--line:#2a3441;--accent:#6ea8fe;--before:#6b7785;--run:#6ea8fe;--bot:#ff8a70;--pin:#f8fafc;--pinink:#0f141a;--good:#5fd39a;--warn:#f0b35a}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.55 system-ui,Segoe UI,Roboto,Arial,sans-serif}
main{max-width:1040px;margin:0 auto;padding:28px 16px 64px}
h1{font-size:28px;line-height:1.2;margin:0 0 6px}h2{font-size:21px;margin:40px 0 10px}h3{font-size:16px;margin:18px 0 6px}
.sub{color:var(--mute);margin:0 0 20px;overflow-wrap:anywhere}.lead{font-size:18px;background:var(--card);border:1px solid var(--line);border-left:5px solid var(--accent);border-radius:8px;padding:14px 16px;margin:0 0 8px}
section.chart{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:18px;margin:16px 0}
.grid{display:grid;grid-template-columns:minmax(0,1.05fr) minmax(0,1fr);gap:20px;align-items:start}
@media (max-width:820px){.grid{grid-template-columns:1fr}}
.shot{position:relative;border:1px solid var(--line);border-radius:6px;overflow:hidden;background:#fff}
.shot img{display:block;width:100%;height:auto}.shot svg{position:absolute;inset:0;width:100%;height:100%}
.pin{fill:var(--pin);stroke:#fff;stroke-width:4}.pin-n{fill:var(--pinink);font:700 28px system-ui,sans-serif}
ol.keys{margin:0;padding-left:22px}ol.keys li{margin:0 0 8px}
.tiers{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:10px;margin-top:14px}
@media (max-width:820px){.tiers{grid-template-columns:1fr}}
.tier{border:1px solid var(--line);border-radius:8px;padding:10px 12px}.tier b{display:block;font-size:12px;letter-spacing:.06em;text-transform:uppercase;color:var(--mute);margin-bottom:4px}
table{border-collapse:collapse;width:100%;background:var(--card);border:1px solid var(--line);border-radius:8px;overflow:hidden}
th,td{padding:8px 10px;border-bottom:1px solid var(--line);text-align:left;font-variant-numeric:tabular-nums}thead th{font-size:13px;color:var(--mute)}
tbody tr:last-child th,tbody tr:last-child td{border-bottom:0}
.wrap{overflow-x:auto}table.bands{margin-top:14px;font-size:14px}caption{caption-side:top;text-align:left;color:var(--mute);font-size:13px;padding:0 0 6px}
.viz{margin:0;background:var(--card);border:1px solid var(--line);border-radius:10px;padding:14px}
.viz figcaption{font-weight:600;margin-bottom:6px}.viz svg{width:100%;height:auto}
.lbl{fill:var(--ink);font:14px system-ui,sans-serif}.val{fill:var(--ink);font:600 14px system-ui,sans-serif}
.b-before{fill:var(--before)}.b-run{fill:var(--run)}.b-bot{fill:var(--bot)}
.three{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}@media (max-width:820px){.three{grid-template-columns:1fr}}
.note{color:var(--mute);font-size:14px;margin:8px 0 0}ul.plain{margin:6px 0 0;padding-left:20px}ul.plain li{margin-bottom:6px}
code{background:rgba(127,127,127,.15);padding:1px 5px;border-radius:4px;font-size:.92em}
"""

    def tiers(toddler, child, adult):
        return (f'<div class="tiers"><div class="tier"><b>For a toddler</b>{toddler}</div>'
                f'<div class="tier"><b>For a child</b>{child}</div><div class="tier"><b>For an adult</b>{adult}</div></div>')

    def chart(num, title, uri, size, marks, keys, explain, tier_html, extra):
        key_items = "".join(f"<li>{k}</li>" for k in keys)
        return (f'<section class="chart"><h2 style="margin-top:0">{num}. {esc(title)}</h2><div class="grid">'
                f"{annotated(uri, size, marks, title)}<div><p>{explain}</p><h3>What the numbers on the picture mark</h3>"
                f'<ol class="keys">{key_items}</ol></div></div>{tier_html}<div class="wrap">{extra}</div></section>')

    body = []
    body.append(f"<h1>PoSoccer team-play training report</h1><p class=\"sub\">Generated {date.today().isoformat()} · runs "
                + ", ".join(esc(rid) for k, rid, _ in RUNS if ev2[k]) + " · graded over "
                + f"{n} matches each</p>")
    body.append(f'<p class="lead">{esc(headline)} {esc(short)}</p>')
    body.append('<p class="note">A completed pass here means the ball went from one player to a teammate over at least 3 m with no opponent touching it in between.</p>')

    body.append("<h2>Graded results</h2>")
    body.append('<div class="wrap"><table><thead><tr><th>Brain (blue side)</th><th>2v2 wins</th><th>2v2 passes</th>'
                "<th>Ground gained by passes</th><th>Goals from a teammate's pass</th><th>1v1 wins</th></tr></thead><tbody>"
                + "".join(table_rows) + "</tbody></table></div>")
    body.append('<p class="note">These come from the evaluation harness, not from the training curves below. '
                "Training curves on this project have pointed the wrong way before, so the grade is what counts. "
                "The benchmark bar is 80% wins at 1v1; no brain is near it.</p>")

    body.append('<div class="three" style="margin-top:16px">'
                + bar_chart(f"Completed passes in {n} matches", pass_rows, "", "Higher is more teamwork.")
                + bar_chart("Goals set up by a teammate", assist_rows, "", "A goal whose last two touches were two players of the same side.")
                + bar_chart("Two-a-side wins", win_rows, "%", "Share of matches won against the scripted bots.")
                + "</div>")

    body.append("<h2>The three TensorBoard charts that matter</h2>"
                '<p class="sub">Screenshots of the live TensorBoard at smoothing 0.9. The faint line is the raw value, the bold line the smoothed one. '
                "Green is the first team run, orange the second.</p>")

    body.append(chart(
        1, "Completed passes per match (PoSoccer/blue_passes)", s_pass, sz_pass,
        [(26, 63, 1), (47, 17, 2), (80, 41, 3), (50, 94, 4)],
        ["Start of the first team run: the brain had only ever played alone, so it almost never passed.",
         "Passing rises as training goes on. The raw line swings a lot because each point covers only a few dozen matches.",
         "The second run (orange) starts where the first ended and stays in the same 0.2 to 0.3 band during training. Its graded pass count is still higher: see the results table.",
         "The table under the chart gives each run's latest smoothed value."],
        "How many completed passes the trained side made in each two-a-side match during training. "
        "This is the most direct measure of whether the players are combining.",
        tiers("The players learned to share the ball a little.",
              "At first each player kept the ball to itself. After practice they pass to a friend about once every three or four games, which is more than before but less than the robot team.",
              "Passes per episode rose from near zero to roughly 0.2 to 0.3 in the first run and stayed in that band in the second. In the graded matches the count rose again between the two runs, so the training curve understates the second run. The scripted bots sit near 0.5."),
        band_table("PoSoccer/blue_passes", "Passes per match", lambda v: f"{v:.2f}")))

    body.append(chart(
        2, "Share of goals set up by a teammate (PoSoccer/blue_assisted_goal_rate)", s_ast, sz_ast,
        [(27, 30, 1), (50, 50, 2), (84, 55, 3), (50, 94, 4)],
        ["Each point is the fraction of the trained side's goals, in that stretch of training, whose last two touches were two teammates.",
         "The line is jumpy because goals are rare: a single assisted goal moves a point a long way.",
         "Read the level, not the wiggles: both runs sit in a low band.",
         "The second run reads lower here, yet in the graded matches it scored twice as many assisted goals. The two runs trained against different bot strengths, so their curves are not like for like; the grades are."],
        "Passing only matters if it leads to goals. This chart shows how often a goal came straight from a teammate's pass.",
        tiers("Sometimes one player helps another score.",
              "When the team scores, we check whether a friend passed the ball just before. That happens for about one goal in eight during practice.",
              "The assisted-goal share during training is roughly 5 to 17% and noisy. The first run began against half-strength bots and the second against strong ones, so the curves cannot be compared directly; use the graded counts in the results table."),
        band_table("PoSoccer/blue_assisted_goal_rate", "Assisted-goal share", lambda v: f"{100 * v:.0f}%")))

    body.append(chart(
        3, "Distance between teammates (PoSoccer/blue_teammate_gap_m)", s_gap, sz_gap,
        [(25, 64, 1), (55, 44, 2), (80, 13, 3), (50, 94, 4)],
        ["Early in the first run the two teammates stay close together, both chasing the ball.",
         "The gap widens steadily through the first run (green): the players are taking up separate positions.",
         "The second run (orange) carries on from there and ends near 12 m.",
         "Latest smoothed value for each run."],
        "The average distance between the two teammates. A small gap means both are swarming the ball; a wider gap means they hold different positions.",
        tiers("The players stopped bunching up.",
              "At first both players ran at the ball together. Now one goes to the ball and the other waits in a good spot.",
              "Mean teammate separation rose from about 6 m to about 9 m in the first run and to about 12 m in the second. This is the clearest and smoothest learned change. Which reward term drove it was not isolated."),
        band_table("PoSoccer/blue_teammate_gap_m", "Teammate gap", lambda v: f"{v:.1f} m")))

    body.append("<h2>How to read this, and what is still missing</h2><ul class=\"plain\">"
                "<li><b>What worked:</b> starting from a brain that could already play, then training two-a-side against bots that hold positions and pass.</li>"
                "<li><b>What the second run added:</b> sharing the goal reward more evenly between scorer, passer and teammate. Graded passes and assisted goals both rose, and so did the one-a-side win rate. It was one run, so how much is the reward change and how much is simply more training is not separated.</li>"
                "<li><b>Why mean reward is not shown:</b> on this project it has repeatedly disagreed with the graded result, so it is not used to judge a run.</li>"
                "<li><b>Not measured:</b> whether passes are chosen at the right moment. The counts say passes happen more often, not that each one was the better option than shooting.</li>"
                "<li><b>Where the files are:</b> grades in <code>results/eval/*.json</code>, curves in <code>results/&lt;run&gt;/STANDARD/</code>, history in <code>docs/training-history.md</code>.</li>"
                "</ul>")

    doc = ("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
           f"<title>PoSoccer team-play training report</title><style>{css}</style></head><body><main>"
           + "\n".join(body) + "</main></body></html>")
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        f.write(doc)
    print(f"wrote {args.out} ({len(doc) // 1024} KB), latest graded run: {latest}")


if __name__ == "__main__":
    main()
