using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// Writes one row per rendered frame for the whole of each trial, to a second CSV
/// alongside the per-grasp file DataRecorder produces.
///
/// WHY A SECOND FILE
///   The per-grasp file records one position per grasp: where the fingertip was at the
///   instant HandGrabInteractable fired. That instant is not the end of the reach. On the
///   2 October pilot the hand was still travelling at a median 499 mm/s when the grab
///   registered, and the recorded fingertip sat a median 4.4 cm short of the target. One
///   sample per grasp cannot tell you that, and cannot be corrected after the fact.
///
///   A frame-by-frame trace can. With the whole approach recorded, the endpoint can be
///   taken at the moment of closest approach rather than at the trigger, and the correction
///   is made in analysis, where the window and the thresholds can be changed and the data
///   re-derived. Doing the same correction in the game would bake one set of thresholds
///   into a build and make the choice unrepeatable.
///
///   It also makes two measurements possible that the per-grasp file cannot support at all:
///     - reaction time by a speed criterion (5% of peak), which needs the speed profile of
///       the whole reach, and therefore cannot be computed in real time - the peak is not
///       known until the reach is over
///     - hand posture through the reach, for the hidden-hand condition in Visit 3
///
/// WHAT IT COSTS
///   About 15 columns at the frame rate, so roughly 10 KB per second of trial time, and a
///   few tens of MB for a full session. Rows are buffered in memory and written out at the
///   end of each trial rather than per frame, so no disk write happens mid-reach.
///
/// SETUP
///   Attach to the same GameObject as ControlManager and DataRecorder. Nothing to wire in
///   the Inspector: fingertips come from ControlManager, grasps from its capture event, and
///   the only thing it needs from SessionRunner is a call at cue onset.
///
///   SessionRunner calls OnCueOnset(trialNumber) at the instant the go signal fires. That is
///   the only required call site. Logging stops on its own once the trial's grasps are done.
///
/// JOINING TO THE PER-GRASP FILE
///   On (trial_number, grasp_in_trial). trial_number here is SessionRunner's, which is the
///   number the main CSV also carries whenever the two agree - DataRecorder already warns
///   when they do not.
///
/// Zara / NPRLab
/// </summary>
public class FingertipFrameLogger : MonoBehaviour
{
    [Header("Recording")]
    [Tooltip("Turn off to stop writing the frame file without removing the component.")]
    public bool recordFrames = true;

    [Tooltip("Keep logging for this long after the last grasp of a trial, so the end of the "
           + "final reach - including the hand settling and withdrawing - is in the trace. "
           + "The endpoint at closest approach can land after the grab fired, so a window "
           + "that closes at the grab would cut off the very samples this file exists for.")]
    public float secondsAfterLastGrasp = 1.0f;

    [Tooltip("Hard stop, measured from cue onset, in case a trial never completes its grasps. "
           + "Without it a lost trial would log until the next cue.")]
    public float maxTrialSeconds = 30f;

    [Tooltip("Extra seconds allowed on the trial timeout when the window opens at the warning "
           + "tone rather than the cue, to cover the foreperiod that runs before it. A safety "
           + "margin only - the window still closes on the last grasp as usual.")]
    public float preCueBudgetSeconds = 10f;

    // False between the warning tone and the cue, while t_since_cue_s is not yet meaningful.
    // Those rows end up with a negative time once the cue restamps the origin.
    private bool _cueStamped = false;

    [Header("Status (read-only)")]
    public int framesWritten = 0;
    public int trialsLogged = 0;
    public string currentFilePath = "";

    private DataRecorder _dataRecorder;
    private TrialCounter _trialCounter;
    private StreamWriter _writer;

    private readonly List<string> _buffer = new List<string>(8192);
    private const int MaxBufferedRows = 4096;

    private bool _logging = false;
    private int _trialNumber = 0;
    private int _trialAttempt = 1;
    private int _lastCueTrial = -1;
    private int _graspInTrial = 0;
    private float _cueOnsetTime = -1f;
    private float _stopAt = -1f;

    // A grasp arrives on the event callback, part way through a frame. It is held here and
    // written onto that frame's row, so the mark sits on a row that also carries the
    // fingertip position - rather than becoming a row of its own with no kinematics on it.
    private bool _graspPending = false;
    private int _pendingTargetIndex = -1;
    private Vector3 _pendingTargetPosition = Vector3.zero;

    private const string Header =
        "trial_number,grasp_in_trial,frame,t_session_s,t_since_cue_s," +
        "left_tip_x,left_tip_y,left_tip_z,left_confidence,left_sample_t," +
        "right_tip_x,right_tip_y,right_tip_z,right_confidence,right_sample_t," +
        "event,event_target_position_number,event_target_x,event_target_y,event_target_z," +
        "trial_attempt";

    // grasp_in_trial    counts grasps seen since cue onset. On a frame carrying a grasp mark
    //                   it is the number of THAT grasp; on ordinary frames it is the number
    //                   of the last grasp, so 0 means the first reach is still in progress.
    // confidence        1 high, 0 low, -1 the hand was not tracked on that frame. Frames at
    //                   -1 have no usable position and must be dropped before any speed is
    //                   computed from them, or the gap reads as a jump.
    // sample_t          the timestamp OVRPlugin gives the hand sample itself, in its own
    //                   clock. Hand tracking updates at about 60 Hz while rendering runs at
    //                   72 or more, so consecutive rows can carry the SAME tracking sample.
    //                   Those rows are not new measurements: differencing position across
    //                   them gives a speed of zero and drags any estimate down. Drop rows
    //                   where sample_t is unchanged from the row before, per hand, before
    //                   computing speed. -1 when the hand was not tracked.
    // event             blank on an ordinary frame, 'cue' on the frame the go signal fired,
    //                   'grasp' on a frame a grasp registered.
    // event_target_*    the grasped target's grid number and centre, written only on a
    //                   'grasp' row. This is what closest approach is measured against.

    private bool _wired = false;

    void Start()
    {
        EnsureWired();
    }

    // Idempotent, and called from OnCueOnset as well as Start, because this component may be
    // added at runtime by SessionRunner when nobody attached it in the scene. A component
    // added that way has not had Start run by the time the first cue fires, so without this
    // the first trial's grasps would go unmarked - the one trial whose absence is easiest to
    // miss, because the file exists and looks healthy.
    private void EnsureWired()
    {
        if (_wired) return;

        if (_dataRecorder == null) _dataRecorder = GetComponent<DataRecorder>();
        if (_trialCounter == null) _trialCounter = GetComponent<TrialCounter>();

        if (ControlManager.Singleton != null)
        {
            ControlManager.Singleton.OnTargetCapturedDetailed -= HandleCapture;
            ControlManager.Singleton.OnTargetCapturedDetailed += HandleCapture;
            _wired = true;
        }
        else
        {
            Debug.LogError("FingertipFrameLogger: ControlManager not found - grasps will not be "
                         + "marked in the frame file. The per-grasp file is unaffected.");
        }
    }

    void OnDisable()
    {
        if (ControlManager.Singleton != null)
            ControlManager.Singleton.OnTargetCapturedDetailed -= HandleCapture;
        Flush();
        CloseFile();
    }

    void OnApplicationQuit()
    {
        Flush();
        CloseFile();
    }

    /// <summary>
    /// Called by SessionRunner at the WARNING TONE, before the foreperiod runs. Opens the
    /// frame window early so the trace covers the time before the cue as well as after it.
    ///
    /// WHY THIS EXISTS. The window used to open at cue onset, which made the first logged
    /// sample and the cue the same moment, so anything the hand did before the go signal was
    /// invisible. Two things followed from that on the 8 October pilot. Onset detection walks
    /// back from peak speed to the last sample below 5% of peak, and in trials where the hand
    /// was already moving it had nowhere to walk back to: it returned the first sample, giving
    /// a reaction time of exactly 0 s, and movement time then absorbed the whole trial. And
    /// seven of 54 acquisition trials had the hand already travelling faster than 100 mm/s at
    /// the cue, which could only be inferred from that first sample rather than seen.
    ///
    /// With the window open through the foreperiod, an early start is visible directly: a
    /// trial whose speed rises before the cue anticipated the go signal, whatever reaction
    /// time the onset search produces for it.
    ///
    /// BUT NOT VIA t_since_cue_s, WHICH NEVER GOES NEGATIVE. This comment used to say it
    /// did. It does not, and cannot: the cue time is unknown while the pre-cue rows are
    /// being written, so they are stamped from the warning, and the column is simply
    /// zeroed a second time at the cue (see the 'warning' branch in LateUpdate). Within
    /// one trial it ramps up, returns to zero, and ramps again.
    ///
    /// Analysis must therefore locate the cue by its ROW, not by a timestamp, and do all
    /// timing on t_session_s, which is absolute. Two readers got this wrong and gave
    /// opposite false answers on the same P60 Visit 1 file: analyse_session.py read the
    /// warning as the cue and reported 24 of 60 trials anticipating (the true figure is
    /// 1), and check_precue_logging.py looked for negative times, found none, and
    /// reported that pre-cue logging was not working at all. Both were fixed on
    /// 16 Oct 2026; see claude/Fix-Record-2026-10-16-Cue-Index-Bug.md.
    ///
    /// Idempotent per trial, so a restarted foreperiod keeps one continuous trace - the
    /// movement that caused the restart is exactly what belongs on the record.
    /// </summary>
    public void OnWarningTone(int trialNumber)
    {
        if (!recordFrames) return;
        if (_logging && trialNumber == _trialNumber) return;   // foreperiod restarted
        BeginWindow(trialNumber, cueKnown: false);
    }

    /// <summary>
    /// Called by SessionRunner at cue onset. When the window is already open from the warning
    /// tone this only stamps the cue; otherwise it opens the window too, so a scene that never
    /// calls OnWarningTone behaves exactly as it did before.
    /// </summary>
    public void OnCueOnset(int trialNumber)
    {
        if (!recordFrames) return;

        if (_logging && trialNumber == _trialNumber && !_cueStamped)
        {
            // Window already open: set the cue origin here and let LateUpdate write the cue
            // row this frame, as it would have done on a cold start.
            _cueOnsetTime = Time.time;
            _cueStamped = true;
            _stopAt = Time.time + maxTrialSeconds;
            _graspPending = false;
            _pendingTargetIndex = -2;
            return;
        }

        BeginWindow(trialNumber, cueKnown: true);
    }

    private void BeginWindow(int trialNumber, bool cueKnown)
    {
        EnsureWired();

        if (_logging) EndTrial();   // a trial that never finished its grasps

        // A second cue for the same trial number means the trial was abandoned and re-run.
        // Without this the frame file would carry two traces under one key and the join to
        // the per-grasp file on (trial_number, grasp_in_trial) would be ambiguous.
        _trialAttempt = (trialNumber == _lastCueTrial) ? _trialAttempt + 1 : 1;
        _lastCueTrial = trialNumber;

        _trialNumber = trialNumber;
        _graspInTrial = 0;
        _cueOnsetTime = Time.time;   // provisional when cueKnown is false; restamped at the cue
        _cueStamped = cueKnown;
        _stopAt = Time.time + maxTrialSeconds + (cueKnown ? 0f : preCueBudgetSeconds);
        _logging = true;

        // The cue row is written by LateUpdate this frame, with the fingertip positions on it,
        // so the trace starts at the go signal with a position already attached rather than
        // at whatever the next frame happens to be.
        _graspPending = false;

        // -2 marks 'cue', -3 marks 'warning', both distinct from -1 'unknown target'.
        //
        // The row opened at the warning tone used to be written as 'cue' as well, so every
        // trial carried two rows labelled 'cue', separated by that trial's foreperiod. Taking
        // the first of them as the go signal puts the entire foreperiod - between 0.7 s and
        // 5.4 s in the 13 Oct session - inside reaction time, with nothing in the file to show
        // it had happened. The two moments are now named differently.
        _pendingTargetIndex = cueKnown ? -2 : -3;
    }

    /// <summary>
    /// Optional. SessionRunner may call this when a trial ends, to close the window
    /// immediately rather than waiting out secondsAfterLastGrasp.
    /// </summary>
    public void OnTrialEnd()
    {
        if (_logging) EndTrial();
    }

    private void HandleCapture(ControlManager.CaptureData d)
    {
        if (!_logging) return;

        _graspInTrial++;
        _graspPending = true;
        _pendingTargetIndex = d.targetIndex;
        _pendingTargetPosition = d.targetPosition;

        // Only the LAST grasp of the trial starts the countdown.
        //
        // Pushing the stop out on every grasp looked equivalent and was not: it made
        // secondsAfterLastGrasp a timeout BETWEEN grasps as well as after them, so a reach
        // that took longer than it ended the trial's logging early and the grasps that
        // followed were never marked. The 5 October test lost 2 of 33 that way - trial 5,
        // where the second reach took 1.79 s against a 1.0 s window.
        //
        // Until the required grasps are in, the window set at cue onset stands: the trial
        // runs to maxTrialSeconds, or until the next cue closes it. With no TrialCounter to
        // ask, that is also the fallback - logging too long costs disk, logging too little
        // costs the measurement.
        int required = _trialCounter != null ? _trialCounter.CapturesRequired() : 0;
        if (required > 0 && _graspInTrial >= required)
            _stopAt = Time.time + secondsAfterLastGrasp;
    }

    // LateUpdate, not Update: ControlManager refreshes the fingertip positions in its own
    // Update, and script execution order is not fixed. Reading them in Update could take
    // last frame's position on some frames and this frame's on others, which would put a
    // frame of jitter into exactly the signal this file exists to measure.
    void LateUpdate()
    {
        if (!_logging || !recordFrames) return;

        WriteFrame();

        if (Time.time >= _stopAt) EndTrial();
    }

    private void WriteFrame()
    {
        Vector3 lt = Vector3.zero, rt = Vector3.zero;
        if (ControlManager.Singleton != null)
        {
            lt = ControlManager.Singleton.LeftIndexTip;
            rt = ControlManager.Singleton.RightIndexTip;
        }

        double leftSample, rightSample;
        int leftConf  = ReadHand(OVRPlugin.Hand.HandLeft,  out leftSample);
        int rightConf = ReadHand(OVRPlugin.Hand.HandRight, out rightSample);

        string ev = "";
        string evIndex = "", evX = "", evY = "", evZ = "";

        if (_pendingTargetIndex == -2)
        {
            ev = "cue";
            _pendingTargetIndex = -1;
        }
        else if (_pendingTargetIndex == -3)
        {
            // The warning tone, which is where the window opens. t_since_cue_s on this row and
            // on every row up to the cue is measured from here, not from the go signal, because
            // the cue time is not known yet when they are written. Analysis takes the 'cue'
            // row as t = 0 and works from t_session_s, which is absolute.
            ev = "warning";
            _pendingTargetIndex = -1;
        }
        else if (_graspPending)
        {
            ev = "grasp";
            evIndex = _pendingTargetIndex > 0
                ? _pendingTargetIndex.ToString(CultureInfo.InvariantCulture) : "";
            evX = F(_pendingTargetPosition.x);
            evY = F(_pendingTargetPosition.y);
            evZ = F(_pendingTargetPosition.z);
            _graspPending = false;
        }

        _buffer.Add(string.Join(",", new string[] {
            _trialNumber.ToString(CultureInfo.InvariantCulture),
            _graspInTrial.ToString(CultureInfo.InvariantCulture),
            Time.frameCount.ToString(CultureInfo.InvariantCulture),
            F(Time.time),
            F(Time.time - _cueOnsetTime),
            F(lt.x), F(lt.y), F(lt.z),
            leftConf.ToString(CultureInfo.InvariantCulture), D(leftSample),
            F(rt.x), F(rt.y), F(rt.z),
            rightConf.ToString(CultureInfo.InvariantCulture), D(rightSample),
            ev, evIndex, evX, evY, evZ,
            _trialAttempt.ToString(CultureInfo.InvariantCulture)
        }));

        framesWritten++;

        // A trial should never reach this, but a trial that never ends would otherwise grow
        // the buffer without limit. Flushing early costs one disk write at a point where
        // something has already gone wrong.
        if (_buffer.Count >= MaxBufferedRows) Flush();
    }

    // Tracking confidence, read straight from OVRPlugin rather than from an OVRHand
    // component, so nothing has to be dragged into an Inspector slot and the reading cannot
    // disagree with the positions - which ControlManager also takes from OVRPlugin.
    //
    // Returns 1 high, 0 low, -1 not tracked. The -1 case is the one that matters: an
    // untracked hand reports a stale or zero position, and a speed computed across that gap
    // is an artefact, not a movement.
    private static int ReadHand(OVRPlugin.Hand hand, out double sampleTime)
    {
        sampleTime = -1.0;
        OVRPlugin.HandState state = default(OVRPlugin.HandState);
        if (!OVRPlugin.GetHandState(OVRPlugin.Step.Render, hand, ref state)) return -1;
        if ((state.Status & OVRPlugin.HandStatus.HandTracked) == 0) return -1;
        sampleTime = state.SampleTimeStamp;
        return state.HandConfidence == OVRPlugin.TrackingConfidence.High ? 1 : 0;
    }

    private void EndTrial()
    {
        _logging = false;
        _graspPending = false;
        _pendingTargetIndex = -1;
        trialsLogged++;
        Flush();
    }

    private void Flush()
    {
        if (_buffer.Count == 0) return;
        if (_writer == null && !OpenFile()) { _buffer.Clear(); return; }

        try
        {
            for (int i = 0; i < _buffer.Count; i++) _writer.WriteLine(_buffer[i]);
            _writer.Flush();
        }
        catch (Exception e)
        {
            Debug.LogError($"FingertipFrameLogger: could not write frame data - {e.Message}");
        }
        _buffer.Clear();
    }

    // Named to match the per-grasp file, with _frames on the end, so the two files from one
    // session sort next to each other and the pair is obvious months later.
    private bool OpenFile()
    {
        try
        {
            string id = "NOID";
            string visit = "Visit1";
            if (_dataRecorder != null)
            {
                if (!string.IsNullOrWhiteSpace(_dataRecorder.participantId))
                    id = Clean(_dataRecorder.participantId);
                visit = Clean(_dataRecorder.visitLabel);
            }

            string name = $"NPRlab_{id}_{visit}_{DateTime.Now:yyyyMMdd_HHmmss}_frames.csv";
            string folder = Application.persistentDataPath;
#if UNITY_EDITOR
            folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "RecordedData"));
            Directory.CreateDirectory(folder);
#endif
            currentFilePath = Path.Combine(folder, name);

            _writer = new StreamWriter(currentFilePath, append: true);
            _writer.WriteLine(Header);

            Debug.Log($"FingertipFrameLogger: recording frames to {currentFilePath}");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"FingertipFrameLogger: COULD NOT CREATE THE FRAME FILE - "
                         + $"frame data is not being saved. {e.Message}");
            return false;
        }
    }

    private void CloseFile()
    {
        if (_writer == null) return;
        try { _writer.Flush(); _writer.Dispose(); }
        catch (Exception) { }
        _writer = null;
    }

    private static string F(float v) => v.ToString("F5", CultureInfo.InvariantCulture);

    private static string D(double v) => v.ToString("F6", CultureInfo.InvariantCulture);

    private static string Clean(string s)
        => string.IsNullOrEmpty(s) ? "" : s.Replace(",", " ").Replace("\n", " ").Replace("\r", " ").Trim();
}
