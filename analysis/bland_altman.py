"""
Bland-Altman agreement between the Quest and Vicon, from a session's pairs file.

    python3 bland_altman.py ~/NPRlab-Data/P60/Visit1_2026-10-15

Writes out/bland_altman_report.txt and out/bland_altman.png.

WHY THIS EXISTS
---------------
The validation report gives an ICC per measure, which is one number for "do the
two systems agree". It cannot say HOW they disagree. Bland-Altman can, and the
validation report names it twice as the right way to present this.

For every paired reach it plots the DIFFERENCE between the two systems against
their MEAN, and reports three things:

  bias                 the mean difference. Does one system read high?
  limits of agreement  bias +- 1.96 SD. How far apart can they be on one reach?
  slope                regression of difference on mean. THE IMPORTANT ONE.

WHY THE SLOPE IS THE IMPORTANT ONE
----------------------------------
A flat slope means the disagreement is a constant offset, the same at every
size of movement. A non-flat slope means the disagreement GROWS with the
measurement, which is proportional bias. That matters here for two reasons.

First, it is what decides whether a Quest measure can be read as a relative
index within a participant. A constant offset cancels in a within-participant
contrast; a proportional one does not.

Second, practice schedule is expected to change movement speed, so a bias that
scales with speed would move between conditions for reasons that have nothing
to do with the participant. That is the exact failure mode that produced a
withdrawn speed-dependence finding in October 2026.

WHY THE MEAN AND NOT THE REFERENCE ON THE X AXIS
------------------------------------------------
Plotting the difference against Vicon alone induces a correlation even when the
two systems agree perfectly apart from noise, because the same Vicon error
appears on both axes with opposite sign. The mean of the two is the standard
choice and does not do that. Bland and Altman make this point themselves.

WHAT THIS IS NOT
----------------
This is ONE session from ONE person. It describes the instruments, not a
result. With several participants the correct analysis is Bland and Altman's
method for multiple observations per individual (Bland & Altman, 2007, J
Biopharm Stat 17:571-582), because reaches from the same person are related and
treating them as independent makes the systems look closer than they are. That
version is NOT implemented here and needs more than one participant to mean
anything.
"""
import csv
import glob
import math
import os
import sys

# Paired columns in validation_pairs.csv: (quest, vicon, label, unit, scale)
PAIRS = [
    ("q_tt",    "v_tt",    "transport time",   "s",     1.0),
    ("q_disp",  "v_disp",  "displacement",     "mm",    1000.0),
    ("q_peak",  "v_peak",  "peak speed",       "mm/s",  1000.0),
    ("q_path",  "v_path",  "path length",      "mm",    1000.0),
    ("q_ratio", "v_ratio", "path ratio",       "",      1.0),
    ("q_nj",    "v_nj",    "normalised jerk",  "",      1.0),
    ("q_peaks", "v_peaks", "speed peaks",      "count", 1.0),
]


def num(row, key):
    v = (row.get(key) or "").strip()
    if not v:
        return None
    try:
        f = float(v)
    except ValueError:
        return None
    return None if math.isnan(f) or math.isinf(f) else f


def regress(xs, ys):
    """Least squares slope of ys on xs, with its standard error and t.

    Written out rather than imported so there is no scipy dependency and so the
    arithmetic is visible. Returns (slope, se, t, n).
    """
    n = len(xs)
    if n < 3:
        return None
    mx = sum(xs) / n
    my = sum(ys) / n
    sxx = sum((x - mx) ** 2 for x in xs)
    if sxx <= 0:
        return None
    sxy = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    slope = sxy / sxx
    inter = my - slope * mx
    resid = [y - (inter + slope * x) for x, y in zip(xs, ys)]
    sse = sum(r * r for r in resid)
    if n <= 2:
        return None
    se = math.sqrt((sse / (n - 2)) / sxx)
    t = slope / se if se > 0 else float("nan")
    return slope, se, t, n


def analyse(rows, qk, vk, scale):
    ms, ds, qs, vs = [], [], [], []
    for r in rows:
        q, v = num(r, qk), num(r, vk)
        if q is None or v is None:
            continue
        q *= scale
        v *= scale
        qs.append(q)
        vs.append(v)
        ms.append((q + v) / 2.0)
        ds.append(q - v)
    n = len(ds)
    if n < 3:
        return None
    bias = sum(ds) / n
    var = sum((d - bias) ** 2 for d in ds) / (n - 1)
    sd = math.sqrt(var)
    out = {
        "n": n, "bias": bias, "sd": sd,
        "lo": bias - 1.96 * sd, "hi": bias + 1.96 * sd,
        "mean_level": sum(ms) / n,
        "ms": ms, "ds": ds,
    }
    # 95% CI on the bias uses the standard error of the mean difference.
    out["bias_ci"] = 1.96 * sd / math.sqrt(n)
    reg = regress(ms, ds)
    if reg:
        slope, se, t, _ = reg
        out["slope"] = slope
        out["slope_ci"] = 1.96 * se      # normal approximation; n is large here
        out["slope_t"] = t
    return out


def main(session):
    out_dir = os.path.join(session, "out")
    hits = glob.glob(os.path.join(out_dir, "*_validation_pairs.csv"))
    if not hits:
        sys.exit("No *_validation_pairs.csv in %s - run run-session.sh first." % out_dir)
    rows = list(csv.DictReader(open(hits[0])))

    L = []
    L.append("=" * 78)
    L.append("BLAND-ALTMAN: Quest against Vicon, per reach")
    L.append("  session : %s" % os.path.basename(os.path.normpath(session)))
    L.append("  pairs   : %s" % os.path.basename(hits[0]))
    L.append("=" * 78)
    L.append("")
    L.append("  bias    mean difference, Quest minus Vicon. A constant offset.")
    L.append("  LoA     limits of agreement, bias +- 1.96 SD. How far apart two")
    L.append("          systems can be on a single reach, 95% of the time.")
    L.append("  slope   regression of the difference on the mean. Flat means the")
    L.append("          offset is constant; non-flat means it grows with the")
    L.append("          measurement, which is proportional bias.")
    L.append("")

    results = []
    for qk, vk, label, unit, scale in PAIRS:
        r = analyse(rows, qk, vk, scale)
        if r is None:
            L.append("  %-18s no usable pairs" % label)
            continue
        r.update(label=label, unit=unit)
        results.append(r)
        u = (" " + unit) if unit else ""
        L.append("  %s  (n = %d)" % (label.upper(), r["n"]))
        L.append("     typical level     %10.3f%s" % (r["mean_level"], u))
        L.append("     bias              %+10.3f%s   (95%% CI %+.3f to %+.3f)"
                 % (r["bias"], u, r["bias"] - r["bias_ci"], r["bias"] + r["bias_ci"]))
        if r["mean_level"] != 0:
            L.append("     bias as %% of level %+9.1f %%" % (100.0 * r["bias"] / r["mean_level"]))
        L.append("     limits of agree.  %+10.3f to %+.3f%s" % (r["lo"], r["hi"], u))
        if "slope" in r:
            # Described, not tested. The interval is printed because it shows the
            # precision of the estimate, and the verdict says only whether it
            # covers zero. No p-value is given and none would be valid: these
            # reaches are not independent, so an ordinary regression understates
            # the standard error. Calling a slope "significant" from this model
            # would be false precision, which is why the wording is descriptive.
            covers = abs(r["slope_t"]) < 1.96
            L.append("     slope             %+10.4f  (95%% CI %+.4f to %+.4f, t = %+.2f, df = %d)"
                     % (r["slope"], r["slope"] - r["slope_ci"], r["slope"] + r["slope_ci"],
                        r["slope_t"], r["n"] - 2))
            L.append("                       interval %s zero"
                     % ("covers" if covers else "does not cover"))
        L.append("")

    L.append("-" * 78)
    L.append("  A slope whose interval does not cover zero indicates that the")
    L.append("  disagreement changes with the size of the measurement. For such a")
    L.append("  measure the offset does not cancel in a within-participant")
    L.append("  contrast, so it cannot be read as a relative index. This is a")
    L.append("  description of these reaches, not a significance test - see below.")
    L.append("")
    L.append("  THE CONFIDENCE INTERVALS ABOVE ASSUME INDEPENDENT REACHES, AND THESE")
    L.append("  ARE NOT INDEPENDENT. They are repeated reaches by one person, so the")
    L.append("  intervals are narrower than the truth and a slope that only just")
    L.append("  clears t = 1.96 should not be read as established. The large slopes")
    L.append("  are not in doubt; a marginal one is.")
    L.append("")
    L.append("  ONE SESSION, ONE PARTICIPANT. This describes the instruments, not")
    L.append("  a result. The correct analysis across participants is Bland and")
    L.append("  Altman's method for multiple observations per individual (2007),")
    L.append("  which is not implemented here and needs more than one person.")
    L.append("=" * 78)

    report = os.path.join(out_dir, "bland_altman_report.txt")
    open(report, "w").write("\n".join(L) + "\n")
    print("\n".join(L))
    print("\n  wrote %s" % report)

    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
    except Exception as e:
        print("  no plot (matplotlib unavailable: %s)" % e)
        return

    n = len(results)
    cols = 3
    rowsn = (n + cols - 1) // cols
    fig, axes = plt.subplots(rowsn, cols, figsize=(4.2 * cols, 3.4 * rowsn))
    axes = axes.ravel() if n > 1 else [axes]
    for ax, r in zip(axes, results):
        ax.scatter(r["ms"], r["ds"], s=12, alpha=0.6)
        ax.axhline(r["bias"], linestyle="-", linewidth=1)
        ax.axhline(r["lo"], linestyle="--", linewidth=1)
        ax.axhline(r["hi"], linestyle="--", linewidth=1)
        ax.axhline(0, linewidth=0.6, color="grey")
        u = (" (%s)" % r["unit"]) if r["unit"] else ""
        ax.set_title(r["label"], fontsize=10)
        ax.set_xlabel("mean of the two systems" + u, fontsize=8)
        ax.set_ylabel("Quest - Vicon" + u, fontsize=8)
        ax.tick_params(labelsize=7)
    for ax in axes[len(results):]:
        ax.axis("off")
    fig.suptitle("Bland-Altman, Quest against Vicon - %s (one participant, descriptive)"
                 % os.path.basename(os.path.normpath(session)), fontsize=11)
    fig.tight_layout(rect=[0, 0, 1, 0.96])
    png = os.path.join(out_dir, "bland_altman.png")
    fig.savefig(png, dpi=140)
    print("  wrote %s" % png)


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("usage: python3 bland_altman.py <session folder>")
    main(sys.argv[1])
