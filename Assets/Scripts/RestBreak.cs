using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

// Scheduled rest breaks, shown to the participant as a countdown.
//
// WHY THIS EXISTS:
// Sixty reaching trials with the arm held up is tiring, and fatigue that builds unevenly
// across a session is noise in exactly the measures the study cares about. A rest at fixed
// points relieves that. But a rest is only harmless to the design if every participant gets
// the SAME rests - the same number, at the same trial counts, for the same length. Timing
// rests with a stopwatch leaves that to the experimenter's attention on the day; if the
// random group happened to rest longer, rest would become a hidden difference between the
// groups. So the app times them, and the start button refuses to run a trial until the rest
// is over.
//
// WHERE THE BREAKS FALL:
// The lists below are TRIAL COUNTER values, and the counter includes the warm-up trials.
// "24,42" in Visit 1 is after the 6 familiarization trials plus 18 and 36 acquisition
// trials - i.e. between each set of 18, as the study plan describes. A rest after counter N
// starts the moment trial N completes.
//
// WHAT THE PARTICIPANT SEES:
// "Rest" and a countdown, in the same place the sequence cue panel appears during trials,
// so no new landmark is added to the scene. Nothing is shown during trials, so the
// hidden-hand condition is unaffected. At the end it shows "Ready" briefly and disappears,
// and a short falling two-note tone tells the experimenter the next trial can be started.
//
// TO TURN IT OFF: set restSeconds to 0 (here, or "restSeconds = 0" in session_config.txt).
// The app then behaves exactly as it did before this component existed.
//
// Added automatically by SessionRunner if it is not on the GameObject; add it to
// ControlManager yourself to edit the settings in the Inspector.
public class RestBreak : MonoBehaviour
{
    [Header("Rest length")]
    [Tooltip("Seconds per rest. 0 turns scheduled rests off entirely. " +
             "session_config.txt can override this with: restSeconds = 30")]
    public float restSeconds = 30f;

    [Header("Where the rests fall (trial COUNTER values, warm-up trials included)")]
    [Tooltip("Visit 1: 6 warm-up + 3 x 18 acquisition. 24,42 = between each set of 18.")]
    public string visit1RestAfter = "24,42";
    [Tooltip("Visit 2: 18 retention, then transfer. 18 = between retention and transfer.")]
    public string visit2RestAfter = "18";
    [Tooltip("Visit 3: 6 re-familiarisation + 4 mini-blocks of 15. 21,36,51 = between mini-blocks.")]
    public string visit3RestAfter = "21,36,51";

    [Header("Display")]
    public Color inkColour = Color.white;
    [Tooltip("Height of the two-line 'Rest / 0:30' text, in metres.")]
    public float textHeightMetres = 0.12f;
    [Tooltip("How long 'Ready' stays up after the countdown, in seconds.")]
    public float readySeconds = 1.5f;

    [Header("Sound")]
    public bool toneWhenRestEnds = true;
    [Range(0f, 1f)] public float volume = 0.45f;

    public bool IsResting => _routine != null;
    public float SecondsLeft => IsResting ? Mathf.Max(0f, _endTime - Time.time) : 0f;

    private TextMeshPro _label;
    private Coroutine _routine;
    private float _endTime;
    private AudioSource _source;
    private AudioClip _endTone;

    // True if a rest is scheduled once `completedTrials` trials are done in this visit.
    public bool IsRestPointAfter(int completedTrials, int visit)
    {
        if (restSeconds <= 0f) return false;
        string list = visit == 2 ? visit2RestAfter : visit == 3 ? visit3RestAfter : visit1RestAfter;
        foreach (int n in Parse(list)) if (n == completedTrials) return true;
        return false;
    }

    public void Begin(int afterTrial, int visit)
    {
        if (restSeconds <= 0f || IsResting) return;
        _routine = StartCoroutine(Run(afterTrial, visit));
    }

    private IEnumerator Run(int afterTrial, int visit)
    {
        float started = Time.time;
        _endTime = started + restSeconds;
        Debug.Log($"REST started after trial {afterTrial} (Visit {visit}) - {restSeconds:F0} s. " +
                  "The start button will wait until it ends.");

        ShowLabel();
        int shown = -1;
        while (Time.time < _endTime)
        {
            int left = Mathf.CeilToInt(_endTime - Time.time);
            if (left != shown)
            {
                shown = left;
                _label.text = $"Rest\n{left / 60}:{left % 60:00}";
            }
            yield return null;
        }

        _routine = null;   // the start button is free from here, even while "Ready" shows
        Debug.Log($"REST ended after trial {afterTrial} (Visit {visit}) - {Time.time - started:F1} s.");
        PlayEndTone();

        _label.text = "Ready";
        yield return new WaitForSeconds(readySeconds);
        if (!IsResting && _label != null) _label.gameObject.SetActive(false);
    }

    // Same anchor as TrialDisplay's cue panel: above the top-centre of the grid, facing the
    // participant. The participant already looks there at the start of every trial.
    private void ShowLabel()
    {
        if (_label == null)
        {
            var go = new GameObject("RestCountdown");
            go.transform.SetParent(transform, false);
            _label = go.AddComponent<TextMeshPro>();
            _label.alignment = TextAlignmentOptions.Center;
            _label.enableWordWrapping = false;
            _label.rectTransform.sizeDelta = new Vector2(0.5f, 0.5f);
        }
        _label.gameObject.SetActive(true);
        _label.color = inkColour;

        // Measured on the widest text it will show, once, so the size does not jump as the
        // digits change.
        _label.text = "Rest\n0:00";
        ScaleToMetres(_label, textHeightMetres);

        var seq = GetComponent<TaskSequencer>();
        var cue = GetComponent<TrialDisplay>();
        if (seq != null)
        {
            Vector3 right = (seq.GridPosition(3) - seq.GridPosition(1)).normalized;
            Vector3 up = (seq.GridPosition(1) - seq.GridPosition(7)).normalized;
            Vector3 forward = Vector3.Cross(right, up).normalized;
            float above = cue != null ? cue.panelAboveGrid : 0.20f;
            float side = cue != null ? cue.panelSideways : 0f;
            _label.transform.position = seq.GridPosition(2) + up * above + right * side;
            _label.transform.rotation = Quaternion.LookRotation(forward, up);
        }
        else if (Camera.main != null)
        {
            Transform cam = Camera.main.transform;
            Vector3 flat = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            _label.transform.position = cam.position + flat * 0.6f;
            _label.transform.rotation = Quaternion.LookRotation(flat, Vector3.up);
        }
    }

    // TextMeshPro's fontSize is not in metres, so render, measure, and scale - the same
    // approach TrialDisplay uses.
    private static void ScaleToMetres(TextMeshPro label, float targetHeightMetres)
    {
        label.transform.localScale = Vector3.one;
        label.fontSize = 10f;
        label.ForceMeshUpdate();
        float rendered = label.textBounds.size.y;
        if (rendered <= 0.0001f) return;
        float parentScale = label.transform.parent != null ? label.transform.parent.lossyScale.y : 1f;
        if (parentScale < 0.0001f) parentScale = 1f;
        label.transform.localScale = Vector3.one * Mathf.Max(0.0001f, targetHeightMetres / rendered / parentScale);
    }

    // A falling two-note tone (G5 then C5). Deliberately unlike every SessionCues sound:
    // the rising task-change chime, the high trial-complete click, and the low warning and
    // go tones. It means "you may start the next trial", not "move".
    private void PlayEndTone()
    {
        if (!toneWhenRestEnds) return;
        if (_source == null)
        {
            _source = gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
        }
        if (_endTone == null) _endTone = Tone("rest_end", new[] { 783.99f, 523.25f }, 0.16f);
        _source.volume = volume;
        _source.PlayOneShot(_endTone);
    }

    private static AudioClip Tone(string name, float[] freqs, float noteSeconds)
    {
        const int rate = 44100;
        int perNote = Mathf.RoundToInt(rate * noteSeconds);
        int total = perNote * freqs.Length;
        float[] data = new float[total];
        int fadeIn = Mathf.Max(1, Mathf.RoundToInt(rate * 0.004f));
        int fadeOut = Mathf.Max(1, Mathf.RoundToInt(rate * 0.030f));
        for (int i = 0; i < total; i++)
        {
            int note = Mathf.Min(i / perNote, freqs.Length - 1);
            int pos = i % perNote;
            float env = 1f;
            if (pos < fadeIn) env *= pos / (float)fadeIn;
            int left = perNote - pos;
            if (left < fadeOut) env *= left / (float)fadeOut;
            data[i] = Mathf.Sin(2f * Mathf.PI * freqs[note] * pos / rate) * 0.5f * env;
        }
        AudioClip clip = AudioClip.Create(name, total, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    private static IEnumerable<int> Parse(string list)
    {
        if (string.IsNullOrWhiteSpace(list)) yield break;
        foreach (string part in list.Split(','))
            if (int.TryParse(part.Trim(), out int n)) yield return n;
    }
}
