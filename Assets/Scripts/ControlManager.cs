using Oculus.Interaction.Input;
using System;
using System.Collections.Generic;
using TMPro;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Netcode;
using Unity.Services.Lobbies;
using Unity.Services.Multiplayer;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;
using static OVRLocatable;

public class ControlManager : NetworkBehaviour
{
    public static ControlManager Singleton { get; private set; }

    [Header("Participant-visible feedback")]
    [Tooltip("Tints the sphere nearest the hand. OFF for real sessions.\n\n" +
             "The tint tells the participant where their hand is relative to the targets. " +
             "In the hidden-hand condition that is exactly the information the condition " +
             "removes, handed back through a different channel, so H4 could not be tested " +
             "with it on. Turning it off only for Visit 3 would make Visit 3 differ from " +
             "training in two ways at once, so it is off for every visit and hand " +
             "visibility stays the single manipulated variable.\n\n" +
             "Only the TINT is affected. The nearest sphere is still the only grabbable " +
             "one - that gating is what makes grasping work and is untouched.")]
    public bool highlightClosestTarget = false;
    // Fires every time a target is successfully grasped/captured (independent of network state).
    public event Action OnTargetCaptured;

    // Everything the data recorder needs about a single grasp.
    public struct CaptureData
    {
        public Vector3 targetPosition;      // centre of the grasped target
        public Vector3 fingerTipPosition;   // index fingertip at the moment of grasp
        public float endpointErrorMeters;   // distance between the two = endpoint error
        public bool usedLeftHand;
        public bool isSimulated;            // true only for the TEST button
        public int targetIndex;             // grid position 1-9, or -1 if unknown
    }

    // Fired alongside OnTargetCaptured, carrying the measurements for recording.
    public event Action<CaptureData> OnTargetCapturedDetailed;

    // Fired whenever a target is spawned, so a trial's stopwatch can be started.
    public event Action OnTargetSpawned;
    [SerializeField] private GameObject _targetPrefab;
    [SerializeField] private float _pivotDistance = 0.2f;
    [SerializeField] private float _pivotScale = 0.2f;
    [SerializeField] private float _captureableRange = 0.3f;
    [SerializeField] private Material _lineRendererMaterial;
    // [SerializeField] private OVRHand _leftHand;
    // [SerializeField] private OVRHand _rightHand;
    [SerializeField] private Transform _leftFingerTipSphere;
    [SerializeField] private Transform _rightFingerTipSphere;
    [SerializeField] private Transform _spawnContentsParent;
    [SerializeField] private TextMeshProUGUI _isDebugLineRenderingText;
    [SerializeField] private LineRenderer _lineRenderer;
    [SerializeField] private Transform _lineRendererDebugController;
    private Vector3 _rightIndexTipPosition;
    private Vector3 _leftIndexTipPosition;
    private int _numberOfTargetsSpawned = 0;
    private List<Transform> _targets;
    private Vector3 _pivotPosition;
    private List<Transform> _targetsInRange;
    private Transform _closestTarget = null;
    private float _debugTimer = 0;
    private float _debugTime = 0.5f;
    private bool _isDebugLineRendering = false;


    void Awake() {
        // Claim the singleton FIRST.
        //
        // This assignment used to sit at the BOTTOM of Awake, after the NetworkManager
        // registration below. NetworkManager.Singleton is null until NetworkManager's own
        // Awake has run, and Unity does not guarantee which component wakes first. When
        // ControlManager won that race, the very first line threw a NullReferenceException,
        // Awake aborted, and ControlManager.Singleton was never assigned. Every script that
        // guards with "if (ControlManager.Singleton == null) return;" then silently did
        // nothing - no targets, no error the experimenter could see in the headset.
        // Assigning first makes the manager work with or without networking running.
        if (Singleton != null && Singleton != this)
        {
            Debug.LogError($"More than one {nameof(ControlManager)} in the scene. Keeping the first.");
            return;
        }
        Singleton = this;

        if (NetworkManager.Singleton == null)
        {
            Debug.LogWarning("ControlManager: no NetworkManager available yet - running locally. " +
                             "This is normal for the standalone headset build, where nothing " +
                             "starts a host or client.");
            return;
        }

        NetworkManager.Singleton.OnClientConnectedCallback += (clientId) =>
        {
            if (clientId == NetworkManager.Singleton.LocalClientId) // only register for self
            {
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(
                    "SpawnFromServer", OnSpawnInputMessageReceived);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(
                    "SpawnRandomFromServer", OnSpawnRandomInputMessageReceived);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(
                    "DespawnFromServer", OnDesapwnInputMessageReceived);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(
                    "DespawnAllFromServer", OnDesapwnAllMessageReceived);
            }
        };
    }

    void Start() {
        _pivotPosition = new Vector3(-_pivotScale, _pivotScale, 0);
        _targetsInRange = new List<Transform>();
        _targets = new List<Transform>();
    }

    void Update()
    {
        UpdateHandPositions();
        FindTargetsInRange();
        FindClosestTarget();
        RenderClosestTargetAim();
        UpdateDebugSphereLineRenderer();
    }

    private void UpdateHandPositions() {
        OVRPlugin.HandState _leftHandState = default(OVRPlugin.HandState);
        OVRPlugin.HandState _rightHandState = default(OVRPlugin.HandState);

        // OVRPlugin reports bone positions in the rig's TRACKING space. Everything this
        // study measures - the target grid, the recorded target positions, endpoint error -
        // is in WORLD space. The two coincide only while OVRCameraRig sits at the world
        // origin unrotated. It currently does, so this conversion is a no-op today and the
        // recorded numbers are unchanged. But nothing enforces it, and anything that moves
        // the rig (the Locomotor in this scene, a recentre) would otherwise corrupt
        // endpoint error silently, with no error message at all. Endpoint error is a
        // primary outcome, so convert explicitly rather than relying on the rig staying put.
        Transform tracking = TrackingSpace();

        if (OVRPlugin.GetHandState(OVRPlugin.Step.Render, OVRPlugin.Hand.HandLeft, ref _leftHandState))
        {
            _leftIndexTipPosition = IndexTipToWorld(_leftHandState, tracking);
        }
        // This second call used to pass 'ref _leftHandState' while querying the RIGHT hand,
        // and then read the tip back out of _leftHandState. It gave the right answer only
        // because the left tip had already been extracted just above: _rightHandState was
        // dead, and swapping the order of these two blocks would have broken the right
        // hand's position with no compile error and no warning.
        if (OVRPlugin.GetHandState(OVRPlugin.Step.Render, OVRPlugin.Hand.HandRight, ref _rightHandState))
        {
            _rightIndexTipPosition = IndexTipToWorld(_rightHandState, tracking);
        }
        _leftFingerTipSphere.position = _leftIndexTipPosition;
        _rightFingerTipSphere.position = _rightIndexTipPosition;
    }

    // The index-fingertip bone, converted from OVRPlugin's tracking-space, right-handed
    // convention into Unity world space. The (x, y, -z) flip is the handedness change and
    // is unchanged from the original code; TransformPoint is the tracking -> world step.
    private static Vector3 IndexTipToWorld(OVRPlugin.HandState state, Transform trackingSpace)
    {
        var tip = state.BonePositions[(int)OVRPlugin.BoneId.XRHand_IndexTip];
        Vector3 inTrackingSpace = new Vector3(tip.x, tip.y, -tip.z);
        return trackingSpace != null ? trackingSpace.TransformPoint(inTrackingSpace)
                                     : inTrackingSpace;
    }

    private OVRCameraRig _cameraRig;
    private bool _trackingSpaceChecked = false;

    // Cached tracking-space transform, with a one-time report of whether it is where the
    // rest of the code assumes it is. Logged rather than assumed, because a silent offset
    // here would bias every endpoint-error measurement without anything looking wrong.
    private Transform TrackingSpace()
    {
        if (_cameraRig == null) _cameraRig = FindAnyObjectByType<OVRCameraRig>();
        Transform tracking = _cameraRig != null ? _cameraRig.trackingSpace : null;

        if (!_trackingSpaceChecked && tracking != null)
        {
            _trackingSpaceChecked = true;
            bool atOrigin = tracking.position.sqrMagnitude < 1e-6f
                            && Quaternion.Angle(tracking.rotation, Quaternion.identity) < 0.1f;
            if (atOrigin)
            {
                Debug.Log("ControlManager: tracking space is at the world origin, so fingertip " +
                          "positions and target positions are already in the same frame.");
            }
            else
            {
                Debug.LogWarning("ControlManager: the rig's tracking space is NOT at the world origin " +
                                 $"(position {tracking.position}, rotation {tracking.rotation.eulerAngles}). " +
                                 "Fingertip positions are converted correctly from here on, but any data " +
                                 "recorded before this fix, with the rig in this state, has endpoint errors " +
                                 "that are wrong by that offset.");
            }
        }
        return tracking;
    }

    // Removes destroyed targets from both lists before anything reads them.
    //
    // WHY: a captured target fades and is destroyed, but it was still sitting in
    // _targetsInRange. FindClosestTarget then read .position off a destroyed object and
    // threw a NullReferenceException - 28 times in one 13-trial session. Update() aborts
    // at that point, so for that frame the closest target is never recalculated. Capture()
    // depends on the closest target, so this was quietly corrupting the thing that decides
    // whether a grasp counts.
    private void PruneDestroyedTargets() {
        for (int i = _targets.Count - 1; i >= 0; i--)
            if (_targets[i] == null) _targets.RemoveAt(i);

        for (int i = _targetsInRange.Count - 1; i >= 0; i--)
            if (_targetsInRange[i] == null) _targetsInRange.RemoveAt(i);

        if (_closestTarget == null) _closestTarget = null;   // collapse a destroyed ref to a real null
    }

    private void FindTargetsInRange() {
        PruneDestroyedTargets();

        for (int i = 0; i < _targets.Count; i++)
        {
            if (Vector3.Distance(_leftIndexTipPosition, _targets[i].position) < _captureableRange || Vector3.Distance(_rightIndexTipPosition, _targets[i].position) < _captureableRange)
            {
                if (!_targetsInRange.Contains(_targets[i]))
                {
                    _targetsInRange.Add(_targets[i]);
                    NetworkDebugConsole.Singleton.SetDebugString($"Target {i} is in range. {_targetsInRange.Count} targets in range.");
                }
            }
            else
            {
                if (_targetsInRange.Contains(_targets[i]))
                {
                    _targetsInRange.Remove(_targets[i]);
                    NetworkDebugConsole.Singleton.SetDebugString($"Target {i} is out of range. {_targetsInRange.Count} targets in range.");
                }
            }
        }
    }

    public void ToggleDebugLineRenderer() {
        if (_isDebugLineRendering)
        {
            _isDebugLineRenderingText.text = "Show Debug";
            _isDebugLineRendering = false;
        }
        else
        {
            _isDebugLineRenderingText.text = "Hide Debug";
            _isDebugLineRendering = true;
        }
    }

    private void FindClosestTarget() {
        Transform _previousClosestTarget = _closestTarget;
        Transform _leftClosestTarget = _closestTarget;
        float _leftClosestDistance = float.PositiveInfinity;
        Transform _rightClosestTarget = _closestTarget;
        float _rightClosestDistance = float.PositiveInfinity;
        if (_numberOfTargetsSpawned > 0)
        {
            if (_targetsInRange.Count > 0)
            {
                for (int i = 0; i < _targetsInRange.Count; i++)
                {
                    if (Vector3.Distance(_leftIndexTipPosition, _targetsInRange[i].position) < _leftClosestDistance)
                    {
                        _leftClosestTarget = _targetsInRange[i];
                        _leftClosestDistance = Vector3.Distance(_leftIndexTipPosition, _targetsInRange[i].position);
                    }
                    if (Vector3.Distance(_rightIndexTipPosition, _targetsInRange[i].position) < _rightClosestDistance)
                    {
                        _rightClosestTarget = _targetsInRange[i];
                        _rightClosestDistance = Vector3.Distance(_rightIndexTipPosition, _targetsInRange[i].position);
                    }
                }
                if (_leftClosestDistance < _rightClosestDistance)
                {
                    _closestTarget = _leftClosestTarget;
                }
                else
                {
                    _closestTarget = _rightClosestTarget;
                }
                if (_closestTarget != _previousClosestTarget)
                {
                    if (_previousClosestTarget.transform.TryGetComponent(out LineRenderer lineRenderer))
                    {
                        Destroy(lineRenderer);
                    }
                }
            }
            else
            {
                if (_closestTarget != null)
                {
                    if (_closestTarget.TryGetComponent(out LineRenderer _lineRenderer))
                    {
                        Destroy(_lineRenderer);
                        _closestTarget = null;
                    }
                }
            }
        }
        else
        {
            _closestTarget = null;
        }
    }

    public void DestroyThisTarget(Transform _targetToBeDestroyed) {
        if (_targets.Contains(_targetToBeDestroyed))
        {
            _targets.Remove(_targetToBeDestroyed);
        }
        if (_targetsInRange.Contains(_targetToBeDestroyed))
        {
            _targetsInRange.Remove(_targetToBeDestroyed);
        }
        if (_closestTarget == _targetToBeDestroyed)
        {
            _closestTarget = null;
        }
    }

    private void RenderClosestTargetAim() {

        if (_targetsInRange.Count > 0 && _isDebugLineRendering)
        {
            if (_closestTarget != null)
            {
                if (_closestTarget.TryGetComponent(out LineRenderer _lineRenderer))
                {
                    /*_lineRenderer.startWidth = 0.01f;
                    _lineRenderer.endWidth = 0.01f;
                    _lineRenderer.useWorldSpace = true;
                    _lineRenderer.material = _lineRendererMaterial;
                    _lineRenderer.positionCount = 2;*/
                    _lineRenderer.SetPosition(0, _closestTarget.position);
                    if (Vector3.Distance(_leftIndexTipPosition, _closestTarget.position) < Vector3.Distance(_rightIndexTipPosition, _closestTarget.position))
                    {
                        _lineRenderer.SetPosition(1, _leftIndexTipPosition);
                    }
                    else
                    {
                        _lineRenderer.SetPosition(1, _rightIndexTipPosition);
                    }
                }
                else
                {
                    _closestTarget.AddComponent<LineRenderer>();
                    LineRenderer _addedLineRenderer = _closestTarget.GetComponent<LineRenderer>();
                    _addedLineRenderer = _closestTarget.GetComponent<LineRenderer>();
                    _addedLineRenderer.startWidth = 0.01f;
                    _addedLineRenderer.endWidth = 0.01f;
                    _addedLineRenderer.useWorldSpace = true;
                    _addedLineRenderer.material = _lineRendererMaterial;
                    _addedLineRenderer.positionCount = 2;
                    _addedLineRenderer.SetPosition(0, _closestTarget.position);
                    if (Vector3.Distance(_leftIndexTipPosition, _closestTarget.position) < Vector3.Distance(_rightIndexTipPosition, _closestTarget.position))
                    {
                        _addedLineRenderer.SetPosition(1, _leftIndexTipPosition);
                    }
                    else
                    {
                        _addedLineRenderer.SetPosition(1, _rightIndexTipPosition);
                    }
                }
            }
        }
    }

    public void RemoveFromTargets(Transform _targetToBeRemoved) {
        if (_targets.Contains(_targetToBeRemoved))
        {
            _targets.Remove(_targetToBeRemoved);
        }
    }

    /*public void SpawnByNumber(int number) {
        GameObject instance;
        number -= 1;
        float yOffSet = 3f;
        switch (number)
        {
            case 0:
                instance = Instantiate(_targetPrefab, _spawnContentsParent);
                instance.transform.localPosition = _pivotPosition + new Vector3((number % 3) * _pivotScale, -(number / 3) * _pivotScale + yOffSet, - _pivotDistance);
                NetworkDebugConsole.Singleton.SetDebugString($"Prefab {number + 1} instantiated locally");
                instance.GetComponent<TargetController>().index = number;
                _targets.Add(instance.transform);
                break;
            case 1:
                instance = Instantiate(_targetPrefab, _spawnContentsParent);
                instance.transform.localPosition = _pivotPosition + new Vector3((number % 3) * _pivotScale, -(number / 3) * _pivotScale + yOffSet, - _pivotDistance);
                NetworkDebugConsole.Singleton.SetDebugString($"Prefab {number + 1} instantiated locally");
                instance.GetComponent<TargetController>().index = number;
                _targets.Add(instance.transform);

                break;
            case 2:
                instance = Instantiate(_targetPrefab, _spawnContentsParent);
                instance.transform.localPosition = _pivotPosition + new Vector3((number % 3) * _pivotScale, -(number / 3) * _pivotScale + yOffSet, - _pivotDistance);
                NetworkDebugConsole.Singleton.SetDebugString($"Prefab {number + 1} instantiated locally");
                instance.GetComponent<TargetController>().index = number;
                _targets.Add(instance.transform);
                break;
            case 3:
                instance = Instantiate(_targetPrefab, _spawnContentsParent);
                instance.transform.localPosition = _pivotPosition + new Vector3((number % 3) * _pivotScale, -(number / 3) * _pivotScale + yOffSet, - _pivotDistance);
                NetworkDebugConsole.Singleton.SetDebugString($"Prefab {number + 1} instantiated locally");
                instance.GetComponent<TargetController>().index = number;
                _targets.Add(instance.transform);
                break;
            case 4:
                instance = Instantiate(_targetPrefab, _spawnContentsParent);
                instance.transform.localPosition = _pivotPosition + new Vector3((number % 3) * _pivotScale, -(number / 3) * _pivotScale + yOffSet, - _pivotDistance);
                NetworkDebugConsole.Singleton.SetDebugString($"Prefab {number + 1} instantiated locally");
                instance.GetComponent<TargetController>().index = number;
                _targets.Add(instance.transform);
                break;
            case 5:
                instance = Instantiate(_targetPrefab, _spawnContentsParent);
                instance.transform.localPosition = _pivotPosition + new Vector3((number % 3) * _pivotScale, -(number / 3) * _pivotScale + yOffSet, - _pivotDistance);
                NetworkDebugConsole.Singleton.SetDebugString($"Prefab {number + 1} instantiated locally");
                instance.GetComponent<TargetController>().index = number;
                _targets.Add(instance.transform);
                break;
            case 6:
                instance = Instantiate(_targetPrefab, _spawnContentsParent);
                instance.transform.localPosition = _pivotPosition + new Vector3((number % 3) * _pivotScale, -(number / 3) * _pivotScale + yOffSet, - _pivotDistance);
                NetworkDebugConsole.Singleton.SetDebugString($"Prefab {number + 1} instantiated locally");
                instance.GetComponent<TargetController>().index = number;
                _targets.Add(instance.transform);
                break;
            case 7:
                instance = Instantiate(_targetPrefab, _spawnContentsParent);
                instance.transform.localPosition = _pivotPosition + new Vector3((number % 3) * _pivotScale, -(number / 3) * _pivotScale + yOffSet, - _pivotDistance);
                NetworkDebugConsole.Singleton.SetDebugString($"Prefab {number + 1} instantiated locally");
                instance.GetComponent<TargetController>().index = number;
                _targets.Add(instance.transform);
                break;
            case 8:
                instance = Instantiate(_targetPrefab, _spawnContentsParent);
                instance.transform.localPosition = _pivotPosition + new Vector3((number % 3) * _pivotScale, -(number / 3) * _pivotScale + yOffSet, - _pivotDistance);
                NetworkDebugConsole.Singleton.SetDebugString($"Prefab {number + 1} instantiated locally");
                instance.GetComponent<TargetController>().index = number;
                _targets.Add(instance.transform);
                break;
            default:
                NetworkDebugConsole.Singleton.SetDebugString("Not a valid number input");
                break;
        }
    }*/

    public void SpawnByNumber(int number) {
        number--;
        if (number < 0 || number > 8)
        {
            NetworkDebugConsole.Singleton.SetDebugString("Not a valid number input");
            return;
        }

        GameObject instance = Instantiate(_targetPrefab, _spawnContentsParent);

        // 1. Calculate the Sphere Center (World Space)
        float radius = _lineRendererDebugController.position.z * 2;
        float zOffset = _lineRendererDebugController.position.y * 1.5f - 1f;
        Vector3 sphereCentre = _spawnContentsParent.position + (_spawnContentsParent.rotation * new Vector3(0, 0, zOffset));

        float hLimit = (_lineRendererDebugController.position.x + 1) * 60f;
        float vLimit = (_lineRendererDebugController.position.x + 1) * 60f;

        // 2. Helper function (returns WORLD position)
        Vector3 GetRotatedPoint(float h, float v) {
            Quaternion arcRotation = Quaternion.Euler(-v, h, 0);
            Vector3 rotatedDirection = _spawnContentsParent.rotation * (arcRotation * Vector3.forward);
            return sphereCentre + (rotatedDirection * radius);
        }

        // 3. Grid Logic (Map 0-8 to a 3x3 grid)
        // Row (0, 1, 2) and Col (0, 1, 2)
        int row = number / 3;
        int col = number % 3;

        // Map 0,1,2 to -Limit/2, 0, +Limit/2
        float hPos = Mathf.Lerp(-hLimit / 2, hLimit / 2, col / 2.0f);
        float vPos = Mathf.Lerp(vLimit / 2, -vLimit / 2, row / 2.0f); // Top to bottom

        // 4. Assign World Position
        instance.transform.position = GetRotatedPoint(hPos, vPos);

        // Cleanup
        instance.GetComponent<TargetController>().index = number;
        _targets.Add(instance.transform);
        _numberOfTargetsSpawned += 1;   // needed for FindClosestTarget to run
        OnTargetSpawned?.Invoke();
        NetworkDebugConsole.Singleton.SetDebugString($"Prefab {number + 1} instantiated at {hPos}, {vPos}");
    }

    public void SpawnRandom(float x, float y, string indx) {
        if (x < 0 || x > 1 || y < 0 || y > 1)
        {
            NetworkDebugConsole.Singleton.SetDebugString($"Not a valid number input. {x}, {y}");
            return;
        }

        GameObject instance = Instantiate(_targetPrefab, _spawnContentsParent);

        // 1. Calculate the Sphere Center (World Space)
        float radius = _lineRendererDebugController.position.z * 2;
        float zOffset = _lineRendererDebugController.position.y * 1.5f - 1f;
        Vector3 sphereCentre = _spawnContentsParent.position + (_spawnContentsParent.rotation * new Vector3(0, 0, zOffset));

        float hLimit = (_lineRendererDebugController.position.x + 1) * 60f;
        float vLimit = (_lineRendererDebugController.position.x + 1) * 60f;

        // 2. Helper function (returns WORLD position)
        Vector3 GetRotatedPoint(float h, float v) {
            Quaternion arcRotation = Quaternion.Euler(-v, h, 0);
            Vector3 rotatedDirection = _spawnContentsParent.rotation * (arcRotation * Vector3.forward);
            return sphereCentre + (rotatedDirection * radius);
        }

        // Map 0,1,2 to -Limit/2, 0, +Limit/2
        float hPos = Mathf.Lerp(-hLimit / 2, hLimit / 2, x);
        float vPos = Mathf.Lerp(-vLimit / 2, vLimit / 2, y);

        // 4. Assign World Position
        instance.transform.position = GetRotatedPoint(hPos, vPos);

        // Cleanup
        instance.GetComponent<TargetController>().hash_for_random = indx;
        instance.GetComponent<TargetController>().SetRandom(true);
        _targets.Add(instance.transform);
        _numberOfTargetsSpawned += 1;   // needed for FindClosestTarget to run
        OnTargetSpawned?.Invoke();
        NetworkDebugConsole.Singleton.SetDebugString($"Prefab {indx} instantiated at {hPos}, {vPos}");
    }

    private void UpdateDebugSphereLineRenderer() {
        // 1. Setup variables
        float radius = _lineRendererDebugController.position.z * 2;

        // Calculate sphere center RELATIVE to the parent's orientation
        // This moves the center forward/backward along the parent's local Z axis
        float zOffset = _lineRendererDebugController.position.y * 1.5f - 1f;
        Vector3 sphereCentre = _spawnContentsParent.position + (_spawnContentsParent.rotation * new Vector3(0, 0, zOffset));

        float hLimit = (_lineRendererDebugController.position.x + 1) * 60f;
        float vLimit = (_lineRendererDebugController.position.x + 1) * 60f;

        int resolution = 10;
        _lineRenderer.positionCount = resolution * 4;

        // The arc starts from the parent's local forward
        Vector3 localForward = Vector3.forward;

        for (int i = 0; i < resolution; i++)
        {
            float t = (float)i / resolution;

            // Helper to calculate the rotated point relative to the parent
            Vector3 GetRotatedPoint(float h, float v) {
                // Apply the h/v rotation first, then "parent" it to the object's rotation
                Quaternion arcRotation = Quaternion.Euler(-v, h, 0);
                Vector3 rotatedDirection = _spawnContentsParent.rotation * (arcRotation * localForward);
                return sphereCentre + (rotatedDirection * radius);
            }

            // Loop 1: Top (Left to Right)
            _lineRenderer.SetPosition(i, GetRotatedPoint(
                Mathf.Lerp(-hLimit / 2, hLimit / 2, t),
                vLimit / 2));

            // Loop 2: Right (Top to Bottom)
            _lineRenderer.SetPosition(i + resolution, GetRotatedPoint(
                hLimit / 2,
                Mathf.Lerp(vLimit / 2, -vLimit / 2, t)));

            // Loop 3: Bottom (Right to Left)
            _lineRenderer.SetPosition(i + (resolution * 2), GetRotatedPoint(
                Mathf.Lerp(hLimit / 2, -hLimit / 2, t),
                -vLimit / 2));

            // Loop 4: Left (Bottom to Top)
            _lineRenderer.SetPosition(i + (resolution * 3), GetRotatedPoint(
                -hLimit / 2,
                Mathf.Lerp(-vLimit / 2, vLimit / 2, t)));
        }
    }

    public void DespawnByNumber(int number) {
        GameObject instance;
        number -= 1;
        switch (number)
        {
            case 0:
                foreach ( var target in _targets )
                {
                    if (target.GetComponent<TargetController>().index == number)
                    {
                        _targets.Remove(target);
                        target.GetComponent<TargetController>().DisappearByNumber();
                        NetworkDebugConsole.Singleton.SetDebugString($"Prefab {number + 1} desapwned locally");
                        break;
                    }
                }
                break;
            case 1:
                foreach (var target in _targets)
                {
                    if (target.GetComponent<TargetController>().index == number)
                    {
                        _targets.Remove(target);
                        target.GetComponent<TargetController>().DisappearByNumber();
                        NetworkDebugConsole.Singleton.SetDebugString($"Prefab {number + 1} desapwned locally");
                        break;
                    }
                }
                break;
            case 2:
                foreach (var target in _targets)
                {
                    if (target.GetComponent<TargetController>().index == number)
                    {
                        _targets.Remove(target);
                        target.GetComponent<TargetController>().DisappearByNumber();
                        NetworkDebugConsole.Singleton.SetDebugString($"Prefab {number + 1} desapwned locally");
                        break;
                    }
                }
                break;
            case 3:
                foreach (var target in _targets)
                {
                    if (target.GetComponent<TargetController>().index == number)
                    {
                        _targets.Remove(target);
                        target.GetComponent<TargetController>().DisappearByNumber();
                        NetworkDebugConsole.Singleton.SetDebugString($"Prefab {number + 1} desapwned locally");
                        break;
                    }
                }
                break;
            case 4:
                foreach (var target in _targets)
                {
                    if (target.GetComponent<TargetController>().index == number)
                    {
                        _targets.Remove(target);
                        target.GetComponent<TargetController>().DisappearByNumber();
                        NetworkDebugConsole.Singleton.SetDebugString($"Prefab {number + 1} desapwned locally");
                        break;
                    }
                }
                break;
            case 5:
                foreach (var target in _targets)
                {
                    if (target.GetComponent<TargetController>().index == number)
                    {
                        _targets.Remove(target);
                        target.GetComponent<TargetController>().DisappearByNumber();
                        NetworkDebugConsole.Singleton.SetDebugString($"Prefab {number + 1} desapwned locally");
                        break;
                    }
                }
                break;
            case 6:
                foreach (var target in _targets)
                {
                    if (target.GetComponent<TargetController>().index == number)
                    {
                        _targets.Remove(target);
                        target.GetComponent<TargetController>().DisappearByNumber();
                        NetworkDebugConsole.Singleton.SetDebugString($"Prefab {number + 1} desapwned locally");
                        break;
                    }
                }
                break;
            case 7:
                foreach (var target in _targets)
                {
                    if (target.GetComponent<TargetController>().index == number)
                    {
                        _targets.Remove(target);
                        target.GetComponent<TargetController>().DisappearByNumber();
                        NetworkDebugConsole.Singleton.SetDebugString($"Prefab {number + 1} desapwned locally");
                        break;
                    }
                }
                break;
            case 8:
                foreach (var target in _targets)
                {
                    if (target.GetComponent<TargetController>().index == number)
                    {
                        _targets.Remove(target);
                        target.GetComponent<TargetController>().DisappearByNumber();
                        NetworkDebugConsole.Singleton.SetDebugString($"Prefab {number + 1} desapwned locally");
                        break;
                    }
                }
                break;
            default:
                NetworkDebugConsole.Singleton.SetDebugString("Not a valid number input");
                break;
        }
    }

    private void OnSpawnInputMessageReceived(ulong senderClientId, FastBufferReader reader) {
        // Read payload in same order as server wrote it
        reader.ReadValueSafe(out FixedString64Bytes text);
        reader.ReadValueSafe(out int number);

        NetworkDebugConsole.Singleton.SetDebugString($"Received from {senderClientId}: {number}, {text}");
        _numberOfTargetsSpawned += 1;
        SpawnByNumber(number);
        // SendHelloToServer(number);
    }

    private void OnSpawnRandomInputMessageReceived(ulong senderClientId, FastBufferReader reader) {
        // Read payload in same order as server wrote it
        reader.ReadValueSafe(out FixedString64Bytes text);
        reader.ReadValueSafe(out float x);
        reader.ReadValueSafe(out float y);
        reader.ReadValueSafe(out string indx);

        NetworkDebugConsole.Singleton.SetDebugString($"Received from {senderClientId}: {x}, {y}, {indx}, {text}");
        _numberOfTargetsSpawned += 1;
        SpawnRandom(x, y, indx);
        // SendHelloToServer(number);
    }

    private void OnDesapwnInputMessageReceived(ulong senderClientId, FastBufferReader reader) {
        // Read payload in same order as server wrote it
        reader.ReadValueSafe(out int number);
        reader.ReadValueSafe(out FixedString64Bytes text);

        NetworkDebugConsole.Singleton.SetDebugString($"Received from {senderClientId}: {number}, {text}");

        bool doesExist = false;
        foreach (var target in _targets)
        {
            if (target.GetComponent<TargetController>().index == number - 1)
            {
                doesExist = true;
                break;
            }
        }
        if (doesExist)
        {
            DespawnByNumber(number);
        }
        else
        {
            NetworkDebugConsole.Singleton.SetDebugString($"Despawn requested to the target {number + 1} which does not exist");
        }
    }

    private void OnDesapwnAllMessageReceived(ulong senderClientId, FastBufferReader reader) {
        // Read payload in same order as server wrote it
        reader.ReadValueSafe(out int number);
        reader.ReadValueSafe(out FixedString64Bytes text);

        DespawnAll();
    }

    private void DespawnAll() {
        NetworkDebugConsole.Singleton.SetDebugString($"{_targets.Count} objects to despawn");
        int _targetsSize = _targets.Count;
        for (int i = _targets.Count - 1; i >= 0; i--)
        {
            NetworkDebugConsole.Singleton.SetDebugString($"Despawning {_targets[i].GetComponent<TargetController>().index + 1}");
            _targets[i].GetComponent<TargetController>().DisappearNow();
        }
        _targets.Clear();
        _targetsInRange.Clear();
        _closestTarget = null;
        _numberOfTargetsSpawned = 0;
        NetworkDebugConsole.Singleton.SetDebugString("Reset");
    }

    public void SendHelloToServer(int number) {
        if (NetworkManager.Singleton.IsClient)
        {
            using var writer = new FastBufferWriter(128, Allocator.Temp);
            writer.WriteValueSafe(number);
            writer.WriteValueSafe(new FixedString64Bytes("Hi server!"));

            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                "HelloFromClient",
                NetworkManager.ServerClientId,
                writer
            );
        }
    }

    public void SendCaptureToServer(int number, Vector3 targetPosition) {
        OnTargetCaptured?.Invoke();
        OnTargetCapturedDetailed?.Invoke(BuildCaptureData(targetPosition, number + 1));
        if (NetworkManager.Singleton.IsClient)
        {
            using var writer = new FastBufferWriter(128, Allocator.Temp);
            writer.WriteValueSafe(number);
            writer.WriteValueSafe(new FixedString64Bytes("Captured"));
            if (Vector3.Distance(targetPosition, _leftIndexTipPosition) < Vector3.Distance(targetPosition, _rightIndexTipPosition))
            {
                Vector3 worldDirection = _leftIndexTipPosition - targetPosition;
                Vector3 localOffset = Quaternion.Inverse(_spawnContentsParent.rotation) * worldDirection;
                writer.WriteValueSafe(localOffset);
            }
            else
            {
                Vector3 worldDirection = _rightIndexTipPosition - targetPosition;
                Vector3 localOffset = Quaternion.Inverse(_spawnContentsParent.rotation) * worldDirection;
                writer.WriteValueSafe(localOffset);
            }

            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                "CaptureFromClient",
                NetworkManager.ServerClientId,
                writer
            );
            NetworkDebugConsole.Singleton.SetDebugString($"Target {number} captured sent to server.");
        }
    }

    public void SendCaptureToServer(string hash, Vector3 targetPosition) {
        OnTargetCaptured?.Invoke();
        OnTargetCapturedDetailed?.Invoke(BuildCaptureData(targetPosition, -1));
        if (NetworkManager.Singleton.IsClient)
        {
            using var writer = new FastBufferWriter(128, Allocator.Temp);
            writer.WriteValueSafe(hash);
            writer.WriteValueSafe(new FixedString64Bytes("Captured"));
            if (Vector3.Distance(targetPosition, _leftIndexTipPosition) < Vector3.Distance(targetPosition, _rightIndexTipPosition))
            {
                Vector3 worldDirection = _leftIndexTipPosition - targetPosition;
                Vector3 localOffset = Quaternion.Inverse(_spawnContentsParent.rotation) * worldDirection;
                writer.WriteValueSafe(localOffset);
            }
            else
            {
                Vector3 worldDirection = _rightIndexTipPosition - targetPosition;
                Vector3 localOffset = Quaternion.Inverse(_spawnContentsParent.rotation) * worldDirection;
                writer.WriteValueSafe(localOffset);
            }

            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                "CaptureRandomFromClient",
                NetworkManager.ServerClientId,
                writer
            );
            NetworkDebugConsole.Singleton.SetDebugString($"Target {hash} captured sent to server.");
        }
    }

    // Works out which hand made the grasp and how far its fingertip was from the
    // target's centre. Mirrors the hand-choice logic already used when reporting to the server.
    private CaptureData BuildCaptureData(Vector3 targetPosition, int targetIndex) {
        float distLeft = Vector3.Distance(targetPosition, _leftIndexTipPosition);
        float distRight = Vector3.Distance(targetPosition, _rightIndexTipPosition);
        bool left = distLeft < distRight;
        return new CaptureData {
            targetPosition = targetPosition,
            fingerTipPosition = left ? _leftIndexTipPosition : _rightIndexTipPosition,
            endpointErrorMeters = left ? distLeft : distRight,
            usedLeftHand = left,
            isSimulated = false,
            targetIndex = targetIndex
        };
    }

    // TESTING ONLY: raises the same event a real grasp does, so the trial-counting
    // logic can be verified without a server connection or a headset.
    public void SimulateCapture() {
        OnTargetCaptured?.Invoke();
        var data = BuildCaptureData(_rightIndexTipPosition, -1);
        data.isSimulated = true;
        OnTargetCapturedDetailed?.Invoke(data);
    }

    // Lets the task sequencer clear the board between trials without going via the network.
    // Exposed so the participant view can switch the debug visuals off without
    // anything needing to be dragged into an Inspector slot.
    public LineRenderer DebugBoundaryLine => _lineRenderer;
    public Transform DebugController => _lineRendererDebugController;

    public void ClearAllTargets() {
        DespawnAll();
    }

    public Transform GetClosestTarget() {
        return _closestTarget;
    }

    // The tracked index fingertips, in WORLD space. Exposed so other components - the home
    // gate, and anything measuring reaction time - can ask where the hand is without each
    // one re-reading and re-converting the bone data and risking a different answer.
    public Vector3 LeftIndexTip  => _leftIndexTipPosition;
    public Vector3 RightIndexTip => _rightIndexTipPosition;

    // Distance from the nearer fingertip to a point. Returns false when neither hand is
    // usable, so callers can tell "far away" from "not tracked" - which matter differently:
    // one is the participant's hand being elsewhere, the other is no data at all.
    public bool TryNearestFingertipDistance(Vector3 point, out float distance, out bool usedLeft)
    {
        float dl = Vector3.Distance(_leftIndexTipPosition, point);
        float dr = Vector3.Distance(_rightIndexTipPosition, point);

        // An untracked hand reports the origin. A real fingertip is never there, so this
        // separates "no hand" from "hand far away" without needing a tracking-confidence API.
        bool leftOk  = _leftIndexTipPosition.sqrMagnitude  > 0.0001f;
        bool rightOk = _rightIndexTipPosition.sqrMagnitude > 0.0001f;

        if (!leftOk && !rightOk) { distance = float.PositiveInfinity; usedLeft = false; return false; }
        if (!leftOk)  { distance = dr; usedLeft = false; return true; }
        if (!rightOk) { distance = dl; usedLeft = true;  return true; }

        usedLeft = dl <= dr;
        distance = usedLeft ? dl : dr;
        return true;
    }

    public void TargetCaptured(Transform _capturedTargetTransform) {
        _targets.Remove(_capturedTargetTransform);
    }
}
