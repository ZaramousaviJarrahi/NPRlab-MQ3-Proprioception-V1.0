#!/usr/bin/env python3
"""
Derives the measurements that cannot be taken in real time, from the frame file
FingertipFrameLogger writes alongside the per-grasp CSV.

    python3 correct_endpoints.py NPRlab_P01_Visit1_20261006_101500.csv \
                                 NPRlab_P01_Visit1_20261006_101500_frames.csv

Writes <per-grasp file>_corrected.csv: every original column, unchanged, plus the
columns below.

WHY THIS IS NOT DONE IN THE GAME
  Two of these cannot be computed in real time at all. Reaction time by a 5%-of-peak
  criterion needs the peak speed of the reach, which is not known until the reach has
  finished. The endpoint at closest approach needs the frames after the grab fired. Both
  are backward-looking by definition.

  The third, the endpoint correction, could in principle be done online, but should not
  be: the window and the thresholds would be baked into a build, and changing them would
  mean rebuilding and re-collecting. Here they are arguments, and the whole dataset can be
  re-derived in seconds.

NEW COLUMNS
  depth_offset_m               the corrected error resolved ALONG the reach axis (home to
                               target). Negative means the hand stopped short. This is a
                               property of the interaction, not of the participant: the grab
                               fires before the hand arrives and the ball then vanishes, so
                               there is nothing left to reach for.
  lateral_error_m              the same error ACROSS the reach axis. This is the aim, and it
                               is the component to analyse as endpoint accuracy. Pooling the
                               two into one distance buries a 3 cm systematic offset inside
                               what looks like a measure of precision.
  endpoint_error_corrected_m   distance from the target centre to the fingertip at its
                               CLOSEST APPROACH, within --window seconds after the grab
                               registered. This is the endpoint-error measure to analyse.
  endpoint_settle_s            how long after the grab the closest approach occurred. Near
                               zero means the grab fired at the end of the reach, as it
                               should; a consistently positive value is the grab firing
                               early, which is what the 2 October pilot showed.
  endpoint_improvement_m       the original measurement minus the corrected one. How much
                               error was an artefact of when the grab fired.
  hand_speed_at_grasp_mps      fingertip speed at the instant the grab registered. The
                               diagnostic for the whole problem: a reach that has finished
                               is near zero here.
  peak_speed_mps               peak fingertip speed on the approach to this grasp.
  rt_speed_criterion_s         FIRST GRASP OF EACH TRIAL ONLY. Cue onset to the first frame
                               speed rose above --onset-frac of the peak and stayed above
                               it. Blank on later grasps, which have no cue of their own.
  frames_in_reach              usable frames the reach was computed from. A small number
                               means tracking dropped out and the row should be treated
                               with suspicion.

Frames whose confidence is -1 (hand not tracked) are dropped. Consecutive frames carrying
the same tracking sample are collapsed to one: hand tracking runs at about 60 Hz while
rendering runs faster, so a repeated sample would otherwise contribute a zero-speed step
and drag every speed estimate down.
"""

import argparse
import csv
import math
import sys
from collections import defaultdict


def read_csv(path):
    with open(path, newline="", encoding="utf-8-sig") as fh:
        return list(csv.DictReader(fh))


def num(row, key):
    """Float from a CSV cell, or None when blank or unparseable."""
    v = (row.get(key) or "").strip()
    if not v:
        return None
    try:
        return float(v)
    except ValueError:
        return None


def hand_frames(frames, hand):
    """
    Usable frames for one hand, in order, de-duplicated by tracking sample.

    Returns a list of (t_since_cue, x, y, z). A frame is usable when confidence is 0 or 1;
    -1 means the hand was not tracked and the position is stale or zero.
    """
    out = []
    last_sample = None
    cx, cy, cz = f"{hand}_tip_x", f"{hand}_tip_y", f"{hand}_tip_z"
    cconf, csamp = f"{hand}_confidence", f"{hand}_sample_t"

    for fr in frames:
        conf = num(fr, cconf)
        if conf is None or conf < 0:
            continue

        sample = num(fr, csamp)
        # Same tracking sample as the previous usable frame: not a new measurement.
        if sample is not None and sample >= 0 and sample == last_sample:
            continue
        last_sample = sample

        t = num(fr, "t_since_cue_s")
        x, y, z = num(fr, cx), num(fr, cy), num(fr, cz)
        if None in (t, x, y, z):
            continue
        out.append((t, x, y, z))

    return out


def speeds(samples):
    """
    Speed at each sample, in m/s, by central difference where possible. Returns a list the
    same length as samples. Central differences are used rather than consecutive ones
    because they are symmetric in time, so a speed peak is not shifted half a frame.
    """
    n = len(samples)
    if n < 2:
        return [0.0] * n

    sp = [0.0] * n
    for i in range(n):
        j = max(0, i - 1)
        k = min(n - 1, i + 1)
        dt = samples[k][0] - samples[j][0]
        if dt <= 0:
            sp[i] = 0.0
            continue
        d = math.dist(samples[j][1:4], samples[k][1:4])
        sp[i] = d / dt
    return sp


def nearest_index(samples, t):
    """Index of the sample closest in time to t."""
    best, best_d = 0, float("inf")
    for i, s in enumerate(samples):
        d = abs(s[0] - t)
        if d < best_d:
            best, best_d = i, d
    return best


def onset_time(samples, sp, end_idx, frac):
    """
    Movement onset by a proportion-of-peak criterion.

    Find the peak speed between the cue and the first grasp, then walk BACK from the peak
    to the last sample below frac * peak. Walking back from the peak rather than forward
    from the cue is what makes the criterion robust: a jitter spike early in the trial
    would otherwise be read as the start of the movement.
    """
    if end_idx <= 0:
        return None, None

    window = sp[: end_idx + 1]
    if not window:
        return None, None

    peak = max(window)
    if peak <= 0:
        return None, None

    peak_i = window.index(peak)
    threshold = frac * peak

    onset_i = 0
    for i in range(peak_i, -1, -1):
        if sp[i] < threshold:
            onset_i = i
            break

    return samples[onset_i][0], peak


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("grasp_csv", help="the per-grasp file DataRecorder wrote")
    ap.add_argument("frames_csv", help="the matching _frames.csv")
    ap.add_argument("-o", "--out", default=None, help="output path")
    ap.add_argument("--window", type=float, default=0.40,
                    help="seconds after the grab to search for closest approach "
                         "(default 0.40)")
    ap.add_argument("--onset-frac", type=float, default=0.05,
                    help="proportion of peak speed that counts as movement onset "
                         "(default 0.05)")
    args = ap.parse_args()

    grasps = read_csv(args.grasp_csv)
    frames = read_csv(args.frames_csv)

    if not frames:
        sys.exit("The frame file is empty - nothing to correct against.")

    by_trial = defaultdict(list)
    for fr in frames:
        key = (fr.get("trial_number") or "").strip()
        if key:
            by_trial[key].append(fr)

    new_cols = ["endpoint_error_corrected_m", "depth_offset_m", "lateral_error_m",
                "endpoint_settle_s", "endpoint_improvement_m",
                "hand_speed_at_grasp_mps", "peak_speed_mps", "rt_speed_criterion_s",
                "frames_in_reach"]

    out_rows = []
    matched = 0

    for g in grasps:
        row = dict(g)
        for c in new_cols:
            row[c] = ""

        # Only grasps the app actually counted have geometry to correct.
        if (g.get("grasp_outcome") or "").strip() != "recorded":
            out_rows.append(row)
            continue

        trial = (g.get("trial_number") or "").strip()
        grasp_n = (g.get("grasp_in_trial") or "").strip()
        tf = by_trial.get(trial)
        if not tf or not grasp_n:
            out_rows.append(row)
            continue

        # The frame carrying this grasp's mark.
        mark = None
        for fr in tf:
            if (fr.get("event") or "").strip() == "grasp" and \
               (fr.get("grasp_in_trial") or "").strip() == grasp_n:
                mark = fr
                break
        if mark is None:
            out_rows.append(row)
            continue

        t_mark = num(mark, "t_since_cue_s")
        tx = num(mark, "event_target_x")
        ty = num(mark, "event_target_y")
        tz = num(mark, "event_target_z")
        if None in (t_mark, tx, ty, tz):
            out_rows.append(row)
            continue

        # Which hand reached. Trust the per-grasp file; fall back to whichever hand has
        # usable frames at the mark.
        used = (g.get("hand_used") or "").strip().lower()
        hand = "left" if used.startswith("l") else "right"

        samples = hand_frames(tf, hand)
        if len(samples) < 3:
            other = "right" if hand == "left" else "left"
            samples = hand_frames(tf, other)
        if len(samples) < 3:
            out_rows.append(row)
            continue

        matched += 1
        sp = speeds(samples)
        mark_i = nearest_index(samples, t_mark)

        # --- closest approach, in the window after the grab ---
        best_d, best_t, best_p = None, None, None
        for i in range(mark_i, len(samples)):
            t, x, y, z = samples[i]
            if t > t_mark + args.window:
                break
            d = math.dist((x, y, z), (tx, ty, tz))
            if best_d is None or d < best_d:
                best_d, best_t, best_p = d, t, (x, y, z)

        if best_d is not None:
            row["endpoint_error_corrected_m"] = f"{best_d:.5f}"
            row["endpoint_settle_s"] = f"{best_t - t_mark:.4f}"
            original = num(g, "endpoint_error_m")
            if original is not None:
                row["endpoint_improvement_m"] = f"{original - best_d:.5f}"

            # --- split the error along and across the reach ---
            #
            # These are not the same quantity and should not be pooled into one number.
            # Along the reach, the hand stops short because the grab fires before the hand
            # has arrived and the ball then disappears, so there is nothing left to reach
            # for: that distance is a property of the interaction. Across the reach, the
            # hand is aiming, and the scatter is the participant's accuracy.
            #
            # The axis is home to target, where home is the fingertip at cue onset - the
            # reach's own direction, not a world axis, so it stays correct wherever in the
            # grid the target sits.
            home = samples[0][1:4]
            ax = (tx - home[0], ty - home[1], tz - home[2])
            mag = math.sqrt(sum(c * c for c in ax))
            if mag > 1e-6:
                ax = tuple(c / mag for c in ax)
                err = (best_p[0] - tx, best_p[1] - ty, best_p[2] - tz)
                depth = sum(e * a for e, a in zip(err, ax))      # negative = stopped short
                perp = tuple(e - depth * a for e, a in zip(err, ax))
                lateral = math.sqrt(sum(c * c for c in perp))
                row["depth_offset_m"] = f"{depth:.5f}"
                row["lateral_error_m"] = f"{lateral:.5f}"

        row["hand_speed_at_grasp_mps"] = f"{sp[mark_i]:.4f}"
        row["peak_speed_mps"] = f"{max(sp[: mark_i + 1]) if mark_i > 0 else 0.0:.4f}"
        row["frames_in_reach"] = str(mark_i + 1)

        # --- reaction time, first grasp of the trial only ---
        if grasp_n == "1":
            t_on, peak = onset_time(samples, sp, mark_i, args.onset_frac)
            if t_on is not None:
                # t_since_cue_s is already measured from cue onset, so this IS the
                # reaction time - no further subtraction.
                row["rt_speed_criterion_s"] = f"{t_on:.4f}"

        out_rows.append(row)

    out_path = args.out or args.grasp_csv.rsplit(".", 1)[0] + "_corrected.csv"
    fieldnames = list(grasps[0].keys()) + new_cols

    with open(out_path, "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=fieldnames)
        w.writeheader()
        w.writerows(out_rows)

    # A short report, because a correction you cannot see the size of is one you cannot
    # sanity-check.
    def col(name):
        return [float(r[name]) for r in out_rows if r.get(name)]

    orig = [v for v in (num(r, "endpoint_error_m") for r in out_rows) if v is not None]
    corr, settle, spd, imp = col("endpoint_error_corrected_m"), col("endpoint_settle_s"), \
                             col("hand_speed_at_grasp_mps"), col("endpoint_improvement_m")

    def med(v):
        if not v:
            return float("nan")
        s = sorted(v)
        n = len(s)
        return s[n // 2] if n % 2 else (s[n // 2 - 1] + s[n // 2]) / 2

    print(f"wrote {out_path}")
    print(f"  grasp rows            {len(out_rows)}")
    print(f"  matched to frames     {matched}")
    if corr:
        print(f"  endpoint error  was   {med(orig) * 100:.2f} cm (median)")
        print(f"                  now   {med(corr) * 100:.2f} cm (median)")
        print(f"                  diff  {med(imp) * 100:.2f} cm")
        dep, lat = col("depth_offset_m"), col("lateral_error_m")
        if dep and lat:
            print(f"    of which depth      {med(dep) * 100:+.2f} cm (along the reach)")
            print(f"    of which lateral    {med(lat) * 100:.2f} cm (across it - the aim)")
        print(f"  settle time           {med(settle) * 1000:.0f} ms after the grab")
        print(f"  speed at grab         {med(spd) * 1000:.0f} mm/s (median)")
    rts = col("rt_speed_criterion_s")
    if rts:
        print(f"  reaction time         {med(rts) * 1000:.0f} ms (median, {len(rts)} trials)")


if __name__ == "__main__":
    main()
