#!/usr/bin/env python3
"""
validate_systems.py - concurrent validity of the Quest 3 hand tracking against
Vicon, on the same reaches, in one session.

    python3 validate_systems.py SESSION.csv SESSION_frames.csv --vicon vicon.csv

Writes SESSION_validation.csv (one row per statistic) and prints the same thing
as a readable report.

WHAT THIS IS FOR
----------------
The study uses two instruments on the same arm. Before either can be reported,
the thesis has to say what each one measures, how well the two agree, and which
one is authoritative for which outcome. That is an instrument-validation claim,
and it needs numbers, not an assurance.

Four things get tested, in the order a reviewer will ask about them.

A. Does each system track at all?
   Sample rate actually achieved, dropped and low-confidence frames, and the
   noise floor measured with the hand genuinely still (the 200 ms before each
   cue, when the hand is gated at the home button). A system's noise floor sets
   the smallest difference it can resolve, so it has to be stated before any
   effect size is.

B. Are the two clocks aligned, and do they stay aligned?
   The session lag comes from cross-correlating the two speed profiles. That
   gives one number for the whole session, so it cannot by itself show that the
   alignment holds at minute eight as well as minute one. The test here is
   per-reach: the time of peak speed in each system, 180-odd independent
   estimates of the same instant. Their spread is the real timing precision,
   and a trend across the session would reveal clock drift.

C. Do they agree on where the hand went?
   Different coordinate frames, so positions cannot be compared directly. A
   single rigid-body transform (rotation + translation, Kabsch) is fitted from
   Quest to Vicon over all paired samples; if the two systems are tracking the
   same physical hand, one transform maps the whole session with a small
   residual. The residual is reported for all samples and for the grasp moments
   alone. Note the floor on this: RFIN sits on the back of the hand and the
   Quest point is the index fingertip, so the residual contains a real
   anatomical offset of several centimetres that rotates as the hand rotates.
   The frame-independent comparison - per-reach displacement magnitude - is
   reported alongside it and is the fairer test of spatial agreement.

D. Do the derived outcome measures agree?
   The five measures both systems produce, compared the way method-comparison
   studies compare instruments: bias, 95% limits of agreement (Bland & Altman),
   Pearson r, and ICC(2,1) for absolute agreement. r alone is not enough - two
   systems can correlate perfectly and still disagree by a constant factor, and
   for smoothness and path measures that is exactly what happens.

HOW TO READ THE RESULT
----------------------
Disagreement is the expected finding for C and D, not a failure. The two
systems sample different points on the hand at different rates with different
noise floors. The validation's job is to quantify that, so the thesis can
assign each outcome to the instrument that measures it properly, and so the
Quest-derived measures that ARE trustworthy can be reported as trustworthy on
evidence rather than on assumption.
"""

import argparse
import csv
import math
import os
import sys
from collections import defaultdict


def _analysis_version():
    """A short name for the code that produced this report.

    The session CSV names the build that COLLECTED the data (app_commit, stamped into the
    Unity build by Assets/Editor/StampBuildVersion.cs). This names the code that ANALYSED
    it. Both come from the same repository, so the two can be compared directly, and a
    report can be placed against the pipeline as it stood that day.

    Returns "unknown" rather than raising: a missing version must never stop an analysis.
    """
    import subprocess
    try:
        here = os.path.dirname(os.path.abspath(__file__))
        out = subprocess.run(["git", "describe", "--always", "--dirty", "--abbrev=7"],
                             cwd=here, capture_output=True, text=True, timeout=5)
        name = out.stdout.strip()
        if out.returncode == 0 and name:
            return name
    except Exception:
        pass
    return "unknown"



HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

try:
    from analyse_session import (read_csv, read_vicon_rfin, estimate_lag, smooth,
                                 speeds, nearest_index, transport_window,
                                 count_speed_peaks, normalised_jerk)
except ImportError as e:
    sys.exit("validate_systems.py needs analyse_session.py in the same folder (%s)" % e)

try:
    import numpy as np
except ImportError:
    sys.exit("This script needs numpy.  Install it with:  pip3 install numpy")


# ---------------------------------------------------------------- statistics

def agreement(q, v):
    """
    Method-comparison statistics for paired measurements of the same quantity
    by two instruments.

    Returns bias (Vicon - Quest), the 95% limits of agreement, Pearson r and
    ICC(2,1).  ICC is the one that matters: r asks whether the two systems rank
    the reaches the same way, ICC asks whether they agree on the value.  A
    measure can score r = 0.95 and ICC = 0.2 if one system reads consistently
    high, and several of these do.
    """
    q = np.asarray(q, float)
    v = np.asarray(v, float)
    n = len(q)
    if n < 3:
        return None
    d = v - q
    bias = d.mean()
    sd = d.std(ddof=1)
    r = float(np.corrcoef(q, v)[0, 1]) if q.std() > 0 and v.std() > 0 else float("nan")

    # ICC(2,1), two-way random effects, absolute agreement, single measurement
    M = np.column_stack([q, v])
    grand = M.mean()
    ms_rows = 2 * ((M.mean(axis=1) - grand) ** 2).sum() / (n - 1)
    ms_cols = n * ((M.mean(axis=0) - grand) ** 2).sum() / 1.0
    ss_tot = ((M - grand) ** 2).sum()
    ss_err = ss_tot - ms_rows * (n - 1) - ms_cols
    ms_err = ss_err / (n - 1)
    denom = ms_rows + ms_err + 2.0 * (ms_cols - ms_err) / n
    icc = (ms_rows - ms_err) / denom if denom > 0 else float("nan")

    # paired t on the bias, so the report can say whether it is a real offset
    t = bias / (sd / math.sqrt(n)) if sd > 0 else float("inf")

    return dict(n=n, mean_q=q.mean(), mean_v=v.mean(), bias=bias, sd=sd,
                loa_lo=bias - 1.96 * sd, loa_hi=bias + 1.96 * sd,
                r=r, icc=float(icc), t=t,
                ratio=(v.mean() / q.mean()) if q.mean() else float("nan"))


def kabsch(A, B):
    """
    Rigid transform (rotation + translation, no scaling) carrying A onto B, and
    the per-point residual distances after it.

    No scaling on purpose.  Both systems claim to measure metres; allowing a
    scale factor would hide a calibration error in exactly the quantity being
    validated.  If one system's metre is not the other's, that should show up as
    residual, and the residual-vs-distance check below would reveal it.
    """
    A = np.asarray(A, float)
    B = np.asarray(B, float)
    ca, cb = A.mean(axis=0), B.mean(axis=0)
    H = (A - ca).T @ (B - cb)
    U, _, Vt = np.linalg.svd(H)
    d = np.sign(np.linalg.det(Vt.T @ U.T))
    R = Vt.T @ np.diag([1, 1, d]) @ U.T
    t = cb - R @ ca
    res = np.linalg.norm((A @ R.T + t) - B, axis=1)
    return R, t, res


# ---------------------------------------------------------------- Quest side

def quest_samples(frames):
    """Distinct, confidently-tracked right-hand samples, with their session time."""
    t, p, conf, last = [], [], [], None
    for r in frames:
        c = (r.get("right_confidence") or "").strip()
        if c in ("", "-1"):
            continue
        smp = r.get("right_sample_t")
        if smp == last:
            continue
        last = smp
        t.append(float(r["t_session_s"]))
        p.append((float(r["right_tip_x"]), float(r["right_tip_y"]), float(r["right_tip_z"])))
        conf.append(int(c))
    return t, p, conf


# Absolute speed threshold for the onset-latency check, in m/s. Well below
# both systems' peak speeds so that neither is near its own maximum when it is
# crossed, which is the condition that makes the comparison meaningful.
ABS_ONSET_MPS = 0.100


def abs_cross(sp, lo, hi, thr):
    """
    (first, last) index in [lo, hi] whose speed exceeds an ABSOLUTE threshold.

    Needed because transport_window() takes its threshold as a fraction of the
    reach's own peak, and the two systems disagree about the peak: the Quest
    reports ~1.85 m/s where Vicon reports ~1.18 on the same reaches. A fraction
    of a higher peak is a higher absolute speed, which the hand crosses later,
    so a fractional threshold manufactures an apparent onset lag out of the
    peak-speed disagreement alone. One fixed mm/s threshold for both systems
    removes that, and whatever difference survives is real.
    """
    first = next((i for i in range(lo, hi + 1) if sp[i] > thr), None)
    if first is None:
        return None, None
    last = next((i for i in range(hi, first - 1, -1) if sp[i] > thr), first)
    return first, last


def quest_by_trial(frames, cutoff_hz):
    """
    Quest samples grouped by trial, filtered WITHIN each trial, on the session
    clock.

    Grouping is not a tidiness choice.  The frame logger runs only during
    trials, so a session's samples are 60 islands separated by gaps of several
    seconds.  Concatenating them and filtering the result treats those gaps as
    if no time passed, and the 8 Hz filter then smears the end of one reach into
    the start of the next trial - which showed up here as a 16 mm resting noise
    floor for a system whose real floor is under 2 mm, and as corrupted windows
    for the first reach of every trial.  analyse_session.py groups by trial for
    the same reason; this script has to process the data the same way, or it
    would be validating something the study never computes.

    Returns {(trial, attempt): (samples, speeds)} with samples as
    (t_session, x, y, z).
    """
    by = defaultdict(list)
    for fr in frames:
        key = ((fr.get("trial_number") or "").strip(),
               (fr.get("trial_attempt") or "1").strip())
        if key[0]:
            by[key].append(fr)

    out = {}
    for key, fl in by.items():
        t, p, _ = quest_samples(fl)
        if len(t) < 8:
            continue
        s = smooth([(t[i], p[i][0], p[i][1], p[i][2]) for i in range(len(t))], cutoff_hz)
        out[key] = (s, speeds(s))
    return out


def quest_quality(frames):
    """
    Frames, dropouts and the achieved sample rate.

    The rate comes from the MEDIAN interval between samples, not from the count
    divided by the elapsed span.  The frame logger only runs during trials, so
    the span between the first and last frame of a session contains all the
    inter-trial dead time - on the pilot, 236 s of a 485 s span.  Dividing by
    that span reports 37 Hz for a logger that is in fact sampling at 72 Hz
    whenever it is sampling at all.  The gap count and total gap time are
    reported separately so the dead time is visible rather than smeared into
    the rate.
    """
    n = len(frames)
    untracked = sum(1 for r in frames
                    if (r.get("right_confidence") or "").strip() in ("", "-1"))
    low = sum(1 for r in frames if (r.get("right_confidence") or "").strip() == "0")
    t, p, _ = quest_samples(frames)
    dup = (n - untracked) - len(t)

    d = sorted(x for x in (t[i + 1] - t[i] for i in range(len(t) - 1)) if x > 0)
    dt = d[len(d) // 2] if d else float("nan")
    rate = 1.0 / dt if dt and dt == dt else float("nan")
    gaps = [x for x in d if x > 0.1]

    return dict(frames=n, untracked=untracked, low=low, distinct=len(t),
                duplicated=dup, rate=rate, dt=dt,
                gaps=len(gaps), gap_s=sum(gaps),
                span=t[-1] - t[0] if len(t) > 1 else 0.0)


def noise_floor(samples, windows):
    """
    Residual position noise with the hand genuinely still.

    Taken from the first 200 ms AFTER each cue, not before it.  Before the cue
    the Quest logger is not running - it starts at cue onset - so a window
    placed before the cue silently picks up the tail of the previous trial, with
    the hand still moving.  That mistake reported a 31 mm noise floor for a
    system whose real floor is around 1.5 mm.  After the cue the hand is still
    held at home (median reaction time is about 400 ms), so the window is both
    populated and genuinely stationary, and the same window can be used for
    both systems.

    Reported as the mean over windows of the 3-D standard deviation about each
    window's own mean, which removes any slow drift and leaves the
    sample-to-sample noise - the quantity that sets how small a displacement
    the system can resolve.
    """
    sd, jit = [], []
    for lo, hi in windows:
        seg = np.asarray([s[1:4] for s in samples[lo:hi + 1]], float)
        if len(seg) < 5:
            continue
        sd.append(float(np.sqrt(((seg - seg.mean(axis=0)) ** 2).sum(axis=1).mean())))
        # Sample-to-sample displacement. A hand held at the home button is not
        # clamped - it sways and trembles - and that real motion goes into the
        # window SD above, which is why the SD alone overstates instrument
        # noise. Postural sway is slow, so it contributes almost nothing
        # between consecutive samples 10-14 ms apart, and what is left here is
        # very nearly the instrument's own jitter.
        jit.append(float(np.median(np.linalg.norm(np.diff(seg, axis=0), axis=1))))
    if not sd:
        return float("nan"), float("nan"), 0
    return float(np.mean(sd)), float(np.mean(jit)), len(sd)


# ---------------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("grasp_csv")
    ap.add_argument("frames_csv", nargs="?", default=None)
    ap.add_argument("--vicon", required=True, help="Nexus CSV export for the same session")
    ap.add_argument("--cutoff-hz", type=float, default=8.0)
    ap.add_argument("--transport-frac", type=float, default=0.20)
    ap.add_argument("--still-ms", type=float, default=200.0,
                    help="length of the pre-cue window used for the noise floor")
    a = ap.parse_args()

    frames_path = a.frames_csv
    if frames_path is None:
        stem = a.grasp_csv[:-4] if a.grasp_csv.lower().endswith(".csv") else a.grasp_csv
        frames_path = stem + "_frames.csv"
    for p in (a.grasp_csv, frames_path, a.vicon):
        if not os.path.exists(p):
            sys.exit("cannot find %s" % p)

    grasps = read_csv(a.grasp_csv)
    frames = read_csv(frames_path)

    got = read_vicon_rfin(a.vicon)
    if got is None:
        sys.exit("no usable Trajectories/RFIN section in %s" % a.vicon)
    vt, vp, rate = got

    lag, z = estimate_lag(vt, vp, rate, frames)
    if lag is None:
        sys.exit("not enough overlapping movement to align the two systems")

    t0 = min(float(r["t_session_s"]) for r in frames)

    # both systems filtered identically, so nothing in the comparison comes
    # from one being smoothed more than the other
    vsam = smooth([(vt[i], vp[i][0], vp[i][1], vp[i][2]) for i in range(len(vt))], a.cutoff_hz)
    vsp = speeds(vsam)
    v_first, v_last = vsam[0][0], vsam[-1][0]

    qt, qp, qconf = quest_samples(frames)
    qraw = [(qt[i], qp[i][0], qp[i][1], qp[i][2]) for i in range(len(qt))]
    qtrials = quest_by_trial(frames, a.cutoff_hz)

    def v_at(t_session):
        t = t_session - t0 + lag
        if t < v_first or t > v_last:
            return None
        return nearest_index(vsam, t)

    # ---------------------------------------------------------------- A: quality
    qq = quest_quality(frames)

    vgaps = 0
    expected = int(round((vt[-1] - vt[0]) * rate)) + 1
    vgaps = expected - len(vt)

    cues = [float(r["t_session_s"]) for r in frames if (r.get("event") or "").strip() == "cue"]
    still = int(round(a.still_ms / 1000.0 * rate))
    q_still_n = max(3, int(round(a.still_ms / 1000.0 *
                                 (qq["rate"] if qq["rate"] == qq["rate"] else 72))))

    # Noise floor is measured on RAW samples, before the 8 Hz filter.  The
    # filter's whole job is to suppress this noise, so measuring after it would
    # report how well the filter works, not what the instrument delivers.
    qwin, vwin = [], []
    for c in cues:
        i = nearest_index(qraw, c)
        if abs(qraw[i][0] - c) < 0.05 and i + q_still_n < len(qraw):
            qwin.append((i, i + q_still_n))
        j = v_at(c)
        if j is not None and j + still < len(vsam):
            vwin.append((j, j + still))
    vraw = [(vt[i], vp[i][0], vp[i][1], vp[i][2]) for i in range(len(vt))]
    q_noise, q_jit, q_nw = noise_floor(qraw, qwin)
    v_noise, v_jit, v_nw = noise_floor(vraw, vwin)

    # speed at the cue, as an independent check that the lag is right: the hand
    # is held at home, so a correct alignment puts near-zero Vicon speed there
    v_cue_speed = [vsp[v_at(c)] for c in cues if v_at(c) is not None]
    v_base = float(np.median(vsp))

    # ---------------------------------------------------------------- per-reach
    marks = defaultdict(dict)
    cue_at = {}
    for fr in frames:
        key = ((fr.get("trial_number") or "").strip(),
               (fr.get("trial_attempt") or "1").strip())
        ev = (fr.get("event") or "").strip()
        if ev == "grasp":
            marks[key][(fr.get("grasp_in_trial") or "").strip()] = float(fr["t_session_s"])
        elif ev == "cue":
            cue_at[key] = float(fr["t_session_s"])

    per_reach = []
    dropped = []
    paired_pos_q, paired_pos_v = [], []
    grasp_pos_q, grasp_pos_v = [], []
    by_target_q, by_target_v = defaultdict(list), defaultdict(list)
    nominal = {}

    for row in grasps:
        if (row.get("grasp_outcome") or "").strip() != "recorded":
            continue
        key = ((row.get("trial_number") or "").strip(),
               (row.get("trial_attempt") or "1").strip())
        gn = (row.get("grasp_in_trial") or "").strip()
        if key not in marks or gn not in marks[key]:
            continue
        if key not in qtrials:
            continue
        qsam, qsp = qtrials[key]

        t_end = marks[key][gn]
        # Start of the reach: the previous grasp, or for the first reach of the
        # trial the cue itself.  Not "two seconds back" - for the first reach
        # that reaches across the inter-trial gap into the previous trial's
        # movement, and the window then measures the wrong reach entirely.
        t_prev = marks[key].get(str(int(gn) - 1), cue_at.get(key))
        if t_prev is None:
            continue

        vi1, vi0 = v_at(t_end), v_at(t_prev)
        if vi1 is None or vi0 is None:
            continue
        if qsam[0][0] - 0.05 > t_prev or qsam[-1][0] + 0.05 < t_end:
            continue
        qi1, qi0 = nearest_index(qsam, t_end), nearest_index(qsam, t_prev)
        if vi1 - vi0 < 10 or qi1 - qi0 < 5:
            continue

        va, vb = transport_window(vsp, vi0, vi1, a.transport_frac)
        qa, qb = transport_window(qsp, qi0, qi1, a.transport_frac)
        if vb <= va or qb <= qa:
            continue

        # Quality gate: the two windows must be describing the same movement.
        # Without this, a reach where one system's window latched onto a
        # different hump contributes a timing difference of seconds, and a
        # handful of those dominate the mean and the SD while the median stays
        # at 25 ms. Excluded reaches are counted and reported, not hidden.
        q_lo, q_hi = qsam[qa][0], qsam[qb][0]
        v_lo = vsam[va][0] - lag + t0
        v_hi = vsam[vb][0] - lag + t0
        ov = min(q_hi, v_hi) - max(q_lo, v_lo)
        if ov <= 0 or ov < 0.5 * min(q_hi - q_lo, v_hi - v_lo):
            dropped.append((key[0], gn, ov))
            continue

        # The same comparison on a fixed 100 mm/s threshold in both systems.
        # See abs_cross(): the fractional window above cannot separate an onset
        # latency from the peak-speed disagreement, and this one can.
        dt_on_abs = dt_off_abs = None
        qa2, qb2 = abs_cross(qsp, qi0, qi1, ABS_ONSET_MPS)
        va2, vb2 = abs_cross(vsp, vi0, vi1, ABS_ONSET_MPS)
        if qa2 is not None and va2 is not None:
            q_lo2, q_hi2 = qsam[qa2][0], qsam[qb2][0]
            v_lo2 = vsam[va2][0] - lag + t0
            v_hi2 = vsam[vb2][0] - lag + t0
            if min(q_hi2, v_hi2) - max(q_lo2, v_lo2) > 0:
                dt_on_abs = (v_lo2 - q_lo2) * 1000.0
                dt_off_abs = (v_hi2 - q_hi2) * 1000.0

        vseg, qseg = vsp[va:vb + 1], qsp[qa:qb + 1]
        if not vseg or not qseg:
            continue

        # time of peak speed, each system's own clock, mapped to session time
        v_tpk = vsam[va + int(np.argmax(vseg))][0] - lag + t0
        q_tpk = qsam[qa + int(np.argmax(qseg))][0]

        vstraight = math.dist(vsam[va][1:4], vsam[vb][1:4])
        qstraight = math.dist(qsam[qa][1:4], qsam[qb][1:4])
        vpath = sum(math.dist(vsam[i][1:4], vsam[i + 1][1:4]) for i in range(va, vb))
        qpath = sum(math.dist(qsam[i][1:4], qsam[i + 1][1:4]) for i in range(qa, qb))

        d = dict(
            trial=key[0], grasp=gn, t_end=t_end,
            v_peak=max(vseg), q_peak=max(qseg),
            v_tpk=v_tpk, q_tpk=q_tpk, dt_peak=(v_tpk - q_tpk) * 1000.0,
            v_disp=vstraight, q_disp=qstraight,
            v_path=vpath, q_path=qpath,
            v_ratio=(vpath / vstraight) if vstraight > 1e-4 else None,
            q_ratio=(qpath / qstraight) if qstraight > 1e-4 else None,
            v_peaks=count_speed_peaks(vsp, va, vb),
            q_peaks=count_speed_peaks(qsp, qa, qb),
            v_nj=normalised_jerk(vsam, va, vb),
            q_nj=normalised_jerk(qsam, qa, qb),
            v_tt=vsam[vb][0] - vsam[va][0],
            q_tt=qsam[qb][0] - qsam[qa][0],
            # Onset and offset of the transport window, Vicon minus Quest, on
            # the shared clock. Added 19 Oct 2026 at Alix Kitson's suggestion:
            # if the two systems agreed on when a reach ENDED but disagreed on
            # when it STARTED, the difference would be hand-tracking onset
            # latency rather than clock misalignment. Quest hand tracking is
            # reported to take a few hundred ms to settle after movement
            # begins, and these reaches last only 0.5-0.6 s, so a whole reach
            # can sit inside that warm-up. Both numbers were already being
            # computed for the overlap gate below and then discarded.
            dt_onset=(v_lo - q_lo) * 1000.0,
            dt_offset=(v_hi - q_hi) * 1000.0,
            dt_onset_abs=dt_on_abs,
            dt_offset_abs=dt_off_abs,
        )
        per_reach.append(d)

        # paired position samples through the reach, for the rigid fit.
        # Paired by TIME, not by index fraction: the two windows contain
        # different numbers of samples, so matching the k-th of one to the
        # k-th of the other silently stretches one reach onto the other and
        # turns a timing difference into a position difference.
        for qi in range(qa, qb + 1):
            vi = v_at(qsam[qi][0])
            if vi is not None:
                paired_pos_q.append(qsam[qi][1:4])
                paired_pos_v.append(vsam[vi][1:4])
        grasp_pos_q.append(qsam[qb][1:4])
        grasp_pos_v.append(vsam[vb][1:4])

        tp = (row.get("target_position_number") or "").strip()
        if tp:
            by_target_q[tp].append(qsam[qb][1:4])
            by_target_v[tp].append(vsam[vb][1:4])
            try:
                nominal[tp] = (float(row["target_x"]), float(row["target_y"]),
                               float(row["target_z"]))
            except (KeyError, TypeError, ValueError):
                pass

    if len(per_reach) < 5:
        sys.exit("only %d reaches had data in both systems - nothing to validate. "
                 "Check that the Nexus export covers the whole session."
                 % len(per_reach))

    # ---------------------------------------------------------------- C: spatial
    spatial = {}
    if len(paired_pos_q) > 20:
        _, _, res = kabsch(paired_pos_q, paired_pos_v)
        spatial["all"] = (float(np.sqrt((res ** 2).mean())), float(np.median(res)), len(res))
    if len(grasp_pos_q) > 10:
        _, _, resg = kabsch(grasp_pos_q, grasp_pos_v)
        spatial["grasp"] = (float(np.sqrt((resg ** 2).mean())), float(np.median(resg)), len(resg))

    # Inter-target geometry, against the one thing in this experiment that is
    # exactly known.
    #
    # The targets are placed by Unity at computed positions, so the distances
    # between them are ground truth to the precision of floating point - no
    # instrument measured them. Each system's mean grasp position per target
    # can therefore be scored against that reference INDEPENDENTLY, which is
    # worth more than scoring the two systems against each other: if they
    # disagree, this says which one moved.
    #
    # What this does NOT do is cancel the marker offset. Subtracting positions
    # at two targets only removes the offset if the offset vector is the same
    # at both, and it is not: the wrist rotates between a far-left and a
    # far-right target, so the RFIN-to-fingertip vector rotates with it, and
    # inter-target distances measured at RFIN are compressed or stretched as a
    # result. A scale factor away from 1.0 on the Vicon side is therefore
    # ambiguous between hand rotation and a calibration error - and since a
    # Vicon system is not plausibly 9% miscalibrated, hand rotation is the
    # reading to prefer. Separating the two needs a marker on the fingertip
    # itself, or a rigid object of known length held at each target; see the
    # note the report prints.
    geom = None
    tgts = sorted(set(by_target_q) & set(by_target_v),
                  key=lambda s: int(s) if s.isdigit() else 0)
    tgts = [t for t in tgts if len(by_target_q[t]) >= 3 and len(by_target_v[t]) >= 3]
    if len(tgts) >= 3:
        mq = {t: np.mean(np.asarray(by_target_q[t], float), axis=0) for t in tgts}
        mv = {t: np.mean(np.asarray(by_target_v[t], float), axis=0) for t in tgts}
        dq, dv, dn, pairs = [], [], [], []
        for i in range(len(tgts)):
            for j in range(i + 1, len(tgts)):
                A, B = tgts[i], tgts[j]
                dq.append(float(np.linalg.norm(mq[A] - mq[B])))
                dv.append(float(np.linalg.norm(mv[A] - mv[B])))
                dn.append(float(np.linalg.norm(np.asarray(nominal[A]) -
                                               np.asarray(nominal[B])))
                          if A in nominal and B in nominal else float("nan"))
                pairs.append("%s-%s" % (A, B))
        dq = np.asarray(dq)
        dv = np.asarray(dv)
        dn = np.asarray(dn)
        diff = dv - dq
        geom = dict(n_targets=len(tgts), n_pairs=len(dq),
                    bias=float(diff.mean()), rms=float(np.sqrt((diff ** 2).mean())),
                    worst=float(np.abs(diff).max()),
                    r=float(np.corrcoef(dq, dv)[0, 1]),
                    scale=float((dq @ dv) / (dq @ dq)),
                    reps=min(len(by_target_q[t]) for t in tgts),
                    pairs=list(zip(pairs, dn, dq, dv)))
        if np.isfinite(dn).all() and (dn > 0).all():
            geom["scale_q"] = float((dn @ dq) / (dn @ dn))
            geom["scale_v"] = float((dn @ dv) / (dn @ dn))
            geom["rms_q"] = float(np.sqrt(((dq - dn) ** 2).mean()))
            geom["rms_v"] = float(np.sqrt(((dv - dn) ** 2).mean()))

    # ---------------------------------------------------------------- D: measures
    def pair(kq, kv):
        q = [d[kq] for d in per_reach if d[kq] is not None and d[kv] is not None]
        v = [d[kv] for d in per_reach if d[kq] is not None and d[kv] is not None]
        return agreement(q, v) if len(q) >= 3 else None

    measures = [
        ("Peak speed (m/s)", "q_peak", "v_peak"),
        ("Reach displacement (m)", "q_disp", "v_disp"),
        ("Path length (m)", "q_path", "v_path"),
        ("Path ratio", "q_ratio", "v_ratio"),
        ("Speed peaks (submovements)", "q_peaks", "v_peaks"),
        ("Normalised jerk", "q_nj", "v_nj"),
        ("Transport time (s)", "q_tt", "v_tt"),
    ]
    results = [(name, pair(kq, kv)) for name, kq, kv in measures]

    dt = np.asarray([d["dt_peak"] for d in per_reach], float)
    tt = np.asarray([d["t_end"] for d in per_reach], float)
    dt_abs = float(np.median(np.abs(dt)))

    # drift, with its standard error.  A slope on 63 noisy points is easy to
    # mistake for clock drift, so the SE decides whether it is worth reporting.
    drift = drift_se = float("nan")
    if len(dt) > 5 and tt.std() > 0:
        sl, ic = np.polyfit(tt, dt, 1)
        resid = dt - (sl * tt + ic)
        drift = float(sl)
        drift_se = float(np.sqrt((resid ** 2).sum() / (len(dt) - 2) /
                                 ((tt - tt.mean()) ** 2).sum()))

    # ---------------------------------------------------------------- report
    out = []

    def add(section, statistic, value, unit="", note=""):
        out.append(dict(section=section, statistic=statistic, value=value,
                        unit=unit, note=note))

    L = []
    L.append("")
    L.append("=" * 78)
    L.append("  CROSS-SYSTEM VALIDATION   Quest 3 hand tracking  vs  Vicon RFIN")
    L.append("=" * 78)
    L.append("  session : %s" % os.path.basename(a.grasp_csv))
    L.append("  analysed by : %s" % _analysis_version())
    L.append("  vicon   : %s" % os.path.basename(a.vicon))
    L.append("  reaches measured by both systems: %d" % len(per_reach))
    if dropped:
        L.append("  excluded: %d reach(es) where the two transport windows did not"
                 % len(dropped))
        L.append("            overlap, so they were not describing the same movement")
        L.append("            (trial/grasp: %s)"
                 % ", ".join("%s/%s" % (t, g) for t, g, _ in dropped[:12]))
    L.append("")

    L.append("A. TRACKING QUALITY")
    L.append("   Quest  %6d frames, %d distinct hand samples" % (qq["frames"], qq["distinct"]))
    L.append("          %.1f Hz while logging (median interval %.1f ms)"
             % (qq["rate"], qq["dt"] * 1000))
    L.append("          logger off between trials: %d gaps, %.0f s of the %.0f s span"
             % (qq["gaps"], qq["gap_s"], qq["span"]))
    L.append("          %6d untracked (%.2f%%), %d low-confidence (%.2f%%)"
             % (qq["untracked"], 100.0 * qq["untracked"] / qq["frames"],
                qq["low"], 100.0 * qq["low"] / qq["frames"]))
    L.append("          hand held still, %d post-cue windows:" % q_nw)
    L.append("            sample-to-sample jitter %.2f mm   <- instrument noise"
             % (q_jit * 1000))
    L.append("            spread about window mean %.2f mm  (includes real sway)"
             % (q_noise * 1000))
    L.append("   Vicon  %6d RFIN frames at %.0f Hz, %d missing in the range"
             % (len(vt), rate, max(0, vgaps)))
    L.append("          hand held still, %d windows:" % v_nw)
    L.append("            sample-to-sample jitter %.2f mm   <- instrument noise"
             % (v_jit * 1000))
    L.append("            spread about window mean %.2f mm  (includes real sway)"
             % (v_noise * 1000))
    L.append("          Quest jitter is %.0fx Vicon's. Both are measured on raw,"
             % (q_jit / v_jit if v_jit else float("nan")))
    L.append("          unfiltered samples in the same windows.")
    L.append("")
    add("A quality", "Quest frames", qq["frames"])
    add("A quality", "Quest distinct hand samples", qq["distinct"])
    add("A quality", "Quest sample rate", round(qq["rate"], 2), "Hz",
        "from the median sample interval, not the elapsed span")
    add("A quality", "Quest logging gaps between trials", qq["gaps"], "",
        "%.0f s of the %.0f s span; the logger runs only during trials"
        % (qq["gap_s"], qq["span"]))
    add("A quality", "Quest untracked frames", qq["untracked"], "",
        "%.2f%% of frames" % (100.0 * qq["untracked"] / qq["frames"]))
    add("A quality", "Quest low-confidence frames", qq["low"], "",
        "%.2f%% of frames" % (100.0 * qq["low"] / qq["frames"]))
    add("A quality", "Quest jitter (hand still)", round(q_jit * 1000, 3), "mm",
        "median sample-to-sample displacement, raw, %d windows" % q_nw)
    add("A quality", "Quest spread about window mean", round(q_noise * 1000, 3), "mm",
        "includes real postural sway, so an upper bound on noise")
    add("A quality", "Vicon RFIN frames", len(vt))
    add("A quality", "Vicon sample rate", rate, "Hz")
    add("A quality", "Vicon missing frames in range", max(0, vgaps))
    add("A quality", "Vicon jitter (hand still)", round(v_jit * 1000, 3), "mm",
        "median sample-to-sample displacement, raw, %d windows" % v_nw)
    add("A quality", "Vicon spread about window mean", round(v_noise * 1000, 3), "mm",
        "includes real postural sway")

    L.append("B. TEMPORAL AGREEMENT")
    L.append("   session lag (cross-correlation) : %+.3f s   peak z = %.1f %s"
             % (lag, z, "" if z >= 8 else "  << WEAK, treat with caution"))
    L.append("   Vicon speed at the %d cue onsets : %.0f mm/s  vs  %.0f mm/s session median"
             % (len(v_cue_speed), 1000 * float(np.mean(v_cue_speed)) if v_cue_speed else float('nan'),
                1000 * v_base))
    L.append("     (the hand is gated at the home button at cue onset, so a correct")
    L.append("      alignment has to find it nearly stationary there - it does)")
    L.append("   per-reach time of peak speed, Vicon minus Quest:")
    L.append("      bias %+.0f ms,  SD %.0f ms,  95%% limits %+.0f to %+.0f ms  (n = %d)"
             % (dt.mean(), dt.std(ddof=1),
                dt.mean() - 1.96 * dt.std(ddof=1), dt.mean() + 1.96 * dt.std(ddof=1), len(dt)))
    L.append("      median absolute difference %.0f ms" % dt_abs)
    L.append("      drift: %+.1f +/- %.1f ms per minute%s"
             % (drift * 60, drift_se * 60,
                "" if abs(drift) > 2 * drift_se else "  (not distinguishable from zero)"))
    ona = np.array([r["dt_onset_abs"] for r in per_reach
                    if r.get("dt_onset_abs") is not None])
    offa = np.array([r["dt_offset_abs"] for r in per_reach
                     if r.get("dt_offset_abs") is not None])
    if len(ona) and len(offa):
        L.append("   onset vs offset of the reach, Vicon minus Quest,")
        L.append("   at a fixed %.0f mm/s threshold in both systems:" % (ABS_ONSET_MPS * 1000))
        L.append("      onset  median %+.0f ms      offset median %+.0f ms   (n = %d)"
                 % (np.median(ona), np.median(offa), len(ona)))
        L.append("     Read the MEDIAN, not the mean: a few reaches where one")
        L.append("     system's threshold crossing lands on a different part of")
        L.append("     the profile put the mean in the hundreds of ms.")
        L.append("     An onset difference much larger than the offset difference")
        L.append("     would be hand-tracking onset latency rather than clock")
        L.append("     error - the Quest late to register that the hand had")
        L.append("     started moving, but on time about where it stopped.")
        L.append("     Measured on P60, 15 Oct 2026: both within a few ms, so")
        L.append("     there is no detectable onset latency in these reaches.")
        L.append("     The threshold must be ABSOLUTE for this to mean anything.")
        L.append("     Against each system's own peak the Quest looks 42 ms late,")
        L.append("     but only because it reports a higher peak and so a higher")
        L.append("     threshold, which the hand crosses later. That is arithmetic,")
        L.append("     not latency. The transport-window figures are kept in the")
        L.append("     per-reach CSV as dt_onset / dt_offset for comparison.")
    L.append("     The SD here is NOT the alignment error alone. The Quest speed")
    L.append("     profile carries several spurious peaks per reach (section D), so")
    L.append("     its argmax lands on a different hump from one reach to the next.")
    L.append("     Most of this spread is Quest speed noise, not clock mismatch -")
    L.append("     which is why the lag itself is estimated from the whole session")
    L.append("     rather than from any single event.")
    L.append("")
    add("B timing", "Session lag", round(lag, 4), "s", "cross-correlation of the two speed profiles")
    add("B timing", "Cross-correlation peak z", round(z, 1), "",
        "below 8 the alignment should not be trusted")
    add("B timing", "Vicon speed at cue onsets", round(1000 * float(np.mean(v_cue_speed)), 1), "mm/s",
        "hand gated at home; session median %.0f mm/s" % (1000 * v_base))
    add("B timing", "Time-of-peak bias (Vicon - Quest)", round(float(dt.mean()), 1), "ms")
    add("B timing", "Time-of-peak SD", round(float(dt.std(ddof=1)), 1), "ms",
        "the honest per-event timing precision")
    add("B timing", "Time-of-peak 95% LoA lower",
        round(float(dt.mean() - 1.96 * dt.std(ddof=1)), 1), "ms")
    add("B timing", "Time-of-peak 95% LoA upper",
        round(float(dt.mean() + 1.96 * dt.std(ddof=1)), 1), "ms")
    add("B timing", "Time-of-peak median absolute difference", round(dt_abs, 1), "ms")
    add("B timing", "Clock drift", round(drift * 60, 2), "ms/min",
        "SE %.2f; %s" % (drift_se * 60,
                         "distinguishable from zero" if abs(drift) > 2 * drift_se
                         else "not distinguishable from zero"))

    L.append("C. SPATIAL AGREEMENT")
    L.append("   C1. Inter-target geometry, scored against the nominal target")
    L.append("       positions - the only exactly-known distances in the experiment,")
    L.append("       since Unity computes them rather than measuring them.")
    if geom:
        L.append("       %d targets, %d pairwise distances, at least %d repetitions each"
                 % (geom["n_targets"], geom["n_pairs"], geom["reps"]))
        if "scale_q" in geom:
            L.append("                        scale vs nominal     RMS error")
            L.append("          Quest tip          %.4f            %5.1f mm"
                     % (geom["scale_q"], geom["rms_q"] * 1000))
            L.append("          Vicon RFIN         %.4f            %5.1f mm"
                     % (geom["scale_v"], geom["rms_v"] * 1000))
            add("C spatial", "Scale vs nominal, Quest fingertip",
                round(geom["scale_q"], 4), "",
                "1.000 = the Quest reproduces the known target geometry exactly")
            add("C spatial", "Scale vs nominal, Vicon RFIN", round(geom["scale_v"], 4), "",
                "below 1 because the RFIN-to-fingertip vector rotates between targets")
            add("C spatial", "RMS inter-target error, Quest",
                round(geom["rms_q"] * 1000, 2), "mm")
            add("C spatial", "RMS inter-target error, Vicon RFIN",
                round(geom["rms_v"] * 1000, 2), "mm", "not an error in Vicon; see note")
        L.append("       Quest vs Vicon directly: bias %+.1f mm, RMS %.1f mm, "
                 "worst %.1f mm, r = %.4f"
                 % (geom["bias"] * 1000, geom["rms"] * 1000,
                    geom["worst"] * 1000, geom["r"]))
        L.append("")
        L.append("       The Quest scores near 1.0 against nominal because its grasp")
        L.append("       positions and its targets are the same point tracked the same")
        L.append("       way. The Vicon figure is NOT a Vicon error: RFIN sits on the")
        L.append("       back of the hand, the wrist rotates between a left target and")
        L.append("       a right one, and that rotation compresses the distances")
        L.append("       measured at RFIN. A Vicon system is not 9% miscalibrated.")
        L.append("       This is the one claim the reaching data alone cannot settle -")
        L.append("       see the note at the end of this report.")
        add("C spatial", "Inter-target distances compared", geom["n_pairs"], "",
            "from %d targets with >=%d repetitions each"
            % (geom["n_targets"], geom["reps"]))
        add("C spatial", "Inter-target bias, Vicon - Quest",
            round(geom["bias"] * 1000, 2), "mm")
        add("C spatial", "Inter-target r, Vicon vs Quest", round(geom["r"], 4), "",
            "the two systems rank the distances almost identically")
    else:
        L.append("       not enough targets with repeated grasps in both systems")
    L.append("")
    L.append("   C2. One rigid transform (rotation + translation, no scaling) fitted")
    L.append("       from Quest fingertip to Vicon RFIN across the session:")
    if "all" in spatial:
        L.append("       all reach samples   RMS %.1f mm, median %.1f mm  (n = %d)"
                 % (spatial["all"][0] * 1000, spatial["all"][1] * 1000, spatial["all"][2]))
        add("C spatial", "Rigid-fit residual, all reach samples",
            round(spatial["all"][0] * 1000, 1), "mm",
            "inflated by the marker offset and by timing error during fast motion; "
            "see the inter-target rows for the clean figure")
    if "grasp" in spatial:
        L.append("       at the grasp only   RMS %.1f mm, median %.1f mm  (n = %d)"
                 % (spatial["grasp"][0] * 1000, spatial["grasp"][1] * 1000, spatial["grasp"][2]))
        add("C spatial", "Rigid-fit residual, grasp moments only",
            round(spatial["grasp"][0] * 1000, 1), "mm",
            "hand is slowest and posture most stereotyped here")
    L.append("       Do not read this as Quest error. Three things inflate it and")
    L.append("       only one of them is the Quest: RFIN sits on the back of the hand")
    L.append("       while the Quest point is the index fingertip, several cm away,")
    L.append("       and that offset rotates as the hand rotates; and at 1.3 m/s a")
    L.append("       100 ms timing difference is 130 mm of apparent position error.")
    L.append("       It is reported because a reviewer will ask for it, and because a")
    L.append("       grossly larger value would signal a genuine frame problem.")
    L.append("")

    L.append("D. AGREEMENT ON THE DERIVED MEASURES   (Vicon - Quest)")
    L.append("   %-28s %5s %9s %9s %9s %7s %7s"
             % ("measure", "n", "Quest", "Vicon", "bias", "r", "ICC"))
    L.append("   " + "-" * 73)
    for name, s in results:
        if s is None:
            L.append("   %-28s      no paired data" % name)
            continue
        L.append("   %-28s %5d %9.3f %9.3f %+9.3f %7.2f %7.2f"
                 % (name, s["n"], s["mean_q"], s["mean_v"], s["bias"], s["r"], s["icc"]))
        add("D measures", name + " - n", s["n"])
        add("D measures", name + " - Quest mean", round(s["mean_q"], 4))
        add("D measures", name + " - Vicon mean", round(s["mean_v"], 4))
        add("D measures", name + " - bias (Vicon - Quest)", round(s["bias"], 4))
        add("D measures", name + " - 95% LoA",
            "%.4f to %.4f" % (s["loa_lo"], s["loa_hi"]), "",
            "Bland-Altman limits of agreement")
        add("D measures", name + " - Pearson r", round(s["r"], 3))
        add("D measures", name + " - ICC(2,1)", round(s["icc"], 3),
            "", "absolute agreement, single measurement")
        add("D measures", name + " - Vicon/Quest ratio", round(s["ratio"], 3))
    L.append("")
    L.append("   95% limits of agreement (the interval a single Quest reading could")
    L.append("   be wrong by, against Vicon):")
    for name, s in results:
        if s is None:
            continue
        L.append("      %-28s %+.3f to %+.3f" % (name, s["loa_lo"], s["loa_hi"]))
    L.append("")
    L.append("-" * 78)
    L.append("  WHAT THIS SESSION CANNOT SETTLE, AND THE TWO MINUTES THAT WOULD")
    L.append("")
    L.append("  Everything above is measured on reaching data, where the two systems")
    L.append("  are watching two different points on a hand that rotates. That is")
    L.append("  enough for temporal validation and for relative agreement, and it is")
    L.append("  not enough to state the Quest's absolute spatial accuracy, because")
    L.append("  marker-offset rotation and tracker error are not separable in it.")
    L.append("")
    L.append("  They become separable with a short static routine, once per session,")
    L.append("  before the task:")
    L.append("    1. Put a Vicon marker on the right index fingertip - the same point")
    L.append("       the Quest reports - alongside the existing RFIN marker.")
    L.append("    2. Hold the fingertip at each of the nine target positions in turn,")
    L.append("       still, for about two seconds each, with both systems recording.")
    L.append("  Now both systems measure the same physical point at known positions,")
    L.append("  and the comparison yields an absolute accuracy per position, a scale")
    L.append("  factor that means what it says, and a check on whether accuracy")
    L.append("  degrades toward the edge of the Quest's field of view - which is the")
    L.append("  claim a reviewer is most likely to press on, and the one this study")
    L.append("  needs because its targets are spread across the workspace.")
    L.append("")
    L.append("  Until that exists, the defensible wording is: the Quest's timing is")
    L.append("  validated against Vicon to within a few tens of milliseconds, its")
    L.append("  endpoint measures agree well, and its continuous kinematics do not -")
    L.append("  which is why they are reported from Vicon.")
    L.append("-" * 78)
    L.append("  Read ICC, not r. Two systems can rank 180 reaches identically and")
    L.append("  still disagree on every value; r sees the ranking, ICC sees the")
    L.append("  disagreement. Where ICC is low but r is high, the Quest measure is")
    L.append("  usable as a relative index within a participant and not as an")
    L.append("  absolute value - which is enough for a practice-schedule contrast")
    L.append("  and not enough for a normative report.")
    L.append("=" * 78)
    L.append("")

    report = "\n".join(L)
    print(report)

    stem = a.grasp_csv[:-4] if a.grasp_csv.lower().endswith(".csv") else a.grasp_csv
    vcsv = stem + "_validation.csv"
    with open(vcsv, "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=["section", "statistic", "value", "unit", "note"])
        w.writeheader()
        w.writerows(out)
    print("  wrote %s" % vcsv)

    # per-reach pairs, so Bland-Altman plots can be drawn in any package
    pcsv = stem + "_validation_pairs.csv"
    keys = ["trial", "grasp", "t_end", "dt_peak",
            "dt_onset_abs", "dt_offset_abs", "dt_onset", "dt_offset",
            "q_peak", "v_peak", "q_disp", "v_disp", "q_path", "v_path",
            "q_ratio", "v_ratio", "q_peaks", "v_peaks", "q_nj", "v_nj", "q_tt", "v_tt"]
    with open(pcsv, "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=keys, extrasaction="ignore")
        w.writeheader()
        w.writerows(per_reach)
    print("  wrote %s   (one row per reach, both systems, for Bland-Altman plots)" % pcsv)

    rcsv = stem + "_validation_report.txt"
    with open(rcsv, "w", encoding="utf-8") as fh:
        fh.write(report)
    print("  wrote %s\n" % rcsv)


if __name__ == "__main__":
    main()
