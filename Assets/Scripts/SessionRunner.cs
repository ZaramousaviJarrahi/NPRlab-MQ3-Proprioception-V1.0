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

    [Header("Visit 2 - retention and transfer")]
    [Tooltip("Retention trials per trained task, 24 h after acquisition. 6 x 3 tasks = 18, " +
             "per the study plan.")]
    public int retentionTrialsPerTask = 6;

    [Tooltip("Trials of EACH novel transfer task. Transfer is about performance on novel " +
             "material, so every extra trial is the participant learning the transfer task " +
             "and diluting the measure. 9 allows both an initial-transfer score (first 3) " +
             "and a stable mean.")]
    public int transferTrials = 9;

    [Tooltip("Retention trials per task IN EACH TEST ORDER. 3 x 3 tasks x 2 orders = 18, " +
             "matching Shea & Morgan exactly.")]
    public int retentionTrialsPerTaskPerOrder = 3;

    public enum RetentionTestOrder { BlockedFirst, RandomFirst }

    [Tooltip("COUNTERBALANCE THIS ACROSS PARTICIPANTS, balanced within each practice group " +
             "(12 and 12 within each group of 24).\n\n" +
             "Retention is tested under BOTH a blocked order and a random order - this only " +
             "sets which half comes first. Without counterbalancing, the second half always " +
             "carries the warm-up and fatigue of the first, and that would load onto the " +
             "test-order comparison.")]
    public RetentionTestOrder retentionTestOrder = RetentionTestOrder.BlockedFirst;

    public enum TransferTaskOrder { Transfer3First, Transfer5First }

    [Tooltip("COUNTERBALANCE THIS ACROSS PARTICIPANTS. Whichever transfer task comes second " +
             "is done by a more fatigued participant who has also just had practice on a " +
             "novel task.")]
    public TransferTaskOrder transferTaskOrder = TransferTaskOrder.Transfer3First;

    public enum RetentionOrder { Random, Blocked }

    [Tooltip("STILL AN OPEN DECISION in the study plan - confirm it before collecting.\n\n" +
             "A retention test given in blocked order arguably favours the group that " +
             "trained in blocked order, and vice versa, so the order of the retention test " +
             "is not a neutral choice. Random is the common default because it tests " +
             "retrieval rather than repetition, but this should be a deliberate decision " +
             "and stated in the methods.")]
    public RetentionOrder retentionOrder = RetentionOrder.Random;

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

        // The visit decides the shape of the session. Visit 1 is acquisition; Visit 2 is
        // retention on the trained tasks followed by the two novel transfer tasks.
        var controls = GetComponent<ExperimenterControls>();
        if (controls != null && controls.visitNumber == 2) { BuildVisit2Plan(); return; }

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

        ExportPlan(random ? "Random" : "Blocked (" + blockedTaskOrder + ")", random);
        PrepareCurrentTrial();
    }

    // Visit 2: 18 retention trials on the TRAINED tasks, then the novel same-complexity
    // transfer task, then the novel greater-complexity one.
    //
    // The transfer tasks always come after retention and always in that order. Retention
    // must be measured before any exposure to novel material, and doing the five-element
    // task first would contaminate the three-element one.
    // Visit 2: retention under BOTH test orders, then the two transfer tasks.
    //
    // WHY BOTH ORDERS, and why this is not a detail:
    //
    // Shea & Morgan's retention result was not a main effect of practice schedule. It came
    // out of the interaction between how people PRACTISED and how they were TESTED:
    // blocked-practice participants tested in a random order were dramatically worse, while
    // blocked-blocked and random-random did not differ significantly. They got that from 18
    // retention trials delivered as 9 in a blocked sequence and 9 in a random sequence, with
    // the order of the two halves counterbalanced across subjects.
    //
    // Run all 18 in a single order instead and there is no test-order factor, so H2 cannot
    // be tested in the form the original result took. Picking one order is also not neutral:
    // whichever you choose hands one practice group a test that matches their training, and
    // there is no principled basis for deciding which group should get that advantage.
    // Testing under both removes the problem rather than splitting it, and costs no extra
    // trials - the same 18 divide in half.
    //
    // The block label records WHICH half each trial was in, because retention test order is
    // now a within-subjects factor and a factor that is not in the data cannot be analysed.
    private void BuildVisit2Plan()
    {
        var trained = new[] { TrialCounter.BlockedTask.TaskA,
                              TrialCounter.BlockedTask.TaskB,
                              TrialCounter.BlockedTask.TaskC };

        // --- the blocked-order half: n of each task in turn, in this participant's order ---
        var blockedHalf = new List<TrialCounter.BlockedTask>();
        foreach (string letter in blockedTaskOrder.Split(','))
        {
            TrialCounter.BlockedTask task = letter.Trim().ToUpper() switch
            {
                "B" => TrialCounter.BlockedTask.TaskB,
                "C" => TrialCounter.BlockedTask.TaskC,
                _   => TrialCounter.BlockedTask.TaskA,
            };
            for (int i = 0; i < retentionTrialsPerTaskPerOrder; i++) blockedHalf.Add(task);
        }

        // --- the random-order half: the same n of each task, shuffled, no immediate repeats ---
        var randomHalf = new List<TrialCounter.BlockedTask>();
        foreach (var t in trained)
            for (int i = 0; i < retentionTrialsPerTaskPerOrder; i++) randomHalf.Add(t);

        // Seeded from the participant's own seed, so the order is reproducible and can be
        // regenerated later from allocation.csv alone if a file is ever lost.
        System.Random rng = randomSeed == 0 ? new System.Random() : new System.Random(randomSeed);
        for (int i = randomHalf.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (randomHalf[i], randomHalf[j]) = (randomHalf[j], randomHalf[i]);
        }
        for (int i = 1; i < randomHalf.Count; i++)
        {
            if (randomHalf[i] != randomHalf[i - 1]) continue;
            for (int k = i + 1; k < randomHalf.Count; k++)
            {
                if (randomHalf[k] == randomHalf[i - 1]) continue;
                (randomHalf[i], randomHalf[k]) = (randomHalf[k], randomHalf[i]);
                break;
            }
        }

        bool blockedFirst = retentionTestOrder == RetentionTestOrder.BlockedFirst;

        if (blockedFirst)
        {
            foreach (var t in blockedHalf) { _order.Add(t); _blockOf.Add("Retention-Blocked"); }
            foreach (var t in randomHalf)  { _order.Add(t); _blockOf.Add("Retention-Random"); }
        }
        else
        {
            foreach (var t in randomHalf)  { _order.Add(t); _blockOf.Add("Retention-Random"); }
            foreach (var t in blockedHalf) { _order.Add(t); _blockOf.Add("Retention-Blocked"); }
        }

        // --- transfer: both novel tasks, order counterbalanced ---
        if (transferTrials != 3)
        {
            Debug.LogWarning($"SessionRunner: transferTrials is {transferTrials}, but the study plan "
                           + "specifies 3 per transfer task, matching Shea & Morgan. Transfer measures "
                           + "performance on NOVEL material, so every extra trial is the participant "
                           + "learning the transfer task and diluting the thing being measured. Set it "
                           + "to 3 in the Inspector unless you are deliberately piloting.");
        }

        bool t3First = transferTaskOrder == TransferTaskOrder.Transfer3First;
        var firstTransfer  = t3First ? TrialCounter.BlockedTask.Transfer3 : TrialCounter.BlockedTask.Transfer5;
        var secondTransfer = t3First ? TrialCounter.BlockedTask.Transfer5 : TrialCounter.BlockedTask.Transfer3;
        string firstLabel  = t3First ? "Transfer-3" : "Transfer-5";
        string secondLabel = t3First ? "Transfer-5" : "Transfer-3";

        for (int i = 0; i < transferTrials; i++) { _order.Add(firstTransfer);  _blockOf.Add(firstLabel); }
        for (int i = 0; i < transferTrials; i++) { _order.Add(secondTransfer); _blockOf.Add(secondLabel); }

        trialIndex = 0;
        totalTrials = _order.Count;
        sessionPlanned = true;

        Debug.Log($"VISIT 2 planned: retention {blockedHalf.Count} blocked-order + {randomHalf.Count} "
                + $"random-order ({(blockedFirst ? "blocked first" : "random first")}), then "
                + $"{transferTrials} {firstLabel} + {transferTrials} {secondLabel} = {totalTrials} trials. "
                + "Block column records which retention half each trial belongs to.");

        ExportPlan($"Visit 2 - retention in both orders ({(blockedFirst ? "blocked first" : "random first")}), "
                 + $"then transfer ({firstLabel} first)", true);
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
    private void ExportPlan(string conditionLabel, bool showSeed)
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
            s.AppendLine($"Condition   : {conditionLabel}");
            if (showSeed) s.AppendLine($"Seed        : {(randomSeed == 0 ? "clock - NOT reproducible" : randomSeed.ToString())}");
            s.AppendLine($"Generated   : {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            // Counted from the actual plan rather than assumed, so it stays correct for
            // any visit rather than only describing a Visit 1 session.
            var perBlock = new System.Collections.Generic.Dictionary<string, int>();
            foreach (string b in _blockOf) { perBlock.TryGetValue(b, out int c); perBlock[b] = c + 1; }
            var parts = new System.Collections.Generic.List<string>();
            foreach (var kv in perBlock) parts.Add($"{kv.Value} {kv.Key.ToLower()}");
            s.AppendLine($"Total       : {string.Join(" + ", parts)} = {totalTrials} trials");
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
        // Ask TaskSequencer rather than repeating the mapping here. The old copy knew only
        // about A, B and C, so once the transfer tasks existed it would have printed Task A's
        // sequence on every transfer row - the sheet would have been confidently wrong.
        return string.Join(" -> ", _taskSequencer.SequenceTextFor(task).Split(','));
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

    [Header("Trial start - home gate and foreperiod")]
    [Tooltip("ON: a trial does not begin until the participant's hand is detected at the home " +
             "marker. This is what makes the start position standard rather than hoped for - " +
             "endpoint error is measured from wherever the reach actually began.\n\n" +
             "OFF: the trial starts on your button press, and whether the hand was at home is " +
             "still recorded, so compliance can be checked afterwards.")]
    public bool requireHandAtHome = true;

    [Tooltip("How long to wait for a confirmed hand at home before starting anyway. Hand " +
             "tracking drops out intermittently, and a participant left sitting in silence " +
             "with no feedback is worse than a flagged trial. The trial is marked as " +
             "unverified in the log so it can be excluded.")]
    public float homeWaitTimeoutSeconds = 10f;

    [Tooltip("Foreperiod options in seconds, chosen at random each trial. A VARIABLE " +
             "foreperiod is the point: a fixed one lets the participant anticipate the go " +
             "signal and start moving before it, which destroys reaction time as a measure - " +
             "and reaction time is where Shea & Morgan's effect was largest.")]
    public float[] foreperiodSeconds = { 1f, 3f, 5f };

    [Header("Trial start - status (read-only)")]
    [Tooltip("True if the hand was confirmed at home when this trial's cue fired.")]
    public bool lastTrialHomeVerified = false;
    [Tooltip("Foreperiod used on the last trial, seconds.")]
    public float lastForeperiod = -1f;
    [Tooltip("Cue onset to the hand leaving home, seconds. -1 if not measured.")]
    public float lastReactionTime = -1f;
    [Tooltip("The trial number the three values above belong to. DataRecorder checks this " +
             "before writing them, so a late measurement can never be filed under the " +
             "wrong trial.")]
    public int lastTrialStartNumber = -1;
    [Tooltip("True if the hand was on the home marker when the foreperiod began but had left it " +
             "before the cue fired - a false start. The trial still ran, and its reaction time " +
             "cannot be measured.")]
    public bool lastTrialFalseStart = false;

    private HomeGate _homeGate;
    private Coroutine _startRoutine;
    private Coroutine _rtRoutine;
    private bool _trialRunning;
    private bool _numberingWarned;

    // Called by the experimenter's start-trial button.
    public void StartNextTrial()
    {
        if (!sessionPlanned) BuildSessionPlan();
        if (trialIndex >= _order.Count)
        {
            Debug.Log("Session already complete - no trials left.");
            return;
        }

        // Guard against a second press while a trial is already being armed - otherwise two
        // foreperiods run at once and the sequence appears twice.
        if (_startRoutine != null)
        {
            Debug.LogWarning("SessionRunner: a trial is already starting - ignoring the extra press.");
            return;
        }

        // A trial that is UNDERWAY also blocks a new one. The guard above only covered the
        // arming window, which ends the moment the cue fires - so a second press once the
        // participant was already reaching ran the arming sequence again underneath the live
        // trial. That reset lastTrialHomeVerified to false and lastReactionTime to -1 partway
        // through, which is why a trial could record home-verified TRUE on its first grasp and
        // FALSE on the rest, and it left a stray coroutine that started the NEXT trial by
        // itself when the hand came back to home.
        if (_trialRunning)
        {
            Debug.LogWarning($"SessionRunner: trial {trialIndex + 1} is still running - ignoring " +
                             "the press. Wait for the completion tone. If this trial cannot be " +
                             "finished, press clear-targets (Y) to abandon it, then start again.");
            return;
        }

        _startRoutine = StartCoroutine(ArmAndStartTrial());
    }

    // The trial-start sequence: hand at home, warning tone, variable foreperiod, then cue
    // onset. Cue onset is also the go signal, as in the original - the stimulus light both
    // identified the task and started the clock. A separate preview would hand the
    // participant planning time before the clock starts, flattening reaction time.
    private System.Collections.IEnumerator ArmAndStartTrial()
    {
        if (_homeGate == null) _homeGate = GetComponent<HomeGate>();
        if (_homeGate != null) _homeGate.ResetForNewTrial();

        lastTrialHomeVerified = false;
        lastTrialFalseStart = false;
        lastReactionTime = -1f;

        // Whether the hand was at home when ARMING finished. This is not the same thing as
        // whether it was there when the cue fired, and conflating the two is what made the
        // reaction times meaningless: the foreperiod is one to five seconds long, so a flag set
        // before it says only that the hand was on the marker up to five seconds earlier.
        bool homeAtArm = false;

        // ---- wait for the hand at home ----
        if (requireHandAtHome && _homeGate != null)
        {
            float waitStarted = Time.time;
            bool complained = false;

            while (!_homeGate.atHome && Time.time - waitStarted < homeWaitTimeoutSeconds)
            {
                if (!complained && Time.time - waitStarted > 2f)
                {
                    complained = true;
                    Debug.Log($"Waiting for the hand at home ({_homeGate.Describe()}) - "
                            + "tell the participant to rest their hand on the marker.");
                }
                yield return null;
            }

            homeAtArm = _homeGate.atHome;

            if (!homeAtArm)
            {
                // Started anyway, and said so. A trial that silently began from an unknown
                // position is the one outcome there is no way to correct for afterwards.
                Debug.LogWarning($"Trial {trialIndex + 1}: STARTED WITHOUT CONFIRMING the hand at "
                               + $"home after {homeWaitTimeoutSeconds:F0}s ({_homeGate.Describe()}). "
                               + "This trial is NOT position-verified - exclude it from analysis.");
            }
        }
        else if (_homeGate != null)
        {
            homeAtArm = _homeGate.atHome;   // not gating, but still recording
        }

        // ---- warning, then a variable foreperiod ----
        if (_cues != null) _cues.Warning();

        float fp = (foreperiodSeconds != null && foreperiodSeconds.Length > 0)
                     ? foreperiodSeconds[Random.Range(0, foreperiodSeconds.Length)]
                     : 2f;
        lastForeperiod = fp;
        yield return new WaitForSeconds(fp);

        // ---- cue onset = go ----
        //
        // Re-checked here, not only at the button press: this coroutine waits for the hand
        // and then for the foreperiod, up to fifteen seconds in total, and the session can
        // finish during that wait. Indexing _order without re-checking threw an
        // ArgumentOutOfRangeException at the end of a session.
        if (trialIndex >= _order.Count)
        {
            Debug.Log("SessionRunner: the session finished while this trial was arming - "
                    + "nothing started.");
            _startRoutine = null;
            yield break;
        }

        float cueOnset = Time.time;
        int trialNumber = trialIndex + 1;          // captured NOW, not read again later
        _trialRunning = true;
        lastTrialStartNumber = trialNumber;

        // Read AT cue onset, which is the only moment the question means anything: was the hand
        // on the marker when the go signal appeared? Everything downstream depends on this - the
        // reach distance is only the intended one if the reach started from home, and reaction
        // time is only measurable if there is a departure from home still to come.
        lastTrialHomeVerified = _homeGate != null && _homeGate.atHome;
        lastTrialFalseStart = homeAtArm && !lastTrialHomeVerified;
        if (lastTrialFalseStart)
        {
            Debug.LogWarning($"Trial {trialNumber}: FALSE START - the hand was on the home marker "
                           + "when the foreperiod began but had left it before the cue. The reach did "
                           + "not start from home, so its distance is not the intended one.");
        }
        if (_taskSequencer != null) _taskSequencer.StartTrial();
        if (_cues != null) _cues.Go();
        waitingToStartTrial = false;

        Debug.Log($"Trial {trialNumber}/{totalTrials} ({currentBlock}) - {_order[trialIndex]} - "
                + $"instruct: {(_taskSequencer != null ? _taskSequencer.currentSequence : "")} "
                + $"| foreperiod {fp:F0}s | home verified: {lastTrialHomeVerified}");

        _startRoutine = null;

        // ---- reaction time: cue onset to the hand leaving home ----
        if (_homeGate != null && lastTrialHomeVerified)
        {
            // The previous trial's measurement is stopped first. It waits up to ten seconds
            // for the hand to leave home, and if it is still running when the next trial
            // arms, it catches THAT trial's movement and logs it under the wrong number -
            // which is what produced impossible 3-4 second "reaction times" recorded before
            // their own trial had even started.
            if (_rtRoutine != null) StopCoroutine(_rtRoutine);
            _rtRoutine = StartCoroutine(MeasureReactionTime(cueOnset, trialNumber));
        }
        else if (_homeGate != null)
        {
            // Not measurable, and saying so is the whole point. Reaction time is cue onset to
            // the hand LEAVING home; if the hand was not at home when the cue fired there is no
            // departure to time. Left running, the measurement would sit waiting and then catch
            // the hand returning home after the trial, reporting that as a several-second
            // "reaction time".
            lastReactionTime = -1f;
            Debug.LogWarning($"Trial {trialNumber}: reaction time not measured - the hand was not " +
                             "at home when the cue fired.");
        }
    }

    // Reaction time is the interval that carried the effect in the original - it roughly
    // doubled under random practice during acquisition and roughly halved at retention -
    // because that is where task identification and movement planning happen. Measured from
    // cue onset to the moment the fingertip leaves the home radius.
    private System.Collections.IEnumerator MeasureReactionTime(float cueOnset, int trialNumber)
    {
        // One and a half seconds. The hand is confirmed on the marker at cue onset before this
        // runs, so the next departure IS the response, and a response that has not begun within
        // 1.5 s of the go signal is not a reaction time - it is a lost trial.
        //
        // The window has to be this tight because of what a loose one does. At ten seconds, and
        // then at three, the measurement outlived the reach and caught the hand returning towards
        // home afterwards, then leaving again - which is why a recorded run showed reaction times
        // of 2.4 to 2.9 s on thirteen trials whose FIRST GRASP had already happened at around one
        // second. A number that large is arriving from after the event it claims to precede.
        float giveUpAt = Time.time + 1.5f;

        while (Time.time < giveUpAt)
        {
            if (_homeGate.LastLeftHomeTime > cueOnset)
            {
                lastReactionTime = _homeGate.LastLeftHomeTime - cueOnset;
                Debug.Log($"Trial {trialNumber}: reaction time {lastReactionTime * 1000f:F0} ms "
                        + "(cue onset to hand leaving home).");
                _rtRoutine = null;
                yield break;
            }
            yield return null;
        }

        lastReactionTime = -1f;
        _rtRoutine = null;
        Debug.LogWarning($"Trial {trialNumber}: reaction time NOT measured - the hand was never "
                       + "seen leaving home within 1.5s of the cue. Either hand tracking dropped out, "
                       + "or the hand had drifted off the marker. The trial itself is fine; only its "
                       + "reaction time is missing.");
    }

    private void HandleTrialComplete()
    {
        _trialRunning = false;
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

    /// Releases the in-progress lock without completing the trial, so a trial that cannot be
    /// finished - a target that never registers, a participant who stops - does not leave the
    /// start button refusing every press for the rest of the session. Wired to clear-targets,
    /// because clearing the board is what the experimenter does when abandoning a trial anyway.
    ///
    /// The trial is NOT advanced: pressing start again re-runs the same trial number, and the
    /// abandoned attempt stays in the CSV with however many grasps it got. Say so in your notes,
    /// because the CSV will show one trial number with more grasps than the task has targets.
    public void AbandonCurrentTrial()
    {
        if (!_trialRunning && _startRoutine == null) return;
        if (_startRoutine != null) { StopCoroutine(_startRoutine); _startRoutine = null; }
        if (_rtRoutine != null) { StopCoroutine(_rtRoutine); _rtRoutine = null; }
        _trialRunning = false;
        waitingToStartTrial = true;
        Debug.LogWarning($"SessionRunner: trial {trialIndex + 1} ABANDONED by the experimenter. "
                       + "Pressing start will run trial " + (trialIndex + 1) + " again. Note this "
                       + "in your session log - the abandoned attempt is still in the CSV.");
    }

    /// The trial number this component thinks is running, counting from 1.
    public int CurrentTrialNumber() => trialIndex + 1;

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
