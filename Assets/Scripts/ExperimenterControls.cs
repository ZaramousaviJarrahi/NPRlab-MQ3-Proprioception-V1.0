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
    [Header("Input routes")]
    public bool useControllerButtons = true;
    public bool useKeyboard = true;

    [Header("On-screen reminder")]
    [Tooltip("Shows the key/button map in the corner. Harmless - it only renders on the " +
             "desktop preview, not in the headset.")]
    public bool showShortcutList = true;

    private ExperimenterMode _experimenterMode;
    private TaskSequencer _taskSequencer;
    private HandVisibilityToggle _handVisibility;

    void Start()
    {
        _experimenterMode = GetComponent<ExperimenterMode>();
        _taskSequencer    = GetComponent<TaskSequencer>();
        _handVisibility   = GetComponent<HandVisibilityToggle>();

        Debug.Log("Experimenter controls ready.  Controller: X=start session, A=start trial, " +
                  "B=toggle hands, Y=clear, both grips+A=re-centre.  " +
                  "Keyboard: S / T / H / C / Shift+R.");
    }

    void Update()
    {
        if (useControllerButtons) ReadControllers();
        if (useKeyboard) ReadKeyboard();
    }

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
        _experimenterMode.StartExperiment();
        Debug.Log("SESSION STARTED (experimenter input).");
    }

    public void StartTrial()
    {
        if (_experimenterMode != null && !_experimenterMode.IsExperimentStarted())
        {
            Debug.LogWarning("Press start-session first (X / S) - nothing is recorded until then.");
            return;
        }
        if (_taskSequencer != null) _taskSequencer.StartTrial();
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
        if (!showShortcutList) return;
        GUI.Label(new Rect(10, 330, 700, 25),
            "Experimenter:  X/S = start session   A/T = start trial   B/H = hands   " +
            "Y/C = clear   grips+A or Shift+R = re-centre");
    }
}
