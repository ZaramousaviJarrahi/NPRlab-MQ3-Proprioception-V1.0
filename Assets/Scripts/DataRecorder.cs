using System;
using System.Globalization;
using System.IO;
using UnityEngine;

// Writes one row per grasp to a CSV file, so a session produces analysable data
// instead of numbers that vanish when the app closes.
//
// Attach to the same GameObject as ControlManager.
//
// The file is written to Application.persistentDataPath:
//   In the Unity editor  -> ~/Library/Application Support/<company>/<product>/
//   On the Quest headset -> /sdcard/Android/data/<package name>/files/
// The full path is printed to the Console at the start of every session and shown
// on screen while recording, so you never have to guess where it went.
//
// Rows are flushed to disk immediately. If the app crashes mid-session you keep
// every trial recorded up to that point, rather than losing the lot.
public class DataRecorder : MonoBehaviour
{
    [Header("Session details")]
    [Tooltip("Typed on screen before you press Start Experiment. Written into every row.")]
    public string participantId = "";
    [Tooltip("Which visit this session is: Visit1 (acquisition), Visit2 (retention/transfer), Visit3 (proprioception).")]
    public string visitLabel = "Visit1";

    [Header("Status (read-only)")]
    public int rowsWritten = 0;
    public int trialsCompleted = 0;
    [Tooltip("Grasps that landed on a target other than the one the sequence called for.")]
    public int orderErrors = 0;
    [Tooltip("Grasps the participant made that the app DECLINED to count - a pass-through under "
           + "the minimum gap, or a select event on a target that was not the closest. Each one "
           + "is written to the CSV as its own row, with grasp_outcome saying which it was.")]
    public int ignoredGrasps = 0;
    public string currentFilePath = "";

    // Found automatically - no Inspector wiring needed.
    private TrialCounter _trialCounter;
    private ExperimenterMode _experimenterMode;
    private HandVisibilityToggle _handVisibility;
    private TaskSequencer _taskSequencer;
    private SessionRunner _sessionRunner;

    private StreamWriter _writer;
    private int _graspInTrial = 0;
    private int _trialNumber = 1;
    private float _trialSpawnTime = -1f;
    private float _firstGraspTime = -1f;
    private bool _trialInProgress = false;
    private bool _numberingWarned = false;
    private float _lastGraspTime = -1f;

    private const string Header =
        "participant_id,visit,timestamp,block,condition,task,trial_number,grasp_in_trial,target_position_number," +
        "time_since_spawn_s,time_since_first_grasp_s,endpoint_error_m," +
        "target_x,target_y,target_z,fingertip_x,fingertip_y,fingertip_z," +
        "hand_used,hands_visible,simulated,grid_width_m,grid_height_m,grid_distance_m," +
        "expected_position_number,order_correct,home_verified,false_start,foreperiod_s,reaction_time_s," +
        "reach_time_s," +

        // Appended rather than slotted in next to the columns they belong with, on purpose:
        // inserting a column shifts every one after it, and anything already written against
        // the old layout - a script, a half-finished analysis - then reads the wrong field
        // without failing. Appending cannot break a reader that does not know about them.
        //
        // grasp_outcome        'recorded' for a counted grasp. Otherwise a grasp the
        //                      participant made and the app declined: 'passthrough_under_min_gap'
        //                      (too soon after the previous one to be a real reach) or
        //                      'not_closest_target'. Filter to grasp_outcome == 'recorded' for
        //                      any analysis of performance; count the others to report how often
        //                      the app intervened. Declined rows do not consume a grasp, so
        //                      grasp_in_trial on them is the grasp they were ATTEMPTING.
        // home_hold_s          how long the hand had been continuously on the marker when the
        //                      foreperiod began. A session where this sits near the required
        //                      dwell is one where the gate is only just being satisfied.
        // foreperiod_restarts  how many times the foreperiod restarted because the hand came off
        //                      the marker. A trial with restarts is valid; many of them is a
        //                      procedural problem, and one invisible in the data until now.
        "grasp_outcome,home_hold_s,foreperiod_restarts";

    void Start()
    {
        _trialCounter = GetComponent<TrialCounter>();
        _experimenterMode = GetComponent<ExperimenterMode>();
        _handVisibility = GetComponent<HandVisibilityToggle>();
        _taskSequencer = GetComponent<TaskSequencer>();
        _sessionRunner = GetComponent<SessionRunner>();

        if (ControlManager.Singleton != null)
        {
            ControlManager.Singleton.OnTargetCapturedDetailed -= HandleCapture;
            ControlManager.Singleton.OnTargetCapturedDetailed += HandleCapture;
            ControlManager.Singleton.OnTargetSpawned -= HandleSpawn;
            ControlManager.Singleton.OnTargetSpawned += HandleSpawn;
            ControlManager.Singleton.OnGraspIgnored -= HandleIgnoredGrasp;
            ControlManager.Singleton.OnGraspIgnored += HandleIgnoredGrasp;
        }
        else
        {
            Debug.LogError("DataRecorder: ControlManager not found - NOTHING WILL BE RECORDED.");
        }

        Debug.Log($"DataRecorder ready. Files will be saved to: {Application.persistentDataPath}");
    }

    void OnDisable()
    {
        if (ControlManager.Singleton != null)
        {
            ControlManager.Singleton.OnTargetCapturedDetailed -= HandleCapture;
            ControlManager.Singleton.OnTargetSpawned -= HandleSpawn;
            ControlManager.Singleton.OnGraspIgnored -= HandleIgnoredGrasp;
        }
        CloseFile();
    }

    void OnApplicationQuit()
    {
        CloseFile();
    }

    // A trial's stopwatch starts at the first spawn after the previous trial finished.
    private void HandleSpawn()
    {
        if (_trialInProgress) return;
        _trialInProgress = true;
        _trialSpawnTime = Time.time;
        _firstGraspTime = -1f;
        _lastGraspTime = -1f;
        _graspInTrial = 0;
    }

    private void HandleCapture(ControlManager.CaptureData d)
    {
        // Don't record anything before the experimenter has started the session.
        if (_experimenterMode != null && !_experimenterMode.IsExperimentStarted()) return;

        if (!_trialInProgress)
        {
            // No spawn was seen (e.g. targets were already on screen). Start the trial here
            // so the grasp is still recorded; time_since_spawn will be blank for this trial.
            _trialInProgress = true;
            _trialSpawnTime = -1f;
            _lastGraspTime = -1f;
            _graspInTrial = 0;
        }

        _graspInTrial++;
        if (_graspInTrial == 1) _firstGraspTime = Time.time;

        // The time for THIS reach alone, measured from whatever the hand was doing before it:
        // cue onset for the first grasp of a trial, the previous grasp for the rest.
        //
        // This is the column that separates reading the cue from executing the movement, and
        // it is the reason the cue panel can stay as it is. The participant reads the diagram
        // once, before the first reach. So grasp 1 contains cue reading, planning and moving,
        // while grasps 2 and 3 contain moving and any re-planning but NO reading - it is
        // already done. If a blocked-versus-random difference appears only on grasp 1 it could
        // be either reading or planning; if it also appears on grasps 2 and 3 then reading
        // cannot be the explanation, because the reading had finished before those reaches
        // began.
        //
        // Shea & Morgan had one total time and one reaction time taken at a switch, so they
        // could not localise the effect within the movement. This can.
        float reachRef = _graspInTrial == 1 ? _trialSpawnTime : _lastGraspTime;
        string reachTime = reachRef >= 0f
            ? (Time.time - reachRef).ToString("F4", CultureInfo.InvariantCulture) : "";
        _lastGraspTime = Time.time;

        string sinceSpawn = _trialSpawnTime >= 0f
            ? (Time.time - _trialSpawnTime).ToString("F4", CultureInfo.InvariantCulture) : "";
        string sinceFirstGrasp = _firstGraspTime >= 0f
            ? (Time.time - _firstGraspTime).ToString("F4", CultureInfo.InvariantCulture) : "";

        // Which target the sequence called for at this point in the trial. Recorded next to
        // the one actually grasped, so a wrong-order trial is visible in the CSV instead of
        // only being findable by opening the plan file and comparing by eye.
        //
        // This is not a cosmetic check. The three trained tasks are matched at 101.02 cm of
        // total reach precisely so that task difficulty cannot be mistaken for a
        // practice-schedule effect, and swapping two targets breaks that: Task B done as
        // 2-4-9 instead of 2-9-4 is 85.87 cm, 15% short. A trial like that is not a harder or
        // easier version of Task B, it is a different task, and it has to be excluded.
        int expected = ExpectedPositionForThisGrasp(_graspInTrial);
        string orderCorrect = "";
        if (expected > 0 && d.targetIndex > 0)
        {
            bool ok = d.targetIndex == expected;
            orderCorrect = ok ? "TRUE" : "FALSE";
            if (!ok)
            {
                orderErrors++;
                Debug.LogWarning($"ORDER ERROR trial {_trialNumber}, grasp {_graspInTrial}: "
                               + $"grasped {d.targetIndex}, the sequence called for {expected}. "
                               + "Recorded as an error and the trial continues.");
            }
        }

        WriteRow(d, true, _graspInTrial, sinceSpawn, sinceFirstGrasp, expected, orderCorrect,
                 reachTime, "recorded");

        // CapturesRequired(), not capturesPerTrial.
        //
        // capturesPerTrial is a fixed Inspector value of 3. CapturesRequired() asks the
        // sequencer how many positions the CURRENT task actually has - three for the trained
        // tasks, five for the greater-complexity transfer task.
        //
        // Reading the fixed field here made DataRecorder disagree with TrialCounter, which
        // uses the method. On a five-target trial the counter correctly waited for five
        // grasps while the recorder started a new trial every three, so one real trial was
        // written out as chunks of three under invented trial numbers, with the sequence
        // running across the boundaries. Three transfer trials became five fake ones, and
        // time_since_spawn_s came out blank for the invented ones.
        //
        // Nothing in the app failed and no grasp was lost - the data was simply relabelled
        // into a shape that never happened. Two components answering the same question in
        // two different ways is how that happens.
        int perTrial = _trialCounter != null ? _trialCounter.CapturesRequired() : 3;
        if (_graspInTrial >= perTrial)
        {
            trialsCompleted++;
            _trialNumber++;
            _graspInTrial = 0;
            _trialInProgress = false;
            _trialSpawnTime = -1f;
            _firstGraspTime = -1f;
            _lastGraspTime = -1f;
        }
    }

    // A grasp the participant made that the app declined to count.
    //
    // Declining is the right call - a pass-through consumes a target the sequence still needs,
    // and a stray select event on a target the hand was not reaching for is not a grasp. But
    // until now a declined grasp existed only as a console warning, which means it was not in
    // the data at all: an analysis would report a clean trial where the participant had in fact
    // touched a target and been refused. In a study whose dependent variables include error
    // rate, the grasps the participant made are the behaviour, and whether the app counted them
    // is a separate fact that belongs in its own column.
    //
    // These rows do NOT advance grasp_in_trial and do not complete a trial. They are
    // deliberately easy to drop: filter grasp_outcome == 'recorded'.
    private void HandleIgnoredGrasp(int positionNumber, string reason)
    {
        if (_experimenterMode != null && !_experimenterMode.IsExperimentStarted()) return;

        // No trial underway means this is not part of a trial - targets left on screen between
        // trials, say. Starting one here would invent a trial out of a rejected grasp.
        if (!_trialInProgress) return;

        ignoredGrasps++;

        int attempting = _graspInTrial + 1;
        int expected = ExpectedPositionForThisGrasp(attempting);
        string orderCorrect = "";
        if (expected > 0 && positionNumber > 0)
            orderCorrect = positionNumber == expected ? "TRUE" : "FALSE";

        string sinceSpawn = _trialSpawnTime >= 0f
            ? (Time.time - _trialSpawnTime).ToString("F4", CultureInfo.InvariantCulture) : "";
        string sinceFirstGrasp = _firstGraspTime >= 0f
            ? (Time.time - _firstGraspTime).ToString("F4", CultureInfo.InvariantCulture) : "";

        // Position number only. There is no CaptureData for a grasp that was never registered,
        // so there is no fingertip position and no endpoint error - and writing a zero would be
        // a measurement that never happened.
        var d = new ControlManager.CaptureData { targetIndex = positionNumber };

        WriteRow(d, false, attempting, sinceSpawn, sinceFirstGrasp, expected, orderCorrect,
                 "", reason);
    }

    private void WriteRow(ControlManager.CaptureData d, bool haveGeometry, int graspNumber,
                          string sinceSpawn, string sinceFirstGrasp,
                          int expected, string orderCorrect, string reachTime, string outcome)
    {
        if (_writer == null && !OpenFile()) return;

        string condition = _trialCounter != null ? _trialCounter.practiceSchedule.ToString() : "";
        string task = _trialCounter != null ? _trialCounter.currentTask.ToString() : "";
        string handsVisible = _handVisibility != null ? _handVisibility.handsVisible.ToString() : "";

        // The three trial-start values are only written if SessionRunner says they belong to
        // THIS trial. They are single "last trial" values, and a reaction time can arrive after
        // the trial that produced it has already been written out - in which case it would
        // otherwise be recorded against the following trial, which is what put a 4.2 s reaction
        // time on the first grasp of a trial that had only been running one second. A blank is
        // a missing measurement; a number under the wrong trial is a wrong measurement, and the
        // second one cannot be spotted later.
        bool startFactsMatch = _sessionRunner != null
                            && _sessionRunner.lastTrialStartNumber == _trialNumber;
        if (!startFactsMatch && !_numberingWarned && _sessionRunner != null
            && _sessionRunner.lastTrialStartNumber > 0)
        {
            _numberingWarned = true;
            Debug.LogWarning($"DataRecorder is writing trial {_trialNumber} while SessionRunner "
                           + $"started trial {_sessionRunner.lastTrialStartNumber}. The two are "
                           + "counting differently, so home_verified, foreperiod_s and "
                           + "reaction_time_s are being left blank rather than guessed at.");
        }

        string row = string.Join(",", new string[] {
            Clean(participantId),
            Clean(visitLabel),
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            _sessionRunner != null ? Clean(_sessionRunner.CurrentBlockLabel()) : "",
            condition,
            task,
            _trialNumber.ToString(CultureInfo.InvariantCulture),
            graspNumber.ToString(CultureInfo.InvariantCulture),
            d.targetIndex > 0 ? d.targetIndex.ToString(CultureInfo.InvariantCulture) : "",
            sinceSpawn,
            sinceFirstGrasp,

            // Blank, not zero, when there is no measurement. A declined grasp was never
            // registered, so it has no fingertip position and no endpoint error; a zero there
            // would read as a perfect grasp.
            haveGeometry ? d.endpointErrorMeters.ToString("F5", CultureInfo.InvariantCulture) : "",
            haveGeometry ? F(d.targetPosition.x) : "",
            haveGeometry ? F(d.targetPosition.y) : "",
            haveGeometry ? F(d.targetPosition.z) : "",
            haveGeometry ? F(d.fingerTipPosition.x) : "",
            haveGeometry ? F(d.fingerTipPosition.y) : "",
            haveGeometry ? F(d.fingerTipPosition.z) : "",
            haveGeometry ? (d.usedLeftHand ? "Left" : "Right") : "",
            handsVisible,
            d.isSimulated ? "TRUE" : "FALSE",
            _taskSequencer != null ? F(_taskSequencer.gridWidth) : "",
            _taskSequencer != null ? F(_taskSequencer.gridHeight) : "",
            _taskSequencer != null ? F(_taskSequencer.gridDistance) : "",

            // How the trial STARTED, read from SessionRunner. These three were already
            // being computed and printed to the log, but a value that only exists in the
            // log cannot be used at analysis time - you would have to sit with a text file
            // and a spreadsheet side by side, matching trials up by hand, for every
            // participant. In the CSV they are just columns you can filter on.
            //
            // false_start     TRUE means the hand was on the marker when the foreperiod began but
            //                 had left before the cue. The reach did not start from home, so its
            //                 distance is not the intended one - exclude, or repeat the trial.
            // home_verified   FALSE means the hand was not confirmed on the home marker when
            //                 the cue fired. Reach distance for that trial is unknown, so the
            //                 trial has to be excluded - which is only possible if it is
            //                 recorded here.
            // foreperiod_s    which of the three foreperiods this trial drew. Needed to show
            //                 the foreperiod really was unpredictable, and to check that
            //                 reaction time does not depend on it.
            // reaction_time_s cue onset to the hand leaving home. Blank when it could not be
            //                 measured. This is the measure the effect was largest on in
            //                 Shea & Morgan, so it should not live only in a log file.
            //
            // The values are read at the moment the row is written, which is inside the
            // trial they belong to: SessionRunner clears all three when the NEXT trial arms,
            // and every grasp of this trial happens before that.
            expected > 0 ? expected.ToString(CultureInfo.InvariantCulture) : "",
            orderCorrect,
            startFactsMatch ? (_sessionRunner.lastTrialHomeVerified ? "TRUE" : "FALSE") : "",
            startFactsMatch ? (_sessionRunner.lastTrialFalseStart ? "TRUE" : "FALSE") : "",
            startFactsMatch ? Secs(_sessionRunner.lastForeperiod) : "",
            startFactsMatch ? Secs(_sessionRunner.lastReactionTime) : "",
            reachTime,

            outcome,
            startFactsMatch ? Secs(_sessionRunner.lastTrialHomeHoldSeconds) : "",
            startFactsMatch ? _sessionRunner.lastTrialForeperiodRestarts
                                  .ToString(CultureInfo.InvariantCulture) : ""
        });

        _writer.WriteLine(row);   // AutoFlush is on, so this reaches disk straight away
        rowsWritten++;
    }

    // The position number the current task's sequence calls for at a given point in the trial,
    // or 0 if it cannot be determined. graspNumber is 1-based, so grasp 1 looks at element 0.
    //
    // Takes the number as an argument rather than reading _graspInTrial, because a declined
    // grasp has to ask about the grasp it was ATTEMPTING - which is one past the last counted
    // one, and _graspInTrial has not moved.
    private int ExpectedPositionForThisGrasp(int graspNumber)
    {
        if (_taskSequencer == null) return 0;
        var seq = _taskSequencer.CurrentSequencePositions();
        if (seq == null) return 0;
        int i = graspNumber - 1;
        return (i >= 0 && i < seq.Count) ? seq[i] : 0;
    }

    private static string F(float v) => v.ToString("F5", CultureInfo.InvariantCulture);

    // A time in seconds, or blank when it was never measured. SessionRunner uses -1 for
    // "no value"; writing -1 into the CSV would let a not-measured trial be averaged in as
    // if it were a real minus-one-second reaction time.
    private static string Secs(float v) =>
        v >= 0f ? v.ToString("F4", CultureInfo.InvariantCulture) : "";

    // Commas would break the CSV into the wrong columns.
    private static string Clean(string s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace(",", " ").Replace("\n", " ").Trim();

    private bool OpenFile()
    {
        try
        {
            string id = string.IsNullOrWhiteSpace(participantId) ? "NOID" : Clean(participantId);
            string name = $"NPRlab_{id}_{Clean(visitLabel)}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            string folder = Application.persistentDataPath;
#if UNITY_EDITOR
            // In the editor, save inside the project folder so the file is easy to find
            // and open. On the headset this block does not exist and the standard
            // persistent data path is used instead.
            folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "RecordedData"));
            Directory.CreateDirectory(folder);
#endif
            currentFilePath = Path.Combine(folder, name);

            _writer = new StreamWriter(currentFilePath, append: true) { AutoFlush = true };
            _writer.WriteLine(Header);

            Debug.Log($"DataRecorder: recording to {currentFilePath}");
            if (string.IsNullOrWhiteSpace(participantId))
            {
                Debug.LogWarning("DataRecorder: no participant ID was entered - this file is named NOID. " +
                                 "Rename it after the session so you know whose data it is.");
            }
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"DataRecorder: COULD NOT CREATE THE DATA FILE - nothing is being saved! {e.Message}");
            return false;
        }
    }

    private void CloseFile()
    {
        if (_writer == null) return;
        _writer.Flush();
        _writer.Dispose();
        _writer = null;
        Debug.Log($"DataRecorder: file closed. {rowsWritten} rows written to {currentFilePath}");
    }

    void OnGUI()
    {
        bool started = _experimenterMode == null || _experimenterMode.IsExperimentStarted();

        if (!started)
        {
            // Session setup, shown only before Start Experiment is pressed.
            GUI.Label(new Rect(10, 215, 120, 25), "Participant ID:");
            participantId = GUI.TextField(new Rect(130, 215, 120, 25), participantId);

            GUI.Label(new Rect(10, 245, 120, 25), "Visit:");
            visitLabel = GUI.TextField(new Rect(130, 245, 120, 25), visitLabel);
        }
        else
        {
            string who = string.IsNullOrWhiteSpace(participantId) ? "NO ID SET" : participantId;
            GUI.Label(new Rect(10, 215, 600, 25),
                      $"Recording: {who} / {visitLabel}  -  {rowsWritten} rows, {trialsCompleted} trials saved"
                      + (orderErrors > 0 ? $"  -  {orderErrors} ORDER ERRORS" : "")
                      + (ignoredGrasps > 0 ? $"  -  {ignoredGrasps} declined grasps" : ""));
            if (!string.IsNullOrEmpty(currentFilePath))
            {
                GUI.Label(new Rect(10, 238, 900, 25), $"File: {currentFilePath}");
            }
        }
    }
}
