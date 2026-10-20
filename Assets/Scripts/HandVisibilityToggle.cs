using System.Collections.Generic;
using Oculus.Interaction;
using UnityEngine;

// Hides / shows the virtual hands for the Visit 3 proprioception condition.
//
// ---------------------------------------------------------------------------------------
// THREE SEPARATE THINGS CAN SHOW A PARTICIPANT WHERE THEIR HAND IS, AND THEY HIDE
// DIFFERENTLY. Every one of them has been a bug at some point.
//
//   1. The hand meshes, drawn by the Interaction SDK's HandVisual components. These are
//      NOT under OVRCameraRig's hand anchors - the SDK puts them under OVRInteraction, a
//      different branch - so the original version of this script, which walked only the
//      hand anchors, never touched them. Hidden via ForceOffVisibility; see below.
//
//   2. The fingertip marker spheres, LeftFingerTip and RightFingerTip. Root-level scene
//      objects that ControlManager moves to the tracked fingertip every frame. A dot on
//      the fingertip is hand position, told more precisely than the hand itself tells it.
//      Hidden by disabling their Renderers, which works because nothing else manages them.
//
//   3. Anything dragged into the override fields below. Same treatment as the spheres.
//
// ---------------------------------------------------------------------------------------
// WHY THE HAND MESHES NEED ForceOffVisibility AND NOT renderer.enabled = false.
//
// HandVisual.UpdateVisibility() runs every frame and owns its SkinnedMeshRenderer:
//
//     if (!Hand.IsTrackedDataValid) { if (IsVisible || ForceOffVisibility) r.enabled = false; }
//     else                         { if (!IsVisible && !ForceOffVisibility) r.enabled = true; }
//
// So setting r.enabled = false from outside survives only until the next frame in which the
// hand is tracked - at which point the SDK turns it straight back on. Observed on the
// headset, 20 Oct 2026: the hand vanished on the button press and reappeared the instant the
// participant reached to grasp, which is the worst possible moment for it to come back.
//
// ForceOffVisibility is the SDK's own supported way to say "keep this off". Set it and
// HandVisual enforces the hidden state itself, every frame, instead of fighting us.
//
// Renderers that are NOT the one HandVisual manages (the prefab carries more than one mesh
// variant) are still disabled directly, since nothing re-asserts those.
//
// HOW HIDING WORKS IN GENERAL: Renderers only, never the GameObject and never the colliders.
// Deactivating would also switch off hand tracking, colliders and the grab interactors, so
// the participant could not reach or grasp. The hidden condition needs the hand to keep
// working and simply not be seen.
public class HandVisibilityToggle : MonoBehaviour
{
    [Header("Optional manual override - normally leave these empty")]
    [Tooltip("Only set these if you deliberately want to hide something other than the " +
             "hand visuals the SDK provides. Leave empty for normal use.")]
    public Transform leftHandOverride;
    public Transform rightHandOverride;

    [Tooltip("Hide the fingertip marker spheres along with the hands. Leave ON for Visit 3 " +
             "- a sphere left on the fingertip tells the participant exactly where their " +
             "hand is, which is what the hidden condition removes. Turn off only if you are " +
             "deliberately testing with the spheres visible.")]
    public bool includeFingertipSpheres = true;

    [Header("Status (read-only while running)")]
    public bool handsVisible = true;
    [Tooltip("HandVisual components currently forced off. Should be 2 when hidden.")]
    public int visualsForcedOff;
    [Tooltip("Extra renderers disabled - the fingertip spheres and any overrides.")]
    public int renderersDisabled;

    private OVRCameraRig _rig;
    private readonly List<Renderer> _hiddenRenderers = new List<Renderer>();
    private readonly List<HandVisual> _forcedOff = new List<HandVisual>();

    void Start()
    {
        handsVisible = true;
        visualsForcedOff = 0;
        renderersDisabled = 0;
        _rig = FindAnyObjectByType<OVRCameraRig>();

        // Count what is actually hideable up front, so a scene that cannot support the
        // hidden condition says so at startup rather than at the first hidden block.
        int visuals = AllHandVisuals().Length;

        if (visuals == 0 && leftHandOverride == null && rightHandOverride == null)
        {
            Debug.LogError("HandVisibilityToggle: no HandVisual in this scene, so there is no " +
                           "visible hand to hide OR to show. Do not run a Visit 3 session.");
        }
        else
        {
            Debug.Log($"Hands visible at start ({visuals} HandVisual found)");
        }
    }

    // Located by component type, not by name or hierarchy position: the Interaction SDK
    // renames and reparents these objects between versions, so a path or a dragged
    // reference would break quietly. A HandVisual is by definition the thing that draws a
    // hand, so this stays correct as long as the SDK does.
    private static HandVisual[] AllHandVisuals()
    {
        return FindObjectsByType<HandVisual>(FindObjectsInactive.Include,
                                             FindObjectsSortMode.None);
    }

    // Everything hidden by disabling renderers directly: the spheres and any overrides.
    // The hand visuals are deliberately NOT here - they go through ForceOffVisibility.
    private IEnumerable<Transform> RendererRoots()
    {
        if (leftHandOverride != null) yield return leftHandOverride;
        if (rightHandOverride != null) yield return rightHandOverride;

        if (includeFingertipSpheres && ControlManager.Singleton != null)
        {
            if (ControlManager.Singleton.LeftFingerTipSphere != null)
                yield return ControlManager.Singleton.LeftFingerTipSphere;
            if (ControlManager.Singleton.RightFingerTipSphere != null)
                yield return ControlManager.Singleton.RightFingerTipSphere;
        }
    }

    void OnGUI()
    {
        // Editor and desktop preview only. OnGUI does not render inside the headset - use
        // the B button on the right controller, or H on a paired keyboard.
        if (handsVisible)
        {
            if (GUI.Button(new Rect(10, 70, 200, 50), "Hide Hands")) HideHands();
        }
        else
        {
            if (GUI.Button(new Rect(10, 70, 200, 50), "Show Hands")) ShowHands();
        }
    }

    public void HideHands()
    {
        // 1. The hand meshes, through the SDK's own switch so it stops re-enabling them.
        _forcedOff.Clear();
        foreach (HandVisual hv in AllHandVisuals())
        {
            if (hv == null || hv.ForceOffVisibility) continue;   // leave already-off alone
            hv.ForceOffVisibility = true;
            _forcedOff.Add(hv);
        }

        // 2. The spheres, overrides, and any extra mesh variants sitting under a HandVisual
        //    that HandVisual itself does not manage.
        _hiddenRenderers.Clear();
        foreach (Transform root in RendererRoots())
            DisableRenderersUnder(root);
        foreach (HandVisual hv in AllHandVisuals())
            if (hv != null) DisableRenderersUnder(hv.transform);

        visualsForcedOff = _forcedOff.Count;
        renderersDisabled = _hiddenRenderers.Count;
        handsVisible = false;

        if (visualsForcedOff == 0 && renderersDisabled == 0)
        {
            // Loud on purpose: an earlier version printed "Hands HIDDEN" even when it had
            // hidden nothing at all, which could invalidate a whole session unnoticed.
            Debug.LogError("HandVisibilityToggle: Hide Hands found NOTHING to hide - " +
                           "the hands are still visible! Do not collect hidden-hand data.");
        }
        else if (visualsForcedOff == 0)
        {
            Debug.LogWarning($"HandVisibilityToggle: {renderersDisabled} renderer(s) hidden but " +
                             "NO hand visual was switched off. That is the fingertip spheres " +
                             "alone - the hands are still visible. Check before collecting.");
        }
        else
        {
            Debug.Log($"Hands HIDDEN ({visualsForcedOff} hand visuals off, " +
                      $"{renderersDisabled} extra renderers disabled)");
        }
    }

    private void DisableRenderersUnder(Transform root)
    {
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (!r.enabled) continue;              // leave already-off renderers alone
            if (_hiddenRenderers.Contains(r)) continue;
            r.enabled = false;
            _hiddenRenderers.Add(r);
        }
    }

    public void ShowHands()
    {
        foreach (HandVisual hv in _forcedOff)
            if (hv != null) hv.ForceOffVisibility = false;
        int visuals = _forcedOff.Count;
        _forcedOff.Clear();

        int restored = 0;
        foreach (Renderer r in _hiddenRenderers)
            if (r != null) { r.enabled = true; restored++; }
        _hiddenRenderers.Clear();

        visualsForcedOff = 0;
        renderersDisabled = 0;
        handsVisible = true;
        Debug.Log($"Hands VISIBLE ({visuals} hand visuals restored, {restored} renderers re-enabled)");
    }
}
