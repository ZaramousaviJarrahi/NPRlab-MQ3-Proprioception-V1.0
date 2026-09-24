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

    [Header("Hands only")]
    [Tooltip("ON for real sessions: disables the CONTROLLER grab interactors, so a target " +
             "can only be grasped with a tracked hand. The controllers stay tracked and the " +
             "experimenter's X / A / B / Y buttons keep working - only grabbing is blocked.")]
    public bool handsOnlyGrasping = true;

    [Tooltip("Objects disabled to block controller grasping. Matched on the name CONTAINING " +
             "any of these, so SDK naming changes are less likely to break it silently.")]
    public string[] controllerGrabObjectNames = { "ControllerGrabInteractor",
                                                  "ControllerDistanceGrabInteractor" };

    private bool _applied = false;
    private float _nextInteractorCheck = 0f;
    private int _interactorsDisabledLastCount = -1;

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

        // Re-checked rather than set once, because OVRCameraRig is [ExecuteInEditMode] and
        // rebuilds its own child hierarchy - the same behaviour that kept wiping the hand
        // anchors out of HandVisibilityToggle's Inspector fields. A controller interactor
        // that comes back mid-session would silently let a controller grasp a target, and
        // nothing in the recorded data would show that it had happened. Once a second is
        // cheap and makes the guarantee hold for the whole session.
        if (Time.time >= _nextInteractorCheck)
        {
            _nextInteractorCheck = Time.time + 1f;
            EnforceHandsOnlyGrasping();
        }
    }

    // Blocks controller grasping WITHOUT touching OVRManager's hand-tracking support.
    // Setting that to "Hands Only" would be the obvious move and is the wrong one: it stops
    // the runtime exposing controllers at all, which kills the OVRInput button reads that
    // ExperimenterControls depends on - so there would be no way to start a session, start
    // a trial, toggle hands or clear. Disabling just the grab interactors leaves the
    // controllers tracked and their buttons live.
    private void EnforceHandsOnlyGrasping()
    {
        int disabled = 0;

        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include,
                                                            FindObjectsSortMode.None))
        {
            bool isControllerGrab = false;
            foreach (string name in controllerGrabObjectNames)
            {
                if (!string.IsNullOrEmpty(name) && t.name.Contains(name)) { isControllerGrab = true; break; }
            }
            if (!isControllerGrab) continue;

            if (t.gameObject.activeSelf == handsOnlyGrasping)
                t.gameObject.SetActive(!handsOnlyGrasping);

            if (handsOnlyGrasping) disabled++;
        }

        // Report only when the count changes, so this does not spam the log every second.
        if (disabled != _interactorsDisabledLastCount)
        {
            _interactorsDisabledLastCount = disabled;
            if (handsOnlyGrasping && disabled == 0)
            {
                Debug.LogWarning("ParticipantView: hands-only grasping is ON but no controller " +
                                 "grab interactors were found to disable. Either the SDK renamed " +
                                 "them - check controllerGrabObjectNames - or they are already " +
                                 "absent. Do not assume controllers are blocked: test it.");
            }
            else if (handsOnlyGrasping)
            {
                Debug.Log($"ParticipantView: hands-only grasping ON, {disabled} controller grab " +
                          "interactor(s) disabled. Controllers stay tracked; buttons still work.");
            }
        }
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
