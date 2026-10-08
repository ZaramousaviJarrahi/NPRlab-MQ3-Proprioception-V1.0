#!/bin/bash
# Double-click this file to analyse every session in the folder it sits in.
#
# HOW TO USE
#   1. Put this file in the folder with your session CSVs.
#   2. Put the two files from one session in that folder:
#        NPRlab_P01_Visit1_20261008_103605.csv
#        NPRlab_P01_Visit1_20261008_103605_frames.csv
#      (both come off the headset together; keep their names as they are)
#   3. Optional: to include Vicon, rename your Nexus export to exactly
#        vicon.csv
#      and put it in the same folder.
#   4. Double-click this file.
#
# It finds every session in the folder, analyses each, and writes three files
# per session next to the originals. Sessions already analysed are done again,
# so it is safe to re-run after replacing a file.

cd "$(dirname "$0")" || exit 1

printf '\n'
printf '=========================================================\n'
printf '  NPRlab session analysis\n'
printf '=========================================================\n'
printf '  folder: %s\n\n' "$(pwd)"

# ---- find the analysis script -------------------------------------------
SCRIPT=""
for candidate in \
    "./analyse_session.py" \
    "./analysis/analyse_session.py" \
    "$HOME/UnityProjects/NPRlab-MQ3-Proprioception-V1.0-main 4/analysis/analyse_session.py"
do
    if [ -f "$candidate" ]; then SCRIPT="$candidate"; break; fi
done

if [ -z "$SCRIPT" ]; then
    printf '  Could not find analyse_session.py.\n\n'
    printf '  Put a copy of it in this folder, next to this file, and\n'
    printf '  double-click again.\n\n'
    read -r -p "  Press Return to close. " _
    exit 1
fi
printf '  script: %s\n' "$SCRIPT"

# ---- Vicon, if present ---------------------------------------------------
VICON_ARG=()
if [ -f "vicon.csv" ]; then
    printf '  vicon:  vicon.csv  (will be aligned automatically)\n'
    VICON_ARG=(--vicon "vicon.csv")
else
    printf '  vicon:  none found (rename a Nexus export to vicon.csv to include it)\n'
fi
printf '\n'

# ---- run every session in the folder -------------------------------------
found=0
for frames in *_frames.csv; do
    [ -e "$frames" ] || continue
    session="${frames%_frames.csv}.csv"
    if [ ! -f "$session" ]; then
        printf '  SKIPPED  %s\n' "$frames"
        printf '           no matching %s in this folder\n\n' "$session"
        continue
    fi
    found=$((found+1))
    python3 "$SCRIPT" "$session" "$frames" "${VICON_ARG[@]}"
done

if [ "$found" -eq 0 ]; then
    printf '  No sessions found.\n\n'
    printf '  This folder needs BOTH files from a session, for example:\n'
    printf '    NPRlab_P01_Visit1_20261008_103605.csv\n'
    printf '    NPRlab_P01_Visit1_20261008_103605_frames.csv\n\n'
    printf '  Files currently here:\n'
    ls -1 *.csv 2>/dev/null | sed 's/^/    /' || printf '    (no CSV files at all)\n'
    printf '\n'
else
    printf '=========================================================\n'
    printf '  Done. %s session(s) analysed.\n' "$found"
    printf '  Look for the files ending _grasps.csv, _trials.csv\n'
    printf '  and _accuracy.csv next to your session files.\n'
    printf '=========================================================\n\n'
fi

read -r -p "Press Return to close this window. " _
