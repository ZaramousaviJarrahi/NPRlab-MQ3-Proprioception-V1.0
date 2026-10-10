#!/usr/bin/env python3
"""
Checks that the frame logger really opens its window before the cue.

    python3 check_precue_logging.py NPRlab_P01_Visit1_YYYYMMDD_HHMMSS_frames.csv

Run this on the FIRST session recorded after the change, before trusting any
reaction time from it. Stdlib only.

Four checks, then the number the change exists to produce.

The window used to open at cue onset, so the first logged sample and the go
signal were the same moment and a reaction that began earlier was invisible.
With the window opening at the warning tone, t_since_cue_s runs negative before
the cue. These checks confirm that happened and that nothing else moved.
"""
import csv
import statistics as st
import sys
from collections import defaultdict


def num(v):
    v = (v or "").strip()
    try:
        return float(v)
    except ValueError:
        return None


def main():
    if len(sys.argv) < 2:
        sys.exit("Pass the _frames.csv from the session you want to check.")
    path = sys.argv[1]
    with open(path, newline="", encoding="utf-8-sig") as fh:
        rows = list(csv.DictReader(fh))
    if not rows:
        sys.exit("That frame file is empty.")

    trials = defaultdict(list)
    for r in rows:
        key = ((r.get("trial_number") or "").strip(),
               (r.get("trial_attempt") or "1").strip())
        if key[0]:
            trials[key].append(r)

    fail = 0

    def ok(m):
        print(f"  PASS  {m}")

    def bad(m):
        nonlocal fail
        print(f"  FAIL  {m}")
        fail = 1

    print(f"\n{'=' * 62}\n  Pre-cue logging check\n{'=' * 62}")
    print(f"  file:   {path.split('/')[-1]}")
    print(f"  rows:   {len(rows)}    trials: {len(trials)}\n")

    # ---- 1. the cue still sits at exactly zero ----------------------------
    print("1. THE CUE IS STILL THE TIME ORIGIN")
    bad_cue, no_cue = [], []
    for key, rs in sorted(trials.items(), key=lambda kv: int(kv[0][0])):
        cues = [r for r in rs if (r.get("event") or "").strip() == "cue"]
        if not cues:
            no_cue.append(key[0])
            continue
        for c in cues:
            t = num(c.get("t_since_cue_s"))
            if t is None or abs(t) > 1e-6:
                bad_cue.append((key[0], t))
    if no_cue:
        bad(f"{len(no_cue)} trial(s) have no cue row at all: {no_cue[:8]}")
    elif bad_cue:
        bad(f"{len(bad_cue)} cue row(s) are not at t=0: {bad_cue[:5]}")
    else:
        ok(f"every one of {len(trials)} trials has a cue row at t_since_cue_s = 0")

    # ---- 2. there is data BEFORE the cue ---------------------------------
    print("\n2. THE WINDOW OPENS BEFORE THE CUE")
    leads = []
    missing = []
    for key, rs in sorted(trials.items(), key=lambda kv: int(kv[0][0])):
        ts = [num(r.get("t_since_cue_s")) for r in rs]
        ts = [t for t in ts if t is not None]
        pre = [t for t in ts if t < -1e-9]
        if not pre:
            missing.append(key[0])
        else:
            leads.append(-min(pre))
    if missing:
        bad(f"{len(missing)} of {len(trials)} trials have NO pre-cue rows: {missing[:8]}")
        print("        The logger is still starting at the cue. Either the build is")
        print("        older than the change, or OnWarningTone is not being called.")
    else:
        ok(f"all {len(trials)} trials carry pre-cue rows")
        print(f"        lead time before the cue: median {st.median(leads):.2f} s, "
              f"min {min(leads):.2f} s, max {max(leads):.2f} s")
        if min(leads) < 0.30:
            bad(f"shortest lead is only {min(leads):.2f} s - too little to see an onset")

    # ---- 3. one continuous trace per trial -------------------------------
    print("\n3. ONE TRACE PER TRIAL, NOT TWO")
    multi = [k[0] for k, rs in trials.items()
             if sum(1 for r in rs if (r.get("event") or "").strip() == "cue") > 1]
    attempts = defaultdict(set)
    for (tn, att) in trials:
        attempts[tn].add(att)
    split = {tn: sorted(a) for tn, a in attempts.items() if len(a) > 1}
    if multi:
        bad(f"{len(multi)} trial(s) contain more than one cue row: {multi[:8]}")
        print("        A restarted foreperiod reopened the window instead of")
        print("        continuing it, so that trial's trace is in two pieces.")
    elif split:
        print(f"  --    {len(split)} trial(s) have several attempts: {list(split.items())[:4]}")
        print("        Expected only if those trials were abandoned and re-run.")
        ok("no trial has two cue rows under one attempt")
    else:
        ok("every trial has exactly one cue row and one attempt")

    # ---- 4. the trace is continuous across the cue -----------------------
    print("\n4. NO GAP AT THE CUE")
    worst = (None, 0.0)
    dts = []
    for key, rs in trials.items():
        ts = sorted(t for t in (num(r.get("t_since_cue_s")) for r in rs) if t is not None)
        for a, b in zip(ts, ts[1:]):
            dts.append(b - a)
            if a < 0 <= b and (b - a) > worst[1]:
                worst = (key[0], b - a)
    med_dt = st.median(dts) if dts else 0.0
    print(f"        sample interval: median {med_dt * 1000:.1f} ms "
          f"({1 / med_dt if med_dt else 0:.1f} Hz)")
    if worst[0] is None:
        print("  --    no trial spans the cue, so there is nothing to check here")
    elif worst[1] > max(0.1, 6 * med_dt):
        bad(f"trial {worst[0]} has a {worst[1] * 1000:.0f} ms gap across the cue - "
            f"the window closed and reopened")
    else:
        ok(f"largest gap across the cue is {worst[1] * 1000:.1f} ms, in line with the sample rate")

    # ---- the payoff ------------------------------------------------------
    print("\nWHAT THE PRE-CUE DATA SHOWS (the reason for the change)")
    if missing:
        print("        nothing yet - there are no pre-cue rows to look at")
    else:
        # DISPLACEMENT, not speed. Differentiating unfiltered position at 72 Hz gives
        # a stationary hand about 100 mm/s of apparent speed - the reason the main
        # pipeline low-passes at 8 Hz before any speed estimate. A speed threshold here
        # flags every trial. Jitter does not accumulate, so net movement does not need
        # filtering: compare where the hand sat early in the window with where it sat
        # just before the cue, using medians so a few noisy samples cannot shift it.
        MOVED_MM = 10.0
        early = []
        for key, rs in sorted(trials.items(), key=lambda kv: int(kv[0][0])):
            pre = []
            for r in rs:
                t = num(r.get("t_since_cue_s"))
                x, y, z = (num(r.get("right_tip_x")), num(r.get("right_tip_y")),
                           num(r.get("right_tip_z")))
                c = num(r.get("right_confidence"))
                if t is not None and t < 0 and None not in (x, y, z) and (c is None or c >= 0):
                    pre.append((t, x, y, z))
            if len(pre) < 10:
                continue
            pre.sort()
            half = len(pre) // 2
            rest = pre[:half]                                   # earlier half: hand parked
            late = [p for p in pre if p[0] >= -0.15] or pre[-5:]  # last 150 ms before the cue
            base = [st.median([p[i] for p in rest]) for i in (1, 2, 3)]
            tip = [st.median([p[i] for p in late]) for i in (1, 2, 3)]
            moved = sum((tip[i] - base[i]) ** 2 for i in range(3)) ** 0.5 * 1000
            if moved > MOVED_MM:
                early.append((key[0], moved))
        if early:
            print(f"        {len(early)} of {len(trials)} trials: the hand had already moved "
                  f"more than {MOVED_MM:.0f} mm BEFORE the cue")
            for tn, d in sorted(early, key=lambda e: -e[1])[:10]:
                print(f"          trial {tn:>3}: moved {d:.0f} mm before the go signal")
            print("        These anticipated the cue. Their reaction times are not usable")
            print("        even where the onset search produced a number.")
        else:
            print(f"        no trial moved more than {MOVED_MM:.0f} mm before the cue - "
                  f"no anticipation in this session")

    print(f"\n{'=' * 62}")
    print("  PRE-CUE LOGGING WORKING" if fail == 0
          else "  NOT WORKING YET - see the FAIL lines above")
    print(f"{'=' * 62}\n")
    return fail


if __name__ == "__main__":
    sys.exit(main())
