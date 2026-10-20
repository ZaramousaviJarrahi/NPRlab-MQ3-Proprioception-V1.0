using UnityEngine;

/// <summary>
/// Turns passthrough on and off, and sets the camera to match.
///
/// WHY THIS EXISTS. Visits 1 and 2 run in AR: passthrough on, the participant sees the real
/// room and their own real hand. Visit 3 has to run with passthrough OFF in both conditions,
/// because there is no way to subtract a real hand from the camera image - Meta exposes no
/// per-region passthrough, and a partial occluder fails exactly at the endpoint, where the
/// hand arrives at the target and where accuracy is measured. So Visit 3 renders a virtual
/// hand in the Visible condition and nothing in the Hidden one, and the only difference
/// between the two conditions is the hand.
///
/// TWO THINGS HAVE TO CHANGE TOGETHER, which is the whole reason this is a component and not
/// one line in SessionRunner:
///
///   1. OVRManager.isInsightPassthroughEnabled - whether the passthrough layer runs at all.
///   2. The centre-eye camera's CLEAR FLAGS and background ALPHA - whether the camera leaves
///      the frame transparent for passthrough to show through, or paints over it.
///
/// Change only the first and you get a black or smeared view rather than a clean switch: the
/// passthrough layer stops producing an image while the camera is still clearing to
/// transparent, so nothing fills the background. Change only the second and passthrough is
/// still running underneath, hidden. Both, in the same call, or neither.
///
/// HOW THE TOGGLE ACTUALLY TAKES EFFECT. OVRManager.isInsightPassthroughEnabled is a plain
/// public field, normally set once in the Inspector, so it looks like a startup-only setting.
/// It is not: OVRManager.Update() calls UpdateInsightPassthrough(isInsightPassthroughEnabled)
/// every frame, compares it against the live state, and initialises or shuts down passthrough
/// to match. Setting the field from script is therefore enough, and the change lands on the
/// next frame. Verified against the installed SDK (com.meta.xr.sdk.core, OVRManager.cs:3022).
///
/// This component does nothing on its own. SessionRunner calls SetPassthrough(false) once at
/// the start of a Visit 3 session and leaves it off for the whole visit.
/// </summary>
public class PassthroughController : MonoBehaviour
{
    [Header("Camera")]
    [Tooltip("Leave empty to find OVRCameraRig's centre-eye camera at runtime. Only set this " +
             "if you deliberately want to drive a different camera.")]
    public Camera targetCamera;

    [Header("What the view looks like with passthrough OFF")]
    [Tooltip("Background colour in VR. A visible dark grey rather than black on purpose: " +
             "black is also what a failed passthrough looks like, and the two should not be " +
             "indistinguishable when you are checking the headset. Replace this with the " +
             "Visit 3 environment once that exists.")]
    public Color vrBackgroundColor = new Color(0.12f, 0.12f, 0.14f, 1f);

    [Tooltip("Use the scene's skybox instead of a flat colour when passthrough is off.")]
    public bool useSkyboxInVR = false;

    [Header("Status (read-only while running)")]
    public bool passthroughOn = true;

    private Camera _cam;
    private bool _captured;
    private CameraClearFlags _originalFlags;
    private Color _originalBackground;

    void Start()
    {
        // Report once, rather than failing silently in a headset where nobody can see a log.
        if (OVRManager.instance == null)
            Debug.LogError("PassthroughController: no OVRManager in this scene. Passthrough " +
                           "cannot be switched - do not run a Visit 3 session until this is fixed.");
        else if (!OVRManager.IsInsightPassthroughSupported())
            Debug.LogWarning("PassthroughController: this device reports that passthrough is " +
                             "not supported. Visits 1 and 2 will not show the room.");

        passthroughOn = OVRManager.instance != null && OVRManager.instance.isInsightPassthroughEnabled;
    }

    /// <summary>
    /// The one call SessionRunner makes. True = AR (Visits 1 and 2), false = VR (Visit 3).
    /// Safe to call repeatedly with the same value.
    /// </summary>
    public void SetPassthrough(bool on)
    {
        Camera cam = ResolveCamera();

        // Remember how the scene was authored, the first time only, so AR is restored to the
        // exact values the scene shipped with rather than to this script's guess at them.
        if (cam != null && !_captured)
        {
            _originalFlags = cam.clearFlags;
            _originalBackground = cam.backgroundColor;
            _captured = true;
        }

        if (OVRManager.instance != null)
            OVRManager.instance.isInsightPassthroughEnabled = on;

        if (cam != null)
        {
            if (on)
            {
                // Back to whatever the scene had. On this project that is SolidColor with a
                // transparent black background, which is what lets passthrough show through.
                cam.clearFlags = _originalFlags;
                cam.backgroundColor = _originalBackground;
            }
            else
            {
                cam.clearFlags = useSkyboxInVR ? CameraClearFlags.Skybox
                                               : CameraClearFlags.SolidColor;
                // Alpha must be 1 here. A transparent clear with passthrough shut down
                // leaves the compositor nothing to show.
                Color c = vrBackgroundColor;
                c.a = 1f;
                cam.backgroundColor = c;
            }
        }

        passthroughOn = on;
        Debug.Log($"PassthroughController: passthrough {(on ? "ON (AR)" : "OFF (VR)")}" +
                  (cam != null ? $", camera clears to {cam.clearFlags}" : ", NO CAMERA FOUND"));
    }

    /// Convenience for a test button in the Inspector or an experimenter key.
    public void TogglePassthrough() => SetPassthrough(!passthroughOn);

    // Resolved at runtime rather than stored in an Inspector field, for the same reason
    // HandVisibilityToggle asks OVRCameraRig for its hand anchors: OVRCameraRig is
    // [ExecuteInEditMode] and rebuilds its own anchors, so a camera dragged into a slot can
    // silently revert to None between sessions.
    private Camera ResolveCamera()
    {
        if (targetCamera != null) return targetCamera;
        if (_cam != null) return _cam;

        OVRCameraRig rig = FindAnyObjectByType<OVRCameraRig>();
        if (rig != null && rig.centerEyeAnchor != null)
            _cam = rig.centerEyeAnchor.GetComponent<Camera>();

        if (_cam == null) _cam = Camera.main;

        if (_cam == null)
            Debug.LogError("PassthroughController: could not find a camera. Passthrough will " +
                           "switch but the view will not, which looks like a black screen.");
        return _cam;
    }

    // Leaving the editor or the scene with passthrough forced off would persist into the next
    // play session and look like a broken build.
    void OnDisable()
    {
        if (_captured && !passthroughOn) SetPassthrough(true);
    }
}
