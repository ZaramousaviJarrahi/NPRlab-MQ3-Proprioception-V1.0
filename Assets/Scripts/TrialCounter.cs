using UnityEngine;

// Attach this to the same GameObject as ControlManager and ExperimenterMode
// (e.g. the ControlManager object in the Main scene).
public class TrialCounter : MonoBehaviour
{
    public enum PracticeSchedule { Blocked, Random }
    public enum BlockedTask { TaskA, TaskB, TaskC }

    [Header("Condition Setup (set this before each session)")]
    public PracticeSchedule practiceSchedule = PracticeSchedule.Blocked;
    [Tooltip("Only used when Practice Schedule = Blocked")]
    public BlockedTask currentTask = BlockedTask.TaskA;

    [Header("Trial Definition")]
    [Tooltip("How many successful grasps make up one trial. Each trial is a 3-step reach sequence per the study design, so this defaults to 3.")]
    public int capturesPerTrial = 3;

    [Header("Session Targets (from the study design)")]
    [Tooltip("Visit 1 Blocked: 18 trials per task (54 total across 3 tasks)")]
    public int trialsPerTaskBlocked = 18;
    [Tooltip("Visit 1 Random: 54 mixed trials total")]
    public int trialsRandom = 54;

    [Header("Testing (turn OFF before running real participants)")]
    [Tooltip("Shows a 'Simulate Capture' button on screen so the counter can be tested "
           + "without the headset or the Windows companion app connected.")]
    public bool showSimulateCaptureButton = false;

    [Header("References")]
    [Tooltip("Drag the ExperimenterMode component here so counting only starts once the experimenter clicks Start Experiment.")]
    public ExperimenterMode experimenterMode;

    private int _captureCountInCurrentTrial = 0;
    private int _trialCount = 0;
    private int _taskACount = 0;
    private int _taskBCount = 0;
    private int _taskCCount = 0;

    void OnEnable()
    {
        TrySubscribe();
    }

    void Start()
    {
        // Belt-and-braces: ControlManager.Singleton may not exist yet at OnEnable time
        // depending on script execution order, so try again here.
        TrySubscribe();

        // Auto-wire the ExperimenterMode on this same GameObject if it wasn't set in the
        // Inspector, so the counter can't silently lose its start-gate.
        if (experimenterMode == null)
        {
            experimenterMode = GetComponent<ExperimenterMode>();
            if (experimenterMode == null)
            {
                Debug.LogWarning("TrialCounter: no ExperimenterMode found on this GameObject. "
                               + "Trials will be counted even before Start Experiment is pressed.");
            }
        }
    }

    void OnDisable()
    {
        if (ControlManager.Singleton != null)
        {
            ControlManager.Singleton.OnTargetCaptured -= HandleTargetCaptured;
        }
    }

    private void TrySubscribe()
    {
        if (ControlManager.Singleton == null) return;
        ControlManager.Singleton.OnTargetCaptured -= HandleTargetCaptured; // avoid double-subscribe
        ControlManager.Singleton.OnTargetCaptured += HandleTargetCaptured;
    }

    private void HandleTargetCaptured()
    {
        // Don't count grasps that happen before the experimenter presses Start Experiment.
        if (experimenterMode != null && !experimenterMode.IsExperimentStarted())
            return;

        _captureCountInCurrentTrial++;

        if (_captureCountInCurrentTrial >= capturesPerTrial)
        {
            _captureCountInCurrentTrial = 0;
            _trialCount++;

            if (practiceSchedule == PracticeSchedule.Blocked)
            {
                switch (currentTask)
                {
                    case BlockedTask.TaskA: _taskACount++; break;
                    case BlockedTask.TaskB: _taskBCount++; break;
                    case BlockedTask.TaskC: _taskCCount++; break;
                }
            }

            Debug.Log($"Trial complete. Total trials this session: {_trialCount}");
        }
    }

    void OnGUI()
    {
        string label;
        if (practiceSchedule == PracticeSchedule.Blocked)
        {
            int taskCount = currentTask == BlockedTask.TaskA ? _taskACount
                          : currentTask == BlockedTask.TaskB ? _taskBCount
                          : _taskCCount;
            label = $"{currentTask}: {taskCount}/{trialsPerTaskBlocked}   (All tasks total: {_trialCount}/{trialsPerTaskBlocked * 3})";
        }
        else
        {
            label = $"Trial: {_trialCount}/{trialsRandom}";
        }

        // Drawn below the "Start Experiment" (y=10) and "Hide/Show Hands" (y=70) buttons.
        label += $"   [grasps this trial: {_captureCountInCurrentTrial}/{capturesPerTrial}]";
        GUI.Label(new Rect(10, 130, 500, 30), label);

        if (showSimulateCaptureButton)
        {
            if (GUI.Button(new Rect(10, 160, 200, 40), "Simulate Capture (TEST)"))
            {
                if (ControlManager.Singleton != null)
                {
                    ControlManager.Singleton.SimulateCapture();
                }
            }
        }
    }

    // Wire this to a debug button, or call it from the Inspector (right-click component -> nothing built in,
    // but you can trigger it from another script) if the experimenter needs to correct a miscount.
    public void ResetCounts()
    {
        _captureCountInCurrentTrial = 0;
        _trialCount = 0;
        _taskACount = 0;
        _taskBCount = 0;
        _taskCCount = 0;
        Debug.Log("Trial counter reset.");
    }
}
