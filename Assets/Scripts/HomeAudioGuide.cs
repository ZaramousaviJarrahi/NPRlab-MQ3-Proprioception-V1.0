using UnityEngine;

/// <summary>
/// Guides the participant's hand back to the home marker using sound alone.
///
/// WHY SOUND
///   In Visit 3 the hand is hidden, so the participant cannot see how far their
///   hand is from home. Without some feedback, returning to home becomes slow
///   and unreliable in exactly the condition the study is measuring — which
///   would put noise straight into the main effect.
///
///   Sound solves this without leaking any vision of the limb. It behaves
///   identically in all three visits, so it is a CONSTANT across conditions,
///   not a confound.
///
/// HOW IT SOUNDS
///   A repeating click, like a car parking sensor. Far away = slow clicks.
///   Close = fast clicks. A single higher confirmation beep when the hand
///   arrives at home, then silence.
///
///   The sound carries DISTANCE ONLY, not direction (it is 2D / non-spatial
///   on purpose). Distance is what they need to know they have arrived;
///   direction would be a stronger spatial cue than the study needs.
///
/// WHEN IT IS ALLOWED TO SOUND
///   ONLY while the trial is waiting for the hand to return home — i.e.
///   between trials. It MUST be silent from cue onset until the grasp,
///   otherwise it would be telling the participant where their hand is
///   DURING the reach, which is precisely the information Visit 3 removes.
///
///   That is why this script does not start itself. SessionRunner calls
///   Begin() and End() around its existing wait loop. See the setup notes
///   at the bottom of this file.
///
/// NO AUDIO FILES NEEDED
///   The click and the confirmation beep are generated in code at startup,
///   so there is nothing to import into the project.
///
/// Attach to the SAME GameObject as HomeGate (the one with ControlManager
/// and TaskSequencer on it).
///
/// Zara / NPRLab — 28 September 2026
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class HomeAudioGuide : MonoBehaviour
{
    [Header("Wiring")]

    [Tooltip("Leave empty and it finds HomeGate on this same GameObject.")]
    public HomeGate homeGate;

    [Header("Distance to click-rate mapping")]

    [Tooltip("Distance at which the clicks are at their SLOWEST, in metres. " +
             "Anything further away sounds the same as this. 0.50 m is about " +
             "a full arm's reach away from home.")]
    public float farDistance = 0.50f;

    [Tooltip("Seconds between clicks when the hand is far away.")]
    public float slowestIntervalSeconds = 0.70f;

    [Tooltip("Seconds between clicks when the hand is just outside the home " +
             "radius. Keep this above about 0.05 or the clicks blur into a buzz.")]
    public float fastestIntervalSeconds = 0.07f;

    [Header("Sound")]

    [Tooltip("Pitch of the guidance click, in Hz. 900 sits above most room " +
             "noise without being sharp.")]
    public float clickHz = 900f;

    [Tooltip("Length of each click, in seconds.")]
    public float clickSeconds = 0.035f;

    [Tooltip("Pitch of the 'you are at home' confirmation beep, in Hz. " +
             "Deliberately higher than the click so it is unmistakable.")]
    public float arrivedHz = 1400f;

    [Tooltip("Length of the confirmation beep, in seconds.")]
    public float arrivedSeconds = 0.18f;

    [Range(0f, 1f)]
    [Tooltip("Volume. Set this once with a participant in the headset and " +
             "then leave it alone for the whole study.")]
    public float volume = 0.6f;

    [Header("Safety")]

    [Tooltip("Stop guiding after this long even if Begin() was never closed " +
             "by End(). Prevents a stuck clicking sound if a trial is " +
             "abandoned. Should be a little LONGER than SessionRunner's " +
             "homeWaitWarnEverySeconds.")]
    public float maxGuidanceSeconds = 12f;

    [Tooltip("Writes a line to the log each time guidance starts and stops. " +
             "Useful while you are setting this up; harmless to leave on.")]
    public bool logGuidance = true;

    [Header("Status (read-only)")]

    /// <summary>True while the clicks are allowed to sound.</summary>
    public bool guiding = false;

    /// <summary>Current gap between clicks, in seconds. -1 when not guiding.</summary>
    public float currentIntervalSeconds = -1f;

    /// <summary>True once the confirmation beep has played this cycle.</summary>
    public bool arrivedBeepPlayed = false;

    // ── internals ─────────────────────────────────────────────────────────
    private AudioSource _src;
    private AudioClip _clickClip;
    private AudioClip _arrivedClip;
    private float _nextClickAt;
    private float _startedAt = -1f;

    private const int SampleRate = 44100;

    void Awake()
    {
        _src = GetComponent<AudioSource>();
        _src.playOnAwake = false;
        _src.loop = false;
        _src.spatialBlend = 0f;   // 2D: distance information only, no direction
        _src.volume = volume;

        _clickClip   = MakeTone("HomeClick",   clickHz,   clickSeconds);
        _arrivedClip = MakeTone("HomeArrived", arrivedHz, arrivedSeconds);
    }

    void Start()
    {
        if (homeGate == null) homeGate = GetComponent<HomeGate>();

        if (homeGate == null)
        {
            Debug.LogError("HomeAudioGuide: no HomeGate found on this GameObject, so there " +
                           "is no distance to turn into sound. Put HomeAudioGuide on the " +
                           "same object as HomeGate.");
            enabled = false;
            return;
        }

        if (fastestIntervalSeconds >= slowestIntervalSeconds)
        {
            Debug.LogWarning("HomeAudioGuide: fastestIntervalSeconds should be SMALLER than " +
                             "slowestIntervalSeconds, otherwise the clicks get slower as the " +
                             "hand gets closer, which is backwards.");
        }

        Debug.Log("HomeAudioGuide: ready. Silent until SessionRunner calls Begin().");
    }

    /// <summary>
    /// Start guiding. SessionRunner calls this immediately BEFORE its
    /// "wait for the hand at home" loop.
    /// </summary>
    public void Begin()
    {
        if (!enabled) return;

        guiding = true;
        arrivedBeepPlayed = false;
        _startedAt = Time.time;
        _nextClickAt = 0f;          // click straight away so they know it started
        _src.volume = volume;

        if (logGuidance) Debug.Log("HomeAudioGuide: guidance ON (waiting for home).");
    }

    /// <summary>
    /// Stop guiding and go silent. SessionRunner calls this immediately AFTER
    /// its wait loop — including when the loop ended on a timeout, so a
    /// participant who never reached home does not get clicked at during the
    /// trial that follows.
    /// </summary>
    public void End()
    {
        if (!guiding) return;

        guiding = false;
        currentIntervalSeconds = -1f;
        _startedAt = -1f;

        if (logGuidance) Debug.Log("HomeAudioGuide: guidance OFF.");
    }

    void Update()
    {
        if (!guiding) return;

        // Failsafe — never click forever.
        if (_startedAt >= 0f && Time.time - _startedAt > maxGuidanceSeconds)
        {
            Debug.LogWarning($"HomeAudioGuide: stopped by its own {maxGuidanceSeconds:F0}s " +
                             "failsafe, so the clicks have gone quiet. Two different things look " +
                             "the same from here: either the wait for the hand at home has " +
                             "genuinely run this long - and the participant has just lost the " +
                             "audible cue to home, at the moment they most need it - or Begin() " +
                             "was called without a matching End(). A 'STILL WAITING' line above " +
                             "means the first; nothing above means the second.");
            End();
            return;
        }

        // Hand tracking lost. Not the same as "far from home", so do not
        // invent a distance for it — go quiet. HomeGate already logs this.
        if (!homeGate.handTracked)
        {
            currentIntervalSeconds = -1f;
            return;
        }

        // Arrived. One confirmation beep, then silence, so the participant is
        // not listening to clicks through the warning cue and foreperiod.
        if (homeGate.atHome)
        {
            if (!arrivedBeepPlayed)
            {
                _src.PlayOneShot(_arrivedClip, volume);
                arrivedBeepPlayed = true;
                if (logGuidance)
                    Debug.Log($"HomeAudioGuide: arrival beep at " +
                              $"{homeGate.distanceToHome * 100f:F1} cm.");
            }
            currentIntervalSeconds = -1f;
            return;
        }

        // Left home again after arriving (e.g. drifted out before the cue).
        // Resume clicking and allow a fresh confirmation beep.
        arrivedBeepPlayed = false;

        // Map distance onto click rate. Clamped at the home radius so the
        // clicks do not race off to infinity right at the boundary.
        float d = Mathf.Clamp(homeGate.distanceToHome, homeGate.homeRadius, farDistance);
        float t01 = Mathf.InverseLerp(homeGate.homeRadius, farDistance, d);
        currentIntervalSeconds = Mathf.Lerp(fastestIntervalSeconds, slowestIntervalSeconds, t01);

        if (Time.time >= _nextClickAt)
        {
            _src.PlayOneShot(_clickClip, volume);
            _nextClickAt = Time.time + currentIntervalSeconds;
        }
    }

    /// <summary>
    /// Builds a short sine tone as an AudioClip in code, with fade in and out
    /// so it does not pop. Saves importing any audio assets.
    /// </summary>
    private static AudioClip MakeTone(string name, float hz, float seconds)
    {
        int n = Mathf.Max(8, (int)(SampleRate * Mathf.Max(0.005f, seconds)));
        var data = new float[n];

        int fadeIn  = Mathf.Max(1, (int)(SampleRate * 0.004f));   // 4 ms
        int fadeOut = Mathf.Max(1, (int)(SampleRate * 0.010f));   // 10 ms

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / SampleRate;
            float env = Mathf.Min(1f, (float)i / fadeIn);
            env = Mathf.Min(env, (float)(n - i) / fadeOut);
            data[i] = Mathf.Sin(2f * Mathf.PI * hz * t) * env;
        }

        var clip = AudioClip.Create(name, n, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// WIRING — ALREADY DONE, nothing to add by hand
// ─────────────────────────────────────────────────────────────────────────────
//
// SessionRunner.ArmAndStartTrial() already calls Begin() before its wait-for-home
// loop and End() after it, and AbandonCurrentTrial() calls End() too, so pressing
// clear-targets (Y) never leaves the clicks running.
//
// The ONE thing still to do in Unity: add this component to the GameObject that
// already has ControlManager, TaskSequencer, HomeGate and SessionRunner on it.
// Select that object, Add Component, search "Home Audio Guide".
//
// An AudioSource is added automatically by RequireComponent. Do not change its
// settings - this script sets what it needs in Awake().
//
// ─────────────────────────────────────────────────────────────────────────────
// SETTING IT UP IN THE HEADSET — do this once, before any participant
// ─────────────────────────────────────────────────────────────────────────────
//
// a) Put the headset on yourself and start a session.
// b) Hold your hand far from home. You should hear slow clicks.
// c) Move slowly towards home. The clicks should speed up smoothly, and you
//    should get ONE higher beep when you arrive, then silence.
// d) If the clicks turn into a buzz just before home, raise
//    fastestIntervalSeconds (try 0.10).
// e) If you cannot hear the clicks over the room, raise volume. Set it once
//    and do not change it again for the rest of the study — a louder cue in
//    one visit than another is a difference between conditions.
// f) Check the log for "guidance ON" / "guidance OFF" lines. Every trial
//    should have one of each. If you see an ON with no OFF, step 3 is wired
//    wrong.
//
// ─────────────────────────────────────────────────────────────────────────────
