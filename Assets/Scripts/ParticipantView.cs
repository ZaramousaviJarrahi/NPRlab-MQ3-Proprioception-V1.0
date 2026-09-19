using UnityEngine;

// Strips the scene back to what a participant should actually see: the targets, and
// nothing else.
//
// WHY THIS MATTERS, not just tidiness:
// ControlManager.Update() calls UpdateDebugSphereLineRenderer() every frame with no
// on/off check, so the cyan boundary arc is always drawn. That arc is a fixed visual
// reference frame hanging in space. In the hidden-hand condition a participant can use
// such landmarks to judge where their unseen hand is - which is precisely the visual
// information the condition is meant to remove. Leaving it on risks confounding H4.
//
// The red cube (the debug controller) used to determine where targets appeared. Since
// TaskSequencer now sets positions explicitly, it no longer affects anything and is
// safe to hide.
//
// Attach to the same GameObject as ControlManager.
public class ParticipantView : MonoBehaviour
{
    [Header("Clean view")]
    [Tooltip("ON for real sessions: hides the boundary lines, the red control cube, and " +
             "the debug/network panels, leaving only the targets. Turn OFF while developing " +
             "if you need to see the debug visuals again.")]
    public bool cleanViewForParticipant = true;

    [Tooltip("Scene objects hidden by name when clean view is on. These are the debug " +
             "console and the networking menu that float in front of the participant.")]
    public string[] alsoHideByName = { "Debug", "Canvas", "Cube" };

    private bool _applied = false;

    void Start()
    {
        Apply(cleanViewForParticipant);
    }

    void Update()
    {
        // The boundary line is redrawn every frame by ControlManager, so a one-off
        // disable at startup is not enough - it has to be held off.
        if (cleanViewForParticipant && ControlManager.Singleton != null)
        {
            LineRenderer boundary = ControlManager.Singleton.DebugBoundaryLine;
            if (boundary != null && boundary.enabled) boundary.enabled = false;
        }

        if (cleanViewForParticipant != _applied) Apply(cleanViewForParticipant);
    }

    public void Apply(bool clean)
    {
        _applied = clean;
        var hidden = new System.Collections.Generic.List<string>();

        if (ControlManager.Singleton != null)
        {
            LineRenderer boundary = ControlManager.Singleton.DebugBoundaryLine;
            if (boundary != null) { boundary.enabled = !clean; hidden.Add("boundary lines"); }

            Transform cube = ControlManager.Singleton.DebugController;
            if (cube != null) { cube.gameObject.SetActive(!clean); hidden.Add(cube.name); }
        }

        // Hide by WHAT THINGS ARE rather than what they are called. The scene contains
        // both "Canvas" and "CanvasRootMenu", and both "Debug" and "Open Debug", so
        // matching names missed the ones the participant could actually see.
        foreach (Canvas c in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            c.gameObject.SetActive(!clean);
            hidden.Add(c.name);
        }

        NetworkDebugConsole console = FindAnyObjectByType<NetworkDebugConsole>(FindObjectsInactive.Include);
        if (console != null) { console.gameObject.SetActive(!clean); hidden.Add(console.name); }

        foreach (string name in alsoHideByName)
        {
            GameObject go = GameObject.Find(name);
            if (go != null) { go.SetActive(!clean); hidden.Add(name); }
        }

        Debug.Log(clean
            ? $"Participant view ON. Hidden: {string.Join(", ", hidden)}"
            : $"Participant view OFF. Restored: {string.Join(", ", hidden)}");
    }

    void OnGUI()
    {
        string label = cleanViewForParticipant ? "Show debug visuals" : "HIDE debug visuals";
        if (GUI.Button(new Rect(530, 285, 180, 40), label))
        {
            cleanViewForParticipant = !cleanViewForParticipant;
            Apply(cleanViewForParticipant);
        }
    }
}
