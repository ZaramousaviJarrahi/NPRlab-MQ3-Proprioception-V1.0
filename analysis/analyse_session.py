#!/usr/bin/env python3
"""
Turns one session's two Quest files into the outcome measures the study plan asks
for. Run it after every session.

    python3 analyse_session.py NPRlab_P01_Visit1_20261008_103605.csv \
                               NPRlab_P01_Visit1_20261008_103605_frames.csv

It writes three files next to the per-grasp file and prints a summary:

    ..._grasps.csv    every original column, plus the derived per-grasp measures
    ..._trials.csv    one row per trial: total trial time, RT, MT, errors
    ..._accuracy.csv  CE, VE and RE per target position and block

Nothing to install. Standard library only, so it runs on any Mac or PC with
Python 3 already on it.

--------------------------------------------------------------------------------
WHY THE FRAME FILE IS NEEDED

The per-grasp file records one position per grasp, taken at the instant the grab
fired. Two of the plan's measures cannot be computed from that at all:

  - Reaction time by a 5%-of-peak-speed criterion needs the peak speed of the
    reach, which is not known until the reach has finished.
  - The endpoint at closest approach needs the frames AFTER the grab fired.

Both are backward-looking by definition. The frame file carries the whole
trajectory, so both are computed here, offline, where the thresholds are
arguments rather than something baked into a build.

--------------------------------------------------------------------------------
WHICH NUMBER IS THE ACCURACY MEASURE

Use lateral_error_m. Not endpoint_error_m, and not radial error.

The fingertip stops short of the target centre on essentially every grasp,
because the grab fires while the hand is still arriving and the target then
disappears. That shortfall is a property of the interaction, not the
participant - and it is not constant: it grows with movement speed. Practice
schedule changes movement speed, so a measure containing it can show a
difference between conditions that is really a speed difference.

This script therefore splits every endpoint into two parts, along and across the
direction of the reach:

    depth_offset_m    along the reach. Negative means stopped short. This is the
                      interaction artefact. Report it as a diagnostic.
    lateral_error_m   across the reach. This is the aim. Analyse this.

--------------------------------------------------------------------------------
"""

import argparse
import csv
import math
import os
import statistics as st
import sys
from collections import defaultdict


# ----------------------------------------------------------------- small helpers

def read_csv(path):
    with open(path, newline="", encoding="utf-8-sig") as fh:
        return list(csv.DictReader(fh))


def num(row, key):
    """Float from a cell, or None when blank or unparseable."""
    v = (row.get(key) or "").strip()
    if not v:
        return None
    try:
        return float(v)
    except ValueError:
        return None


def med(v):
    return st.median(v) if v else float("nan")


def fmt(v, dp=2, mult=1.0):
    return "" if v is None else f"{v * mult:.{dp}f}"


# ----------------------------------------------------------------- frame handling

def hand_samples(frames, hand):
    """
    Usable samples for one hand, in order, de-duplicated by tracking timestamp.

    Returns [(t_since_cue, x, y, z)].

    Two filters matter here. Frames where confidence is -1 are not tracked at all:
    the position is stale or zero and differencing across them invents a jump.
    And hand tracking updates more slowly than the display refreshes, so
    consecutive frames can repeat the SAME tracking sample - those are not new
    measurements, and leaving them in drags every speed estimate towards zero.
    """
    out, last_sample = [], None
    cx, cy, cz = f"{hand}_tip_x", f"{hand}_tip_y", f"{hand}_tip_z"
    cconf, csamp = f"{hand}_confidence", f"{hand}_sample_t"

    for fr in frames:
        conf = num(fr, cconf)
        if conf is None or conf < 0:
            continue
        sample = num(fr, csamp)
        if sample is not None and sample >= 0 and sample == last_sample:
            continue
        last_sample = sample
        t, x, y, z = (num(fr, "t_since_cue_s"), num(fr, cx), num(fr, cy), num(fr, cz))
        if None in (t, x, y, z):
            continue
        out.append((t, x, y, z))
    return out


def _butter2(fc, fs):
    """Second-order Butterworth low-pass coefficients, by bilinear transform."""
    w = math.tan(math.pi * fc / fs)
    n = 1.0 / (1.0 + math.sqrt(2.0) * w + w * w)
    b0 = w * w * n
    return (b0, 2 * b0, b0), (2.0 * (w * w - 1.0) * n,
                              (1.0 - math.sqrt(2.0) * w + w * w) * n)


def _pass(x, b, a):
    b0, b1, b2 = b
    a1, a2 = a
    y = [0.0] * len(x)
    x1 = x2 = y1 = y2 = 0.0
    for i, v in enumerate(x):
        o = b0 * v + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        x2, x1 = x1, v
        y2, y1 = y1, o
        y[i] = o
    return y


def filtfilt(x, fc, fs):
    """
    Zero-lag low-pass: run the filter forwards, then backwards over the result.

    Running it once would delay the signal, which would shift every event in
    time - onset, peak speed, closest approach. Running it both ways cancels
    that delay exactly. Edges are padded with the first and last value so the
    filter does not start from zero and carve a dip into the beginning of the
    reach.
    """
    if len(x) < 12:
        return list(x)
    b, a = _butter2(fc, fs)
    pad = min(len(x) // 3, 30)
    ext = [x[0]] * pad + list(x) + [x[-1]] * pad
    y = _pass(_pass(ext, b, a)[::-1], b, a)[::-1]
    return y[pad:len(y) - pad]


def smooth(s, fc):
    """
    Low-pass the x, y and z of a sample series.

    WHY THIS IS NOT OPTIONAL. Quest hand tracking jitters about 1.5 mm from
    sample to sample. At 72 Hz that is 100 mm/s of speed on a hand that is
    sitting still, and it does three things to the unfiltered numbers:

      - peak speed is whatever the largest noise spike was, not the movement
      - every noise wiggle is a local maximum, so a single smooth reach counts
        as eight or nine "submovements"
      - path length accumulates the jitter, so a straight reach looks 60% longer
        than the straight line between its ends

    It also biases endpoint error DOWNWARD: closest approach takes the minimum
    over samples, and the minimum of a noisy signal is whichever sample the
    noise happened to push nearest the target.

    8 Hz is well above voluntary arm movement, which lives below about 5 Hz, so
    this removes the noise without touching the movement.
    """
    if len(s) < 12:
        return list(s)
    dts = [s[i + 1][0] - s[i][0] for i in range(len(s) - 1)]
    dt = st.median(dts)
    if dt <= 0:
        return list(s)
    fs = 1.0 / dt
    if fc >= fs / 2.0:                       # cutoff must stay below Nyquist
        return list(s)
    xs = filtfilt([p[1] for p in s], fc, fs)
    ys = filtfilt([p[2] for p in s], fc, fs)
    zs = filtfilt([p[3] for p in s], fc, fs)
    return [(s[i][0], xs[i], ys[i], zs[i]) for i in range(len(s))]


def speeds(s):
    """
    Speed at each sample, m/s, by central difference.

    Central rather than consecutive differences because they are symmetric in
    time, so a speed peak is not shifted half a sample earlier or later.
    """
    n = len(s)
    if n < 2:
        return [0.0] * n
    out = [0.0] * n
    for i in range(n):
        j, k = max(0, i - 1), min(n - 1, i + 1)
        dt = s[k][0] - s[j][0]
        out[i] = 0.0 if dt <= 0 else math.dist(s[j][1:4], s[k][1:4]) / dt
    return out


def nearest_index(s, t):
    best, best_d = 0, float("inf")
    for i, v in enumerate(s):
        d = abs(v[0] - t)
        if d < best_d:
            best, best_d = i, d
    return best


def onset_index(sp, end_i, frac):
    """
    Movement onset: walk BACK from the peak to the last sample below frac*peak.

    Backwards from the peak rather than forwards from the cue, because a single
    noisy sample early in the trial would otherwise be read as the movement
    starting.
    """
    if end_i <= 0:
        return None
    window = sp[:end_i + 1]
    peak = max(window) if window else 0.0
    if peak <= 0:
        return None
    peak_i = window.index(peak)
    thr = frac * peak
    for i in range(peak_i, -1, -1):
        if sp[i] < thr:
            return i
    return 0


def transport_window(sp, lo, hi, frac):
    """
    The transport phase of a reach: the run of samples around peak speed that
    stay above frac * peak.

    Needed because grasps 2 and 3 have no stationary moment to measure from. The
    task asks for one continuous movement through all three targets, so between
    grasps the hand withdraws from the one it just took and flows into the next
    without stopping - the speed trace is a bell sitting on 150-450 mm/s of
    hand-adjusting, never returning to zero.

    A proportion-of-peak ONSET rule cannot work there: at 5% of a 2.2 m/s peak
    the threshold is 110 mm/s, below the fidget, so it walks back to the start of
    the interval and sweeps both targets' dwell into the "reach". That is what
    made a smooth reach look 60% longer than straight with six submovements.

    Taking the contiguous region around the peak instead isolates the bell and
    leaves the dwell out, and it applies identically to all three grasps, so they
    stay comparable with each other.

    Reaction time does NOT use this. For the first grasp the hand really is held
    at home, so the plan's 5%-of-peak criterion is both valid and the one
    specified, and it is measured separately.
    """
    seg = sp[lo:hi + 1]
    if len(seg) < 5:
        return lo, hi
    peak = max(seg)
    if peak <= 0:
        return lo, hi
    pk = lo + seg.index(peak)
    thr = frac * peak
    a = pk
    while a > lo and sp[a - 1] >= thr:
        a -= 1
    b = pk
    while b < hi and sp[b + 1] >= thr:
        b += 1
    return a, b


def count_speed_peaks(sp, lo, hi, frac=0.10):
    """
    Local maxima in the speed profile between two sample indices.

    More than one peak means the reach was not a single smooth movement - the
    participant corrected mid-flight. Small ripples are ignored by requiring a
    peak to reach frac of the reach's maximum.
    """
    seg = sp[lo:hi + 1]
    if len(seg) < 3:
        return 0
    thr = frac * max(seg)
    return sum(1 for i in range(1, len(seg) - 1)
               if seg[i] >= thr and seg[i] > seg[i - 1] and seg[i] >= seg[i + 1])


def normalised_jerk(s, lo, hi):
    """
    Dimensionless jerk: sqrt(0.5 * integral(jerk^2) * duration^5 / length^2).

    Normalising by duration and path length is what makes it comparable between
    a fast short reach and a slow long one - raw jerk is not.
    """
    seg = s[lo:hi + 1]
    if len(seg) < 5:
        return None
    dur = seg[-1][0] - seg[0][0]
    length = sum(math.dist(seg[i][1:4], seg[i + 1][1:4]) for i in range(len(seg) - 1))
    if dur <= 0 or length <= 0:
        return None

    # position -> velocity -> acceleration -> jerk, by central differences
    def deriv(series):
        n = len(series)
        out = []
        for i in range(n):
            j, k = max(0, i - 1), min(n - 1, i + 1)
            dt = series[k][0] - series[j][0]
            if dt <= 0:
                out.append((series[i][0], 0.0, 0.0, 0.0))
            else:
                out.append((series[i][0],
                            (series[k][1] - series[j][1]) / dt,
                            (series[k][2] - series[j][2]) / dt,
                            (series[k][3] - series[j][3]) / dt))
        return out

    jerk = deriv(deriv(deriv(seg)))
    integral = 0.0
    for i in range(len(jerk) - 1):
        dt = jerk[i + 1][0] - jerk[i][0]
        if dt <= 0:
            continue
        a = sum(c * c for c in jerk[i][1:4])
        b = sum(c * c for c in jerk[i + 1][1:4])
        integral += 0.5 * (a + b) * dt          # trapezoid
    try:
        return math.sqrt(0.5 * integral * (dur ** 5) / (length ** 2))
    except (ValueError, OverflowError):
        return None


# ----------------------------------------------------------------------- the work

NEW_COLS = [
    "endpoint_error_corrected_m", "depth_offset_m", "lateral_error_m",
    "endpoint_settle_s", "endpoint_improvement_m",
    "reach_time_corrected_s", "hand_speed_at_grasp_mps", "peak_speed_mps",
    "time_after_peak_pct", "speed_peaks", "path_ratio", "normalised_jerk",
    "transport_time_s",
    "rt_speed_criterion_s", "samples_in_reach",
]


def analyse(grasps, frames, window, onset_frac, cutoff_hz, transport_frac):
    by_trial = defaultdict(list)
    for fr in frames:
        key = ((fr.get("trial_number") or "").strip(),
               (fr.get("trial_attempt") or "1").strip())
        if key[0]:
            by_trial[key].append(fr)

    out, matched = [], 0

    for g in grasps:
        row = dict(g)
        for c in NEW_COLS:
            row[c] = ""

        if (g.get("grasp_outcome") or "").strip() != "recorded":
            out.append(row)
            continue

        trial = (g.get("trial_number") or "").strip()
        attempt = (g.get("trial_attempt") or "1").strip()
        gn = (g.get("grasp_in_trial") or "").strip()
        tf = by_trial.get((trial, attempt))
        if not tf or not gn:
            out.append(row)
            continue

        mark = next((fr for fr in tf
                     if (fr.get("event") or "").strip() == "grasp"
                     and (fr.get("grasp_in_trial") or "").strip() == gn), None)
        if mark is None:
            out.append(row)
            continue

        t_mark = num(mark, "t_since_cue_s")
        tx, ty, tz = (num(mark, "event_target_x"),
                      num(mark, "event_target_y"),
                      num(mark, "event_target_z"))
        if None in (t_mark, tx, ty, tz):
            out.append(row)
            continue

        hand = "left" if (g.get("hand_used") or "").strip().lower().startswith("l") else "right"
        s = hand_samples(tf, hand)
        if len(s) < 5:
            s = hand_samples(tf, "right" if hand == "left" else "left")
        if len(s) < 5:
            out.append(row)
            continue
        s = smooth(s, cutoff_hz)

        matched += 1
        sp = speeds(s)
        mark_i = nearest_index(s, t_mark)

        # --- closest approach, in the window after the grab ---
        best_d = best_t = best_p = best_i = None
        for i in range(mark_i, len(s)):
            t, x, y, z = s[i]
            if t > t_mark + window:
                break
            d = math.dist((x, y, z), (tx, ty, tz))
            if best_d is None or d < best_d:
                best_d, best_t, best_p, best_i = d, t, (x, y, z), i

        if best_d is not None:
            row["endpoint_error_corrected_m"] = f"{best_d:.5f}"
            row["endpoint_settle_s"] = f"{best_t - t_mark:.4f}"
            orig = num(g, "endpoint_error_m")
            if orig is not None:
                row["endpoint_improvement_m"] = f"{orig - best_d:.5f}"

            # --- split the error along and across the reach ---
            # The axis is this reach's own start to its target, so "depth" means
            # the same thing wherever in the grid the target sits. A world axis
            # would not: the grid is anchored to head yaw, and reach direction
            # differs for every target.
            start = s[0][1:4] if gn == "1" else None
            if start is None:
                prev = next((fr for fr in tf
                             if (fr.get("event") or "").strip() == "grasp"
                             and (fr.get("grasp_in_trial") or "").strip() == str(int(gn) - 1)), None)
                if prev is not None:
                    pt = num(prev, "t_since_cue_s")
                    start = s[nearest_index(s, pt)][1:4] if pt is not None else s[0][1:4]
                else:
                    start = s[0][1:4]

            ax = (tx - start[0], ty - start[1], tz - start[2])
            mag = math.sqrt(sum(c * c for c in ax))
            if mag > 1e-6:
                ax = tuple(c / mag for c in ax)
                err = (best_p[0] - tx, best_p[1] - ty, best_p[2] - tz)
                depth = sum(e * a for e, a in zip(err, ax))
                perp = tuple(e - depth * a for e, a in zip(err, ax))
                row["depth_offset_m"] = f"{depth:.5f}"
                row["lateral_error_m"] = f"{math.sqrt(sum(c * c for c in perp)):.5f}"

        end_i = best_i if best_i is not None else mark_i

        # --- the reach that led to this grasp ---
        #
        # Onset is detected the same way for EVERY grasp, not just the first.
        #
        # Starting grasps 2 and 3 at the previous grasp mark looked equivalent and
        # was not: the hand sits at the target it just took for a couple of
        # hundred milliseconds before setting off again, and it sits at the new
        # one after arriving. Including those two dwells put the hand's wobble
        # into the movement - it pushed path ratio from about 1.15 to 1.5, and
        # turned one smooth reach into six "submovements". The transport is the
        # part between them, so onset has to be found inside the interval rather
        # than assumed to be at its start.
        if gn == "1":
            search_from = 0
        else:
            prev = next((fr for fr in tf
                         if (fr.get("event") or "").strip() == "grasp"
                         and (fr.get("grasp_in_trial") or "").strip() == str(int(gn) - 1)), None)
            pt = num(prev, "t_since_cue_s") if prev is not None else None
            search_from = nearest_index(s, pt) if pt is not None else 0

        rel = onset_index(sp[search_from:], mark_i - search_from, onset_frac)
        lo = search_from + rel if rel is not None else search_from

        # Reaction time exists only for the first grasp: it is measured from cue
        # onset, and grasps 2 and 3 have no cue of their own.
        if gn == "1" and rel is not None:
            row["rt_speed_criterion_s"] = f"{s[lo][0]:.4f}"

        row["hand_speed_at_grasp_mps"] = f"{sp[mark_i]:.4f}"
        row["samples_in_reach"] = str(max(0, end_i - lo + 1))
        row["reach_time_corrected_s"] = f"{s[end_i][0] - s[lo][0]:.4f}"

        # Movement quality is measured over the TRANSPORT window, not the whole
        # inter-grasp interval, for the reason in transport_window().
        ta, tb = transport_window(sp, lo, end_i, transport_frac)
        row["transport_time_s"] = f"{s[tb][0] - s[ta][0]:.4f}"

        seg_sp = sp[ta:tb + 1]
        if seg_sp:
            peak = max(seg_sp)
            row["peak_speed_mps"] = f"{peak:.4f}"
            pk_i = ta + seg_sp.index(peak)
            total = s[tb][0] - s[ta][0]
            if total > 0:
                row["time_after_peak_pct"] = f"{100.0 * (s[tb][0] - s[pk_i][0]) / total:.1f}"
            row["speed_peaks"] = str(count_speed_peaks(sp, ta, tb))

        straight = math.dist(s[ta][1:4], s[tb][1:4])
        if straight > 1e-4:
            path = sum(math.dist(s[i][1:4], s[i + 1][1:4]) for i in range(ta, tb))
            row["path_ratio"] = f"{path / straight:.4f}"

        nj = normalised_jerk(s, ta, tb)
        if nj is not None:
            row["normalised_jerk"] = f"{nj:.2f}"

        out.append(row)

    return out, matched


def per_trial(rows):
    """One row per trial: the plan's Stage 1 primary outcome and its two parts."""
    trials = defaultdict(list)
    for r in rows:
        if (r.get("grasp_outcome") or "").strip() != "recorded":
            continue
        trials[(r.get("trial_number", ""), r.get("trial_attempt", "1"))].append(r)

    out = []
    for (tn, att), rs in sorted(trials.items(), key=lambda kv: (int(kv[0][0] or 0), kv[0][1])):
        rs.sort(key=lambda r: int(r.get("grasp_in_trial") or 0))
        last = rs[-1]
        first = rs[0]

        # Total trial time ends at CLOSEST APPROACH on the final grasp, not at the
        # grab. The grab fires early by an amount that varies with speed, so
        # measuring to it puts that same speed-dependent bias into the Stage 1
        # primary outcome.
        t_end = None
        settle = num(last, "endpoint_settle_s")
        base = num(last, "time_since_spawn_s")
        if settle is not None and base is not None:
            t_end = base + settle

        rt = num(first, "rt_speed_criterion_s")
        out.append({
            "participant_id": first.get("participant_id", ""),
            "visit": first.get("visit", ""),
            "block": first.get("block", ""),
            "condition": first.get("condition", ""),
            "task": first.get("task", ""),
            "trial_number": tn,
            "trial_attempt": att,
            "foreperiod_s": first.get("foreperiod_s", ""),
            "home_verified": first.get("home_verified", ""),
            "false_start": first.get("false_start", ""),
            "grasps": str(len(rs)),
            "total_trial_time_s": fmt(t_end, 4),
            "reaction_time_s": fmt(rt, 4),
            "movement_time_s": fmt(t_end - rt, 4) if (t_end is not None and rt is not None) else "",
            "order_errors": str(sum(1 for r in rs if (r.get("order_correct") or "").strip() == "FALSE")),
            "mean_lateral_error_m": fmt(st.mean([v for v in (num(r, "lateral_error_m") for r in rs) if v is not None]), 5)
            if any(num(r, "lateral_error_m") is not None for r in rs) else "",
            "mean_depth_offset_m": fmt(st.mean([v for v in (num(r, "depth_offset_m") for r in rs) if v is not None]), 5)
            if any(num(r, "depth_offset_m") is not None for r in rs) else "",
        })
    return out


def accuracy_table(rows):
    """
    CE, VE and RE per target position within each block.

    Computed from the same deviation vectors so the identity RMS(RE)^2 = CE^2 +
    VE^2 holds - which is also a check that the pipeline is doing what it says.

    CE is systematic bias, VE is spread around the participant's own mean, RE is
    the plain average distance. CE and RE both contain the depth artefact; VE is
    the one least affected by it, because a constant offset does not change the
    spread about the mean. The depth column is here so the size of that artefact
    is visible rather than buried.
    """
    cells = defaultdict(list)
    for r in rows:
        if (r.get("grasp_outcome") or "").strip() != "recorded":
            continue
        pos = (r.get("target_position_number") or "").strip()
        lat, dep = num(r, "lateral_error_m"), num(r, "depth_offset_m")
        if not pos or lat is None or dep is None:
            continue
        cells[(r.get("block", ""), pos)].append((dep, lat))

    out = []
    for (block, pos), v in sorted(cells.items(), key=lambda kv: (kv[0][0], int(kv[0][1]))):
        if len(v) < 2:
            continue
        mdep = st.mean([a for a, _ in v])
        mlat = st.mean([b for _, b in v])
        ce = math.hypot(mdep, mlat)
        ve = math.sqrt(st.mean([(a - mdep) ** 2 + (b - mlat) ** 2 for a, b in v]))
        re = st.mean([math.hypot(a, b) for a, b in v])
        rms = math.sqrt(st.mean([a * a + b * b for a, b in v]))
        out.append({
            "block": block, "target_position_number": pos, "n": str(len(v)),
            "CE_m": f"{ce:.5f}", "VE_m": f"{ve:.5f}", "RE_m": f"{re:.5f}",
            "mean_depth_offset_m": f"{mdep:.5f}", "mean_lateral_error_m": f"{mlat:.5f}",
            "check_rms_vs_CE_VE": f"{rms:.5f} vs {math.hypot(ce, ve):.5f}",
        })
    return out


# ============================================================================
# VICON
#
# Optional. Everything above runs on the two Quest files alone and needs nothing
# installed; this section needs numpy, for the cross-correlation only.
#
# THE ALIGNMENT PROBLEM, AND WHY IT IS SOLVABLE WITHOUT A SYNC SIGNAL
#
# Nothing connects the two systems: Vicon frame 1 and the Quest's session clock
# start at unrelated moments, so a Vicon capture is one undifferentiated block
# with no way to say which frames belong to which trial.
#
# But both systems recorded the SAME HAND. Vicon's RFIN marker and the Quest's
# index fingertip trace the same movement, so their speed profiles are the same
# signal sampled twice. Cross-correlating them recovers the offset - the
# movement is the sync signal.
#
# On the 8 October pilot this gave a lag of 3.810 s with a peak 6x clearer than
# any rival, and it was checked against information the correlation never saw:
# at the 60 cue onsets, where the home gate guarantees the hand is stationary,
# the aligned Vicon speed reads 26 mm/s against a 102 mm/s baseline. Scanning
# the lag puts a cliff at +4.15 s, which is where the assumed cue starts landing
# after movement onset; onset is about 400 ms after the cue, implying a lag near
# 3.75 s. Two methods, 60 ms apart.
#
# That 60 ms is the honest precision. It is ample for labelling frames by trial
# and for 100 Hz kinematics; it is not frame-exact, and the lag is one number
# for a whole session, so it assumes the clocks do not drift. Over nine minutes
# that holds. It is re-estimated per session rather than carried across.
# ============================================================================

VICON_COLS = ["vicon_peak_speed_mps", "vicon_path_ratio", "vicon_speed_peaks",
              "vicon_normalised_jerk", "vicon_transport_time_s"]


def read_vicon_rfin(path):
    """
    Pull the RFIN marker out of a Nexus CSV export.

    The export holds several sections (Joints, Model Outputs, Segments,
    Trajectories), each with its own header block and its own frame coverage.
    Trajectories carries the raw markers and is usually the only one covering
    the whole trial - the modelled sections are empty unless the pipeline has
    been run over the full range in Nexus.

    Returns (times_seconds, positions_metres, rate) or None.
    """
    rows = []
    with open(path, newline="", encoding="utf-8-sig") as fh:
        rows = list(csv.reader(fh))

    known = {"Joints", "Trajectories", "Model Outputs", "Segments", "Devices"}
    secs = [(i, r[0].strip()) for i, r in enumerate(rows) if r and r[0].strip() in known]
    traj = next((i for i, nm in secs if nm == "Trajectories"), None)
    if traj is None:
        return None

    rate = float(rows[traj + 1][0])
    objs = rows[traj + 2]
    col = next((k for k, c in enumerate(objs) if c.strip().endswith("RFIN")), None)
    if col is None:
        return None

    end = next((i for i, _ in secs if i > traj), len(rows))
    t, p = [], []
    for r in rows[traj + 5:end]:
        if not r or len(r) < col + 3:
            continue
        try:
            f = int(r[0])
        except ValueError:
            continue
        v = r[col:col + 3]
        if all(c.strip() for c in v):
            t.append(f / rate)
            p.append((float(v[0]) / 1000.0, float(v[1]) / 1000.0, float(v[2]) / 1000.0))
    if len(t) < 100:
        return None
    return t, p, rate


def estimate_lag(vt, vp, rate, frames):
    """
    Offset between the Quest session clock and Vicon time, by cross-correlating
    the two speed profiles. Returns (lag_seconds, z_score_of_peak).

    The Quest logger only runs during trials, so its profile has gaps; those are
    left as zeros. The signal is distinctive enough that it still locks on, and
    the z-score reports how clearly - below about 8 the result should not be
    trusted, and the summary says so.
    """
    import numpy as np

    vt = np.asarray(vt)
    vp = np.asarray(vp)
    vsp = np.zeros(len(vt))
    dt = vt[2:] - vt[:-2]
    ok = dt > 0
    vsp[1:-1][ok] = np.linalg.norm(vp[2:][ok] - vp[:-2][ok], axis=1) / dt[ok]

    qt, qp, last = [], [], None
    for r in frames:
        if (r.get("right_confidence") or "").strip() in ("", "-1"):
            continue
        smp = r.get("right_sample_t")
        if smp == last:
            continue
        last = smp
        qt.append(float(r["t_session_s"]))
        qp.append([float(r["right_tip_x"]), float(r["right_tip_y"]), float(r["right_tip_z"])])
    if len(qt) < 100:
        return None, 0.0
    qt = np.asarray(qt)
    qp = np.asarray(qp)
    qsp = np.zeros(len(qt))
    dt = qt[2:] - qt[:-2]
    ok = dt > 0
    qsp[1:-1][ok] = np.linalg.norm(qp[2:][ok] - qp[:-2][ok], axis=1) / dt[ok]
    qsp = np.clip(qsp, 0, 5.0)          # drop tracking spikes before correlating

    t0 = qt.min()
    dur = max(vt[-1], qt[-1] - t0) + 60
    n = int(dur * rate) + 1
    V = np.zeros(n)
    V[np.clip((vt * rate).astype(int), 0, n - 1)] = vsp
    Q = np.zeros(n)
    Q[np.clip(((qt - t0) * rate).astype(int), 0, n - 1)] = qsp

    k = np.ones(int(0.25 * rate))
    k /= k.sum()
    Ve = np.convolve(V, k, "same")
    Qe = np.convolve(Q, k, "same")
    Ve -= Ve.mean()
    Qe -= Qe.mean()

    N = 1 << (2 * n - 1).bit_length()
    C = np.fft.irfft(np.fft.rfft(Ve, N) * np.conj(np.fft.rfft(Qe, N)), N)
    C = np.concatenate([C[-(n - 1):], C[:n]])
    lags = np.arange(-n + 1, n)
    best = int(np.argmax(C))
    z = (C[best] - C.mean()) / (C.std() or 1.0)
    return lags[best] / rate, z


def add_vicon(rows, frames, vicon_path, cutoff_hz, transport_frac):
    """Label Vicon frames by trial and grasp, and derive the same measures from RFIN."""
    got = read_vicon_rfin(vicon_path)
    if got is None:
        return rows, None, 0.0, "no usable Trajectories/RFIN section in that file"
    vt, vp, rate = got

    try:
        lag, z = estimate_lag(vt, vp, rate, frames)
    except ImportError:
        return rows, None, 0.0, "numpy is needed for Vicon alignment (pip3 install numpy)"
    if lag is None:
        return rows, None, 0.0, "not enough overlapping movement to align"

    t0 = min(float(r["t_session_s"]) for r in frames)
    samples = [(vt[i], vp[i][0], vp[i][1], vp[i][2]) for i in range(len(vt))]
    samples = smooth(samples, cutoff_hz)
    sp = speeds(samples)

    def at(t_session):
        return nearest_index(samples, t_session - t0 + lag)

    # map every grasp to its Vicon window, using the SAME transport logic as the
    # Quest side so the two are measured the same way and can be compared
    marks = defaultdict(dict)
    for fr in frames:
        if (fr.get("event") or "").strip() == "grasp":
            key = ((fr.get("trial_number") or "").strip(), (fr.get("trial_attempt") or "1").strip())
            marks[key][(fr.get("grasp_in_trial") or "").strip()] = float(fr["t_session_s"])

    done = 0
    for row in rows:
        for c in VICON_COLS:
            row.setdefault(c, "")
        if (row.get("grasp_outcome") or "").strip() != "recorded":
            continue
        key = ((row.get("trial_number") or "").strip(), (row.get("trial_attempt") or "1").strip())
        gn = (row.get("grasp_in_trial") or "").strip()
        if key not in marks or gn not in marks[key]:
            continue
        i1 = at(marks[key][gn])
        prev = marks[key].get(str(int(gn) - 1))
        i0 = at(prev) if prev is not None else max(0, i1 - int(2.0 * rate))
        if i1 - i0 < 10:
            continue
        a, b = transport_window(sp, i0, i1, transport_frac)
        seg = sp[a:b + 1]
        if not seg:
            continue
        row["vicon_peak_speed_mps"] = f"{max(seg):.4f}"
        row["vicon_speed_peaks"] = str(count_speed_peaks(sp, a, b))
        row["vicon_transport_time_s"] = f"{samples[b][0] - samples[a][0]:.4f}"
        straight = math.dist(samples[a][1:4], samples[b][1:4])
        if straight > 1e-4:
            path = sum(math.dist(samples[i][1:4], samples[i + 1][1:4]) for i in range(a, b))
            row["vicon_path_ratio"] = f"{path / straight:.4f}"
        nj = normalised_jerk(samples, a, b)
        if nj is not None:
            row["vicon_normalised_jerk"] = f"{nj:.2f}"
        done += 1

    return rows, lag, z, f"{done} grasps labelled"


def write(path, rows, fieldnames):
    with open(path, "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=fieldnames, extrasaction="ignore")
        w.writeheader()
        w.writerows(rows)


def main():
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("grasp_csv", help="the per-grasp file")
    ap.add_argument("frames_csv", nargs="?", default=None,
                    help="the matching _frames.csv (found automatically if omitted)")
    ap.add_argument("--vicon", default=None,
                    help="optional Nexus CSV export. Aligned to the Quest by "
                         "cross-correlating the two hand-speed profiles; no sync "
                         "signal needed. Requires numpy.")
    ap.add_argument("--window", type=float, default=0.40,
                    help="seconds after the grab to search for closest approach (default 0.40)")
    ap.add_argument("--onset-frac", type=float, default=0.05,
                    help="proportion of peak speed counting as movement onset (default 0.05)")
    ap.add_argument("--transport-frac", type=float, default=0.20,
                    help="fraction of peak speed bounding the transport phase "
                         "(default 0.20). Used for path, peaks, jerk - NOT for "
                         "reaction time, which uses --onset-frac from the cue.")
    ap.add_argument("--cutoff-hz", type=float, default=8.0,
                    help="low-pass cutoff for hand position, Hz (default 8). Removes "
                         "tracking jitter; voluntary arm movement is below ~5 Hz.")
    args = ap.parse_args()

    frames_csv = args.frames_csv
    if frames_csv is None:
        guess = args.grasp_csv.rsplit(".", 1)[0] + "_frames.csv"
        if os.path.exists(guess):
            frames_csv = guess
        else:
            sys.exit("Could not find the frame file. Pass it as the second argument.")

    grasps, frames = read_csv(args.grasp_csv), read_csv(frames_csv)
    if not frames:
        sys.exit("The frame file is empty.")

    rows, matched = analyse(grasps, frames, args.window, args.onset_frac,
                            args.cutoff_hz, args.transport_frac)
    trials = per_trial(rows)
    acc = accuracy_table(rows)

    vicon_lag = vicon_z = None
    vicon_note = ""
    if args.vicon:
        rows, vicon_lag, vicon_z, vicon_note = add_vicon(
            rows, frames, args.vicon, args.cutoff_hz, args.transport_frac)

    stem = args.grasp_csv.rsplit(".", 1)[0]
    cols = list(grasps[0].keys()) + NEW_COLS + (VICON_COLS if args.vicon else [])
    write(stem + "_grasps.csv", rows, cols)
    write(stem + "_trials.csv", trials, list(trials[0].keys()) if trials else [])
    write(stem + "_accuracy.csv", acc, list(acc[0].keys()) if acc else [])

    # ------------------------------------------------------------------ summary
    def col(name, src=rows):
        return [v for v in (num(r, name) for r in src) if v is not None]

    rec = [r for r in rows if (r.get("grasp_outcome") or "").strip() == "recorded"]
    dec = [r for r in rows if (r.get("grasp_outcome") or "").strip()
           not in ("recorded", "trial_abandoned", "")]

    print(f"\n{'=' * 62}")
    print(f"  {os.path.basename(args.grasp_csv)}")
    print(f"{'=' * 62}")
    print(f"  trials {len(trials)}    grasps {len(rec)}    matched to frames {matched}")
    if dec:
        print(f"  grasps the app declined: {len(dec)}")
    bad = sum(1 for r in rec if (r.get("home_verified") or "").strip() == "FALSE")
    fs = sum(1 for r in rec if (r.get("false_start") or "").strip() == "TRUE")
    oe = sum(1 for r in rec if (r.get("order_correct") or "").strip() == "FALSE")
    print(f"  home not verified {bad}    false starts {fs}    order errors {oe}")

    print(f"\n  TIMING")
    print(f"    total trial time     {med(col('total_trial_time_s', trials)):6.3f} s")
    print(f"    reaction time        {med(col('reaction_time_s', trials)) * 1000:6.0f} ms   (5% of peak speed)")
    print(f"    movement time        {med(col('movement_time_s', trials)):6.3f} s")
    print(f"    reach time per grasp {med(col('reach_time_corrected_s')):6.3f} s")
    print(f"    transport per reach  {med(col('transport_time_s')):6.3f} s   (the movement itself)")

    print(f"\n  ENDPOINT   <- analyse lateral, not the others")
    print(f"    lateral error        {med(col('lateral_error_m')) * 100:6.2f} cm   THE AIM")
    print(f"    depth offset         {med(col('depth_offset_m')) * 100:+6.2f} cm   interaction artefact")
    print(f"    radial (uncorrected) {med(col('endpoint_error_m')) * 100:6.2f} cm   contains both")
    print(f"    speed at the grab    {med(col('hand_speed_at_grasp_mps')) * 1000:6.0f} mm/s")

    print(f"\n  MOVEMENT QUALITY")
    print(f"    peak speed           {med(col('peak_speed_mps')) * 1000:6.0f} mm/s")
    print(f"    speed peaks          {med(col('speed_peaks')):6.1f}      (1 = one smooth movement)")
    print(f"    path ratio           {med(col('path_ratio')):6.3f}      (1.0 = straight)")
    print(f"    time after peak      {med(col('time_after_peak_pct')):6.1f} %")
    print(f"    normalised jerk      {med(col('normalised_jerk')):6.1f}      (lower = smoother)")

    conf = [r for r in frames if (r.get("right_confidence") or "").strip() == "-1"]
    print(f"\n  TRACKING")
    print(f"    hand position low-passed at {args.cutoff_hz:.0f} Hz before speed/path/jerk")
    print(f"    frames {len(frames)}, untracked {len(conf)} ({100.0 * len(conf) / max(1, len(frames)):.1f}%)")

    if args.vicon:
        print(f"\n  VICON")
        if vicon_lag is None:
            print(f"    not used: {vicon_note}")
        else:
            trust = "clear" if vicon_z >= 8 else "WEAK - check this"
            print(f"    alignment lag {vicon_lag:+.3f} s   peak z={vicon_z:.1f}  ({trust})")
            print(f"    {vicon_note}")
            print(f"    peak speed      {med(col('vicon_peak_speed_mps')) * 1000:6.0f} mm/s   "
                  f"(Quest: {med(col('peak_speed_mps')) * 1000:.0f})")
            print(f"    path ratio      {med(col('vicon_path_ratio')):6.3f}      "
                  f"(Quest: {med(col('path_ratio')):.3f})")
            print(f"    speed peaks     {med(col('vicon_speed_peaks')):6.1f}      "
                  f"(Quest: {med(col('speed_peaks')):.1f})")

    print(f"\n  WROTE")
    for suffix in ("_grasps.csv", "_trials.csv", "_accuracy.csv"):
        print(f"    {os.path.basename(stem + suffix)}")
    print()


if __name__ == "__main__":
    main()
