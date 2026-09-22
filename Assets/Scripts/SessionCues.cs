using UnityEngine;

// Audible cues for the experimenter, played through the headset speakers.
//
// WHY THIS EXISTS:
// In the blocked condition the experimenter reads the same three numbers eighteen times,
// then must switch to a different sequence for the next eighteen. Keeping that tally in
// your head, out loud, while also watching a participant, is the sort of thing that works
// for fifteen trials and then quietly doesn't. Miscounting the switch point contaminates
// the block boundary, which is the comparison the whole design rests on.
//
// The Quest's speakers are open-air and audible to everyone in the room, so a sound is the
// one channel that reaches the experimenter without putting anything in the participant's
// visual field. A short click marks each completed trial; a distinct rising chime marks a
// change of task, which is the cue to start reading the next sequence.
//
// The cues carry no information about performance - they mark structure, not accuracy - so
// they do not tell the participant whether a reach was good, and they do not give away hand
// position in the hidden-hand condition.
//
// Tones are generated in code, so there are no audio files to import or wire up.
//
// Attach to the same GameObject as ControlManager.
public class SessionCues : MonoBehaviour
{
    [Header("Cues")]
    [Tooltip("A short click each time a trial's third grasp completes. Lets you tally " +
             "without watching the targets.")]
    public bool clickOnTrialComplete = true;

    [Tooltip("A rising three-note chime when the task changes - your cue to start reading " +
             "the next sequence. This is the one that matters.")]
    public bool chimeOnTaskChange = true;

    [Range(0f, 1f)] public float volume = 0.45f;

    private AudioSource _source;
    private AudioClip _click;
    private AudioClip _chime;

    void Start()
    {
        _source = gameObject.AddComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.spatialBlend = 0f;          // 2D - same level wherever the head is pointing
        _source.volume = volume;

        _click = Tone("cue_click", new[] { 880f }, 0.07f);
        _chime = Tone("cue_chime", new[] { 523.25f, 659.25f, 783.99f }, 0.16f);   // C-E-G
    }

    public void TrialComplete()
    {
        if (clickOnTrialComplete && _source != null && _click != null)
        {
            _source.volume = volume;
            _source.PlayOneShot(_click);
        }
    }

    public void TaskChanged(string newTask)
    {
        if (chimeOnTaskChange && _source != null && _chime != null)
        {
            _source.volume = volume;
            _source.PlayOneShot(_chime);
        }
        Debug.Log($"NEW BLOCK - start reading the {newTask} sequence.");
    }

    // Builds a clip from a list of note frequencies, with a short fade in and out on each
    // note so it sounds like a tone rather than a click of its own.
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
            int posInNote = i % perNote;
            float t = posInNote / (float)rate;

            float env = 1f;
            if (posInNote < fadeIn) env *= posInNote / (float)fadeIn;
            int left = perNote - posInNote;
            if (left < fadeOut) env *= left / (float)fadeOut;

            data[i] = Mathf.Sin(2f * Mathf.PI * freqs[note] * t) * 0.5f * env;
        }

        AudioClip clip = AudioClip.Create(name, total, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
