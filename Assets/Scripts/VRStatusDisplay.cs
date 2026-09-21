using System.Collections.Generic;
using TMPro;
using UnityEngine;

// Shows session state and recent log messages as 3D text inside the headset.
//
// WHY THIS EXISTS:
// Unity's OnGUI text does not render in a VR build, so every other readout in this project
// is invisible in the headset. That makes any problem undiagnosable: you put it on, nothing
// happens, and there is no error to read. This puts the information where it can be seen.
//
// It also captures Debug.Log / LogWarning / LogError through Application.logMessageReceived,
// so errors appear in front of you instead of vanishing into logcat.
//
// Head-locked by default so it is always in view while testing. For a real participant
// session, turn showInHeadset off or move it out of their field of view.
//
// Attach to the same GameObject as ControlManager.
public class VRStatusDisplay : MonoBehaviour
{
    [Header("Display")]
    public bool showInHeadset = true;
    [Tooltip("Metres in front of the eyes.")]
    public float distance = 1.0f;
    [Tooltip("Metres above eye level. Positive puts it above the targets.")]
    public float heightOffset = 0.40f;
    public float textSize = 0.5f;

    [Header("Log capture")]
    public bool showLogMessages = true;
    [Range(1, 12)] public int logLines = 4;

    private TextMeshPro _text;
    private Transform _head;
    private readonly List<string> _log = new List<string>();

    private SessionRunner _sessionRunner;
    private TrialCounter _trialCounter;
    private TaskSequencer _taskSequencer;
    private DataRecorder _dataRecorder;
    private ExperimenterControls _controls;
    private ExperimenterMode _mode;

    void Start()
    {
        _sessionRunner = GetComponent<SessionRunner>();
        _trialCounter  = GetComponent<TrialCounter>();
        _taskSequencer = GetComponent<TaskSequencer>();
        _dataRecorder  = GetComponent<DataRecorder>();
        _controls      = GetComponent<ExperimenterControls>();
        _mode          = GetComponent<ExperimenterMode>();

        ResolveHead();
        CreateText();

        if (showLogMessages) Application.logMessageReceived += OnLog;

        Debug.Log($"VRStatusDisplay ready. head={(_head != null ? _head.name : "NONE")} " +
                  $"font={(_text != null && _text.font != null ? _text.font.name : "NONE")}");
    }

    void OnDestroy()
    {
        Application.logMessageReceived -= OnLog;
    }

    // The head anchor may not exist yet when Start runs (OVRCameraRig builds its anchors
    // in its own Update), so this is retried every frame until it succeeds. Without a head
    // the text was left sitting at world origin - usually behind or below the participant,
    // which looks exactly like "nothing is showing".
    private void ResolveHead()
    {
        var rig = FindAnyObjectByType<OVRCameraRig>();
        if (rig != null && rig.centerEyeAnchor != null) { _head = rig.centerEyeAnchor; return; }
        if (Camera.main != null) _head = Camera.main.transform;
    }

    private void CreateText()
    {
        GameObject go = new GameObject("VRStatusText");
        _text = go.AddComponent<TextMeshPro>();
        // Centred, in a panel about a metre wide.
        //
        // This used to be TopLeft alignment in a 4 x 2 METRE rectangle. The rectangle is
        // centred on the head, so left-aligned text began two metres to the left - roughly
        // 63 degrees off-axis at one metre's distance, well outside the headset's field of
        // view. Only the right-hand end of each line was visible, which looked like the
        // display was broken rather than mis-sized.
        //
        // Centre alignment keeps every line in front of the wearer regardless of how long
        // it is, and word wrapping keeps a long message inside the panel instead of running
        // off the edge.
        _text.alignment = TextAlignmentOptions.Top;
        _text.fontSize = textSize;
        _text.color = Color.white;
        _text.enableWordWrapping = true;
        _text.rectTransform.sizeDelta = new Vector2(1.2f, 0.9f);

        if (_text.font == null && TMP_Settings.defaultFontAsset != null)
            _text.font = TMP_Settings.defaultFontAsset;
        if (_text.font == null)
            _text.font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        if (_text.font == null)
            Debug.LogError("VRStatusDisplay: no TextMeshPro font asset - the text will not render. " +
                           "Window > TextMeshPro > Import TMP Essential Resources.");
    }

    // OVRManager logs OnApplicationFocus / OnApplicationPause every time the headset is
    // donned, doffed, or loses focus - several lines a minute. Left unfiltered they push
    // every useful message off the display within seconds, which is exactly what made the
    // readout useless. Errors are never filtered.
    private static readonly string[] NoiseFilters =
    {
        "OnApplicationFocus", "OnApplicationPause", "OnApplicationQuit",
        "OVRManager", "OVRPlugin",
    };

    private void OnLog(string message, string stackTrace, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception)
        {
            foreach (string noise in NoiseFilters)
                if (message.Contains(noise)) return;
        }

        string tag = type == LogType.Error || type == LogType.Exception ? "<color=red>ERR</color>"
                   : type == LogType.Warning ? "<color=yellow>WRN</color>"
                   : "   ";
        _log.Add($"{tag} {message}");
        while (_log.Count > logLines) _log.RemoveAt(0);
    }

    void LateUpdate()
    {
        if (_text == null) return;

        _text.gameObject.SetActive(showInHeadset);
        if (!showInHeadset) return;

        if (_head == null) ResolveHead();

        // Keep it in front of the eyes, upright, regardless of head tilt.
        if (_head != null)
        {
            Vector3 forward = _head.forward; forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
            forward.Normalize();

            _text.transform.position = _head.position + forward * distance + Vector3.up * heightOffset;
            _text.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }

        _text.text = BuildStatus();
    }

    private string BuildStatus()
    {
        var s = new System.Text.StringBuilder();

        string who = _controls != null ? _controls.ParticipantId() : "?";
        int visit = _controls != null ? _controls.visitNumber : 0;
        bool started = _mode != null && _mode.IsExperimentStarted();

        s.AppendLine($"<b>{who}   Visit {visit}</b>   {(started ? "RUNNING" : "not started - press X")}");

        if (_sessionRunner != null)
        {
            if (_sessionRunner.sessionPlanned)
            {
                s.AppendLine($"Trial {_sessionRunner.trialIndex + 1} / {_sessionRunner.totalTrials}   " +
                             $"[{_sessionRunner.currentBlock}]   " +
                             $"{(_trialCounter != null ? _trialCounter.currentTask.ToString() : "")}   " +
                             $"{(_sessionRunner.waitingToStartTrial ? "READY - press A" : "running")}");
            }
            else
            {
                s.AppendLine("<color=yellow>session not planned yet</color>");
            }
        }
        else s.AppendLine("<color=red>NO SessionRunner component</color>");

        if (_taskSequencer != null && !string.IsNullOrEmpty(_taskSequencer.currentSequence))
            s.AppendLine($"<b>SAY:  {_taskSequencer.currentSequence}</b>");

        if (_dataRecorder != null)
            s.AppendLine($"rows {_dataRecorder.rowsWritten}   trials {_dataRecorder.trialsCompleted}");

        if (showLogMessages && _log.Count > 0)
        {
            s.AppendLine("---");
            foreach (string line in _log) s.AppendLine(line);
        }

        return s.ToString();
    }
}
