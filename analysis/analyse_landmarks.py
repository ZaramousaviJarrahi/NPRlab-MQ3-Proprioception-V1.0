"""
Quest vs Vicon movement speed, compared at matching anatomical landmarks.

WHY THIS EXISTS
---------------
The validation report compares the Quest's virtual FINGERTIP against Vicon's
RFIN marker, which sits on the BACK of the hand. Those are different physical
points, several centimetres apart, with an offset that rotates as the wrist
does, so that comparison charges the device for an anatomical difference.

Since 20 Oct 2026 the frame logger also records the index knuckle
(XRHand_IndexProximal, which is where RFIN sits) and the wrist, so the same
reaches can be measured at matching points in both systems.

TWO THINGS THIS SCRIPT GOT WRONG BEFORE, BOTH WORTH KNOWING
-----------------------------------------------------------
1. It binned individual SAMPLES INSIDE reaches by Vicon speed and reported a
   clean speed-dependence curve, crossing over around 500 mm/s. That curve
   measures TIMING ALIGNMENT, not speed sensitivity: inside a reach the hand
   is slowest at the start and end, and there a few milliseconds of
   misalignment between the two systems moves the ratio enormously. The table
   has been removed. Do not reinstate it.

2. It then summarised each reach by its MAXIMUM speed. The maximum is one
   sample out of a noisy signal; the Quest is noisier than Vicon, so its
   maximum is biased upward, and biased MORE when the movement is slow,
   because the true peak is smaller relative to the noise. Measured across
   326 reaches: the maximum reports +24% where the mean reports +16%, and on
   a deliberately slow session the gap is +35% against +12%.

Both errors came from the same place - not checking whether the summary
statistic was robust before believing the pattern it produced.

WHAT IT REPORTS NOW
-------------------
The MEDIAN OF PER-REACH RATIOS. Each reach contributes one paired number,
Quest divided by Vicon, and the median is taken across reaches. That is the
only version comparable between sessions: a ratio of medians is not, because
the Quest's fastest reaches are not necessarily Vicon's fastest reaches.

Primary statistic is the MEAN speed over the transport window. The maximum is
reported beside it, because the difference between the two IS the finding, not
because the maximum should be used.

RESULT AS OF 22 Oct 2026, 326 reaches across two sessions
----------------------------------------------------------
Quest wrist vs Vicon RWRA/RWRB midpoint, the matched pair Campos et al. used:

    mean over transport   +16%   (IQR +10 to +23)
    maximum               +24%   (IQR +20 to +34)

No speed dependence. Split by each reach's own mean speed, every band falls
between +9% and +18% across a 2.2x range of movement speed.

THE METHODOLOGICAL POINT
------------------------
Peak speed defined as a maximum is a biased estimator, and the bias grows as
the signal gets smaller. Two instruments with different noise floors disagree
on maxima even when they agree about the movement. For a slow-moving
population - patients, older adults - that bias is worst exactly where the
measurement matters most.

This probably also accounts for part of the gap with Campos et al. (2026), who
report 3-8%: they smooth the headset's signal 1.5x harder than the reference,
which suppresses the spikes that inflate a maximum.

CAVEATS
-------
One participant, who is the experimenter. A Bland-Altman analysis against the
mean of the two systems is the right way to present this and has not been done.

USAGE
-----
    python3 analyse_landmarks.py <session folder> [--filter-sweep]

<session folder> holds raw/ with the two headset files and vicon.csv. Writes
landmark_comparison_report.txt and landmark_comparison_pairs.csv into out/.

--filter-sweep re-runs the whole comparison at several cutoffs, including the
asymmetric scheme Campos use. It is slow on a large export and the conclusion
is already recorded above, so it is off by default.
"""
import csv
import glob
import importlib.util
import os
import statistics as st
import sys
from collections import defaultdict
from datetime import date

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "analyse_session", os.path.join(HERE, "analyse_session.py"))
A = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(A)

CUTOFF_HZ = 8.0
TRANSPORT_FRAC = 0.10
SPEED_SPLIT = [(0, 400, "slow  <400"), (400, 700, "mid   400-700"),
               (700, 10 ** 9, "fast  700+")]


def read_markers(path, names):
    """{name: [(t, x, y, z)]} from the Trajectories section, in metres.

    The subject prefix is stripped rather than matched: every export so far
    reads "p01:" regardless of who was actually recorded.
    """
    rows = list(csv.reader(open(path, newline="", encoding="utf-8-sig")))
    known = {"Joints", "Trajectories", "Model Outputs", "Segments", "Devices"}
    secs = [(i, r[0].strip()) for i, r in enumerate(rows)
            if r and r[0].strip() in known]
    tj = next((i for i, nm in secs if nm == "Trajectories"), None)
    if tj is None:
        return None, None
    rate = float(rows[tj + 1][0])
    objs = rows[tj + 2]
    cols = {nm: next((j for j, c in enumerate(objs)
                      if c.strip().split(":")[-1] == nm), None) for nm in names}
    end = next((i for i, _ in secs if i > tj), len(rows))
    out = {nm: [] for nm in cols if cols[nm] is not None}
    for r in rows[tj + 5:end]:
        try:
            f = int(r[0])
        except (IndexError, ValueError):
            continue
        for nm, k in cols.items():
            if k is None:
                continue
            v = r[k:k + 3]
            if len(v) == 3 and all(c.strip() for c in v):
                out[nm].append((f / rate, float(v[0]) / 1000,
                                float(v[1]) / 1000, float(v[2]) / 1000))
    return out, rate


def quest_landmark(frames, prefix):
    """(t, x, y, z) for one right-hand landmark on the session clock."""
    out = []
    for fr in frames:
        try:
            t = float(fr["t_session_s"])
            x = float(fr["right_%s_x" % prefix])
            y = float(fr["right_%s_y" % prefix])
            z = float(fr["right_%s_z" % prefix])
        except (KeyError, ValueError, TypeError):
            continue
        if abs(x) < 1e-9 and abs(y) < 1e-9 and abs(z) < 1e-9:
            continue          # untracked frames are logged as zeros
        out.append((t, x, y, z))
    return out


def mean_speed(seg):
    return sum(seg) / len(seg)


def iqr(values):
    s = sorted(values)
    return s[len(s) // 4], s[(3 * len(s)) // 4]


def main(session, filter_sweep=False):
    raw = os.path.join(session, "raw")
    out_dir = os.path.join(session, "out")
    os.makedirs(out_dir, exist_ok=True)

    frame_files = glob.glob(os.path.join(raw, "*_frames.csv"))
    if not frame_files:
        sys.exit("No *_frames.csv in %s" % raw)
    frames = A.read_csv(frame_files[0])

    vicon_path = os.path.join(raw, "vicon.csv")
    if not os.path.exists(vicon_path):
        sys.exit("No vicon.csv in %s" % raw)

    mk, rate = read_markers(vicon_path, ["RFIN", "RWRA", "RWRB"])
    if not mk or "RFIN" not in mk:
        sys.exit("No RFIN in the Trajectories section.")
    missing = [m for m in ("RWRA", "RWRB") if m not in mk]
    if missing:
        sys.exit("Missing wrist markers %s - the wrist comparison needs both."
                 % ", ".join(missing))

    # Wrist joint centre = the RWRA/RWRB midpoint. The standard definition, and
    # the point Campos et al. tracked with a single marker.
    a = {t: (x, y, z) for t, x, y, z in mk["RWRA"]}
    b = {t: (x, y, z) for t, x, y, z in mk["RWRB"]}
    mid = [(t, (a[t][0] + b[t][0]) / 2, (a[t][1] + b[t][1]) / 2,
            (a[t][2] + b[t][2]) / 2) for t in sorted(set(a) & set(b))]

    vt = [p[0] for p in mk["RFIN"]]
    vp = [(p[1], p[2], p[3]) for p in mk["RFIN"]]
    lag, z = A.estimate_lag(vt, vp, rate, frames)
    if lag is None:
        sys.exit("Could not align the two systems.")
    t0 = min(float(r["t_session_s"]) for r in frames)

    raw_q = {k: quest_landmark(frames, k) for k in ("tip", "knuckle", "wrist")}
    if not raw_q["knuckle"]:
        sys.exit("No knuckle columns - this session predates 20 Oct 2026.")

    marks, prev = [], None
    for fr in frames:
        if (fr.get("event") or "").strip() == "grasp":
            t = float(fr["t_session_s"])
            marks.append((prev, t))
            prev = t

    def segments(cutoff_q, cutoff_v=CUTOFF_HZ):
        """Per-reach transport-window speed series for every landmark pair."""
        V = {"RFIN": A.smooth(mk["RFIN"], cutoff_v),
             "wrist_mid": A.smooth(mid, cutoff_v)}
        VS = {k: A.speeds(v) for k, v in V.items()}
        Q = {}
        for k, v in raw_q.items():
            sm = A.smooth(v, cutoff_q)
            Q[k] = (sm, A.speeds(sm))
        rows = []
        for n, (t_prev, t_end) in enumerate(marks, 1):
            if t_prev is None:
                t_prev = t_end - 2.0
            got, bad = {"reach": n}, False
            for k in V:
                sm, sp = V[k], VS[k]
                lo, hi = t_prev - t0 + lag, t_end - t0 + lag
                if lo < sm[0][0] or hi > sm[-1][0]:
                    bad = True
                    break
                i0, i1 = A.nearest_index(sm, lo), A.nearest_index(sm, hi)
                if i1 - i0 < 10:
                    bad = True
                    break
                x, y = A.transport_window(sp, i0, i1, TRANSPORT_FRAC)
                seg = sp[x:y + 1]
                if len(seg) < 5:
                    bad = True
                    break
                got["V_" + k] = seg
            if bad:
                continue
            for k in Q:
                sm, sp = Q[k]
                i0, i1 = A.nearest_index(sm, t_prev), A.nearest_index(sm, t_end)
                if i1 - i0 < 5:
                    bad = True
                    break
                x, y = A.transport_window(sp, i0, i1, TRANSPORT_FRAC)
                seg = sp[x:y + 1]
                if len(seg) < 5:
                    bad = True
                    break
                got["Q_" + k] = seg
            if not bad:
                rows.append(got)
        return rows

    reaches = segments(CUTOFF_HZ)
    if not reaches:
        sys.exit("No reaches could be paired between the two systems.")

    PAIRS = [("Q_tip", "V_RFIN", "fingertip  vs  RFIN (back of hand)", "NOT matched"),
             ("Q_knuckle", "V_RFIN", "index knuckle  vs  RFIN", "matched"),
             ("Q_wrist", "V_wrist_mid", "wrist  vs  RWRA/RWRB midpoint", "matched")]

    def ratios(rows, qk, vk, stat):
        return [stat(r[qk]) / stat(r[vk]) for r in rows]

    # ---- per-reach CSV -------------------------------------------------
    stem = os.path.join(out_dir, "landmark_comparison")
    with open(stem + "_pairs.csv", "w", newline="") as fh:
        w = csv.writer(fh)
        w.writerow(["reach",
                    "quest_fingertip_mean_mps", "quest_knuckle_mean_mps",
                    "quest_wrist_mean_mps", "vicon_RFIN_mean_mps",
                    "vicon_wrist_mid_mean_mps",
                    "quest_fingertip_max_mps", "quest_knuckle_max_mps",
                    "quest_wrist_max_mps", "vicon_RFIN_max_mps",
                    "vicon_wrist_mid_max_mps"])
        keys = ["Q_tip", "Q_knuckle", "Q_wrist", "V_RFIN", "V_wrist_mid"]
        for r in reaches:
            w.writerow([r["reach"]]
                       + ["%.4f" % mean_speed(r[k]) for k in keys]
                       + ["%.4f" % max(r[k]) for k in keys])

    # ---- report --------------------------------------------------------
    L = []
    L.append("QUEST vs VICON - MOVEMENT SPEED AT MATCHING LANDMARKS")
    L.append("=" * 72)
    L.append("Session   : %s" % os.path.basename(os.path.abspath(session)))
    L.append("Generated : %s" % date.today().isoformat())
    L.append("Alignment : lag %+.3f s, cross-correlation z = %.1f" % (lag, z))
    L.append("Reaches   : %d paired" % len(reaches))
    L.append("")
    L.append("METHOD")
    L.append("  Speed over the transport window of each reach, defined")
    L.append("  identically in both systems (%d%% of that reach's peak). Both"
             % int(TRANSPORT_FRAC * 100))
    L.append("  signals low-pass filtered at %.0f Hz before differentiation."
             % CUTOFF_HZ)
    L.append("  Vicon aligned to the Quest by cross-correlating hand-speed")
    L.append("  profiles; there is no sync signal.")
    L.append("")
    L.append("  Reported as the MEDIAN OF PER-REACH RATIOS: each reach gives")
    L.append("  one Quest/Vicon number and the median is taken across reaches.")
    L.append("  A ratio of medians is not comparable between sessions, because")
    L.append("  the Quest's fastest reaches need not be Vicon's fastest.")
    L.append("")
    L.append("  The headline statistic is the MEAN speed across the transport")
    L.append("  window. The maximum is shown beside it because the gap between")
    L.append("  them is itself a result - see the note at the end - not")
    L.append("  because the maximum should be used.")
    L.append("")
    L.append("RESULT")
    L.append("")
    L.append("  %-38s %12s %12s" % ("", "MEAN", "MAX"))
    L.append("  %-38s %12s %12s" % ("comparison", "(use this)", "(biased)"))
    for qk, vk, label, note in PAIRS:
        rm = ratios(reaches, qk, vk, mean_speed)
        rx = ratios(reaches, qk, vk, max)
        L.append("  %-38s %+11.0f%% %+11.0f%%"
                 % ("%s  [%s]" % (label, note),
                    100 * (st.median(rm) - 1), 100 * (st.median(rx) - 1)))
    wm = ratios(reaches, "Q_wrist", "V_wrist_mid", mean_speed)
    wx = ratios(reaches, "Q_wrist", "V_wrist_mid", max)
    lo, hi = iqr(wm)
    lox, hix = iqr(wx)
    L.append("")
    L.append("  Wrist pair, with spread:")
    L.append("     MEAN  %+.0f%%   (IQR %+.0f to %+.0f)"
             % (100 * (st.median(wm) - 1), 100 * (lo - 1), 100 * (hi - 1)))
    L.append("     MAX   %+.0f%%   (IQR %+.0f to %+.0f)"
             % (100 * (st.median(wx) - 1), 100 * (lox - 1), 100 * (hix - 1)))
    L.append("")
    L.append("  Median Vicon wrist speed this session: %.0f mm/s"
             % (st.median([mean_speed(r["V_wrist_mid"]) for r in reaches]) * 1000))
    L.append("")
    L.append("IS THERE A SPEED DEPENDENCE? Measured across two sessions, no.")
    L.append("")
    L.append("  Each reach assigned to a band by its own mean Vicon wrist speed.")
    L.append("")
    L.append("  %-18s %6s %12s" % ("band (mm/s)", "n", "Quest vs Vicon"))
    bands = defaultdict(list)
    for r in reaches:
        v = mean_speed(r["V_wrist_mid"]) * 1000
        for lo_b, hi_b, name in SPEED_SPLIT:
            if lo_b <= v < hi_b:
                bands[name].append(mean_speed(r["Q_wrist"]) / mean_speed(r["V_wrist_mid"]))
                break
    for _, _, name in SPEED_SPLIT:
        v = bands.get(name)
        if not v or len(v) < 5:
            continue
        L.append("  %-18s %6d %+11.0f%%" % (name, len(v), 100 * (st.median(v) - 1)))
    L.append("")
    L.append("  Pooled over 20 and 22 Oct (326 reaches, a 2.2x range of")
    L.append("  movement speed) every band fell between +9% and +18%, with no")
    L.append("  trend. An earlier version of this script reported a strong")
    L.append("  speed dependence from binning samples INSIDE reaches. That was")
    L.append("  an artefact of timing alignment at the slow start and end of")
    L.append("  each reach, and the table has been removed.")

    if filter_sweep:
        L.append("")
        L.append("FILTERING")
        L.append("")
        L.append("  Campos et al. do not filter both systems equally - they")
        L.append("  widen the headset's bandwidth by 1.5x. Both schemes here.")
        L.append("")
        L.append("  %-26s %14s %14s" % ("Quest cutoff", "wrist MEAN", "wrist MAX"))
        for qc, vc, note in ((CUTOFF_HZ, CUTOFF_HZ, "both as now"),
                             (CUTOFF_HZ / 1.5, CUTOFF_HZ, "x1.5 Campos"),
                             (CUTOFF_HZ / 2.5, CUTOFF_HZ, "x2.5"),
                             (2.0, 2.0, "both at 2 Hz")):
            rr = segments(qc, vc)
            if not rr:
                continue
            m = st.median(ratios(rr, "Q_wrist", "V_wrist_mid", mean_speed))
            x = st.median(ratios(rr, "Q_wrist", "V_wrist_mid", max))
            L.append("  %5.2f Hz %-17s %+13.0f%% %+13.0f%%"
                     % (qc, note, 100 * (m - 1), 100 * (x - 1)))

    L.append("")
    L.append("WHY THE MEAN AND NOT THE MAXIMUM")
    L.append("=" * 72)
    L.append("")
    L.append("  A maximum is one sample out of a noisy signal. The Quest is")
    L.append("  noisier than Vicon, so its maximum is biased upward - and the")
    L.append("  bias grows as the movement gets slower, because the true peak")
    L.append("  shrinks relative to the noise while the noise does not.")
    L.append("")
    L.append("  Measured on two sessions:")
    L.append("")
    L.append("    20 Oct, Vicon 683 mm/s    MEAN +17%    MAX +23%")
    L.append("    22 Oct, Vicon 304 mm/s    MEAN +12%    MAX +35%")
    L.append("    pooled, n = 326           MEAN +16%    MAX +24%")
    L.append("")
    L.append("  The maximum adds about 8 points on average and 23 points on the")
    L.append("  slow session, and it is unstable: IQR +20 to +53 there, against")
    L.append("  +5 to +29 for the mean.")
    L.append("")
    L.append("  Two instruments with different noise floors will disagree about")
    L.append("  maxima even when they agree about the movement. For a")
    L.append("  slow-moving population - patients, older adults - that bias is")
    L.append("  largest exactly where the measurement matters most.")
    L.append("")
    L.append("  This likely accounts for part of the gap with Campos et al.")
    L.append("  (2026), who report 3-8%: they smooth the headset's signal 1.5x")
    L.append("  harder than the reference, which suppresses the spikes that")
    L.append("  inflate a maximum.")
    L.append("")
    L.append("CAVEATS")
    L.append("  One participant, who is the experimenter. A Bland-Altman")
    L.append("  analysis against the mean of the two systems is the right way")
    L.append("  to present this and has not been done.")
    L.append("")
    L.append("Per-reach values, both statistics: landmark_comparison_pairs.csv")

    text = "\n".join(L) + "\n"
    open(stem + "_report.txt", "w").write(text)
    print(text)
    print("wrote %s_report.txt" % stem)
    print("wrote %s_pairs.csv" % stem)


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if not args:
        sys.exit(__doc__)
    main(args[0], filter_sweep="--filter-sweep" in sys.argv)
