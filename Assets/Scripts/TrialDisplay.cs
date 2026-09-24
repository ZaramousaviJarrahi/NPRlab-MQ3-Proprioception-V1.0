using System.Collections.Generic;
using TMPro;
using UnityEngine;

// Shows the participant which order to grasp the targets in.
//
// HOW IT SHOWS IT, AND WHY THAT MATTERS MORE THAN IT LOOKS:
//
// The default is a CUE PANEL: a small 3x3 diagram, set apart from the grid, with the order
// marked on it. The participant reads the pattern off the panel and maps it onto the nine
// real targets themselves.
//
// This is deliberate, and it follows the original. Shea & Morgan's diagrams were "located
// directly behind the stimulus lights and attached to the front of a barrier" - the
// barriers themselves were never labelled. The participant saw a coloured light, found the
// matching diagram, and mapped that pattern onto the physical workspace.
//
// Writing the numbers onto the targets instead removes that mapping step: the task becomes
// "touch the one labelled 1, then 2, then 3". It is not the follow-the-dot failure of
// revealing targets one at a time - everything is shown at once - but it lands in the same
// place, because no planning is required. Planning is where the group difference lives: in
// the original the effect was proportionally largest in REACTION time, which is the measure
// that contains task identification and movement planning.
//
// There is no memory demand either way. The panel stays visible for the whole trial, so
// nothing has to be recalled - which is also how the original worked, and why it found no
// forgetting over ten days.
//
// DigitsOnTargets is kept as an option so the two can be piloted against each other, but it
// is not the study-plan default.
//
// Attach to the same GameObject as TaskSequencer and ControlManager.
public class TrialDisplay : MonoBehaviour
{
    public enum Mode
    {
        CuePanel,         // a separate 3x3 diagram - the participant maps it onto the grid
        DigitsOnTargets   // numbers written on the target spheres themselves
    }

    [Header("How the sequence is shown")]
    [Tooltip("CuePanel is the study-plan default and matches Shea & Morgan: the pattern is " +
             "shown apart from the targets, so the participant has to map it onto the grid. " +
             "DigitsOnTargets removes that planning step - for piloting only.")]
    public Mode mode = Mode.CuePanel;

    public bool show = true;

    [Header("Cue panel")]
    [Tooltip("How far ABOVE the top row of the grid the panel sits, in metres. The home " +
             "marker is below the grid, so the panel goes above it.")]
    public float panelAboveGrid = 0.20f;

    [Tooltip("Sideways offset from the grid centre, in metres. 0 = centred.")]
    public float panelSideways = 0f;

    [Tooltip("Height of the whole 3x3 diagram, in metres.")]
    public float panelHeightMetres = 0.13f;

    [Header("Digits on targets (piloting only)")]
    [Tooltip("Height of each digit, in metres.")]
    public float digitHeightMetres = 0.05f;

    [Tooltip("How far in front of the ball the digit floats, towards the participant.")]
    public float digitForwardOffset = 0.055f;

    [Header("Appearance")]
    public Color inkColour = Color.white;

    private TaskSequencer _sequencer;
    private readonly List<TextMeshPro> _digits = new List<TextMeshPro>();
    private TextMeshPro _panel;
    private int _lastTrialsStarted = -1;
    private string _lastSequence = null;
    private Mode _lastMode;
    private bool _lastShow;

    void Start()
    {
        _sequencer = GetComponent<TaskSequencer>();
        _lastMode = mode;
        _lastShow = show;
        if (_sequencer == null)
        {
            Debug.LogError("TrialDisplay: no TaskSequencer on this GameObject, so there is no " +
                           "sequence to show. Put TrialDisplay on the same object as " +
                           "ControlManager and TaskSequencer.");
        }
    }

    void Update()
    {
        if (_sequencer == null) return;

        // Trial start and trial end are already visible in TaskSequencer's public state:
        // StartTrial() increments trialsStarted and fills currentSequence, ClearTargets()
        // empties it. Watching those means TaskSequencer needs no events added to it.
        bool changed = _sequencer.trialsStarted != _lastTrialsStarted
                       || _sequencer.currentSequence != _lastSequence
                       || mode != _lastMode
                       || show != _lastShow;
        if (!changed) return;

        _lastTrialsStarted = _sequencer.trialsStarted;
        _lastSequence      = _sequencer.currentSequence;
        _lastMode          = mode;
        _lastShow          = show;

        HideAll();
        if (!show || string.IsNullOrEmpty(_sequencer.currentSequence)) return;

        List<int> positions = _sequencer.CurrentSequencePositions();
        if (positions.Count == 0) return;

        if (mode == Mode.CuePanel) ShowPanel(positions);
        else                       ShowDigitsOnTargets(positions);
    }

    // ---- the grid's own axes, derived from positions the sequencer already exposes ----
    private void GridAxes(out Vector3 right, out Vector3 up, out Vector3 forward)
    {
        right   = (_sequencer.GridPosition(3) - _sequencer.GridPosition(1)).normalized;
        up      = (_sequencer.GridPosition(1) - _sequencer.GridPosition(7)).normalized;
        forward = Vector3.Cross(right, up).normalized;   // points AWAY from the participant
    }

    private void ShowPanel(List<int> positions)
    {
        if (_panel == null) _panel = MakeLabel("SequenceCuePanel");
        _panel.gameObject.SetActive(true);
        _panel.text  = DiagramText(positions);
        _panel.color = inkColour;

        GridAxes(out Vector3 right, out Vector3 up, out Vector3 forward);

        // Anchored off the TOP-CENTRE of the grid, because the home marker occupies the
        // space below it.
        Vector3 anchor = _sequencer.GridPosition(2) + up * panelAboveGrid + right * panelSideways;

        ScaleToMetres(_panel, panelHeightMetres);
        _panel.transform.position = anchor;
        _panel.transform.rotation = Quaternion.LookRotation(forward, up);

        Debug.Log($"TrialDisplay: cue panel showing the pattern for {string.Join("-", positions)} "
                + "(participant maps it onto the grid).");
    }

    // A 3x3 picture of the grid: the order number at each sequence position, a dot elsewhere.
    // mspace forces every character to the same width so the columns line up in a font that
    // is not monospaced.
    private string DiagramText(List<int> positions)
    {
        var sb = new System.Text.StringBuilder("<mspace=0.62em>");
        for (int row = 0; row < 3; row++)
        {
            if (row > 0) sb.Append('\n');
            for (int col = 0; col < 3; col++)
            {
                int n = row * 3 + col + 1;
                int order = positions.IndexOf(n);
                sb.Append(order >= 0 ? (order + 1).ToString() : "·");
                if (col < 2) sb.Append("  ");
            }
        }
        return sb.Append("</mspace>").ToString();
    }

    private void ShowDigitsOnTargets(List<int> positions)
    {
        GridAxes(out Vector3 right, out Vector3 up, out Vector3 forward);

        while (_digits.Count < positions.Count) _digits.Add(MakeLabel($"SequenceDigit{_digits.Count + 1}"));

        for (int i = 0; i < positions.Count; i++)
        {
            TextMeshPro d = _digits[i];
            d.gameObject.SetActive(true);
            d.text  = (i + 1).ToString();
            d.color = inkColour;
            ScaleToMetres(d, digitHeightMetres);
            d.transform.position = _sequencer.GridPosition(positions[i]) - forward * digitForwardOffset;
            d.transform.rotation = Quaternion.LookRotation(forward, up);
        }

        Debug.Log($"TrialDisplay: digits 1-{positions.Count} on targets {string.Join(", ", positions)} "
                + "(PILOT MODE - no mapping step, not the study-plan default).");
    }

    private TextMeshPro MakeLabel(string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var tmp = go.AddComponent<TextMeshPro>();
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.rectTransform.sizeDelta = new Vector2(0.5f, 0.5f);
        return tmp;
    }

    // TextMeshPro's fontSize is not a physical measurement: what it means in metres depends
    // on the font asset, so picking a number by eye gives text that is either invisible or -
    // as happened on the first build - the better part of a metre tall. So render it, measure
    // what actually came out, and scale the transform until it is the requested height.
    private static void ScaleToMetres(TextMeshPro label, float targetHeightMetres)
    {
        label.transform.localScale = Vector3.one;
        label.fontSize = 10f;              // arbitrary: the scale below does the real work
        label.ForceMeshUpdate();

        float rendered = label.textBounds.size.y;
        if (rendered <= 0.0001f) return;   // nothing measurable yet - leave it rather than guess

        float parentScale = label.transform.parent != null ? label.transform.parent.lossyScale.y : 1f;
        if (parentScale < 0.0001f) parentScale = 1f;

        label.transform.localScale = Vector3.one * Mathf.Max(0.0001f, targetHeightMetres / rendered / parentScale);
    }

    private void HideAll()
    {
        if (_panel != null) _panel.gameObject.SetActive(false);
        foreach (TextMeshPro d in _digits) if (d != null) d.gameObject.SetActive(false);
    }
}
