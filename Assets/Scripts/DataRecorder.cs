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

    private const string Header =
        "participant_id,visit,timestamp,block,condition,task,trial_number,grasp_in_trial,target_position_number," +
        "time_since_spawn_s,time_since_first_grasp_s,endpoint_error_m," +
        "target_x,target_y,target_z,fingertip_x,fingertip_y,fingertip_z," +
        "hand_used,hands_visible,simulated,grid_width_m,grid_height_m,grid_distance_m," +
        "expected_position_number,order_correct,home_verified,false_start,foreperiod_s,reaction_time_s";

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
            _graspInTrial = 0;
        }

        _graspInTrial++;
        if (_graspInTrial == 1) _firstGraspTime = Time.time;

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
        int expected = ExpectedPositionForThisGrasp();
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

        WriteRow(d, sinceSpawn, sinceFirstGrasp, expected, orderCorrect);

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
        }
    }

    private void WriteRow(ControlManager.CaptureData d, string sinceSpawn, string sinceFirstGrasp,
                          int expected, string orderCorrect)
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
            _graspInTrial.ToString(CultureInfo.InvariantCulture),
            d.targetIndex > 0 ? d.targetIndex.ToString(CultureInfo.InvariantCulture) : "",
            sinceSpawn,
            sinceFirstGrasp,
            d.endpointErrorMeters.ToString("F5", CultureInfo.InvariantCulture),
            F(d.targetPosition.x), F(d.targetPosition.y), F(d.targetPosition.z),
            F(d.fingerTipPosition.x), F(d.fingerTipPosition.y), F(d.fingerTipPosition.z),
            d.usedLeftHand ? "Left" : "Right",
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
            startFactsMatch ? Secs(_sessionRunner.lastReactionTime) : ""
        });

        _writer.WriteLine(row);   // AutoFlush is on, so this reaches disk straight away
        rowsWritten++;
    }

    // The position number the current task's sequence calls for at this point in the trial,
    // or 0 if it cannot be determined. _graspInTrial has already been incremented when this
    // is called, so grasp 1 looks at element 0.
    private int ExpectedPositionForThisGrasp()
    {
        if (_taskSequencer == null) return 0;
        var seq = _taskSequencer.CurrentSequencePositions();
        if (seq == null) return 0;
        int i = _graspInTrial - 1;
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
                      + (orderErrors > 0 ? $"  -  {orderErrors} ORDER ERRORS" : ""));
            if (!string.IsNullOrEmpty(currentFilePath))
            {
                GUI.Label(new Rect(10, 238, 900, 25), $"File: {currentFilePath}");
            }
        }
    }
}
