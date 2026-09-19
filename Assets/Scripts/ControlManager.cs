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
    // Fires every time a target is successfully grasped/captured (independent of network state).
    public event Action OnTargetCaptured;
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
        if (Singleton != null)
        {
            throw new Exception($"Detected more than one instance of {nameof(NetworkDebugConsole)}! " +
                $"Do you have more than one component attached to a {nameof(GameObject)}");
        }
        Singleton = this;
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

        if (OVRPlugin.GetHandState(OVRPlugin.Step.Render, OVRPlugin.Hand.HandLeft, ref _leftHandState))
        {
            _leftIndexTipPosition = new Vector3(_leftHandState.BonePositions[(int)OVRPlugin.BoneId.XRHand_IndexTip].x,
                _leftHandState.BonePositions[(int)OVRPlugin.BoneId.XRHand_IndexTip].y,
                - _leftHandState.BonePositions[(int)OVRPlugin.BoneId.XRHand_IndexTip].z);
        }
        if (OVRPlugin.GetHandState(OVRPlugin.Step.Render, OVRPlugin.Hand.HandRight, ref _leftHandState))
        {
            _rightIndexTipPosition = new Vector3(_leftHandState.BonePositions[(int)OVRPlugin.BoneId.XRHand_IndexTip].x,
                _leftHandState.BonePositions[(int)OVRPlugin.BoneId.XRHand_IndexTip].y,
                - _leftHandState.BonePositions[(int)OVRPlugin.BoneId.XRHand_IndexTip].z);
        }
        _leftFingerTipSphere.position = _leftIndexTipPosition;
        _rightFingerTipSphere.position = _rightIndexTipPosition;
    }

    private void FindTargetsInRange() {
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

    // TESTING ONLY: raises the same event a real grasp does, so the trial-counting
    // logic can be verified without a server connection or a headset.
    public void SimulateCapture() {
        OnTargetCaptured?.Invoke();
    }

    public Transform GetClosestTarget() {
        return _closestTarget;
    }

    public void TargetCaptured(Transform _capturedTargetTransform) {
        _targets.Remove(_capturedTargetTransform);
    }
}
