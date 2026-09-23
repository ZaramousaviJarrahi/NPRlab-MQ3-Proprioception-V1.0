using System;
using UnityEngine;

// Attach this to the same GameObject as ControlManager and ExperimenterMode
// (e.g. the ControlManager object in the Main scene).
public class TrialCounter : MonoBehaviour
{
    // Raised the moment a trial's last grasp lands, so the session runner can advance.
    public event Action OnTrialComplete;

    public enum PracticeSchedule { Blocked, Random }
    // TaskA/B/C are the trained tasks. Transfer3 and Transfer5 are the novel Visit 2
    // transfer tasks and are never practised. New values go on the END - Unity stores an
    // enum in a scene by its NUMBER, so inserting one in the middle would silently change
    // what every saved reference means.
    public enum BlockedTask { TaskA, TaskB, TaskC, Transfer3, Transfer5 }

    [Header("Condition Setup (set this before each session)")]
    public PracticeSchedule practiceSchedule = PracticeSchedule.Blocked;
    [Tooltip("Only used when Practice Schedule = Blocked")]
    public BlockedTask currentTask = BlockedTask.TaskA;

    [Header("Trial Definition")]
    [Tooltip("Fallback only. The number of grasps in a trial normally comes from the length " +
             "of the current task's sequence, because the trained tasks are three grasps and " +
             "the greater-complexity transfer task is five. This value is used only when no " +
             "TaskSequencer is present.")]
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

    private TaskSequencer _taskSequencer;

    // How many grasps complete the CURRENT trial. Read fresh each time rather than cached:
    // Visit 2 switches from three-grasp trained tasks to a five-grasp transfer task partway
    // through the session, and a cached value would end those trials two grasps early.
    public int CapturesRequired()
    {
        if (_taskSequencer == null) _taskSequencer = GetComponent<TaskSequencer>();
        return _taskSequencer != null ? _taskSequencer.CurrentSequenceLength()
                                      : Mathf.Max(1, capturesPerTrial);
    }

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

        // Diagnostic, visible in the headset via VRStatusDisplay.
        //
        // A Transfer-5 trial is ending after three grasps even though the code asks the
        // TaskSequencer how long the current task is. Rather than reason about why from
        // the outside, this prints what the app actually believes at the moment it decides:
        // which task it thinks it is running, how many grasps that task needs, and how many
        // it has counted. Whichever of those three is wrong, this will name it.
        int required = CapturesRequired();
        Debug.Log($"grasp {_captureCountInCurrentTrial}/{required}   task={currentTask}   " +
                  $"seq=[{(GetComponent<TaskSequencer>() != null ? GetComponent<TaskSequencer>().SequenceTextFor(currentTask) : "no sequencer")}]");

        if (_captureCountInCurrentTrial >= required)
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
                    default: break;          // transfer tasks are counted by SessionRunner
                }
            }

            Debug.Log($"Trial complete. Total trials this session: {_trialCount}");
            OnTrialComplete?.Invoke();
        }
    }

    void OnGUI()
    {
        string label;
        if (practiceSchedule == PracticeSchedule.Blocked)
        {
            int taskCount = currentTask == BlockedTask.TaskA ? _taskACount
                          : currentTask == BlockedTask.TaskB ? _taskBCount
                          : currentTask == BlockedTask.TaskC ? _taskCCount
                          : _trialCount;
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
