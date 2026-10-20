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
    ..._vicon_precision.csv   RFIN endpoint precision per target, inside the Vicon
                              frame - written only when --vicon is supplied
    ..._vicon_trajectory.csv  RFIN path variability per target, whole reach and
                              final 25% - written only when --vicon is supplied

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

    Returns [(t_session, x, y, z)] - the SESSION clock, not t_since_cue_s.

    This matters more than it looks. t_since_cue_s is zeroed at the warning tone
    AND again at the cue, so within one trial it ramps up, drops back to zero,
    and ramps again. It is not a clock, and used as one it breaks three things
    that all read the time column:

      - speeds() divides by the gap between samples. Across the reset that gap
        is NEGATIVE, so speed at and either side of the cue came out as zero.
      - smooth() takes the median gap to get the sampling rate.
      - nearest_index() matches a timestamp against the column. With two ramps
        there are two samples at (say) 1.9 s, and it could return the one in the
        foreperiod instead of the one after the cue.

    t_session_s is monotonic across the whole file, so all three behave. The one
    thing that genuinely needs cue-relative time is the reported reaction time,
    which subtracts the cue sample's own timestamp - see cue_sample_index().

    Older frame logs without a t_session_s column fall back to t_since_cue_s and
    inherit all of the above; re-export rather than relying on it.

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
        t = num(fr, "t_session_s")
        if t is None:
            t = num(fr, "t_since_cue_s")       # legacy logs only; see docstring
        x, y, z = num(fr, cx), num(fr, cy), num(fr, cz)
        if None in (t, x, y, z):
            continue
        out.append((t, x, y, z))
    return out


def cue_sample_index(tf, hand):
    """
    (index, timestamp) of the cue: the first hand sample at or after it, and the
    cue event's own session time.

    Both are needed. The index is where every "start of the trial" search has to
    begin; the timestamp is what reaction time is measured from, and using the
    sample's time instead would quantise RT to the 13.9 ms tracking interval and
    bias it short by up to one sample.

    This exists because t_since_cue_s is NOT time since the cue. The logger
    zeroes it at the warning tone AND again at the cue, so every trial contains
    two ramps from zero and the column cannot tell them apart. Anything that
    looked up "the start of the trial" as sample 0 was therefore reading the
    WARNING, which on P60 Visit 1 sat a median 2.5 s earlier - and right after
    the participant had repositioned their hand at home, so it was still moving.

    What that cost, measured on P60 Visit 1 (60 trials):
      - speed_at_cue_mps read the warning. 24/60 trials looked like anticipation
        (>100 mm/s). At the actual cue only 2/60 are above it, median 14 mm/s,
        which is below the 32 mm/s tracking-noise floor. There was no
        anticipation problem; there was a column measuring the wrong instant.
      - the reach axis for grasp 1 started at the warning position, a median
        21.5 mm away from where the hand actually was at the cue, tilting the
        axis a median 2.4 deg. This one turned out to be nearly harmless, and
        the reason is worth keeping: the tilt rotates the ENDPOINT ERROR vector,
        not the reach. Those errors are about 9 mm, so 2.4 deg moves the
        depth/lateral split by sin(2.4) * 9 = 0.4 mm. Re-running P60 Visits 1
        and 2 confirmed it: 298 of 298 grasps moved under 5 mm, 90th percentile
        0.4 mm. Scaling the tilt by the 400 mm reach length instead would give
        17 mm and would be the wrong quantity.
      - the RT onset search began at the warning, so it could lock onto the hand
        settling during the foreperiod. 1/60 trials did: trial 11 reported
        2291 ms for a real 305 ms.

    Counting is exact rather than approximate: hand_samples is a causal filter
    over the frames in order, so the number of samples the pre-cue frames yield
    is precisely the index at which the post-cue ones begin. Matching on the
    time column instead would be ambiguous, which is the bug this fixes.

    Falls back to 0 when no cue event is present, which is the old behaviour and
    is correct for the trial-1 window that opens at the cue already.
    """
    ci = next((i for i, fr in enumerate(tf)
               if (fr.get("event") or "").strip() == "cue"), None)
    if ci is None:
        return 0, None
    return len(hand_samples(tf[:ci], hand)), num(tf[ci], "t_session_s")


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
            # Index 0 is ambiguous and must not be reported as an onset. The frame
            # log starts AT cue onset, so a sub-threshold sample at index 0 means
            # onset was at or before the first sample there is - which is not a
            # measurable onset, and as a reaction time comes out as exactly 0.
            return i if i > 0 else None
    # Walked all the way back from the peak without ever dropping below the
    # threshold: the hand was already moving when logging began. There is no
    # onset in this data. Returning 0 here reported RT = 0 s on the 8 Oct pilot.
    return None


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
    "endpoint_error_corrected_m", "endpoint_outcome",
    "depth_offset_m", "lateral_error_m",
    "endpoint_settle_s", "endpoint_improvement_m",
    "reach_time_corrected_s", "hand_speed_at_grasp_mps", "peak_speed_mps",
    "time_after_peak_pct", "speed_peaks", "path_ratio", "normalised_jerk",
    "transport_time_s",
    "rt_speed_criterion_s", "speed_at_cue_mps", "samples_in_reach",
]


def analyse(grasps, frames, window, onset_frac, cutoff_hz, transport_frac,
            max_endpoint_error=0.10):
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

        t_mark = num(mark, "t_session_s")      # same clock as hand_samples
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

        # Where the cue actually is. See cue_sample_index(): sample 0 is the
        # warning tone, not the cue. Clamped so a cue found at or after the
        # grasp can never invert a search interval.
        cue_i, t_cue = cue_sample_index(tf, hand)
        if cue_i >= mark_i:
            cue_i, t_cue = 0, None

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
            start = s[cue_i][1:4] if gn == "1" else None
            if start is None:
                prev = next((fr for fr in tf
                             if (fr.get("event") or "").strip() == "grasp"
                             and (fr.get("grasp_in_trial") or "").strip() == str(int(gn) - 1)), None)
                if prev is not None:
                    pt = num(prev, "t_session_s")
                    start = s[nearest_index(s, pt)][1:4] if pt is not None else s[cue_i][1:4]
                else:
                    start = s[cue_i][1:4]

            ax = (tx - start[0], ty - start[1], tz - start[2])
            mag = math.sqrt(sum(c * c for c in ax))
            if mag > 1e-6:
                ax = tuple(c / mag for c in ax)
                err = (best_p[0] - tx, best_p[1] - ty, best_p[2] - tz)
                depth = sum(e * a for e, a in zip(err, ax))
                perp = tuple(e - depth * a for e, a in zip(err, ax))
                row["depth_offset_m"] = f"{depth:.5f}"
                row["lateral_error_m"] = f"{math.sqrt(sum(c * c for c in perp)):.5f}"

            # --- validity threshold on the endpoint -----------------------------
            #
            # A grasp can register with the fingertip nowhere near the target. Two
            # such grasps appeared in the first 300 collected (15 Oct 2026): 41.2 cm
            # on Visit 1 trial 40, 32.8 cm on Visit 2 trial 1. Both were saved as
            # ordinary valid grasps, and each one on its own moved its cell's VE by
            # an order of magnitude - position 3 read 93 mm where the other
            # seventeen reaches to it gave 15 mm.
            #
            # The cut is at half the MINIMUM inter-target spacing: the grid is
            # 250 mm between columns and 200 mm between rows, so beyond 100 mm the
            # fingertip may be nearer a different target than the intended one and
            # the measurement is no longer interpretable. That is a geometric
            # argument rather than a percentile, which is what makes it
            # preregisterable.
            #
            # The data does not constrain the choice tightly: across those 300
            # grasps the largest valid endpoint error was 60 mm and the smallest
            # failure 328 mm, so any cut between 80 and 250 mm flags exactly the
            # same two. Insensitivity over a 3x range is the point - this is a
            # boundary, not a tuned parameter.
            #
            # WHAT IS AND IS NOT DISCARDED. Only the endpoint. depth_offset_m and
            # lateral_error_m are blanked, which removes the grasp from
            # accuracy_table() and from the per-trial means automatically, since
            # both already skip rows where those are absent. Everything else
            # survives: the trial keeps its total time, reaction time, movement
            # time and order-error status, none of which depend on where the grasp
            # registered. On Visit 1 trial 40 the reach itself was entirely normal
            # - 0.46 m displacement, ordinary speed profile - and only the
            # registration was wrong, so discarding the trial would throw away good
            # timing data to fix an endpoint problem.
            #
            # The Vicon endpoint columns are also left alone, for the same reason:
            # a Quest registration failure says nothing about where RFIN was.
            #
            # endpoint_error_corrected_m is KEPT so the size of the failure stays
            # visible in the file rather than vanishing into a blank.
            if max_endpoint_error and best_d > max_endpoint_error:
                row["endpoint_outcome"] = "out_of_range"
                row["depth_offset_m"] = ""
                row["lateral_error_m"] = ""
            else:
                row["endpoint_outcome"] = "ok"

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
            search_from = cue_i        # the cue, not the warning
        else:
            prev = next((fr for fr in tf
                         if (fr.get("event") or "").strip() == "grasp"
                         and (fr.get("grasp_in_trial") or "").strip() == str(int(gn) - 1)), None)
            pt = num(prev, "t_session_s") if prev is not None else None
            search_from = nearest_index(s, pt) if pt is not None else 0

        rel = onset_index(sp[search_from:], mark_i - search_from, onset_frac)
        lo = search_from + rel if rel is not None else search_from

        # Reaction time exists only for the first grasp: it is measured from cue
        # onset, and grasps 2 and 3 have no cue of their own.
        if gn == "1" and rel is not None:
            # s[][0] is session time now, so subtract the cue to get RT. Prefer
            # the cue event's own timestamp; fall back to its sample only if the
            # event carries no session time.
            zero = t_cue if t_cue is not None else s[cue_i][0]
            row["rt_speed_criterion_s"] = f"{s[lo][0] - zero:.4f}"

        row["hand_speed_at_grasp_mps"] = f"{sp[mark_i]:.4f}"

        # How fast the hand was AT THE CUE. Anything well above the tracking
        # noise floor (about 32 mm/s, from the held-still validation) means the
        # hand was already moving when the go signal appeared - the trial
        # anticipated it. Worth having as a column because such a trial can
        # still produce a plausible-looking RT: if the hand dips below threshold
        # after the cue the search locks onto that dip and measures a second
        # movement rather than the response.
        #
        # This read sp[0] until 16 Oct 2026, which was the warning tone. See
        # cue_sample_index() for what that cost and how it was found.
        if gn == "1":
            row["speed_at_cue_mps"] = f"{sp[cue_i]:.4f}"

        # For GRASP 1 an unfound onset means there is no defined start, so these
        # stay blank rather than silently measuring from the cue. For grasps 2
        # and 3 it means something different and harmless: onset could not be
        # placed inside the interval, so the reach is measured from the previous
        # grasp, which is where it does in fact begin. Only grasp 1 loses them.
        if rel is not None or gn != "1":
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
            "rt_speed_criterion_s": fmt(rt, 4),
            "movement_time_s": fmt(t_end - rt, 4) if (t_end is not None and rt is not None) else "",
            "order_errors": str(sum(1 for r in rs if (r.get("order_correct") or "").strip() == "FALSE")),
            "mean_lateral_error_m": fmt(st.mean([v for v in (num(r, "lateral_error_m") for r in rs) if v is not None]), 5)
            if any(num(r, "lateral_error_m") is not None for r in rs) else "",
            "mean_depth_offset_m": fmt(st.mean([v for v in (num(r, "depth_offset_m") for r in rs) if v is not None]), 5)
            if any(num(r, "depth_offset_m") is not None for r in rs) else "",
        })
    return out


def manipulation_check(rows):
    """
    Did the hidden condition actually hide the hands?

    Visit 3's whole result rests on one bit per trial, and nothing in this
    pipeline looked at it until now. `hands_visible` is written from the live
    state of HandVisibilityToggle, and since 20 Oct it reports what actually
    happened rather than what was asked for - a hide that silently fails leaves
    it TRUE. So a row with block=Hidden and hands_visible=True is a trial the
    participant could see their hand on, inside the condition defined by not
    being able to.

    That failure has already happened once in this project. The 28 Sep pilot
    looked like it had two conditions and had one, because the flag recorded
    the request and the request did nothing. It was invisible in the data.

    Returns (rows_of_table, list_of_problem_strings).
    """
    cells = defaultdict(lambda: {"True": 0, "False": 0, "": 0})
    for r in rows:
        if (r.get("grasp_outcome") or "").strip() != "recorded":
            continue
        blk = (r.get("block") or "").strip()
        hv = (r.get("hands_visible") or "").strip()
        key = "True" if hv.lower() == "true" else ("False" if hv.lower() == "false" else "")
        cells[blk][key] += 1

    expected = {"Hidden": "False", "Visible": "True", "Refamiliarisation": "True"}

    out, problems = [], []
    for blk in sorted(cells):
        c = cells[blk]
        total = c["True"] + c["False"] + c[""]
        want = expected.get(blk)
        wrong = 0 if want is None else total - c[want] - 0
        if want is not None:
            wrong = total - c[want]
        out.append({
            "block": blk, "n": str(total),
            "hands_visible_TRUE": str(c["True"]),
            "hands_visible_FALSE": str(c["False"]),
            "blank": str(c[""]),
            "expected": want or "(no expectation)",
            "verdict": "-" if want is None else ("ok" if wrong == 0 else f"{wrong} WRONG"),
        })
        if want is not None and wrong:
            problems.append(
                f"{blk}: {wrong} of {total} grasps have hands_visible != {want}. "
                + ("The hidden condition did not hold on those trials and they are "
                   "not usable for H4." if blk == "Hidden" else
                   "The hands were not visible when they should have been."))
    return out, problems


def accuracy_table(rows, min_n=10):
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

    # The plan reports VE only at 10 or more observations in a cell: below that
    # the spread about the participant's own mean is unstable and can read as
    # falsely HIGH precision. vicon_precision_table() already carries that flag;
    # this table did not, so it printed a VE for n=3 with nothing to say it was
    # not usable. Measured on Visit 2, 15 Oct 2026: not one of its 22 cells
    # reached 10 - retention gives 3 to 6 per cell and transfer gives 9 - so
    # every VE in that file was below the plan's own threshold and nothing said
    # so.
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
            "enough_for_VE": "yes" if len(v) >= min_n else f"NO - needs {min_n}",
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

def vicon_precision_table(rows, min_n=10):
    """
    Endpoint PRECISION from Vicon RFIN: the spread of the reach endpoint across
    repeated reaches to the same target, computed entirely inside Vicon's own
    coordinate frame.

    This is the study plan's independent validation of precision. It does not
    depend on Quest hand tracking and it needs no spatial alignment between the
    two systems - only the temporal alignment that add_vicon() has already done,
    which is what says which reach went to which target.

    Reported per (block, target position), because bias and spread both vary with
    target position and pooling across positions would mix a position effect into
    whatever else is being compared.

    VE is the 3D variable error: RMS distance from the participant's own mean
    endpoint for that target. A constant marker offset does not change it, which
    is why precision survives the RFIN offset that makes accuracy unusable.

    `enough_for_VE` follows the plan's rule that VE needs at least 10 observations
    in a cell. Below that the spread about the mean is unstable and can read as
    falsely high precision, so the flag is carried in the file rather than left to
    be remembered.
    """
    cells = defaultdict(list)
    for r in rows:
        if (r.get("grasp_outcome") or "").strip() != "recorded":
            continue
        pos = (r.get("target_position_number") or "").strip()
        x = num(r, "vicon_endpoint_x_m")
        y = num(r, "vicon_endpoint_y_m")
        z = num(r, "vicon_endpoint_z_m")
        if not pos or x is None or y is None or z is None:
            continue
        cells[(r.get("block", ""), r.get("task", ""), pos)].append((x, y, z))

    # --- sanity: is each endpoint nearest its OWN target? --------------------
    #
    # A Vicon endpoint can land on the wrong reach. On 15 Oct 2026, Visit 2
    # trial 29 (Transfer-5, position 3) produced an endpoint 45 cm from the
    # other eight reaches to that target and nearest to position 7, while the
    # Quest reported a perfectly ordinary grasp for the same reach. One such
    # point took that cell's VE from about 15 mm to 160.49 mm.
    #
    # The test is the same idea as the Quest-side 10 cm rule, and equally
    # non-circular: take each cell's ROBUST centre (the median, so one bad
    # point cannot drag it), then ask which centre each endpoint is closest to.
    # An endpoint nearer some other target than its own is not a measurement of
    # the target it is filed under, whatever produced it.
    #
    # Flagged points are excluded from VE and listed in `excluded`, so the
    # number is visible rather than silently absorbed. This does not explain the
    # cause - it stops the symptom reaching a result.
    def _median_point(pts):
        return tuple(st.median([p[i] for p in pts]) for i in range(3))

    centres = {k: _median_point(v) for k, v in cells.items() if len(v) >= 3}
    excluded = defaultdict(int)
    if len(centres) >= 2:
        for key in list(cells):
            own = centres.get(key)
            if own is None:
                continue
            others = [c for k, c in centres.items()
                      if k[2] != key[2] and k[0] == key[0]]   # same block, other targets
            if not others:
                continue
            kept = []
            for p in cells[key]:
                d_own = math.dist(p, own)
                if any(math.dist(p, o) < d_own for o in others):
                    excluded[key] += 1
                else:
                    kept.append(p)
            if kept:
                cells[key] = kept

    out = []
    for (block, task, pos), pts in sorted(cells.items(),
                                          key=lambda kv: (kv[0][0], kv[0][1], int(kv[0][2]))):
        if len(pts) < 2:
            continue
        mx = st.mean([p[0] for p in pts])
        my = st.mean([p[1] for p in pts])
        mz = st.mean([p[2] for p in pts])
        ve = math.sqrt(st.mean([(p[0] - mx) ** 2 + (p[1] - my) ** 2 + (p[2] - mz) ** 2
                                for p in pts]))
        sdx = st.stdev([p[0] for p in pts]) if len(pts) > 1 else float("nan")
        sdy = st.stdev([p[1] for p in pts]) if len(pts) > 1 else float("nan")
        sdz = st.stdev([p[2] for p in pts]) if len(pts) > 1 else float("nan")
        out.append({
            "block": block, "task": task, "target_position_number": pos,
            "n": str(len(pts)),
            "excluded_wrong_target": str(excluded.get((block, task, pos), 0)),
            "enough_for_VE": "yes" if len(pts) >= min_n else f"NO - needs {min_n}",
            "vicon_VE_mm": f"{ve * 1000.0:.2f}",
            "sd_x_mm": f"{sdx * 1000.0:.2f}", "sd_y_mm": f"{sdy * 1000.0:.2f}",
            "sd_z_mm": f"{sdz * 1000.0:.2f}",
            "mean_x_m": f"{mx:.4f}", "mean_y_m": f"{my:.4f}", "mean_z_m": f"{mz:.4f}",
        })
    return out


def trajectory_variability_table(trajectories, n_points=101, min_n=10):
    """
    Spatial variability of the movement PATH across repeated reaches to the same
    target, from Vicon RFIN.

    Precision asks whether the hand ends in the same place. This asks whether it
    gets there the same way - a different question, and the one that should move
    first if removing sight of the hand forces online correction.

    Each reach is resampled to `n_points` by normalised path distance rather than
    by time, so reaches of different duration are compared position for position
    along the path instead of instant for instant. The across-reach SD is taken at
    each point and reported two ways:

      mean_sd_mm      averaged over the whole reach
      terminal_sd_mm  averaged over the final 25%, where endpoint corrections live

    The terminal figure is the more sensitive of the two for Visit 3: a hand that
    cannot be seen is corrected late, not early.

    Like the precision table this lives entirely in Vicon's frame and is grouped by
    target position, never pooled across positions.
    """
    out = []
    for (block, task, pos), paths in sorted(trajectories.items(),
                                            key=lambda kv: (kv[0][0], kv[0][1], int(kv[0][2]))):
        resampled = []
        for path in paths:
            if len(path) < 3:
                continue
            d = [0.0]
            for i in range(1, len(path)):
                d.append(d[-1] + math.dist(path[i - 1], path[i]))
            if d[-1] <= 1e-9:
                continue
            u = [x / d[-1] for x in d]
            grid = [i / (n_points - 1) for i in range(n_points)]
            cols = []
            for axis in range(3):
                vals, j = [], 0
                for g in grid:
                    while j < len(u) - 2 and u[j + 1] < g:
                        j += 1
                    span = u[j + 1] - u[j]
                    f = 0.0 if span <= 0 else (g - u[j]) / span
                    vals.append(path[j][axis] + f * (path[j + 1][axis] - path[j][axis]))
                cols.append(vals)
            resampled.append(list(zip(*cols)))
        if len(resampled) < 2:
            continue

        pointwise = []
        for i in range(n_points):
            sds = [st.stdev([r[i][axis] for r in resampled]) for axis in range(3)]
            pointwise.append(math.sqrt(sum(v * v for v in sds)))
        tail = pointwise[int(0.75 * n_points):]
        out.append({
            "block": block, "task": task, "target_position_number": pos,
            "n_reaches": str(len(resampled)),
            "enough_for_report": "yes" if len(resampled) >= min_n else f"NO - needs {min_n}",
            "mean_sd_mm": f"{1000.0 * st.mean(pointwise):.2f}",
            "terminal_sd_mm": f"{1000.0 * st.mean(tail):.2f}",
            "max_sd_mm": f"{1000.0 * max(pointwise):.2f}",
        })
    return out


VICON_COLS = ["vicon_peak_speed_mps", "vicon_path_ratio", "vicon_speed_peaks",
              "vicon_normalised_jerk", "vicon_transport_time_s",
              "vicon_time_after_peak_pct",
              "vicon_endpoint_x_m", "vicon_endpoint_y_m", "vicon_endpoint_z_m",
              "vicon_endpoint_lead_s"]

# Joint kinematics - the twelfth and last planned outcome measure.
#
# Angles at the moment of the grasp, and range of motion over the transport
# window, for the three joints the marker set supports. Measured on the same
# window as every other Vicon measure above, so they describe the same reach.
JOINT_COLS = ["vicon_elbow_flex_grasp_deg", "vicon_elbow_flex_rom_deg",
              "vicon_shoulder_flex_grasp_deg", "vicon_shoulder_flex_rom_deg",
              "vicon_shoulder_abd_grasp_deg",
              "vicon_wrist_flex_grasp_deg", "vicon_wrist_dev_grasp_deg"]


# WHICH EULER CONVENTION, AND WHY NOT THE ONE ISB RECOMMENDS.
#
# The shoulder is read from RShoAnglesXZY. RShoAnglesYZY is also in every
# export and is closer to the ISB recommendation for a ball joint, but it is
# not safe on this data:
#
#   session            RShoAnglesXZY        RShoAnglesYZY
#   P60 Visit 1        clean                clean
#   P60 Visit 2        clean                clean
#   CAL01 Visit 3      clean                X spans 5686 deg, Z spans 5683 deg
#                                           (15.8 turns), 9 and 8 jumps > 90 deg
#
# The Visit 3 failure is gimbal lock, and the evidence is specific: all nine
# jumps occur at YZY elevation below 0.8 degrees, median 0.5. When the middle
# rotation of a YZY sequence goes to zero the two outer rotations become
# degenerate and trade against each other, which is exactly why X and Z span
# nearly equal and opposite amounts. 67.7% of all apparent motion in YZY X
# happens in the 12.6% of frames with elevation below 20 degrees - the arm
# hanging at rest between trials.
#
# UNWRAPPING DOES NOT FIX THIS, which is worth stating because the study plan
# once claimed it would. Unwrapping repairs 180-degree wraps in an otherwise
# well-defined signal. At 0.5 degrees of elevation the plane-of-elevation angle
# is genuinely undefined and no post-processing recovers it.
#
# Why Visits 1 and 2 looked fine: P60 dropped below 10 degrees on 0.65% of
# frames. Visit 3 does so on 6.34% and reaches 0.1, because it is a longer
# session with more rest and the arm comes all the way down. Checking one
# session was not enough, and on 20 Oct 2026 it produced the wrong answer.
#
# XZY's own gimbal lock is at second-rotation +-90 degrees. Across all three
# sessions that component stays within -45.4 to +57.1, so it is nowhere near.
#
# Component identification is geometric, not assumed:
#   RElbAnglesXZY X  = elbow flexion.    r = -0.995 against shoulder-to-wrist
#                                        distance (n = 48,656, P60 Visit 1).
#   RShoAnglesXZY X  = humerus elevation. r = -0.980 against the angle of the
#                                        RGH->REJC vector from vertical
#                                        (n = 41,565, CAL01 Visit 3).
#   RShoAnglesXZY Y  = the orthogonal component, r = +0.048 against the same -
#                      abduction/adduction.
SHOULDER_CONV = "RShoAnglesXZY"
ELBOW_CONV = "RElbAnglesXZY"
WRIST_CONV = "RWristAnglesXZY"


def read_vicon_angles(path):
    """
    Pull the joint angles out of the Model Outputs section of a Nexus export.

    Returns (by_frame, rate, coverage_pct) where by_frame maps
    "RElbAnglesXZY_X" -> a list indexed by (frame number - 1), holding a float
    or None. Indexed by frame rather than packed, so a time converts straight
    to an index and gaps stay visible instead of silently closing up.

    Returns None if there is no Model Outputs section, which is the normal
    state for an export whose pipeline was not run over the full range.

    THE SUBJECT PREFIX IS STRIPPED. Columns read "p01:RElbAnglesXZY" even in a
    CAL01 Visit 3 export, because the Nexus subject has never been renamed. The
    prefix is matched off rather than trusted; relying on it would have silently
    returned nothing for every session so far.
    """
    with open(path, newline="", encoding="utf-8-sig") as fh:
        rows = list(csv.reader(fh))

    known = {"Joints", "Trajectories", "Model Outputs", "Segments", "Devices"}
    secs = [(i, r[0].strip()) for i, r in enumerate(rows) if r and r[0].strip() in known]
    mo = next((i for i, nm in secs if nm == "Model Outputs"), None)
    if mo is None:
        return None

    try:
        rate = float(rows[mo + 1][0])
    except (IndexError, ValueError):
        return None

    objs = rows[mo + 2]
    comps = rows[mo + 3] if len(rows) > mo + 3 else []

    wanted = {SHOULDER_CONV, ELBOW_CONV, WRIST_CONV}
    cols = {}
    cur = ""
    for k, c in enumerate(objs):
        if c.strip():
            cur = c.strip().split(":")[-1]          # strip "p01:" and friends
        if cur in wanted and k < len(comps):
            axis = comps[k].strip()
            if axis in ("X", "Y", "Z"):
                cols[f"{cur}_{axis}"] = k
    if not cols:
        return None

    end = next((i for i, _ in secs if i > mo), len(rows))
    body = rows[mo + 5:end]
    nframes = 0
    for r in body:
        try:
            nframes = max(nframes, int(r[0]))
        except (IndexError, ValueError):
            continue
    if nframes == 0:
        return None

    by_frame = {name: [None] * nframes for name in cols}
    filled = 0
    for r in body:
        try:
            f = int(r[0])
        except (IndexError, ValueError):
            continue
        any_here = False
        for name, k in cols.items():
            if k < len(r) and r[k].strip():
                try:
                    by_frame[name][f - 1] = float(r[k])
                    any_here = True
                except ValueError:
                    pass
        if any_here:
            filled += 1

    coverage = 100.0 * filled / nframes if nframes else 0.0
    return by_frame, rate, coverage


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


def add_vicon(rows, frames, vicon_path, cutoff_hz, transport_frac, endpoint_window=0.40):
    """Label Vicon frames by trial and grasp, and derive the same measures from RFIN.

    Returns (rows, lag, z, note, trajectories), where trajectories maps
    (block, task, target position) -> list of reach paths, each a list of (x, y, z)
    in metres. Collected here rather than recomputed later because this is the only
    place that knows which Vicon samples belong to which reach.
    """
    traj = defaultdict(list)
    _angle_coverage = [0.0]          # filled in below; read by main() for the summary
    got = read_vicon_rfin(vicon_path)
    if got is None:
        return rows, None, 0.0, "no usable Trajectories/RFIN section in that file", {}
    vt, vp, rate = got

    try:
        lag, z = estimate_lag(vt, vp, rate, frames)
    except ImportError:
        return rows, None, 0.0, "numpy is needed for Vicon alignment (pip3 install numpy)", {}
    if lag is None:
        return rows, None, 0.0, "not enough overlapping movement to align", {}

    # Joint angles, if the pipeline was run over the full range in Nexus.
    # Deliberately not fatal: everything above works without them.
    ang = read_vicon_angles(vicon_path)
    angles, ang_rate, ang_cov = (ang if ang else (None, rate, 0.0))
    _angle_coverage[0] = ang_cov
    add_vicon.last_angle_coverage = ang_cov
    add_vicon.last_angle_names = sorted(angles.keys()) if angles else []

    def angle_at(name, t_vicon):
        """Angle value at a Vicon-clock time, or None.

        Looked up by TIME rather than by position in the marker series. The
        marker series drops frames with missing data, so its index and the
        Model Outputs row number are not the same thing - using one for the
        other would mis-assign angles by however many frames had been dropped
        earlier in the capture, with no symptom.
        """
        if not angles or name not in angles:
            return None
        i = int(round(t_vicon * ang_rate)) - 1
        if i < 0 or i >= len(angles[name]):
            return None
        return angles[name][i]

    def angle_span(name, t_from, t_to):
        """Range of motion between two Vicon-clock times, or None."""
        if not angles or name not in angles:
            return None
        a = int(round(t_from * ang_rate)) - 1
        b = int(round(t_to * ang_rate)) - 1
        if a > b:
            a, b = b, a
        a, b = max(0, a), min(len(angles[name]) - 1, b)
        v = [x for x in angles[name][a:b + 1] if x is not None]
        return (max(v) - min(v)) if len(v) >= 2 else None

    t0 = min(float(r["t_session_s"]) for r in frames)
    samples = [(vt[i], vp[i][0], vp[i][1], vp[i][2]) for i in range(len(vt))]
    samples = smooth(samples, cutoff_hz)
    sp = speeds(samples)

    v_first, v_last = samples[0][0], samples[-1][0]

    def at(t_session):
        """
        Vicon sample index for a Quest timestamp, or None if that moment is
        outside the capture.

        The bounds check is not optional. nearest_index returns the CLOSEST
        sample, so without it every grasp after the end of the Vicon data maps
        silently to its last frame and yields a number that looks like a
        measurement. A Nexus export truncated to part of the session - which is
        easy to produce, and was how this was found - would then report Vicon
        measures for trials it never recorded.
        """
        t = t_session - t0 + lag
        if t < v_first or t > v_last:
            return None
        return nearest_index(samples, t)

    # map every grasp to its Vicon window, using the SAME transport logic as the
    # Quest side so the two are measured the same way and can be compared
    marks = defaultdict(dict)
    for fr in frames:
        if (fr.get("event") or "").strip() == "grasp":
            key = ((fr.get("trial_number") or "").strip(), (fr.get("trial_attempt") or "1").strip())
            marks[key][(fr.get("grasp_in_trial") or "").strip()] = float(fr["t_session_s"])

    done = 0
    for row in rows:
        for c in VICON_COLS + JOINT_COLS:
            row.setdefault(c, "")
        if (row.get("grasp_outcome") or "").strip() != "recorded":
            continue
        key = ((row.get("trial_number") or "").strip(), (row.get("trial_attempt") or "1").strip())
        gn = (row.get("grasp_in_trial") or "").strip()
        if key not in marks or gn not in marks[key]:
            continue
        i1 = at(marks[key][gn])
        if i1 is None:
            continue
        prev = marks[key].get(str(int(gn) - 1))
        i0 = at(prev) if prev is not None else None
        if i0 is None:
            i0 = max(0, i1 - int(2.0 * rate))
        if i1 - i0 < 10:
            continue
        a, b = transport_window(sp, i0, i1, transport_frac)
        seg = sp[a:b + 1]
        if not seg:
            continue
        row["vicon_peak_speed_mps"] = f"{max(seg):.4f}"

        # Joint angles on the same transport window as everything else here, so
        # they describe the same reach rather than a slightly different one.
        t_grasp = samples[i1][0]
        t_a, t_b = samples[a][0], samples[b][0]

        def put(colname, value, fmt="{:.2f}"):
            if value is not None:
                row[colname] = fmt.format(value)

        put("vicon_elbow_flex_grasp_deg", angle_at(ELBOW_CONV + "_X", t_grasp))
        put("vicon_elbow_flex_rom_deg", angle_span(ELBOW_CONV + "_X", t_a, t_b))
        put("vicon_shoulder_flex_grasp_deg", angle_at(SHOULDER_CONV + "_X", t_grasp))
        put("vicon_shoulder_flex_rom_deg", angle_span(SHOULDER_CONV + "_X", t_a, t_b))
        put("vicon_shoulder_abd_grasp_deg", angle_at(SHOULDER_CONV + "_Y", t_grasp))
        put("vicon_wrist_flex_grasp_deg", angle_at(WRIST_CONV + "_X", t_grasp))
        put("vicon_wrist_dev_grasp_deg", angle_at(WRIST_CONV + "_Y", t_grasp))

        # Time after peak velocity, the deceleration phase as a percentage of the
        # transport phase. Same arithmetic as the Quest column above, on Vicon's
        # trace. This is the version to report: the measure depends on locating the
        # speed peak, and Quest tracking noise splits a single reach into several
        # apparent peaks - on this session the Quest saw a median of 3 per reach
        # where Vicon saw 1 - so the peak it finds is often not the real one.
        pos_key = (row.get("block", ""), row.get("task", ""),
                   (row.get("target_position_number") or "").strip())
        if pos_key[2]:
            traj[pos_key].append([tuple(samples[i][1:4]) for i in range(a, b + 1)])

        pk_i = a + seg.index(max(seg))
        span = samples[b][0] - samples[a][0]
        if span > 0:
            row["vicon_time_after_peak_pct"] = (
                f"{100.0 * (samples[b][0] - samples[pk_i][0]) / span:.1f}")
        row["vicon_speed_peaks"] = str(count_speed_peaks(sp, a, b))
        row["vicon_transport_time_s"] = f"{samples[b][0] - samples[a][0]:.4f}"
        straight = math.dist(samples[a][1:4], samples[b][1:4])
        if straight > 1e-4:
            path = sum(math.dist(samples[i][1:4], samples[i + 1][1:4]) for i in range(a, b))
            row["vicon_path_ratio"] = f"{path / straight:.4f}"
        nj = normalised_jerk(samples, a, b)
        if nj is not None:
            row["vicon_normalised_jerk"] = f"{nj:.2f}"

        # Where RFIN ended this reach, in Vicon's own frame. read_vicon_rfin()
        # converts the export's millimetres to metres, so these are METRES, matching
        # every other distance in this file. This is what the
        # precision table below is built from: the scatter of this point across
        # repeated reaches to the SAME target. It needs no coordinate alignment with
        # the headset, because every term is in the Vicon frame.
        # NOT samples[b]. The transport window ends at the first sample below 20%
        # of peak speed, which on a clean reach is the target - but a reach
        # containing a pause dips below that threshold MID-FLIGHT, and b then
        # marks the pause rather than the arrival. Measured on Visit 2, 15 Oct
        # 2026: 38% of endpoints landed more than 100 ms before their grasp, the
        # worst by 1.16 s, and one of them fell nearer a different target.
        #
        # Arrival is the FIRST LOCAL speed minimum at or after the grasp. The hand
        # is still moving when the grasp fires, decelerates into the target, then
        # accelerates away - so the first time the speed stops falling is the
        # moment it arrived. This mirrors the Quest side, which takes closest
        # approach within --window seconds of the grab; closest approach cannot be
        # computed here, because the target is known in Unity coordinates and this
        # is the Vicon frame with no co-registration, but the first local minimum
        # finds the same instant without needing one.
        #
        # It must be the FIRST local minimum, not the slowest sample in the
        # interval: written the slow-sample way, it finds wherever the hand paused
        # longest, which is the NEXT target whenever the hand dwells there before
        # that grasp registers. That put three of Transfer-5's nine position-1
        # endpoints onto position 3, half a metre out.
        nxt = marks[key].get(str(int(gn) + 1))
        i_next = at(nxt) if nxt is not None else None
        limit = min(len(samples) - 1, i1 + int(endpoint_window * rate))
        if i_next is not None:
            limit = min(limit, i_next - 1)
        e_i = i1
        while e_i < limit and sp[e_i + 1] <= sp[e_i]:
            e_i += 1
        row["vicon_endpoint_lead_s"] = f"{(i1 - b) / rate:.3f}"
        ex, ey, ez = samples[e_i][1:4]
        row["vicon_endpoint_x_m"] = f"{ex:.5f}"
        row["vicon_endpoint_y_m"] = f"{ey:.5f}"
        row["vicon_endpoint_z_m"] = f"{ez:.5f}"
        done += 1

    pct = 100.0 * done / max(1, sum(1 for r in rows if (r.get("grasp_outcome") or "").strip() == "recorded"))
    return (rows, lag, z,
            f"{done} grasps labelled ({pct:.0f}% of the session covered by this export)",
            dict(traj))


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
    ap.add_argument("--max-endpoint-error", type=float, default=0.10,
                    help="metres; a grasp whose closest approach is further than "
                         "this from the target is marked out_of_range and dropped "
                         "from the endpoint measures, keeping its timing (default "
                         "0.10 = half the minimum inter-target spacing). 0 disables.")
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
                            args.cutoff_hz, args.transport_frac,
                            args.max_endpoint_error)
    trials = per_trial(rows)
    acc = accuracy_table(rows)
    mcheck, mproblems = manipulation_check(rows)

    vicon_lag = vicon_z = None
    vicon_note = ""
    vicon_traj = {}
    if args.vicon:
        rows, vicon_lag, vicon_z, vicon_note, vicon_traj = add_vicon(
            rows, frames, args.vicon, args.cutoff_hz, args.transport_frac, args.window)

    stem = args.grasp_csv.rsplit(".", 1)[0]

    # The app's own column measures cue onset to the hand LEAVING THE HOME REGION.
    # That is not movement onset: in pilot data it ran a median 188 ms later than
    # the speed criterion, because the hand can accelerate while still inside the
    # home volume. Emit it under a name that says so, and accept either spelling
    # from the source file so sessions recorded before the rename still process.
    for _r in rows:
        if "reaction_time_s" in _r:
            _r["cue_to_home_exit_s"] = _r.pop("reaction_time_s")
    cols = [("cue_to_home_exit_s" if c == "reaction_time_s" else c)
            for c in grasps[0].keys()] + NEW_COLS + \
           ((VICON_COLS + JOINT_COLS) if args.vicon else [])
    write(stem + "_grasps.csv", rows, cols)
    write(stem + "_trials.csv", trials, list(trials[0].keys()) if trials else [])
    write(stem + "_accuracy.csv", acc, list(acc[0].keys()) if acc else [])
    if mcheck:
        write(stem + "_manipulation_check.csv", mcheck, list(mcheck[0].keys()))

    vprec = vicon_precision_table(rows) if args.vicon else []
    if vprec:
        write(stem + "_vicon_precision.csv", vprec, list(vprec[0].keys()))

    vtraj = trajectory_variability_table(vicon_traj) if vicon_traj else []
    if vtraj:
        write(stem + "_vicon_trajectory.csv", vtraj, list(vtraj[0].keys()))

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

    # Printed before anything else substantive: if the manipulation did not hold,
    # every number below it is describing something other than the intended design.
    if any(r["expected"] != "(no expectation)" for r in mcheck):
        print(f"\n  MANIPULATION CHECK  (hands_visible vs block)")
        for r in mcheck:
            if r["expected"] == "(no expectation)":
                continue
            print(f"    {r['block']:18s} n={r['n']:>3}   "
                  f"visible {r['hands_visible_TRUE']:>3} / hidden {r['hands_visible_FALSE']:>3}"
                  f"   expected {r['expected']:<5}  {r['verdict']}")
        if mproblems:
            print()
            for pr in mproblems:
                print(f"    *** {pr}")
        else:
            print("    the hidden condition held on every trial.")

    print(f"\n  TIMING")
    print(f"    total trial time     {med(col('total_trial_time_s', trials)):6.3f} s")
    print(f"    reaction time        {med(col('rt_speed_criterion_s', trials)) * 1000:6.0f} ms   (5% of peak speed)")
    print(f"    movement time        {med(col('movement_time_s', trials)):6.3f} s")
    print(f"    reach time per grasp {med(col('reach_time_corrected_s')):6.3f} s")
    print(f"    transport per reach  {med(col('transport_time_s')):6.3f} s   (the movement itself)")

    no_rt = [r for r in trials if not (r.get("rt_speed_criterion_s") or "").strip()]
    early = [r for r in rec if (r.get("grasp_in_trial") or "").strip() == "1"
             and (num(r, "speed_at_cue_mps") or 0.0) > 0.10]
    if no_rt or early:
        print(f"    --")
        print(f"    no RT                {len(no_rt):6d}      trials: hand already moving when logging began")
        print(f"    moving at the cue    {len(early):6d}      trials >100 mm/s at the first sample - "
              f"anticipation. RT is suspect on these even where a number was produced.")

    print(f"\n  ENDPOINT   <- analyse lateral, not the others")
    oor = [r for r in rec if (r.get("endpoint_outcome") or "").strip() == "out_of_range"]
    if oor:
        print(f"    OUT OF RANGE         {len(oor):6d}      grasps further than "
              f"{args.max_endpoint_error * 100:.0f} cm from the target - dropped from")
        print(f"                                     the endpoint measures, kept for timing:")
        for r in sorted(oor, key=lambda r: -(num(r, 'endpoint_error_corrected_m') or 0))[:6]:
            print(f"                                     trial {r.get('trial_number','?'):>3}"
                  f" grasp {r.get('grasp_in_trial','?')}"
                  f" position {r.get('target_position_number','?')}"
                  f"  {(num(r, 'endpoint_error_corrected_m') or 0) * 100:.1f} cm")
        print(f"    --")
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

            # JOINT ANGLE COVERAGE. Reported every session, because a partial
            # Model Outputs section is invisible otherwise: the angles that are
            # present look entirely normal, and the grasps outside the covered
            # range simply come back blank. On 15 Oct an export carried angles
            # for 4.8% of the session - 23 seconds of eight minutes - and that
            # went unnoticed for five days.
            #
            # Both of Nexus's First/Last Frame settings must be plain
            # First Frame / Last Frame, never Full Frame:
            #   Pipeline > Run Dynamic BodyLanguage Model   (what is COMPUTED)
            #   Pipeline > Export ASCII CSV                 (what is WRITTEN)
            # Fixing only the second gives complete markers and empty angles.
            _cov = getattr(add_vicon, "last_angle_coverage", None)
            if _cov is None:
                print("    joint angles    none in this export (no Model Outputs section)")
            else:
                _n = len(col("vicon_elbow_flex_grasp_deg"))
                _tot = len(rec)
                _flag = "" if _cov >= 95 else "   <-- LOW, re-export from Nexus"
                print(f"    joint angles    {_cov:5.1f}% of frames,"
                      f" {_n} of {_tot} grasps{_flag}")
                if _cov < 95:
                    print("                    Set BOTH Nexus First/Last Frame settings:")
                    print("                      Run Dynamic BodyLanguage Model, and Export ASCII CSV.")

    print(f"\n  WROTE")
    _written = ["_grasps.csv", "_trials.csv", "_accuracy.csv"]
    if vprec:
        _written.append("_vicon_precision.csv")
    if vtraj:
        _written.append("_vicon_trajectory.csv")
    for suffix in _written:
        print(f"    {os.path.basename(stem + suffix)}")
    print()


if __name__ == "__main__":
    main()
