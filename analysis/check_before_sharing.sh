#!/bin/bash
# Run this before giving anyone the repository link.
#
# WHY THE HISTORY AND NOT JUST THE FILES
#
# Deleting a file does not remove it from a repository. Once a commit is pushed,
# the file stays in the history and anyone with the link can fetch it, forever.
# A clean working tree therefore proves nothing. Every check below that matters
# looks at the whole history, across all branches.
#
# Four of these checks pass only if the answer is "nothing found". Treat any
# other answer as a reason not to share the link until it is sorted out.

cd "$(git -C "$(dirname "$0")" rev-parse --show-toplevel 2>/dev/null)" 2>/dev/null || {
    echo "Not inside a git repository."; exit 2; }

fail=0   # data-safety problems: these block sharing
warn=0   # everything else: worth knowing, does not block

# What counts as data. Deliberately wide: a false alarm costs you a look, a
# miss costs a participant's data on a public URL. Add formats as they appear.
# .mat is Unity's MATERIAL extension, not data. 'participant' alone matches
# ParticipantView.cs. Both were tried and both cried wolf on this repo, which is
# worse than a narrow check: an alarm that fires 17 times gets ignored.
DATA_EXT='\.csv$|\.tsv$|\.xlsx$|\.xls$|\.sav$|\.dta$'
DATA_NAME='recordeddata|allocation|participants|consent|NPRlab_P|_Visit[0-9]'
# Source and asset files never hold participant data, so the name rule skips them.
CODE_EXT='\.cs$|\.cs\.meta$|\.meta$|\.asset$|\.prefab$|\.unity$|\.shader$|\.mat$|\.json$|\.md$'
DATA_ANY="$DATA_EXT|$DATA_NAME"

ok()   { printf '  PASS  %s\n' "$1"; }
bad()  { printf '  FAIL  %s\n' "$1"; fail=1; }
note() { printf '  --    %s\n' "$1"; }
warnf(){ printf '  WARN  %s\n' "$1"; warn=1; }

printf '\n=========================================================\n'
printf '  Pre-share check\n'
printf '=========================================================\n'
printf '  repo:   %s\n' "$(pwd)"
printf '  branch: %s\n\n' "$(git rev-parse --abbrev-ref HEAD)"

# ---- 1. participant data in the history ---------------------------------
printf 'DATA IN THE HISTORY (all branches, all commits ever)\n'

ALL_ADDED=$(git log --all --diff-filter=A --name-only --pretty=format: 2>/dev/null | sort -u | grep . || true)

hits=$(printf '%s\n' "$ALL_ADDED" | grep -iE "$DATA_EXT" || true)
if [ -z "$hits" ]; then ok "no data file has ever been committed"
else bad "a data file was committed at some point:"; printf '%s\n' "$hits" | sed 's/^/          /'; fi

hits=$(printf '%s\n' "$ALL_ADDED" | grep -iE "$DATA_NAME|session_config\.txt" | grep -ivE "$CODE_EXT" || true)
if [ -z "$hits" ]; then ok "nothing from RecordedData, no allocation table, no participant file"
else bad "study data, allocation or a participant file was committed:"
     printf '%s\n' "$hits" | sed 's/^/          /'; fi

# ---- 2. what is tracked right now ---------------------------------------
printf '\nTRACKED NOW\n'
hits=$(git ls-files | grep -iE "$DATA_ANY" | grep -ivE "$CODE_EXT" || true)
if [ -z "$hits" ]; then ok "no data files are tracked"
else bad "data files are tracked:"; printf '%s\n' "$hits" | sed 's/^/          /'; fi

# ---- 3. data sitting in the folder, not yet committed -------------------
printf '\nUNCOMMITTED DATA IN THE FOLDER\n'
hits=$(git status --untracked-files=all --short | grep -iE "$DATA_ANY" | grep -ivE "$CODE_EXT" || true)
if [ -z "$hits" ]; then ok "no loose data files are exposed to the next commit"
else bad "data is visible to git and could be committed by a stray 'git add':"
     printf '%s\n' "$hits" | sed 's/^/          /'; fi

# ---- 4. the rules that keep it that way ---------------------------------
printf '\nIGNORE RULES\n'
miss=""
grep -q '^/RecordedData/' .gitignore 2>/dev/null || miss="$miss /RecordedData/"
grep -q '^\*\.csv'        .gitignore 2>/dev/null || miss="$miss *.csv"
if [ -z "$miss" ]; then ok ".gitignore excludes /RecordedData/ and *.csv"
else bad ".gitignore is missing:$miss"; fi
note "'git add -f' overrides these. Never force-add a data file."

# ---- 5. is the local work actually on GitHub ----------------------------
printf '\nPUSH STATE\n'
up=$(git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>/dev/null || true)
if [ -z "$up" ]; then warnf "this branch has no upstream - nothing is on GitHub yet"
else
    git fetch --quiet 2>/dev/null
    read -r ahead behind <<<"$(git rev-list --left-right --count "$up"...HEAD 2>/dev/null | awk '{print $2, $1}')"
    if [ "${ahead:-0}" = "0" ]; then ok "in sync with $up"
    else warnf "$ahead commit(s) not pushed yet - the link will not show them"; fi
    [ "${behind:-0}" != "0" ] && note "$behind commit(s) on the remote you do not have locally"
fi

# ---- 6. who can read it -------------------------------------------------
printf '\nVISIBILITY\n'
slug=$(git remote get-url origin 2>/dev/null | sed -e 's#.*github\.com[:/]##' -e 's#\.git$##')
if [ -z "$slug" ]; then note "no github remote found"
else
    body=$(curl -s -m 15 "https://api.github.com/repos/$slug" 2>/dev/null)
    if printf '%s' "$body" | grep -q '"full_name"'; then
        note "$slug is PUBLIC - anyone can read it, no sign-in needed"
    elif printf '%s' "$body" | grep -q '"message"'; then
        note "$slug is not readable anonymously: private, or no network right now"
    else
        note "could not reach the GitHub API - check in a private browser window instead"
    fi
fi

# ---- verdict ------------------------------------------------------------
printf '\n=========================================================\n'
if [ "$fail" -eq 0 ]; then
    if [ "$warn" -eq 0 ]; then printf '  CLEAN - safe to share the link.\n'
    else printf '  NO DATA EXPOSED - safe to share, but see the WARN lines above.\n'; fi
else
    printf '  PROBLEM FOUND - do not share the link yet.\n'
    printf '  Data already pushed is not removed by deleting the file.\n'
    printf '  Ask before trying to rewrite history.\n'
fi
printf '=========================================================\n\n'
exit "$fail"
