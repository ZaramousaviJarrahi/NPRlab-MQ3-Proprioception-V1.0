using System.Collections.Generic;
using UnityEngine;

// Runs a whole block of trials so the experimenter does not have to change settings
// between them.
//
// WHY THIS EXISTS:
// Visit 1 is 54 acquisition trials. In the blocked condition that is 18 of Task A, then
// 18 of B, then 18 of C. In the RANDOM condition the task changes on nearly every trial -
// editing the Inspector 54 times mid-session, with a participant waiting, is not a thing
// anyone can do reliably. This builds the whole trial order up front and steps through it.
//
// HOW A SESSION RUNS:
//   1. Press start-session (X / S).
//   2. The runner builds the trial order and shows the first one, without spawning.
//   3. You read the sequence aloud to the participant.
//   4. Press start-trial (A / T). Targets spawn, the participant performs the sequence.
//   5. After the third grasp the targets clear and the next trial is shown, ready.
//   6. Repeat from 3.
//
// It deliberately does NOT auto-spawn the next trial. You need a moment between trials to
// instruct the participant, and an unprompted respawn would start the clock before they
// are ready.
//
// Attach to the same GameObject as ControlManager.
public class SessionRunner : MonoBehaviour
{
    [Header("Session plan")]
    [Tooltip("Practice trials at the start. Excluded from the numbered trials and flagged " +
             "as Familiarization in the data so you can drop them from analysis.")]
    public int familiarizationTrials = 6;

    [Tooltip("Trials of each task in the main block. 18 x 3 tasks = 54, matching Shea & Morgan.")]
    public int trialsPerTask = 18;

    [Tooltip("Blocked only: the order the three tasks are practised in. Counterbalance this " +
             "across participants (ABC, BCA, CAB...).")]
    public string blockedTaskOrder = "A,B,C";

    [Header("Random condition")]
    [Tooltip("Stops the same task appearing twice in a row. Pure randomisation produces runs " +
             "of the same task, which look like blocked practice and weaken the contrast.")]
    public bool avoidImmediateRepeats = true;
    [Tooltip("Set a number to make the random order reproducible for a given participant. " +
             "0 uses the clock, giving a different order each time.")]
    public int randomSeed = 0;

    [Header("Status (read-only)")]
    public string currentBlock = "";
    public int trialIndex = 0;
    public int totalTrials = 0;
    public bool sessionPlanned = false;
    public bool waitingToStartTrial = false;

    private TrialCounter _trialCounter;
    private TaskSequencer _taskSequencer;
    private ExperimenterMode _experimenterMode;
    private SessionCues _cues;
    private TrialCounter.BlockedTask _lastAnnouncedTask;
    private bool _haveAnnouncedATask = false;

    private readonly List<TrialCounter.BlockedTask> _order = new List<TrialCounter.BlockedTask>();
    private readonly List<string> _blockOf = new List<string>();

    void Start()
    {
        _trialCounter    = GetComponent<TrialCounter>();
        _taskSequencer   = GetComponent<TaskSequencer>();
        _experimenterMode = GetComponent<ExperimenterMode>();
        _cues             = GetComponent<SessionCues>();

        if (_trialCounter != null)
        {
            _trialCounter.OnTrialComplete -= HandleTrialComplete;
            _trialCounter.OnTrialComplete += HandleTrialComplete;
        }
        else
        {
            Debug.LogError("SessionRunner: no TrialCounter found - cannot advance trials.");
        }
    }

    void OnDisable()
    {
        if (_trialCounter != null) _trialCounter.OnTrialComplete -= HandleTrialComplete;
    }

    // ---------------------------------------------------------------- planning

    public void BuildSessionPlan()
    {
        _order.Clear();
        _blockOf.Clear();

        var tasks = new[] { TrialCounter.BlockedTask.TaskA,
                            TrialCounter.BlockedTask.TaskB,
                            TrialCounter.BlockedTask.TaskC };

        // Familiarization: cycle through the tasks so they meet all three.
        for (int i = 0; i < familiarizationTrials; i++)
        {
            _order.Add(tasks[i % 3]);
            _blockOf.Add("Familiarization");
        }

        bool random = _trialCounter != null &&
                      _trialCounter.practiceSchedule == TrialCounter.PracticeSchedule.Random;

        if (random)
        {
            var pool = new List<TrialCounter.BlockedTask>();
            foreach (var t in tasks)
                for (int i = 0; i < trialsPerTask; i++) pool.Add(t);

            System.Random rng = randomSeed == 0
                ? new System.Random()
                : new System.Random(randomSeed);

            // Fisher-Yates, then fix any immediate repeats by swapping forward.
            for (int i = pool.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }

            if (avoidImmediateRepeats)
            {
                for (int i = 1; i < pool.Count; i++)
                {
                    if (pool[i] != pool[i - 1]) continue;
                    for (int k = i + 1; k < pool.Count; k++)
                    {
                        if (pool[k] == pool[i - 1]) continue;
                        if (k + 1 < pool.Count && pool[k + 1] == pool[i]) continue;
                        (pool[i], pool[k]) = (pool[k], pool[i]);
                        break;
                    }
                }
            }

            foreach (var t in pool) { _order.Add(t); _blockOf.Add("Acquisition"); }
        }
        else
        {
            // Blocked: all of one task, then all of the next.
            foreach (string letter in blockedTaskOrder.Split(','))
            {
                TrialCounter.BlockedTask task = letter.Trim().ToUpper() switch
                {
                    "B" => TrialCounter.BlockedTask.TaskB,
                    "C" => TrialCounter.BlockedTask.TaskC,
                    _   => TrialCounter.BlockedTask.TaskA,
                };
                for (int i = 0; i < trialsPerTask; i++)
                {
                    _order.Add(task);
                    _blockOf.Add("Acquisition");
                }
            }
        }

        trialIndex = 0;
        totalTrials = _order.Count;
        sessionPlanned = true;

        Debug.Log($"Session planned: {familiarizationTrials} familiarization + " +
                  $"{totalTrials - familiarizationTrials} acquisition = {totalTrials} trials, " +
                  $"{(random ? "RANDOM" : "BLOCKED " + blockedTaskOrder)}.");

        ExportPlan(random);
        PrepareCurrentTrial();
    }

    // Writes the whole trial order to a text file next to the data.
    //
    // WHY: the experimenter reads the target sequence aloud for every trial, so they need
    // the running order on paper. Working it out by hand is fine for the blocked condition
    // and impossible for the random one. Having the APP export its own plan means the sheet
    // can never disagree with what the app actually does - which a separately-written sheet
    // eventually would, the first time a setting changed and only one of them was updated.
    //
    // Pull this file off the headset with SideQuest (Files tab) and print it.
    private void ExportPlan(bool random)
    {
        try
        {
            var controls = GetComponent<ExperimenterControls>();
            string who   = controls != null ? controls.ParticipantId() : "NOID";
            int visit    = controls != null ? controls.visitNumber : 0;

            string folder = SessionConfig.DataFolder();
            string path = System.IO.Path.Combine(folder,
                $"NPRlab_{who}_Visit{visit}_PLAN_{System.DateTime.Now:yyyyMMdd_HHmmss}.txt");

            var s = new System.Text.StringBuilder();
            s.AppendLine($"NPRlab session plan");
            s.AppendLine($"Participant : {who}");
            s.AppendLine($"Visit       : {visit}");
            s.AppendLine($"Condition   : {(random ? "Random" : "Blocked (" + blockedTaskOrder + ")")}");
            if (random) s.AppendLine($"Seed        : {(randomSeed == 0 ? "clock - NOT reproducible" : randomSeed.ToString())}");
            s.AppendLine($"Generated   : {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            s.AppendLine($"Total       : {familiarizationTrials} familiarization + " +
                         $"{totalTrials - familiarizationTrials} acquisition = {totalTrials} trials");
            s.AppendLine();
            s.AppendLine("Trial  Block            Task    Say");
            s.AppendLine("-----  ---------------  ------  --------------");

            // A blank line at each point where the experimenter switches to a new sequence
            // for a RUN of trials, so the switch points stand out on paper.
            //
            // Not on every task change: familiarization cycles A, B, C, A, B, C, so that
            // rule double-spaced the whole first section and buried the one boundary that
            // actually matters - familiarization ending and acquisition starting.
            for (int i = 0; i < _order.Count; i++)
            {
                if (i > 0)
                {
                    bool blockChanged = _blockOf[i] != _blockOf[i - 1];
                    bool newTaskRun = _order[i] != _order[i - 1] &&
                                      RunLengthAt(i) >= MinRunToCountAsBlock;
                    if (blockChanged || newTaskRun) s.AppendLine();
                }
                s.AppendLine($"{i + 1,5}  {_blockOf[i],-15}  {_order[i],-6}  {SequenceFor(_order[i])}");
            }

            System.IO.File.WriteAllText(path, s.ToString());
            Debug.Log($"Session plan written to {path}");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"SessionRunner: could not write the session plan - {e.Message}");
        }
    }

    // How many trials in a row share this trial's task, starting at index i.
    //
    // This is what separates a real BLOCK from an incidental repeat. In the blocked
    // condition a task runs for 18 trials; in the random condition it almost never runs
    // for more than one. Both the printed sheet and the audible chime key off this, so a
    // random session doesn't get a separator and a chime on every single trial - a cue
    // that fires constantly carries no information.
    private int RunLengthAt(int i)
    {
        if (i < 0 || i >= _order.Count) return 0;
        int n = 1;
        while (i + n < _order.Count && _order[i + n] == _order[i]) n++;
        return n;
    }

    private const int MinRunToCountAsBlock = 3;

    // The spoken sequence for a task, taken from TaskSequencer so the sheet and the app
    // read from one source rather than two copies that can drift apart.
    private string SequenceFor(TrialCounter.BlockedTask task)
    {
        if (_taskSequencer == null) return "";
        string raw = task == TrialCounter.BlockedTask.TaskB ? _taskSequencer.taskB
                   : task == TrialCounter.BlockedTask.TaskC ? _taskSequencer.taskC
                   : _taskSequencer.taskA;
        return string.Join(" -> ", raw.Split(','));
    }

    // ---------------------------------------------------------------- running

    // Sets the task for the upcoming trial but does NOT spawn anything yet.
    private void PrepareCurrentTrial()
    {
        if (trialIndex >= _order.Count)
        {
            currentBlock = "COMPLETE";
            waitingToStartTrial = false;
            Debug.Log($"SESSION COMPLETE - {totalTrials} trials finished.");
            return;
        }

        if (_trialCounter != null) _trialCounter.currentTask = _order[trialIndex];
        currentBlock = _blockOf[trialIndex];
        waitingToStartTrial = true;

        // Sound the block-change chime when the task the experimenter must read out
        // changes. In the blocked condition this fires three times in a session and is
        // the signal to switch to the next sequence, so it is not something to count.
        TrialCounter.BlockedTask upcoming = _order[trialIndex];
        if (!_haveAnnouncedATask || upcoming != _lastAnnouncedTask)
        {
            // Only for a genuine block - a run of several trials on the same task. In the
            // random condition the task changes nearly every trial, so chiming on each one
            // would be noise; there the experimenter works straight off the sheet and the
            // per-trial click is the only cue needed.
            bool startsARealBlock = RunLengthAt(trialIndex) >= MinRunToCountAsBlock;
            if (_haveAnnouncedATask && startsARealBlock && _cues != null)
                _cues.TaskChanged(upcoming.ToString());

            _lastAnnouncedTask = upcoming;
            _haveAnnouncedATask = true;
        }
    }

    // Called by the experimenter's start-trial button.
    public void StartNextTrial()
    {
        if (!sessionPlanned) BuildSessionPlan();
        if (trialIndex >= _order.Count)
        {
            Debug.Log("Session already complete - no trials left.");
            return;
        }

        if (_taskSequencer != null) _taskSequencer.StartTrial();
        waitingToStartTrial = false;

        Debug.Log($"Trial {trialIndex + 1}/{totalTrials} ({currentBlock}) - " +
                  $"{_order[trialIndex]} - instruct: {(_taskSequencer != null ? _taskSequencer.currentSequence : "")}");
    }

    private void HandleTrialComplete()
    {
        if (!sessionPlanned) return;
        StartCoroutine(AdvanceAfterRecordingCompletes());
    }

    // Deferred by one frame ON PURPOSE - do not inline this back into HandleTrialComplete.
    //
    // TrialCounter raises OnTrialComplete the instant the third grasp lands. DataRecorder
    // writes that same grasp's row from the same event, as a separate subscriber, and it
    // reads the task and block labels at the moment it writes. Advancing straight away
    // changed currentTask and currentBlock BEFORE that row was written, so every third row
    // in the CSV carried the NEXT trial's task and block - one grasp in three mislabelled,
    // which would have put a third of the acquisition trials in the wrong condition.
    //
    // Waiting one frame lets every subscriber finish handling the grasp before the session
    // moves on, so each row is recorded under the trial it actually belongs to.
    private System.Collections.IEnumerator AdvanceAfterRecordingCompletes()
    {
        yield return null;

        // Clear the board so the participant isn't reaching while you instruct the next one.
        if (_taskSequencer != null) _taskSequencer.ClearTargets();
        if (_cues != null) _cues.TrialComplete();

        trialIndex++;
        PrepareCurrentTrial();
    }

    public string CurrentBlockLabel() => sessionPlanned ? currentBlock : "";

    // ---------------------------------------------------------------- display

    void OnGUI()
    {
        if (_experimenterMode != null && !_experimenterMode.IsExperimentStarted()) return;
        if (!sessionPlanned) return;

        GUIStyle big = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };

        if (trialIndex >= _order.Count)
        {
            GUI.Label(new Rect(10, 375, 700, 30), "SESSION COMPLETE", big);
            return;
        }

        string state = waitingToStartTrial ? "READY - press start-trial" : "running";
        GUI.Label(new Rect(10, 375, 900, 30),
                  $"Trial {trialIndex + 1} of {totalTrials}   [{currentBlock}]   " +
                  $"{_order[trialIndex]}   -   {state}", big);
    }
}
