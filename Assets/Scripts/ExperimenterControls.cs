using UnityEngine;

// Lets the experimenter drive a session from inside VR.
//
// WHY THIS EXISTS:
// Every other control in this project is an OnGUI button, which needs a mouse cursor.
// In a standalone headset build there is no cursor, so those buttons cannot be pressed
// no matter whether they render. Without this script the app is unusable on the Quest.
//
// TWO INPUT ROUTES, use whichever works in your setup:
//
//   Quest controller          Keyboard (Bluetooth, paired to the headset)
//   -----------------         -----------------------------------------
//   X  (left)   Start session       S
//   A  (right)  Start trial         T
//   B  (right)  Toggle hands        H
//   Y  (left)   Clear targets       C
//   both grips + A  Re-centre grid  Shift + R
//
// A Bluetooth keyboard is usually the safer choice: picking up a controller can switch
// the headset out of hand-tracking mode, which would stop the participant grasping. If
// you do use controllers, set OVRManager > Hand Tracking Support to "Controllers and
// Hands" on the OVRCameraRig, or hand tracking will cut out.
//
// Re-centring the grid is deliberately awkward to trigger (two grips held, or Shift+R)
// because doing it mid-session moves every target and would break comparability with
// the trials already recorded.
//
// Attach to the same GameObject as ControlManager.
public class ExperimenterControls : MonoBehaviour
{
    [Header("Participant - set BEFORE starting the session")]
    [Tooltip("Right thumbstick UP/DOWN changes the participant number, LEFT/RIGHT the visit. "
           + "Locked once the session starts. There is no keyboard in VR, so this is how the "
           + "ID gets into the data file.")]
    public int participantNumber = 1;
    public string participantPrefix = "P";
    [Range(1, 3)] public int visitNumber = 1;

    [Header("Input routes")]
    public bool useControllerButtons = true;
    public bool useKeyboard = true;

    [Header("On-screen reminder")]
    [Tooltip("Shows the key/button map in the corner. Harmless - it only renders on the " +
             "desktop preview, not in the headset.")]
    public bool showShortcutList = true;

    [Tooltip("Clickable START SESSION / START TRIAL buttons on the desktop preview. The " +
             "keyboard shortcuts do not work in this project (it uses the new Input System), " +
             "so these are how you test in the editor. They do not appear in the headset.")]
    public bool showTestButtons = true;

    private ExperimenterMode _experimenterMode;
    private TaskSequencer _taskSequencer;
    private HandVisibilityToggle _handVisibility;
    private SessionRunner _sessionRunner;
    private DataRecorder _dataRecorder;
    private float _lastAdjust;

    void Start()
    {
        _experimenterMode = GetComponent<ExperimenterMode>();
        _taskSequencer    = GetComponent<TaskSequencer>();
        _handVisibility   = GetComponent<HandVisibilityToggle>();
        _sessionRunner    = GetComponent<SessionRunner>();
        _dataRecorder     = GetComponent<DataRecorder>();

        Debug.Log("Experimenter controls ready.  Controller: X=start session, A=start trial, " +
                  "B=toggle hands, Y=clear, both grips+A=re-centre.  " +
                  "Keyboard: S / T / H / C / Shift+R.");
    }

    void Update()
    {
        ReadSetupInput();
        if (useControllerButtons) ReadControllers();
        if (useKeyboard) ReadKeyboard();
    }

    // Participant number and visit are adjustable only before the session begins, so they
    // cannot be changed halfway through and split one person's data across two IDs.
    private void ReadSetupInput()
    {
        if (_experimenterMode != null && _experimenterMode.IsExperimentStarted()) return;
        if (Time.time - _lastAdjust < 0.25f) return;   // stops the stick spinning through values

        try
        {
            Vector2 stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
            if (stick.y > 0.6f)       { participantNumber++;  _lastAdjust = Time.time; }
            else if (stick.y < -0.6f) { participantNumber--;  _lastAdjust = Time.time; }
            else if (stick.x > 0.6f)  { visitNumber++;        _lastAdjust = Time.time; }
            else if (stick.x < -0.6f) { visitNumber--;        _lastAdjust = Time.time; }
        }
        catch (System.Exception) { /* no controllers - keyboard below still works */ }

        try
        {
            if (Input.GetKeyDown(KeyCode.UpArrow))    { participantNumber++; _lastAdjust = Time.time; }
            if (Input.GetKeyDown(KeyCode.DownArrow))  { participantNumber--; _lastAdjust = Time.time; }
            if (Input.GetKeyDown(KeyCode.RightArrow)) { visitNumber++;       _lastAdjust = Time.time; }
            if (Input.GetKeyDown(KeyCode.LeftArrow))  { visitNumber--;       _lastAdjust = Time.time; }
        }
        catch (System.Exception) { }

        participantNumber = Mathf.Clamp(participantNumber, 1, 999);
        visitNumber = Mathf.Clamp(visitNumber, 1, 3);
    }

    public string ParticipantId() => $"{participantPrefix}{participantNumber:00}";

    private void ReadControllers()
    {
        // Guarded: OVRInput throws if the OVR runtime is not present (e.g. some editor setups).
        try
        {
            bool bothGrips = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch)
                          && OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);

            if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))        // A
            {
                if (bothGrips) RecentreGrid(); else StartTrial();
            }
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch)) ToggleHands();   // B
            if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch)) StartSession();  // X
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch)) ClearTargets();  // Y
        }
        catch (System.Exception)
        {
            useControllerButtons = false;
            Debug.LogWarning("ExperimenterControls: controller input unavailable, using keyboard only.");
        }
    }

    private void ReadKeyboard()
    {
        // Guarded: throws if the project is set to the new Input System only.
        try
        {
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (Input.GetKeyDown(KeyCode.R) && shift) { RecentreGrid(); return; }
            if (Input.GetKeyDown(KeyCode.S)) StartSession();
            if (Input.GetKeyDown(KeyCode.T)) StartTrial();
            if (Input.GetKeyDown(KeyCode.H)) ToggleHands();
            if (Input.GetKeyDown(KeyCode.C)) ClearTargets();
        }
        catch (System.Exception)
        {
            useKeyboard = false;
            Debug.LogWarning("ExperimenterControls: keyboard input unavailable (project uses the " +
                             "new Input System), using controllers only.");
        }
    }

    // --- actions -----------------------------------------------------------

    public void StartSession()
    {
        if (_experimenterMode == null) return;
        if (_experimenterMode.IsExperimentStarted())
        {
            Debug.Log("Session already started.");
            return;
        }
        if (_dataRecorder != null)
        {
            _dataRecorder.participantId = ParticipantId();
            _dataRecorder.visitLabel = "Visit" + visitNumber;
        }
        _experimenterMode.StartExperiment();
        if (_sessionRunner != null) _sessionRunner.BuildSessionPlan();
        Debug.Log("SESSION STARTED (experimenter input).");
    }

    public void StartTrial()
    {
        if (_experimenterMode != null && !_experimenterMode.IsExperimentStarted())
        {
            Debug.LogWarning("Press start-session first (X / S) - nothing is recorded until then.");
            return;
        }
        if (_sessionRunner != null) _sessionRunner.StartNextTrial();
        else if (_taskSequencer != null) _taskSequencer.StartTrial();
    }

    public void ToggleHands()
    {
        if (_handVisibility == null) return;
        if (_handVisibility.handsVisible) _handVisibility.HideHands();
        else _handVisibility.ShowHands();
    }

    public void ClearTargets()
    {
        if (_taskSequencer != null) _taskSequencer.ClearTargets();

        // Clearing the board mid-trial means "abandon this one". SessionRunner now refuses to
        // start a new trial while one is running, so without this the start button would keep
        // refusing after a trial that could not be completed.
        if (_sessionRunner != null) _sessionRunner.AbandonCurrentTrial();
    }

    public void RecentreGrid()
    {
        if (_taskSequencer == null) return;
        _taskSequencer.CalibrateGrid();
        Debug.LogWarning("GRID RE-CENTRED mid-session. Target positions have moved - trials " +
                         "recorded before and after this point are not directly comparable.");
    }

    void OnGUI()
    {
        // CLICKABLE BUTTONS - editor only, and the only way to drive a session on the
        // desktop preview. This project uses the new Input System, so Input.GetKeyDown
        // never fires and the keyboard shortcuts above are dead in the editor. OnGUI is
        // not rendered in a headset build, so these cost nothing there.
        if (showTestButtons)
        {
            GUIStyle btn = new GUIStyle(GUI.skin.button) { fontSize = 16, fontStyle = FontStyle.Bold };
            float x = 740f, y = 60f, w = 230f, h = 38f;

            GUI.Label(new Rect(x, y - 24f, w, 22f), "TEST BUTTONS (editor only)");
            if (GUI.Button(new Rect(x, y, w, h), "1.  START SESSION  (X)", btn)) StartSession();
            if (GUI.Button(new Rect(x, y + 44f, w, h), "2.  START TRIAL  (A)", btn)) StartTrial();
            if (GUI.Button(new Rect(x, y + 88f, w, h), "Toggle hands  (B)", btn)) ToggleHands();
            if (GUI.Button(new Rect(x, y + 132f, w, h), "Clear targets  (Y)", btn)) ClearTargets();
            if (GUI.Button(new Rect(x, y + 176f, w, h), "Re-centre grid", btn)) RecentreGrid();

            GUI.Label(new Rect(x, y + 220f, w, 22f),
                      _experimenterMode == null ? "NO ExperimenterMode!" :
                      (_experimenterMode.IsExperimentStarted() ? "session: RUNNING" : "session: not started"));
        }

        if (_experimenterMode != null && !_experimenterMode.IsExperimentStarted())
        {
            GUIStyle huge = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold };
            GUI.Label(new Rect(10, 375, 900, 40),
                      $"PARTICIPANT {ParticipantId()}    VISIT {visitNumber}", huge);
            GUI.Label(new Rect(10, 415, 900, 25),
                      "Right thumbstick: up/down = participant, left/right = visit.  Then press X to start.");
        }

        if (!showShortcutList) return;
        GUI.Label(new Rect(10, 330, 700, 25),
            "Experimenter:  X/S = start session   A/T = start trial   B/H = hands   " +
            "Y/C = clear   grips+A or Shift+R = re-centre");
    }
}
