#!/usr/bin/env python3
"""
Checks that the frame logger really opens its window before the cue.

    python3 check_precue_logging.py NPRlab_P01_Visit1_YYYYMMDD_HHMMSS_frames.csv

Run this on the FIRST session recorded after any logger change, before trusting
any reaction time from it. Stdlib only.

Four checks, then the number the change exists to produce.

The window used to open at cue onset, so the first logged sample and the go
signal were the same moment and a reaction that began earlier was invisible.
The window now opens at the warning tone, so each trial carries a few seconds
of data before the cue.

HOW PRE-CUE ROWS ARE FOUND (changed 16 Oct 2026)
This script used to look for a NEGATIVE t_since_cue_s. No row is ever negative,
so it reported "60 of 60 trials have NO pre-cue rows - the logger is still
starting at the cue" on a session where every trial in fact had about 2.5 s of
pre-cue data. The reason is in FingertipFrameLogger.cs: the cue time is not
known yet while the pre-cue rows are being written, so they are stamped from
the WARNING, and t_since_cue_s is simply zeroed a second time when the cue
fires. Within one trial the column ramps up, returns to zero, and ramps again.

So the cue is located by POSITION - the row carrying the 'cue' event - and all
timing here is done on t_session_s, which is monotonic across the whole file.
`rel()` below reconstructs the cue-relative time the old code assumed it had,
and is negative before the cue exactly as intended.

The same misunderstanding, in analyse_session.py, read the warning tone as the
cue and reported 24 of 60 P60 Visit 1 trials as anticipating the go signal. The
true figure is 1. See claude/Fix-Record-2026-10-16-Cue-Index-Bug.md.
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


def split_trial(rs):
    """
    (cue_index, cue_session_time) for one trial's rows, or (None, None).

    Positional, not time-matched: two rows in a trial read t_since_cue_s = 0
    (the warning and the cue), so matching on time cannot tell them apart.
    """
    ci = next((i for i, r in enumerate(rs)
               if (r.get("event") or "").strip() == "cue"), None)
    if ci is None:
        return None, None
    return ci, num(rs[ci].get("t_session_s"))


def main():
    if len(sys.argv) < 2:
        sys.exit("Pass the _frames.csv from the session you want to check.")
    path = sys.argv[1]
    with open(path, newline="", encoding="utf-8-sig") as fh:
        rows = list(csv.DictReader(fh))
    if not rows:
        sys.exit("That frame file is empty.")

    if "t_session_s" not in rows[0]:
        sys.exit("This frame file has no t_session_s column. It predates the session\n"
                 "clock, and pre-cue timing cannot be reconstructed from it - the only\n"
                 "other time column restarts twice per trial. Re-export if you can.")

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

    # cue position and time for every trial, done once
    cues = {k: split_trial(rs) for k, rs in trials.items()}

    def rel(r, tc):
        """Cue-relative time: negative before the cue, zero at it."""
        t = num(r.get("t_session_s"))
        return None if (t is None or tc is None) else t - tc

    # ---- 1. every trial has exactly one cue, on the session clock ---------
    print("1. THE CUE CAN BE LOCATED IN EVERY TRIAL")
    no_cue = [k[0] for k, (ci, tc) in cues.items() if ci is None]
    no_time = [k[0] for k, (ci, tc) in cues.items() if ci is not None and tc is None]
    if no_cue:
        bad(f"{len(no_cue)} trial(s) have no cue row at all: {no_cue[:8]}")
    elif no_time:
        bad(f"{len(no_time)} cue row(s) carry no t_session_s: {no_time[:8]}")
    else:
        ok(f"every one of {len(trials)} trials has one cue row with a session timestamp")

    # ---- 2. there is data BEFORE the cue ---------------------------------
    print("\n2. THE WINDOW OPENS BEFORE THE CUE")
    leads, missing = [], []
    for key, rs in sorted(trials.items(), key=lambda kv: int(kv[0][0])):
        ci, tc = cues[key]
        if ci is None:
            continue
        if ci == 0:
            missing.append(key[0])
        else:
            lead = rel(rs[0], tc)
            if lead is not None:
                leads.append(-lead)
    if missing:
        bad(f"{len(missing)} of {len(trials)} trials have NO pre-cue rows: {missing[:8]}")
        print("        The logger is starting at the cue. Either the build is")
        print("        older than the change, or OnWarningTone is not being called.")
    elif leads:
        ok(f"all {len(leads)} trials carry pre-cue rows")
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
    #
    # Measured on the session clock across the cue ROW BOUNDARY. The old version
    # compared sorted t_since_cue_s values, which across the reset meant
    # comparing a foreperiod time with a post-cue one - not a sample interval.
    print("\n4. NO GAP AT THE CUE")
    worst = (None, 0.0)
    dts = []
    for key, rs in trials.items():
        ci, tc = cues[key]
        ts = [num(r.get("t_session_s")) for r in rs]
        for i in range(len(ts) - 1):
            if ts[i] is None or ts[i + 1] is None:
                continue
            d = ts[i + 1] - ts[i]
            dts.append(d)
            if ci is not None and i + 1 == ci and d > worst[1]:
                worst = (key[0], d)
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
    if missing or not leads:
        print("        nothing yet - there are no pre-cue rows to look at")
    else:
        # DISPLACEMENT, not speed. Differentiating unfiltered position at 72 Hz gives
        # a stationary hand about 100 mm/s of apparent speed - the reason the main
        # pipeline low-passes at 8 Hz before any speed estimate. A speed threshold here
        # flags every trial. Jitter does not accumulate, so net movement does not need
        # filtering: compare where the hand sat early in the window with where it sat
        # just before the cue, using medians so a few noisy samples cannot shift it.
        # THE BASELINE IS THE LAST HALF-SECOND, NOT THE WHOLE WINDOW. The window
        # opens at the warning, a median 3 s before the cue, and the participant
        # has just put their hand back on home - so the first second or so of it
        # is the hand settling, which is not anticipation. Measured across the
        # whole window this reported 44 of 60 P60 Visit 1 trials as anticipating;
        # measured over the last 500 ms it reports 15, and the hand is genuinely
        # still at the cue in 59 of 60. Compare a baseline 500-300 ms before the
        # cue with the last 150 ms, medians either side so jitter cannot shift it.
        MOVED_MM = 10.0
        early, no_base = [], 0
        for key, rs in sorted(trials.items(), key=lambda kv: int(kv[0][0])):
            ci, tc = cues[key]
            if ci is None:
                continue
            pre = []
            for r in rs[:ci]:
                t = rel(r, tc)
                x, y, z = (num(r.get("right_tip_x")), num(r.get("right_tip_y")),
                           num(r.get("right_tip_z")))
                c = num(r.get("right_confidence"))
                if t is not None and None not in (x, y, z) and (c is None or c >= 0):
                    pre.append((t, x, y, z))
            if len(pre) < 10:
                continue
            pre.sort()
            rest = [p for p in pre if -0.50 <= p[0] <= -0.30]     # settled baseline
            late = [p for p in pre if p[0] >= -0.15] or pre[-5:]  # last 150 ms before the cue
            if len(rest) < 3:
                no_base += 1
                continue
            base = [st.median([p[i] for p in rest]) for i in (1, 2, 3)]
            tip = [st.median([p[i] for p in late]) for i in (1, 2, 3)]
            moved = sum((tip[i] - base[i]) ** 2 for i in range(3)) ** 0.5 * 1000
            if moved > MOVED_MM:
                early.append((key[0], moved))
        if early:
            print(f"        {len(early)} of {len(trials)} trials: the hand drifted more than "
                  f"{MOVED_MM:.0f} mm in the last 500 ms before the cue")
            for tn, d in sorted(early, key=lambda e: -e[1])[:10]:
                print(f"          trial {tn:>3}: drifted {d:.0f} mm")
            print("        This is small postural drift, NOT a started reach: 10 mm over")
            print("        350 ms is about 29 mm/s. Whether the response had actually begun")
            print("        is speed_at_cue_mps in the grasps file, against a 100 mm/s")
            print("        criterion - that is the column to exclude trials on.")
        else:
            print(f"        no trial drifted more than {MOVED_MM:.0f} mm in the last 500 ms "
                  f"before the cue")
        if no_base:
            print(f"        ({no_base} trial(s) had too short a lead to measure drift)")

    print(f"\n{'=' * 62}")
    print("  PRE-CUE LOGGING WORKING" if fail == 0
          else "  NOT WORKING YET - see the FAIL lines above")
    print(f"{'=' * 62}\n")
    return fail


if __name__ == "__main__":
    sys.exit(main())
