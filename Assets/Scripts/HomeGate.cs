using System;
using UnityEngine;

// Watches whether the participant's hand is at the home marker, and provides the timing
// that depends on it.
//
// WHY A GATE RATHER THAN AN INSTRUCTION:
//
// Every trial is supposed to start from the same place. Asking the participant to return
// to home and trusting that they did makes the start posture a thing you hope for and
// cannot check afterwards - and endpoint error, which is a primary outcome, is measured
// from wherever the reach actually began. Gating the trial on it means a trial cannot
// begin from the wrong place, so there is nothing left to enforce by eye.
//
// It also makes REACTION TIME measurable: cue onset to the moment the fingertip leaves the
// home radius. That is the measure that carried the effect in Shea & Morgan - their group
// difference was proportionally largest in RT, because that is where task identification
// and movement planning live.
//
// THIS COMPONENT ONLY OBSERVES. It does not start, stop or block anything on its own -
// SessionRunner asks it. That separation is deliberate: home detection can be verified on
// its own, with the working trial flow untouched, before anything depends on it.
//
// Attach to the same GameObject as TaskSequencer and ControlManager.
public class HomeGate : MonoBehaviour
{
    [Header("Home detection")]
    [Tooltip("How close the fingertip must be to the home marker to count as 'at home', in " +
             "metres. Too small and hand-tracking jitter makes it impossible to satisfy; too " +
             "large and the start posture stops being consistent. 0.08 is a starting point - " +
             "check it against the log before collecting.")]
    public float homeRadius = 0.08f;

    [Tooltip("Extra distance the hand must travel BEYOND the radius before it counts as " +
             "having left, in metres. Without this, jitter at the edge of the radius flickers " +
             "between at-home and left-home and reaction time becomes noise.")]
    public float exitHysteresis = 0.02f;

    [Header("Status (read-only)")]
    public bool atHome = false;
    public bool handTracked = false;
    public float distanceToHome = -1f;

    /// Raised the moment the hand leaves home after having been there.
    public event Action OnLeftHome;
    /// Raised the moment the hand arrives at home after having been away.
    public event Action OnArrivedHome;

    /// Time.time when the hand last left home, or -1 if it has not since this was reset.
    public float LastLeftHomeTime { get; private set; } = -1f;
    /// Time.time when the hand last arrived at home, or -1.
    public float LastArrivedHomeTime { get; private set; } = -1f;

    [Header("Calibration logging")]
    [Tooltip("ON while you are setting the radius. Writes the distance to the log once a " +
             "second plus every arrive/leave, so the numbers can be read from the log file " +
             "instead of watched in the Inspector. Turn OFF before collecting - it is noise " +
             "in a real session.")]
    public bool logCalibration = true;

    [Tooltip("Seconds between calibration lines.")]
    public float logEverySeconds = 1f;

    private TaskSequencer _sequencer;
    private bool _wasAtHome = false;
    private float _nextComplaint = 0f;
    private float _nextLog = 0f;
    private int _transitions = 0;
    private float _minSeen = float.PositiveInfinity;
    private float _maxSeenWhileAtHome = 0f;

    void Start()
    {
        _sequencer = GetComponent<TaskSequencer>();
        if (_sequencer == null)
        {
            Debug.LogError("HomeGate: no TaskSequencer on this GameObject, so there is no home " +
                           "position to watch. Put HomeGate on the same object as ControlManager.");
            enabled = false;
        }
    }

    void Update()
    {
        if (_sequencer == null || ControlManager.Singleton == null) return;

        Vector3 home = _sequencer.HomePosition();
        handTracked = ControlManager.Singleton.TryNearestFingertipDistance(home, out float d, out _);
        distanceToHome = handTracked ? d : -1f;

        if (!handTracked)
        {
            // Hand tracking dropping out is NOT the same as the hand being away from home,
            // and must never be silently treated as either. Say so, occasionally.
            if (atHome || Time.time >= _nextComplaint)
            {
                _nextComplaint = Time.time + 5f;
                Debug.LogWarning("HomeGate: neither hand is tracked, so whether the participant " +
                                 "is at the home marker is unknown. Check hand tracking - a " +
                                 "controller held in that hand switches it off.");
            }
            atHome = false;
            _wasAtHome = false;
            return;
        }

        // Hysteresis: arriving uses the radius, leaving needs radius + margin. A single
        // threshold makes the boundary chatter under tracking jitter, and reaction time is
        // measured from exactly this transition.
        bool nowAtHome = _wasAtHome ? d <= homeRadius + exitHysteresis
                                    : d <= homeRadius;

        if (nowAtHome && !_wasAtHome)
        {
            LastArrivedHomeTime = Time.time;
            _transitions++;
            OnArrivedHome?.Invoke();
            if (logCalibration)
                Debug.Log($"HomeGate: ARRIVED at home, {d * 100f:F1} cm. (transition #{_transitions})");
        }
        else if (!nowAtHome && _wasAtHome)
        {
            LastLeftHomeTime = Time.time;
            _transitions++;
            OnLeftHome?.Invoke();
            if (logCalibration)
                Debug.Log($"HomeGate: LEFT home, {d * 100f:F1} cm. (transition #{_transitions})");
        }

        atHome = nowAtHome;
        _wasAtHome = nowAtHome;

        // Calibration trace. The point is to answer two questions from the log alone:
        // what distance does a hand resting on the marker actually report, and does the
        // at-home flag hold steady or chatter. Counting transitions answers the second
        // without anyone having to watch the Inspector.
        if (logCalibration && Time.time >= _nextLog)
        {
            _nextLog = Time.time + Mathf.Max(0.1f, logEverySeconds);
            if (d < _minSeen) _minSeen = d;
            if (nowAtHome && d > _maxSeenWhileAtHome) _maxSeenWhileAtHome = d;

            Debug.Log($"HomeGate: {d * 100f:F1} cm  atHome={nowAtHome}  "
                    + $"| closest seen {_minSeen * 100f:F1} cm  "
                    + $"| transitions so far {_transitions}  "
                    + $"| radius {homeRadius * 100f:F1} cm (+{exitHysteresis * 100f:F1} to leave)");
        }
    }

    /// Clears the remembered transitions, so the next ones belong to the coming trial.
    public void ResetForNewTrial()
    {
        LastLeftHomeTime = -1f;
        LastArrivedHomeTime = -1f;
    }

    /// One line describing the current state, for logs and for the status display.
    public string Describe()
    {
        if (!handTracked) return "home: hand not tracked";
        return atHome ? $"home: AT HOME ({distanceToHome * 100f:F1} cm)"
                      : $"home: away ({distanceToHome * 100f:F1} cm)";
    }
}
