using System.Collections.Generic;
using UnityEngine;

// Hides / shows the virtual hands for the Visit 3 proprioception condition.
//
// WHY THERE ARE NO HAND FIELDS TO DRAG IN ANY MORE:
// OVRCameraRig is marked [ExecuteInEditMode] and rebuilds its own hand anchors inside
// EnsureGameObjectIntegrity(), which runs every Update - in the editor as well as at
// runtime. Any anchor dragged into an Inspector field gets replaced by a new object and
// the reference silently reverts to None. So this script asks OVRCameraRig for its
// current anchors at runtime instead of storing them.
//
// HOW HIDING WORKS:
// It disables the Renderers under each hand anchor rather than deactivating the anchor
// itself. Deactivating the anchor would also switch off hand tracking, colliders and the
// grab interactors, so the participant could not reach or grasp. For the hidden-hand
// condition we need the hand to keep working and simply not be seen.
public class HandVisibilityToggle : MonoBehaviour
{
    [Header("Optional manual override - normally leave these empty")]
    [Tooltip("Only set these if you deliberately want to hide something other than " +
             "OVRCameraRig's own hand anchors. Leave empty for normal use.")]
    public Transform leftHandOverride;
    public Transform rightHandOverride;

    [Header("Status (read-only while running)")]
    public bool handsVisible = true;

    private OVRCameraRig _rig;
    private readonly List<Renderer> _hiddenRenderers = new List<Renderer>();

    void Start()
    {
        handsVisible = true;
        _rig = FindAnyObjectByType<OVRCameraRig>();

        if (_rig == null && leftHandOverride == null && rightHandOverride == null)
        {
            Debug.LogError("HandVisibilityToggle: no OVRCameraRig found in this scene. " +
                           "Hide Hands will NOT work - do not run a hidden-hand session until this is fixed.");
        }
        else
        {
            Debug.Log("Hands visible at start");
        }
    }

    // Yields whatever should be treated as "the hands" right now.
    private IEnumerable<Transform> HandRoots()
    {
        if (leftHandOverride != null) yield return leftHandOverride;
        else if (_rig != null && _rig.leftHandAnchor != null) yield return _rig.leftHandAnchor;

        if (rightHandOverride != null) yield return rightHandOverride;
        else if (_rig != null && _rig.rightHandAnchor != null) yield return _rig.rightHandAnchor;
    }

    void OnGUI()
    {
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
        if (_rig == null) _rig = FindAnyObjectByType<OVRCameraRig>();

        _hiddenRenderers.Clear();
        foreach (Transform root in HandRoots())
        {
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled) continue;          // leave already-off renderers alone
                r.enabled = false;
                _hiddenRenderers.Add(r);
            }
        }

        handsVisible = false;

        if (_hiddenRenderers.Count == 0)
        {
            // Loud on purpose: the old version printed "Hands HIDDEN" even when it had
            // hidden nothing at all, which could invalidate a whole session unnoticed.
            Debug.LogError("HandVisibilityToggle: Hide Hands found NOTHING to hide - " +
                           "the hands are still visible! Do not collect hidden-hand data until this is fixed.");
        }
        else
        {
            Debug.Log($"Hands HIDDEN ({_hiddenRenderers.Count} renderers disabled)");
        }
    }

    public void ShowHands()
    {
        int restored = 0;
        foreach (Renderer r in _hiddenRenderers)
        {
            if (r != null) { r.enabled = true; restored++; }
        }
        _hiddenRenderers.Clear();
        handsVisible = true;
        Debug.Log($"Hands VISIBLE ({restored} renderers re-enabled)");
    }
}
