#!/usr/bin/env python3
"""
analyse_spatialcheck.py  —  nine-position Quest/Vicon spatial accuracy check

Run it on the SpatialCheck pair described in run-sheet section 3b: RFIN on the
index fingernail, fingertip held at each of the nine grid positions for about
two seconds, both systems recording.

    python3 analyse_spatialcheck.py vicon.csv NPRlab_..._frames.csv

Writes <quest_stem>_spatialcheck_report.txt next to the Quest file and prints
the same thing.

WHAT IT DOES
  1. Finds the stationary holds in each system independently, by looking for
     runs of near-zero fingertip speed.
  2. Throws away holds that are not on the target plane (hand at rest, tracking
     dropouts), by keeping only holds near the median depth.
  3. Sorts the survivors into a 3x3 lattice and pairs the two systems by grid
     cell, so no assumption is made about clock alignment.
  4. Fits one rigid transform, rotation and translation, NO SCALING, and
     reports the residual per position.

WHY NO SCALING
  Allowing scale would absorb the very calibration error the check exists to
  measure. The scaled fit is printed separately as a diagnostic only: a scale
  far from 1.0 means a real geometry problem, not something to correct away.

AXES ARE DETECTED, NOT ASSUMED
  The nine targets lie in one fronto-parallel plane, so the two axes with the
  largest spread are the in-plane ones and the third is depth. This works for
  both coordinate frames without being told which is which.

  Note that because the targets are coplanar, this recording CANNOT determine
  the depth axis mapping. The in-plane mapping and the in-plane accuracy are
  what it establishes. Add one hold at a different distance if you want the
  depth axis settled too.
"""

import argparse
import math
import os
import statistics as st
import sys

try:
    import numpy as np
except ImportError:
    sys.exit("numpy is required:  pip3 install --break-system-packages numpy")


# --------------------------------------------------------------------------
# Defaults. Both were validated on the 15 Oct 2026 P60 SpatialCheck.
# --------------------------------------------------------------------------
QUEST_SPEED_MM_S = 50.0     # below this the fingertip counts as stationary
VICON_SPEED_MM_S = 80.0     # Vicon is less smoothed, so it needs more headroom
MIN_HOLD_S = 0.6            # a hold must last at least this long
PLANE_TOLERANCE_MM = 80.0   # how far off the target plane a hold may sit
NOMINAL_COL_MM = 250.0
NOMINAL_ROW_MM = 200.0


# ==========================================================================
# Readers
# ==========================================================================

def read_vicon_rfin(path):
    """Pull RFIN X/Y/Z out of a Nexus ASCII export. Returns [(t_s, x, y, z)] in mm."""
    text = open(path, encoding="utf-8-sig", errors="replace").read().split("\n")

    traj_start = None
    for i, row in enumerate(text):
        if row.strip() == "Trajectories":
            traj_start = i
            break
    if traj_start is None:
        sys.exit(f"{path}: no Trajectories section. Re-export with Trajectories ticked.")

    rate = float(text[traj_start + 1].strip())
    names = text[traj_start + 2].split(",")

    col = None
    for j, n in enumerate(names):
        if n.strip().endswith("RFIN") and "|" not in n:
            col = j
            break
    if col is None:
        sys.exit(f"{path}: no RFIN column found.\n"
                 "  In the Nexus export dialog tick Trajectories -> Components\n"
                 "  (Trajectory Count on its own exports no positions), and check the\n"
                 "  trajectory is LABELLED RFIN rather than left grey.")

    pts = []
    for row in text[traj_start + 5:]:
        p = row.split(",")
        if len(p) <= col + 2 or not p[0].strip().isdigit():
            continue
        try:
            pts.append((int(p[0]) / rate, float(p[col]), float(p[col + 1]), float(p[col + 2])))
        except ValueError:
            continue          # marker absent on this frame
    if not pts:
        sys.exit(f"{path}: RFIN column present but empty.")
    return pts


def read_quest_frames(path, hand="right"):
    """Pull the tracked fingertip out of the Quest frame log. Returns mm."""
    import csv
    rows = list(csv.DictReader(open(path, encoding="utf-8-sig")))
    if not rows:
        sys.exit(f"{path}: empty.")
    tx, ty, tz = f"{hand}_tip_x", f"{hand}_tip_y", f"{hand}_tip_z"
    if tx not in rows[0]:
        sys.exit(f"{path}: no column {tx}.")

    pts, zeros, lowconf = [], 0, 0
    for r in rows:
        try:
            t = float(r["t_session_s"])
            x, y, z = float(r[tx]), float(r[ty]), float(r[tz])
        except (ValueError, KeyError):
            continue
        # The frame log writes lost tracking as exact zeros, not blanks.
        if x == 0.0 and y == 0.0 and z == 0.0:
            zeros += 1
            continue
        try:
            if float(r[f"{hand}_confidence"]) < 1:
                lowconf += 1
        except (ValueError, KeyError):
            pass
        pts.append((t, x * 1000.0, y * 1000.0, z * 1000.0))
    return pts, zeros, lowconf, len(rows)


# ==========================================================================
# Holds
# ==========================================================================

def find_holds(pts, speed_thresh, min_dur):
    """Runs of near-zero speed. Returns [(t_start, duration, x, y, z, n_samples)]."""
    runs, cur = [], []
    for i in range(1, len(pts)):
        dt = pts[i][0] - pts[i - 1][0]
        if dt <= 0 or dt > 0.05:        # a gap in the data breaks the run
            if cur:
                runs.append(cur)
                cur = []
            continue
        speed = math.dist(pts[i][1:4], pts[i - 1][1:4]) / dt
        if speed < speed_thresh:
            cur.append(pts[i])
        else:
            if cur:
                runs.append(cur)
            cur = []
    if cur:
        runs.append(cur)

    out = []
    for r in runs:
        dur = r[-1][0] - r[0][0]
        if dur < min_dur:
            continue
        out.append((r[0][0], dur,
                    st.mean(q[1] for q in r),
                    st.mean(q[2] for q in r),
                    st.mean(q[3] for q in r),
                    len(r)))
    return out


def plane_axes(holds):
    """For a fronto-parallel grid the two widest-spread axes are in-plane."""
    spreads = [max(h[2 + a] for h in holds) - min(h[2 + a] for h in holds) for a in range(3)]
    order = sorted(range(3), key=lambda a: -spreads[a])
    return sorted(order[:2]), order[2]          # (in_plane_axes, depth_axis)


def on_plane(holds, depth_axis, tol):
    med = st.median(h[2 + depth_axis] for h in holds)
    return [h for h in holds if abs(h[2 + depth_axis] - med) <= tol], med


def cluster_1d(values, gap):
    """Split sorted values wherever the gap exceeds `gap`. Returns list of groups."""
    idx = sorted(range(len(values)), key=lambda i: values[i])
    groups, cur = [], [idx[0]]
    for a, b in zip(idx, idx[1:]):
        if values[b] - values[a] > gap:
            groups.append(cur)
            cur = []
        cur.append(b)
    groups.append(cur)
    return groups


def assign_cells(holds, in_plane, nominal_col, nominal_row):
    """Sort holds into a 3x3 lattice. Returns {(col, row): hold}."""
    ax_h, ax_v = in_plane                       # horizontal, vertical
    hv = [h[2 + ax_h] for h in holds]
    vv = [h[2 + ax_v] for h in holds]

    cols = cluster_1d(hv, nominal_col * 0.5)
    rows = cluster_1d(vv, nominal_row * 0.5)
    cols = sorted(sorted(cols, key=len, reverse=True)[:3],
                  key=lambda g: st.mean(hv[i] for i in g))
    rows = sorted(sorted(rows, key=len, reverse=True)[:3],
                  key=lambda g: -st.mean(vv[i] for i in g))   # top row first

    col_of = {i: n for n, g in zip("LMR", cols) for i in g}
    row_of = {i: n for n, g in zip("TMB", rows) for i in g}

    cells = {}
    for i, h in enumerate(holds):
        if i in col_of and i in row_of:
            key = (col_of[i], row_of[i])
            # if two holds land in one cell, keep the one with more samples
            if key not in cells or h[5] > cells[key][5]:
                cells[key] = h
    spacing = (
        [st.mean(hv[i] for i in cols[k + 1]) - st.mean(hv[i] for i in cols[k]) for k in range(2)],
        [st.mean(vv[i] for i in rows[k]) - st.mean(vv[i] for i in rows[k + 1]) for k in range(2)],
    )
    return cells, spacing


# ==========================================================================
# Fit
# ==========================================================================

def kabsch(P, Q, allow_scale=False):
    """Rigid transform taking P onto Q. Scale is off unless asked for."""
    cP, cQ = P.mean(0), Q.mean(0)
    P0, Q0 = P - cP, Q - cQ
    U, S, Vt = np.linalg.svd(P0.T @ Q0)
    d = np.sign(np.linalg.det(Vt.T @ U.T))
    R = Vt.T @ np.diag([1, 1, d]) @ U.T
    s = (S * np.array([1, 1, d])).sum() / (P0 ** 2).sum() if allow_scale else 1.0
    return R, cQ - s * R @ cP, s


CELL_TO_POSITION = {("L", "T"): 1, ("M", "T"): 2, ("R", "T"): 3,
                    ("L", "M"): 4, ("M", "M"): 5, ("R", "M"): 6,
                    ("L", "B"): 7, ("M", "B"): 8, ("R", "B"): 9}


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("vicon_csv")
    ap.add_argument("quest_frames_csv")
    ap.add_argument("--hand", default="right", choices=["right", "left"])
    ap.add_argument("--quest-speed", type=float, default=QUEST_SPEED_MM_S)
    ap.add_argument("--vicon-speed", type=float, default=VICON_SPEED_MM_S)
    ap.add_argument("--min-hold", type=float, default=MIN_HOLD_S)
    ap.add_argument("--plane-tol", type=float, default=PLANE_TOLERANCE_MM)
    args = ap.parse_args()

    L = []
    def say(s=""):
        L.append(s)
        print(s)

    say("=" * 74)
    say("  NINE-POSITION SPATIAL ACCURACY CHECK   Quest fingertip vs Vicon RFIN")
    say("=" * 74)
    say(f"  vicon : {os.path.basename(args.vicon_csv)}")
    say(f"  quest : {os.path.basename(args.quest_frames_csv)}")

    vpts = read_vicon_rfin(args.vicon_csv)
    qpts, zeros, lowconf, nrows = read_quest_frames(args.quest_frames_csv, args.hand)

    say("")
    say("A. DATA QUALITY")
    say(f"   Vicon  RFIN present on {len(vpts)} frames, "
        f"{vpts[0][0]:.2f} to {vpts[-1][0]:.2f} s")
    say(f"   Quest  {len(qpts)} tracked samples of {nrows} rows, "
        f"{qpts[0][0]:.2f} to {qpts[-1][0]:.2f} s")
    if zeros:
        say(f"          {zeros} samples with tracking lost (logged as 0,0,0) - excluded")
    if lowconf:
        say(f"          {lowconf} low-confidence samples ({100*lowconf/max(nrows,1):.1f}%)")

    vh_all = find_holds(vpts, args.vicon_speed, args.min_hold)
    qh_all = find_holds(qpts, args.quest_speed, args.min_hold)
    if len(vh_all) < 4 or len(qh_all) < 4:
        say("")
        say(f"   Too few holds found (Vicon {len(vh_all)}, Quest {len(qh_all)}).")
        say("   Hold each position more deliberately, or relax --vicon-speed /")
        say("   --quest-speed. The detector needs a clear stop-move-stop pattern.")
        return

    v_in, v_ax = plane_axes(vh_all)
    q_in, q_ax = plane_axes(qh_all)
    vh, vmed = on_plane(vh_all, v_ax, args.plane_tol)
    qh, qmed = on_plane(qh_all, q_ax, args.plane_tol)

    say("")
    say("B. HOLDS")
    say(f"   Vicon  {len(vh_all)} holds found, {len(vh)} on the target plane "
        f"(axis {'XYZ'[v_ax]}, median {vmed:.0f} mm)")
    say(f"   Quest  {len(qh_all)} holds found, {len(qh)} on the target plane "
        f"(axis {'xyz'[q_ax]}, median {qmed:.0f} mm)")
    say("   Holds off the plane are the hand at rest or tracking dropouts.")

    vcells, vspace = assign_cells(vh, v_in, NOMINAL_COL_MM, NOMINAL_ROW_MM)
    qcells, qspace = assign_cells(qh, q_in, NOMINAL_COL_MM, NOMINAL_ROW_MM)

    say("")
    say("C. GRID COVERAGE   (position numbers assume 1-9 left to right, top to bottom)")
    say("      pos   Vicon   Quest")
    missing_q, missing_v = [], []
    for cell, pos in sorted(CELL_TO_POSITION.items(), key=lambda kv: kv[1]):
        v_ok, q_ok = cell in vcells, cell in qcells
        if v_ok and not q_ok:
            missing_q.append(pos)
        if q_ok and not v_ok:
            missing_v.append(pos)
        flag = ""
        if v_ok and not q_ok:
            flag = "   <- QUEST TRACKING DROPOUT"
        elif q_ok and not v_ok:
            flag = "   <- Vicon marker missing here"
        elif not v_ok and not q_ok:
            flag = "   <- not held"
        say(f"       {pos}      {'yes' if v_ok else ' - '}     {'yes' if q_ok else ' - '}{flag}")
    if missing_q:
        say("")
        say(f"   Positions {missing_q} were recorded by Vicon and NOT by the Quest.")
        say("   The finger was there. This is Quest hand tracking giving out, and")
        say("   it is the left column that historically does so for the right hand.")

    paired = sorted(set(vcells) & set(qcells), key=lambda c: CELL_TO_POSITION[c])
    if len(paired) < 4:
        say("")
        say(f"   Only {len(paired)} positions in both systems - too few to fit.")
        return

    V = np.array([vcells[c][2:5] for c in paired])
    Q = np.array([qcells[c][2:5] for c in paired])

    R, t, _ = kabsch(Q, V, allow_scale=False)
    res = np.linalg.norm((R @ Q.T).T + t - V, axis=1)
    _, _, scale = kabsch(Q, V, allow_scale=True)

    say("")
    say(f"D. RIGID FIT, NO SCALING   ({len(paired)} paired positions)")
    say(f"   RMS residual  {np.sqrt((res ** 2).mean()):6.2f} mm")
    say(f"   median        {np.median(res):6.2f} mm")
    say(f"   worst         {res.max():6.2f} mm   at position "
        f"{CELL_TO_POSITION[paired[int(np.argmax(res))]]}")
    say("")
    say("     pos   residual   Quest hold   Vicon hold")
    qdurs = [qcells[c][1] for c in paired]
    short = st.median(qdurs) * 0.6
    for c, r in zip(paired, res):
        qd, vd = qcells[c][1], vcells[c][1]
        flag = "   <- short/fragmented Quest hold" if qd < short else ""
        say(f"      {CELL_TO_POSITION[c]}    {r:6.2f} mm    {qd:5.2f} s      {vd:5.2f} s{flag}")
    say("")
    say("   A short Quest hold next to a full-length Vicon one means the finger")
    say("   was held still and the Quest lost or fragmented the track. That is")
    say("   degraded tracking at that position, short of an outright dropout,")
    say("   and it inflates that position's residual.")
    say("")
    say(f"   Scale if it were allowed: {scale:.4f}  (diagnostic only - do not apply)")
    say("   Near 1.0 means the two systems agree about distance. Far from 1.0 with")
    say("   the marker ON the fingertip means a real geometry problem. A scale")
    say("   error seen in reaching data but NOT here is the RFIN-to-fingertip")
    say("   offset rotating, which is not a Vicon error.")

    say("")
    say("E. AXIS MAPPING")
    for i, lab in enumerate(["Quest x", "Quest y", "Quest z"]):
        row = R.T[i]
        j = int(np.argmax(abs(row)))
        mark = "" if j in v_in else "   <- depth; NOT determined by a coplanar target set"
        say(f"   {lab} -> Vicon {'XYZ'[j]}  ({row[j]:+.3f}){mark}")

    say("")
    say(f"F. GRID GEOMETRY   (nominal {NOMINAL_COL_MM:.0f} mm columns, "
        f"{NOMINAL_ROW_MM:.0f} mm rows)")
    say(f"   Vicon columns  {vspace[0][0]:7.1f} {vspace[0][1]:7.1f} mm")
    say(f"   Quest columns  {qspace[0][0]:7.1f} {qspace[0][1]:7.1f} mm")
    say(f"   Vicon rows     {vspace[1][0]:7.1f} {vspace[1][1]:7.1f} mm")
    say(f"   Quest rows     {qspace[1][0]:7.1f} {qspace[1][1]:7.1f} mm")
    say("   Where BOTH systems agree on a spacing that is off nominal, the")
    say("   discrepancy is where the targets render or where the finger was put,")
    say("   not either tracker.")

    say("")
    say("-" * 74)
    rms = np.sqrt((res ** 2).mean())
    if rms < 15:
        say("  VERDICT: the systems agree. This is normal.")
    elif rms < 30:
        say("  VERDICT: acceptable, but higher than the 9.8 mm reference from")
        say("  13 Oct 2026. Short or unsteady holds inflate this - check the hold")
        say("  durations in section B before treating it as a tracking problem.")
    else:
        say("  VERDICT: too high. Check for a units error, an axis flip, or a")
        say("  mis-anchored grid before using any data from this session.")
    say("=" * 74)

    out = os.path.join(os.path.dirname(os.path.abspath(args.quest_frames_csv)),
                       os.path.splitext(os.path.basename(args.quest_frames_csv))[0]
                       + "_spatialcheck_report.txt")
    with open(out, "w") as fh:
        fh.write("\n".join(L) + "\n")
    print(f"\nreport written to {out}")


if __name__ == "__main__":
    main()
