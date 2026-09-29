using System;
using UnityEngine;

/// <summary>
/// Detects the moment the participant's hand actually STARTS MOVING after a
/// target appears. This is what splits one number into two:
///
///     target appears ──► hand starts moving ──► grasp
///                    └─ reaction time ─┘└─ reach time ─┘
///
/// Without this, reach_time_s measures spawn-to-grasp (thinking + moving
/// combined) and reaction_time_s can't be calculated at all.
///
/// HOW IT WORKS
///   Watches the hand position every frame. When its speed crosses a
///   threshold and STAYS above it for a few frames in a row, that's onset.
///   The "few frames in a row" part matters — Quest hand tracking is jittery
///   and a single noisy frame would otherwise trigger a false onset.
///
/// SETUP
///   Attach to the ControlManager object, alongside your other scripts.
///
/// Zara / NPRLab — 28 September 2026
/// </summary>
public class MovementOnsetDetector : MonoBehaviour
{
    [Header("Detection settings")]

    [Tooltip("Hand speed that counts as 'moving', in metres per second. " +
             "0.05 (5 cm/s) is the usual value in reaching studies. " +
             "Raise it if you get false onsets from hand-tracking jitter.")]
    public float speedThreshold = 0.05f;

    [Tooltip("How many frames in a row must exceed the threshold before it " +
             "counts. Guards against single noisy frames. At ~72 fps, " +
             "3 frames is about 40 ms.")]
    public int framesAboveThreshold = 3;

    [Tooltip("Smoothing window for hand position. Larger = smoother but " +
             "slightly later onset. 3 is a good compromise.")]
    public int smoothingFrames = 3;

    [Tooltip("Give up waiting for movement after this long, in seconds. " +
             "Prevents a hung trial if the participant never moves.")]
    public float timeoutSeconds = 10f;

    [Header("Read-only — for DataRecorder")]

    /// <summary>True once movement onset has been detected this trial.</summary>
    public bool OnsetDetected { get; private set; }

    /// <summary>Time.realtimeSinceStartup at the moment the target spawned.</summary>
    public float SpawnTime { get; private set; }

    /// <summary>Time.realtimeSinceStartup at movement onset. -1 if not detected.</summary>
    public float OnsetTime { get; private set; } = -1f;

    /// <summary>
    /// Seconds from target appearing to hand starting to move.
    /// float.NaN if onset was never detected — write an empty cell for that.
    /// </summary>
    public float ReactionTime =>
        OnsetDetected ? OnsetTime - SpawnTime : float.NaN;

    /// <summary>Fires when onset is detected. Argument is the reaction time.</summary>
    public event Action<float> OnMovementOnset;

    // ── internals ─────────────────────────────────────────────────────────
    private bool _armed;                 // waiting for movement
    private int _consecutiveFrames;
    private Vector3[] _buffer;
    private int _bufferCount;
    private Vector3 _lastSmoothed;
    private bool _haveLast;
    private OVRCameraRig _rig;

    void Start()
    {
        _buffer = new Vector3[Mathf.Max(1, smoothingFrames)];

        if (ControlManager.Singleton == null)
        {
            Debug.LogError("[MovementOnset] ControlManager.Singleton is null. " +
                           "Check script execution order.");
            return;
        }

        ControlManager.Singleton.OnTargetSpawned += HandleTargetSpawned;
        ControlManager.Singleton.OnTargetCaptured += HandleCaptured;

        Debug.Log("[MovementOnset] Ready.");
    }

    void OnDestroy()
    {
        if (ControlManager.Singleton == null) return;
        ControlManager.Singleton.OnTargetSpawned -= HandleTargetSpawned;
        ControlManager.Singleton.OnTargetCaptured -= HandleCaptured;
    }

    /// <summary>Start watching for movement. Called when a target appears.</summary>
    public void Arm()
    {
        _armed = true;
        OnsetDetected = false;
        OnsetTime = -1f;
        SpawnTime = Time.realtimeSinceStartup;
        _consecutiveFrames = 0;
        _bufferCount = 0;
        _haveLast = false;
    }

    /// <summary>Stop watching. Called on grasp, or when a trial is abandoned.</summary>
    public void Disarm() => _armed = false;

    private void HandleTargetSpawned() => Arm();
    private void HandleCaptured() => Disarm();

    void Update()
    {
        if (!_armed || OnsetDetected) return;

        // Timeout — participant never moved
        if (Time.realtimeSinceStartup - SpawnTime > timeoutSeconds)
        {
            Debug.LogWarning($"[MovementOnset] No movement within {timeoutSeconds}s. " +
                             "Reaction time will be blank for this trial.");
            _armed = false;
            return;
        }

        if (!TryGetHandPosition(out Vector3 raw)) return;

        // Rolling average to damp hand-tracking jitter
        _buffer[_bufferCount % _buffer.Length] = raw;
        _bufferCount++;
        if (_bufferCount < _buffer.Length) return;

        Vector3 sum = Vector3.zero;
        foreach (var p in _buffer) sum += p;
        Vector3 smoothed = sum / _buffer.Length;

        if (!_haveLast)
        {
            _lastSmoothed = smoothed;
            _haveLast = true;
            return;
        }

        float speed = Vector3.Distance(smoothed, _lastSmoothed) / Mathf.Max(Time.deltaTime, 1e-5f);
        _lastSmoothed = smoothed;

        if (speed >= speedThreshold)
        {
            _consecutiveFrames++;
            if (_consecutiveFrames >= framesAboveThreshold)
            {
                // Onset happened when the run STARTED, not when it finished,
                // so step back by the frames we needed to confirm it.
                OnsetTime = Time.realtimeSinceStartup
                            - (framesAboveThreshold * Time.deltaTime);
                OnsetDetected = true;
                _armed = false;

                float rt = ReactionTime;
                Debug.Log($"[MovementOnset] Onset detected. Reaction time {rt:F3}s");
                OnMovementOnset?.Invoke(rt);
            }
        }
        else
        {
            _consecutiveFrames = 0;
        }
    }

    /// <summary>
    /// Where is the hand right now?
    ///
    /// ⚠️ CHECK THIS AGAINST YOUR SETUP. It uses the OVRCameraRig hand
    /// anchors — the same runtime-lookup pattern as HandVisibilityToggle,
    /// because Inspector references to those anchors don't survive.
    ///
    /// If ControlManager computes its fingertip position differently (it must,
    /// since CaptureData carries fingerTipPosition), use that method instead
    /// so onset and endpoint error are measured from the SAME point. Otherwise
    /// you're timing the wrist and measuring error at the fingertip.
    /// </summary>
    private bool TryGetHandPosition(out Vector3 pos)
    {
        pos = Vector3.zero;

        if (_rig == null) _rig = FindObjectOfType<OVRCameraRig>();
        if (_rig == null) return false;

        // Right hand by default — participants are right-hand dominant per
        // the inclusion criteria.
        Transform anchor = _rig.rightHandAnchor;
        if (anchor == null) return false;

        pos = anchor.position;
        return true;
    }
}
