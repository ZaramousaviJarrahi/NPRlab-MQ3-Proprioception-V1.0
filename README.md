# NPRlab MQ3 Proprioception

A Meta Quest 3 application and analysis pipeline for a study of **blocked versus
random practice** on an upper-limb reaching task, with simultaneous Quest hand
tracking and Vicon motion capture.

> **Status: no participant data has been collected yet.**
> Everything produced so far comes from bench sessions run to test the
> instrument. No number in this repository or its outputs is a study result.

---

## The study

Participants perform a seated reaching-and-grasping task in a Meta Quest 3
headset. One group practises in a blocked schedule, the other in a random
schedule. Movement is recorded at the same time by two independent systems: the
Quest's own hand tracking (~72 Hz) and a Vicon Valkyrie array (100 Hz).

There are three visits:

| Visit | What happens | When |
|---|---|---|
| 1 | Acquisition | Day 1 |
| 2 | Retention and transfer | 20–48 h later |
| 3 | Proprioception: virtual hand visible vs hidden | 7 days later, ±1 day |

Visits 1 and 2 run in augmented reality, with passthrough on, so the
participant sees the real room and their own hand. **Visit 3 runs in virtual
reality, with passthrough off in both conditions.** This is not a stylistic
choice: Meta exposes no per-region passthrough, so there is no way to subtract
the participant's real hand from the camera image. The hidden condition has to
remove the real hand as well as the virtual one, which means the camera feed
must be off throughout Visit 3. The reasoning is documented at the top of
`Assets/Scripts/PassthroughController.cs`.

Because both Visit 3 conditions share the same environment, the AR-to-VR change
is constant across them and cannot account for a difference between them.

---

## Why the app and the analysis live in one repository

The Unity application and the Python analysis are kept together deliberately.
Every data file written by the app is stamped with the git commit it was built
from, so a single commit hash identifies both the software that collected a
session and the code that analysed it. Splitting them into two repositories
would make that guarantee impossible to keep.

---

## Layout

```
Assets/Scripts/     the Unity application
analysis/           the Python analysis pipeline
Assets/Scenes/      Main 2.unity is the scene that gets built
```

### Unity side

| Script | Responsibility |
|---|---|
| `SessionRunner.cs` | Runs a session: trials, blocks, conditions, cue onset, abandon handling |
| `ControlManager.cs` | Grasp detection, targets, grab volumes |
| `TaskSequencer.cs` | Builds trial and task order from the allocation row; places the target grid and home marker relative to the seated participant |
| `DataRecorder.cs` | Writes the per-grasp session CSV |
| `FingertipFrameLogger.cs` | Per-frame hand logging: fingertip, knuckle, wrist |
| `SessionConfig.cs` | Configuration as data, so a build plus one allocation row reproduces a session |
| `HomeGate.cs` | Gates each trial on the hand being at the home marker |
| `HomeAudioGuide.cs` | Audio guidance back to the home position |
| `MovementOnsetDetector.cs` | Movement onset |
| `HandVisibilityToggle.cs` | Shows or hides the virtual hand — the Visit 3 manipulation |
| `PassthroughController.cs` | Switches between AR and VR, camera settings included |
| `ExperimenterControls.cs` | Experimenter mode and keyboard shortcuts |

### Analysis side

All Python, standard library only except NumPy on the Vicon path.

| Script | What it does |
|---|---|
| `analyse_session.py` | Main pipeline: per-reach measures and session summary |
| `validate_systems.py` | Quest versus Vicon cross-system validation |
| `bland_altman.py` | Agreement between the two systems: bias, limits of agreement, slope with confidence interval |
| `analyse_landmarks.py` | Fingertip versus knuckle versus wrist comparison |
| `analyse_spatialcheck.py` | Nine-position static spatial check |
| `check_precue_logging.py` | Verifies that logging starts before the cue |
| `selftest.py` | Checks the pipeline without reading the pipeline — the best script to start from |
| `check_before_sharing.sh` | Scans the whole git history for participant data before the repo link is shared |

`bland_altman.py` reports slopes with confidence intervals and **deliberately
reports no p-values**, because reaches within a session are not independent.
The across-participant version, using the Bland and Altman (2007)
repeated-measures method, is not written yet and waits for real data.

---

## Requirements

- Unity `6000.0.49f1`
- Meta XR SDK `77.0.0`
- Meta Quest 3, developer mode enabled
- Python 3 for the analysis scripts
- Vicon Nexus for the motion capture export

## Running a session

1. Open the project in Unity and open `Assets/Scenes/Main 2.unity`.
2. Build to the headset (Android build target).
3. Start the Vicon capture **before** the Quest session and stop it
   **after**, so the Vicon record fully contains the Quest record.
4. Export from Nexus with both Trajectories and Model Outputs, and rename the
   export to exactly `vicon.csv` in the session folder.
5. Run the analysis scripts in `analysis/` against the session folder.

---

## Data handling

No participant data is kept in this repository, and none ever has been.

- `.gitignore` excludes `/RecordedData/` and `*.csv`
- `analysis/check_before_sharing.sh` checks the entire history, across all
  branches, rather than just the working tree — deleting a file does not remove
  it from a repository, so only a history-wide check means anything
- Run that script before giving anyone the repository link

---

## Branches

`visit-3` is the active branch and carries the current work. `main` is the
lab's original version. Other branches are feature work, merged or awaiting
review.

---

## Origin and credit

This is a fork of [HATlab-UVIC/NPRlab-MQ3-Proprioception-V1.0](https://github.com/HATlab-UVIC/NPRlab-MQ3-Proprioception-V1.0).
The original Quest application was written by **David Kim** under the
supervision of **Dr. David Kennedy**, and the networking, scene setup and
grasping foundation in this repository are his work.

The study design, the Visit 3 proprioception protocol, the session and
configuration layer, the data recording format and the entire `analysis/`
pipeline were added for this project.

Licensed under the MIT License, © 2025 HATlab UVIC. See `LICENSE`.
