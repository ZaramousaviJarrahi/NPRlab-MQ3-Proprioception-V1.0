using System.Collections.Generic;
using UnityEngine;

// Puts the targets on screen without needing the Windows companion app.
//
// ControlManager.SpawnByNumber(n) places one target at grid position n (1-9, a 3x3
// grid). Normally those calls arrive as network messages from the Windows app; this
// script simply makes them locally, so the task can be run and tested standalone.
//
// Following the study plan: all nine objects are visible for the whole trial, and a
// "task" is the ORDER the participant is told to grasp them in (e.g. Task A = 5,1,7).
// The order is not enforced - any object can be grasped - so that wrong grasps are
// recorded as errors rather than being silently prevented. DataRecorder writes the
// grid position of every grasp, which is what makes error rate analysable afterwards.
//
// Attach to the same GameObject as ControlManager.
public class TaskSequencer : MonoBehaviour
{
    [Header("Target grid - all measurements in METRES")]
    [Tooltip("ON = targets are placed on an exact grid you define below, identical for every " +
             "participant. OFF = the original placement, whose size and distance came from the " +
             "position of a debug object in the scene and changed whenever anyone moved it.")]
    public bool useFixedGrid = true;
    [Tooltip("Total width of the 3x3 grid, left edge to right edge.")]
    public float gridWidth = 0.50f;
    [Tooltip("Total height of the 3x3 grid, top row to bottom row.")]
    public float gridHeight = 0.40f;
    [Tooltip("Distance straight out in front of the participant to the plane of the grid.")]
    public float gridDistance = 0.45f;
    [Tooltip("Shifts the whole grid up (+) or down (-) relative to eye height. Negative puts it " +
             "below eye level, which is usually more comfortable for seated reaching.")]
    public float gridHeightOffset = -0.15f;

    [Header("Home position - where the hand starts each trial")]
    [Tooltip("A marker the participant rests their hand on before every trial, so the "
           + "first reach is a controlled distance rather than starting from wherever "
           + "their hand happened to be.")]
    public bool showHomeMarker = true;
    [Tooltip("How far BELOW the bottom row of the grid the home marker sits, in metres.")]
    public float homeBelowBottomRow = 0.10f;
    [Tooltip("How far TOWARDS the participant the home marker sits, in metres.")]
    public float homeTowardParticipant = 0.15f;
    [Tooltip("Diameter of the home marker sphere, in metres.")]
    public float homeMarkerSize = 0.05f;

    [Tooltip("ON = the home marker sits where the participant's resting hand was MEASURED to "
           + "be (press Measure home with their hand at rest), and the two offsets above are "
           + "ignored until the session ends. OFF = always the computed position above.\n\n"
           + "Measuring is the reliable option, and it is not a convenience. The offsets place "
           + "the marker at a guessed point, and a marker the hand cannot rest on comfortably "
           + "will not be held through a one-to-five-second foreperiod - which is precisely "
           + "what a reaction time depends on. Arm length, seat height and posture all differ "
           + "between participants; a single pair of numbers cannot be right for all of them.")]
    public bool useMeasuredHome = true;

    [Tooltip("ON = the home marker changes colour and shrinks while the hand is inside the home "
           + "radius, so the participant can see they are holding it. Reset to neutral at cue "
           + "onset, so it never informs the reach itself.")]
    public bool homeMarkerFeedback = true;

    [Header("Where the grid is anchored")]
    [Tooltip("Captured once from the participant's head position, so the grid sits in front of " +
             "wherever they are sitting - then stays put. No grabbing a cube to adjust it.")]
    public bool anchorToParticipant = true;

    [Header("The three matched sequences (grid positions 1-9)")]
    [Tooltip("Task A order, comma separated. Must be matched with B and C on total " +
             "reach distance and number of direction changes - see the study plan.")]
    public string taskA = "2,7,6";
    public string taskB = "2,9,4";
    public string taskC = "4,3,8";

    [Header("Visit 2 transfer sequences (novel - never practised)")]
    [Tooltip("Same-complexity transfer. Matched to A, B and C: 1.011 m of reach, two " +
             "movements, one 143.8-degree direction change. Novel sequence, identical " +
             "difficulty - which is what 'same-complexity transfer' means.")]
    public string transfer3 = "6,1,8";

    [Tooltip("Greater-complexity transfer. Four movements instead of two, but each movement " +
             "is the same size and involves the same kind of direction reversal as a trained " +
             "one, so the added complexity is MORE ELEMENTS, not harder elements.\n\n" +
             "It deliberately reuses no consecutive pair of positions from any trained task. " +
             "Every perfectly matched five-target sequence turns out to be two trained tasks " +
             "joined end to end, which participants would recognise as familiar chunks and " +
             "which would favour whichever group chunks better.")]
    public string transfer5 = "1,3,7,9,5";

    [Header("What appears")]
    [Tooltip("ON = all nine objects visible, participant grasps the instructed order " +
             "(the study plan's design). OFF = only the three targets of the sequence appear.")]
    public bool showAllNineObjects = true;

    [Header("Status (read-only)")]
    public string currentSequence = "";
    public int trialsStarted = 0;

    private TrialCounter _trialCounter;
    private ExperimenterMode _experimenterMode;

    void Start()
    {
        _trialCounter = GetComponent<TrialCounter>();
        _experimenterMode = GetComponent<ExperimenterMode>();

        if (ControlManager.Singleton == null)
        {
            Debug.LogError("TaskSequencer: ControlManager not found - cannot spawn targets.");
        }
    }

    // The raw comma-separated sequence for any task, in one place, so the spawner, the
    // trial counter and the printed plan all read from the same source.
    public string SequenceTextFor(TrialCounter.BlockedTask task)
    {
        switch (task)
        {
            case TrialCounter.BlockedTask.TaskB:     return taskB;
            case TrialCounter.BlockedTask.TaskC:     return taskC;
            case TrialCounter.BlockedTask.Transfer3: return transfer3;
            case TrialCounter.BlockedTask.Transfer5: return transfer5;
            default:                                 return taskA;
        }
    }

    // How many grasps the current task consists of. A trial is over after this many, which
    // is why it must not be hard-wired: the trained tasks are three grasps, the greater-
    // complexity transfer task is five.
    public int CurrentSequenceLength()
    {
        int n = CurrentSequencePositions().Count;
        return n > 0 ? n : 3;
    }

    // Reads the sequence for whichever task the TrialCounter is currently set to.
    public List<int> CurrentSequencePositions()
    {
        string raw = SequenceTextFor(_trialCounter != null ? _trialCounter.currentTask
                                                            : TrialCounter.BlockedTask.TaskA);

        var positions = new List<int>();
        foreach (string part in raw.Split(','))
        {
            if (int.TryParse(part.Trim(), out int n) && n >= 1 && n <= 9) positions.Add(n);
            else if (!string.IsNullOrWhiteSpace(part))
            {
                Debug.LogWarning($"TaskSequencer: '{part.Trim()}' is not a position between 1 and 9 - ignoring it.");
            }
        }
        return positions;
    }

    private Vector3 _gridOrigin;
    private Quaternion _gridRotation = Quaternion.identity;
    private bool _calibrated = false;

    // True once CalibrateGrid() has captured where the participant is sitting.
    //
    // Exposed because HomePosition() and GridPosition() are meaningless before it: they are
    // offsets from _gridOrigin, which is the world origin until calibration runs. HomeGate
    // reads HomePosition() every frame from the moment the app starts, so without this the
    // home gate can sit waiting for a hand to arrive at a point that is not in the room.
    public bool IsCalibrated => _calibrated;

    private Vector3 _measuredHomeLocal;
    private bool _haveMeasuredHome = false;

    /// True once the home marker has been placed from a measured resting hand.
    public bool HasMeasuredHome => _haveMeasuredHome;

    /// Puts the home marker where the participant's hand actually is, and keeps it there.
    ///
    /// The experimenter asks the participant to rest their reaching hand comfortably - the
    /// posture they will return to between every trial - and presses this. The fingertip
    /// position is stored RELATIVE to the grid origin, so it travels with the calibrated frame
    /// and stays put for the rest of the session.
    ///
    /// Why this exists: on 1 October the marker was 20 cm in front of the head at roughly the
    /// height of the middle target row, which is mid-air. Every trial of that session recorded
    /// the hand arriving, withdrawing within 200-400 ms, and resting 20-27 cm away for the
    /// whole foreperiod. No gate threshold can fix a marker the hand will not stay on, and the
    /// result was 60 trials with no reaction time at all.
    public bool CaptureHomeFromHand()
    {
        if (ControlManager.Singleton == null)
        {
            Debug.LogError("TaskSequencer: no ControlManager, so the hand position cannot be read "
                         + "and home cannot be measured.");
            return false;
        }

        if (!_calibrated)
        {
            Debug.LogWarning("TaskSequencer: the grid was not calibrated, so there was no frame to "
                           + "store the measured home position in. Calibrating from the CURRENT head "
                           + "position first - the participant must be seated and facing forward.");
            CalibrateGrid();
        }

        if (!ControlManager.Singleton.TryTrackedFingertip(out Vector3 tip, out bool usedLeft))
        {
            Debug.LogError("TaskSequencer: home NOT measured - neither hand is tracked. Check hand "
                         + "tracking; a controller held in that hand switches it off. The marker is "
                         + "still at its computed position.");
            return false;
        }

        _measuredHomeLocal = Quaternion.Inverse(_gridRotation) * (tip - _gridOrigin);
        _haveMeasuredHome = true;
        UpdateHomeMarker();

        Debug.Log($"Home MEASURED from the {(usedLeft ? "left" : "right")} hand: "
                + $"{_measuredHomeLocal.z * 100f:F1} cm in front, "
                + $"{_measuredHomeLocal.y * 100f:F1} cm vertically and "
                + $"{_measuredHomeLocal.x * 100f:F1} cm sideways of the head, "
                + $"{Vector3.Distance(HomePosition(), GridPosition(5)) * 100f:F1} cm from the centre "
                + "target. Check that against the participant's reach before you start - this is the "
                + "distance every first reach of the session will be.");
        return true;
    }

    /// Back to the computed position - for when a measurement was taken by mistake.
    public void ClearMeasuredHome()
    {
        _haveMeasuredHome = false;
        UpdateHomeMarker();
        Debug.Log("Home measurement cleared - the marker is back at its computed position. "
                + $"{HomeDescription()}");
    }

    /// The home position as an offset from the participant's head, for logs and the plan file.
    /// A start point that is not recorded cannot be checked afterwards, and every reach
    /// distance in the session is measured from it.
    public Vector3 HomeLocalOffset()
    {
        if (useMeasuredHome && _haveMeasuredHome) return _measuredHomeLocal;
        return new Vector3(0f,
                           -gridHeight / 2f - homeBelowBottomRow + gridHeightOffset,
                           gridDistance - homeTowardParticipant);
    }

    /// One line saying where home is and how it got there.
    public string HomeDescription()
    {
        Vector3 o = HomeLocalOffset();
        string how = (useMeasuredHome && _haveMeasuredHome) ? "measured from the resting hand"
                                                            : "computed from the grid offsets";
        return $"home {how}: {o.z * 100f:F1} cm in front, {o.y * 100f:F1} cm vertically, "
             + $"{o.x * 100f:F1} cm sideways of the head";
    }

    // Captures where the participant is sitting, once. Everything after this is fixed.
    public void CalibrateGrid()
    {
        var rig = FindAnyObjectByType<OVRCameraRig>();
        if (anchorToParticipant && rig != null && rig.centerEyeAnchor != null)
        {
            _gridOrigin = rig.centerEyeAnchor.position;

            // Use only the horizontal facing direction, so a tilted or turned head
            // does not tilt the whole grid.
            Vector3 forward = rig.centerEyeAnchor.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
            _gridRotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
        }
        else
        {
            _gridOrigin = transform.position;
            _gridRotation = Quaternion.identity;
            if (anchorToParticipant)
            {
                Debug.LogWarning("TaskSequencer: no headset found, so the grid is anchored to the " +
                                 "scene instead of the participant. Fine for desk testing.");
            }
        }

        _calibrated = true;
        if (useFixedGrid) ApplyFixedGrid();   // move any targets already on screen
        Debug.Log($"Grid calibrated: {gridWidth}m wide x {gridHeight}m high, " +
                  $"{gridDistance}m in front, offset {gridHeightOffset}m vertically.");
    }

    // Exact position of grid slot 1-9. Slot 1 is top-left, 9 is bottom-right.
    public Vector3 GridPosition(int number)
    {
        int n = Mathf.Clamp(number - 1, 0, 8);
        int row = n / 3;
        int col = n % 3;
        float x = Mathf.Lerp(-gridWidth / 2f, gridWidth / 2f, col / 2f);
        float y = Mathf.Lerp(gridHeight / 2f, -gridHeight / 2f, row / 2f) + gridHeightOffset;
        return _gridOrigin + _gridRotation * new Vector3(x, y, gridDistance);
    }

    // Moves every spawned target onto its exact grid slot, overriding the original
    // angle-and-debug-object placement.
    private void ApplyFixedGrid()
    {
        int moved = 0;
        foreach (TargetController tc in FindObjectsByType<TargetController>(FindObjectsSortMode.None))
        {
            tc.transform.position = GridPosition(tc.index + 1);   // index is 0-based
            moved++;
        }
        Debug.Log($"Fixed grid applied to {moved} targets.");
        UpdateHomeMarker();
    }

    private GameObject _homeMarker;

    [Tooltip("Material for the home marker. ASSIGN ONE for headset builds - see the note in " +
             "ApplyHomeMarkerMaterial. Left empty, a material is built at runtime, which works " +
             "in the Editor but can come out invisible in a build.")]
    public Material homeMarkerMaterial;

    [Tooltip("Colour used only when no material is assigned above.")]
    public Color homeMarkerColour = Color.white;

    // WHY THIS IS NOT JUST "set the colour to white":
    //
    // GameObject.CreatePrimitive assigns the BUILT-IN Standard shader. This project renders
    // with URP, which cannot draw that shader - so the marker came out magenta or invisible
    // even though the object existed, was active and was in the right place.
    //
    // The runtime fallback below finds a URP shader by name, which works in the Editor. It is
    // NOT reliable in a player build: a shader referenced only through Shader.Find, with no
    // material in any scene using it, can be stripped out of the build entirely, and
    // Shader.Find then returns null on the headset while working perfectly on the desktop.
    //
    // So assigning homeMarkerMaterial in the Inspector is the reliable path - a material
    // asset referenced from the scene is always included. The fallback exists so the marker
    // still appears if nobody assigns one.
    private void ApplyHomeMarkerMaterial()
    {
        Renderer r = _homeMarker.GetComponent<Renderer>();
        if (r == null) return;

        if (homeMarkerMaterial != null)
        {
            r.material = homeMarkerMaterial;
            Debug.Log("Home marker using the assigned material.");
            return;
        }

        Shader s = Shader.Find("Universal Render Pipeline/Unlit");
        if (s == null) s = Shader.Find("Universal Render Pipeline/Lit");
        if (s == null) s = Shader.Find("Sprites/Default");

        if (s != null)
        {
            var m = new Material(s);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", homeMarkerColour);
            if (m.HasProperty("_Color"))     m.SetColor("_Color", homeMarkerColour);
            r.material = m;
            Debug.Log($"Home marker built a runtime material from shader '{s.name}'. For the "
                    + "headset build, assign Home Marker Material in the Inspector instead - a "
                    + "runtime-found shader can be stripped from a build and the marker would "
                    + "then be invisible on the device but fine in the Editor.");
        }
        else
        {
            r.material.color = homeMarkerColour;
            Debug.LogError("TaskSequencer: could not find a URP shader for the home marker, so it "
                         + "is using the built-in one, which URP cannot render - the marker will "
                         + "be magenta or invisible. Assign Home Marker Material in the Inspector.");
        }
    }

    // The fixed start point for every trial. Matched task distances are measured
    // from here, so moving it changes the geometry of the whole study.
    public Vector3 HomePosition()
    {
        if (useMeasuredHome && _haveMeasuredHome)
            return _gridOrigin + _gridRotation * _measuredHomeLocal;

        float y = -gridHeight / 2f - homeBelowBottomRow + gridHeightOffset;
        float z = gridDistance - homeTowardParticipant;
        return _gridOrigin + _gridRotation * new Vector3(0f, y, z);
    }

    private void UpdateHomeMarker()
    {
        if (!showHomeMarker)
        {
            if (_homeMarker != null) _homeMarker.SetActive(false);
            return;
        }

        if (_homeMarker == null)
        {
            _homeMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _homeMarker.name = "HomePosition";

            // No collider: the home marker must never be grabbable or counted
            // as a target.
            Collider col = _homeMarker.GetComponent<Collider>();
            if (col != null) Destroy(col);

            ApplyHomeMarkerMaterial();
        }

        _homeMarker.SetActive(true);
        _homeMarker.transform.position = HomePosition();
        _homeMarker.transform.localScale = Vector3.one * homeMarkerSize * _markerScale;
    }

    /// What the home marker is currently saying to the participant.
    public enum HomeMarkerState
    {
        Neutral,     // between trials, and from cue onset onwards
        HandInside,  // the hand is within the home radius but has not been there long enough
        Held         // held long enough to satisfy the gate
    }

    private HomeMarkerState _markerState = HomeMarkerState.Neutral;
    private float _markerScale = 1f;

    /// Shows the participant whether they are holding home.
    ///
    /// Without this the only feedback is audible, and a participant who cannot see their hand
    /// (Visit 3) or simply does not realise the marker has to be HELD gets no indication that
    /// touching it and withdrawing is not enough. SessionRunner drives this during arming and
    /// sets it back to Neutral at cue onset - a distance cue during the reach would hand back
    /// exactly the information the hidden-hand condition removes.
    public void SetHomeMarkerState(HomeMarkerState state)
    {
        if (!homeMarkerFeedback) state = HomeMarkerState.Neutral;
        if (state == _markerState && _homeMarker != null) return;
        _markerState = state;

        switch (state)
        {
            case HomeMarkerState.HandInside:
                TintHomeMarker(new Color(1f, 0.78f, 0.25f));   // amber: inside, keep still
                _markerScale = 1f;
                break;
            case HomeMarkerState.Held:
                TintHomeMarker(new Color(0.25f, 0.85f, 0.4f)); // green: the gate is satisfied
                _markerScale = 0.8f;
                break;
            default:
                TintHomeMarker(homeMarkerColour);
                _markerScale = 1f;
                break;
        }

        if (_homeMarker != null)
            _homeMarker.transform.localScale = Vector3.one * homeMarkerSize * _markerScale;
    }

    // Colour is set through whichever property the material actually has: URP uses _BaseColor,
    // the built-in pipeline _Color. Setting only one is how a tint silently does nothing.
    // Scale changes alongside it so the state is still readable if neither property exists.
    private void TintHomeMarker(Color c)
    {
        if (_homeMarker == null) return;
        Renderer r = _homeMarker.GetComponent<Renderer>();
        if (r == null || r.material == null) return;

        if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", c);
        if (r.material.HasProperty("_Color"))     r.material.SetColor("_Color", c);
    }

    private bool _matchCheckDone = false;

    // Guards the one requirement the study calls non-negotiable: the three trained tasks
    // must be matched on total reach distance and direction change, or blocked-vs-random
    // is confounded with task difficulty.
    //
    // This is checked rather than trusted because the sequences live in TWO places - these
    // Inspector fields and session_config.txt - and the file wins silently. A stale line in
    // that file replaces a matched set with an unmatched one, the session runs normally to
    // the end, and the recorded data looks perfectly clean while being unusable. That is
    // exactly what happened with 7,3,6 / 9,1,4 / 1,2,4, where Task C was 47% shorter than
    // the other two. Nothing in the app said a word about it.
    //
    // Deliberately duplicates the small parsing loop instead of refactoring the live
    // sequence code: this runs immediately before real data is recorded, and a validator
    // is not worth the risk of changing the thing it validates.
    public void ValidateMatchedSequences()
    {
        string[] names = { "taskA", "taskB", "taskC" };
        string[] raw   = { taskA,   taskB,   taskC  };
        float[]  total = new float[3];
        float[]  turn  = new float[3];

        for (int i = 0; i < 3; i++)
        {
            var p = new List<int>();
            foreach (string part in raw[i].Split(','))
                if (int.TryParse(part.Trim(), out int n) && n >= 1 && n <= 9) p.Add(n);

            if (p.Count < 3)
            {
                Debug.LogError($"TaskSequencer: {names[i]} = '{raw[i]}' is not a usable 3-target "
                             + "sequence. Not starting a matched-set check.");
                return;
            }

            for (int j = 0; j < p.Count - 1; j++)
                total[i] += Vector3.Distance(GridPosition(p[j]), GridPosition(p[j + 1]));

            turn[i] = Vector3.Angle(GridPosition(p[1]) - GridPosition(p[0]),
                                    GridPosition(p[2]) - GridPosition(p[1]));
        }

        float longest  = Mathf.Max(total[0], Mathf.Max(total[1], total[2]));
        float shortest = Mathf.Min(total[0], Mathf.Min(total[1], total[2]));
        float spreadCm = (longest - shortest) * 100f;
        float spreadPc = shortest > 0.001f ? (longest - shortest) / shortest * 100f : 999f;

        string detail = $"{names[0]}={raw[0]} {total[0] * 100f:F1}cm/{turn[0]:F0}deg, "
                      + $"{names[1]}={raw[1]} {total[1] * 100f:F1}cm/{turn[1]:F0}deg, "
                      + $"{names[2]}={raw[2]} {total[2] * 100f:F1}cm/{turn[2]:F0}deg";

        // 1 cm of tolerance: the matched families on this grid are exact, so anything
        // beyond rounding means a different set, not a rounding difference.
        if (spreadCm > 1.0f)
        {
            Debug.LogError("TaskSequencer: THE THREE TRAINED SEQUENCES ARE NOT MATCHED. "
                         + $"Longest minus shortest = {spreadCm:F1} cm ({spreadPc:F0}%). {detail}. "
                         + "Task difficulty will be confounded with practice schedule and the data "
                         + "will not be usable for the blocked-vs-random comparison. Check the "
                         + "TaskSequencer Inspector AND session_config.txt - the file overrides the "
                         + "Inspector. The matched set is 2,7,6 / 2,9,4 / 4,3,8.");
        }
        else
        {
            Debug.Log($"TaskSequencer: sequences matched to within {spreadCm:F2} cm. {detail}");
        }
    }

    public void StartTrial()
    {
        if (ControlManager.Singleton == null) return;

        // Checked once, here rather than in Start(), so it runs after SessionConfig has
        // applied session_config.txt and before the first grasp is ever recorded.
        if (!_matchCheckDone) { _matchCheckDone = true; ValidateMatchedSequences(); }

        List<int> sequence = CurrentSequencePositions();
        if (sequence.Count == 0)
        {
            Debug.LogError("TaskSequencer: the current task has no valid positions - nothing to spawn.");
            return;
        }

        ControlManager.Singleton.ClearAllTargets();

        // Each spawn is isolated: if one position fails, the rest still spawn and we
        // find out exactly which one broke instead of silently getting fewer targets.
        List<int> wanted = showAllNineObjects
            ? new List<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9 }
            : sequence;

        int ok = 0;
        foreach (int n in wanted)
        {
            try
            {
                ControlManager.Singleton.SpawnByNumber(n);
                ok++;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"TaskSequencer: spawning position {n} FAILED - {e.GetType().Name}: {e.Message}");
            }
        }

        int actuallyInScene = FindObjectsByType<TargetController>(FindObjectsSortMode.None).Length;
        Debug.Log($"Spawn report: asked for {wanted.Count}, {ok} calls succeeded, "
                + $"{actuallyInScene} target objects now in the scene.");

        if (useFixedGrid)
        {
            if (!_calibrated) CalibrateGrid();
            ApplyFixedGrid();
        }

        currentSequence = string.Join("  ->  ", sequence);
        trialsStarted++;
        Debug.Log($"Trial started. Instruct the participant: {currentSequence}");
    }

    public void ClearTargets()
    {
        if (ControlManager.Singleton == null) return;
        ControlManager.Singleton.ClearAllTargets();
        currentSequence = "";
        Debug.Log("Targets cleared.");
    }

    void OnGUI()
    {
        // Only useful once the session has started.
        if (_experimenterMode != null && !_experimenterMode.IsExperimentStarted()) return;

        if (GUI.Button(new Rect(10, 285, 200, 40), "Start Trial"))  StartTrial();
        if (GUI.Button(new Rect(220, 285, 140, 40), "Clear"))       ClearTargets();
        if (GUI.Button(new Rect(370, 285, 150, 40), "Re-centre grid")) CalibrateGrid();

        if (!string.IsNullOrEmpty(currentSequence))
        {
            // Big and readable - this is what you read aloud to the participant.
            GUIStyle big = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            GUI.Label(new Rect(10, 330, 600, 40), $"Say: {currentSequence}", big);
        }
    }
}
