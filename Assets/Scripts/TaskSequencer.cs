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
    private List<int> CurrentSequencePositions()
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

    // The fixed start point for every trial. Matched task distances are measured
    // from here, so moving it changes the geometry of the whole study.
    public Vector3 HomePosition()
    {
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

            // White, so it is clearly not one of the red targets.
            Renderer r = _homeMarker.GetComponent<Renderer>();
            if (r != null) r.material.color = Color.white;
        }

        _homeMarker.SetActive(true);
        _homeMarker.transform.position = HomePosition();
        _homeMarker.transform.localScale = Vector3.one * homeMarkerSize;
    }

    public void StartTrial()
    {
        if (ControlManager.Singleton == null) return;

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
