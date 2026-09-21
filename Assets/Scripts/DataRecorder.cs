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

    private const string Header =
        "participant_id,visit,timestamp,block,condition,task,trial_number,grasp_in_trial,target_position_number," +
        "time_since_spawn_s,time_since_first_grasp_s,endpoint_error_m," +
        "target_x,target_y,target_z,fingertip_x,fingertip_y,fingertip_z," +
        "hand_used,hands_visible,simulated,grid_width_m,grid_height_m,grid_distance_m";

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

        WriteRow(d, sinceSpawn, sinceFirstGrasp);

        int perTrial = _trialCounter != null ? _trialCounter.capturesPerTrial : 3;
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

    private void WriteRow(ControlManager.CaptureData d, string sinceSpawn, string sinceFirstGrasp)
    {
        if (_writer == null && !OpenFile()) return;

        string condition = _trialCounter != null ? _trialCounter.practiceSchedule.ToString() : "";
        string task = _trialCounter != null ? _trialCounter.currentTask.ToString() : "";
        string handsVisible = _handVisibility != null ? _handVisibility.handsVisible.ToString() : "";

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
            _taskSequencer != null ? F(_taskSequencer.gridDistance) : ""
        });

        _writer.WriteLine(row);   // AutoFlush is on, so this reaches disk straight away
        rowsWritten++;
    }

    private static string F(float v) => v.ToString("F5", CultureInfo.InvariantCulture);

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
                      $"Recording: {who} / {visitLabel}  -  {rowsWritten} rows, {trialsCompleted} trials saved");
            if (!string.IsNullOrEmpty(currentFilePath))
            {
                GUI.Label(new Rect(10, 238, 900, 25), $"File: {currentFilePath}");
            }
        }
    }
}
