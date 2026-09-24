using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using UnityEngine;

public class TargetController : MonoBehaviour
{
    [HideInInspector] public int index;
    [HideInInspector] public string hash_for_random;
    [SerializeField] private float _disappearingDuration = 2f;
    [SerializeField] private Material _disappearingMaterial;
    [SerializeField] private AudioSource _audioSource;
    [SerializeField] private AudioClip _audioClip;
    private float _disappearTime = 0;
    private bool _startDisappearing = false;
    private Grabbable _grabbable;
    private HandGrabInteractable _handGrabInteractable;
    private GrabInteractable _grabInteractable;
    private bool _random = false;
    private MeshRenderer _meshRenderer;
    private Collider _collider;
    private Material _initialMaterial;

    void Start()
    {
        _meshRenderer = GetComponent<MeshRenderer>();
        _grabbable = GetComponent<Grabbable>();
        _handGrabInteractable = transform.GetChild(0).GetComponent<HandGrabInteractable>();
        _grabInteractable = transform.GetChild(0).GetComponent<GrabInteractable>();
        _collider = GetComponent<Collider>();
        _disappearTime = 0;
        _initialMaterial = _meshRenderer.material;
    }

    void Update()
    {
        if (_startDisappearing)
        {
            _disappearTime += Time.deltaTime;
            if ( _disappearTime > _disappearingDuration)
            {
                _startDisappearing = false;
                ControlManager.Singleton.DestroyThisTarget(transform);
                GameObject.Destroy(gameObject);
            }
            else
            {
                Material _material = _meshRenderer.material;
                _material.color = new Color(_material.color.r, _material.color.g, _material.color.b, (_disappearingDuration - _disappearTime) / _disappearingDuration);
                _meshRenderer.material = _material;
            }
        }
        else
        {
            // Grab gating and the visual tint were one and the same. They are separated
            // here: the nearest sphere is still the only grabbable one - remove that and
            // nine overlapping grabbables fight each other - but the tint is now optional,
            // because it tells the participant where their hand is and that is the very
            // information the hidden-hand condition is meant to remove.
            bool isClosest = transform == ControlManager.Singleton.GetClosestTarget();

            // Remembered so Capture() can tell a legitimate grasp from a stray event even
            // after the closest target has moved on. See the note there.
            if (isClosest) _lastClosestTime = Time.time;

            _collider.enabled = isClosest;
            _grabbable.enabled = isClosest;

            bool tint = isClosest && ControlManager.Singleton.highlightClosestTarget;
            _meshRenderer.material = tint ? _disappearingMaterial : _initialMaterial;
        }
    }

    public void DisappearByNumber() {
        _meshRenderer.material = _disappearingMaterial;
        _startDisappearing = true;
        _grabbable.enabled = false;
        _handGrabInteractable.enabled = false;
        _grabInteractable.enabled = false;
        _audioSource.PlayOneShot(_audioClip);
    }

    public void DisappearNow() {
        ControlManager.Singleton.DestroyThisTarget(transform);
        GameObject.Destroy(gameObject);
    }

    [Tooltip("How long after a target stops being the closest one a grasp of it is still " +
             "accepted, in seconds. Covers the gap between the hand closing and the select " +
             "event arriving - during which the hand has already started moving.")]
    [SerializeField] private float _closestGraceSeconds = 0.5f;

    private float _lastClosestTime = -1f;

    // WHY THIS IS NOT JUST "am I the closest target right now":
    //
    // It used to be. The problem is that the closest target is recomputed every frame from
    // the fingertip position, and the select event arrives a frame or two AFTER the hand has
    // closed and started moving away - by which time a different target can be closest. The
    // check then failed, Capture() returned in silence, the target never faded, and no row
    // was recorded. A grasp that the participant made and saw simply did not exist in the
    // data, with nothing anywhere saying so.
    //
    // The check was also redundant. Update() enables Grabbable ONLY on the closest target,
    // so a select event on this object is already proof that it was the legitimate one at
    // the moment the hand closed. What is kept here is a grace window, which protects
    // against a genuinely stray event without throwing away real grasps, plus logging: a
    // rejected grasp is now a warning, because silent data loss is the worst outcome.
    public void Capture() {
        if (_startDisappearing) return;     // already captured - never count a grasp twice

        bool closestNow = transform == ControlManager.Singleton.GetClosestTarget();
        bool closestRecently = _lastClosestTime > 0f
                               && (Time.time - _lastClosestTime) <= _closestGraceSeconds;

        if (!closestNow && !closestRecently)
        {
            float ago = _lastClosestTime > 0f ? Time.time - _lastClosestTime : -1f;
            Debug.LogWarning($"Target at position {index + 1}: grasp IGNORED - it was not the "
                           + $"closest target (last closest {(ago < 0f ? "never" : ago.ToString("F2") + "s ago")}). "
                           + "Nothing was recorded for this grasp. If this happens repeatedly, raise "
                           + "Closest Grace Seconds on the Target prefab.");
            return;
        }

        _meshRenderer.material = _disappearingMaterial;
        _startDisappearing = true;
        _grabbable.enabled = false;
        _handGrabInteractable.enabled = false;
        _grabInteractable.enabled = false;
        _audioSource.PlayOneShot(_audioClip);

        Debug.Log($"Target at position {index + 1} CAPTURED "
                + $"(closest now: {closestNow}, within grace: {closestRecently}).");

        if (_random)
        {
            ControlManager.Singleton.SendCaptureToServer(hash_for_random, transform.position);
        }
        else
        {
            ControlManager.Singleton.SendCaptureToServer(index, transform.position);
        }
    }

    public void SetRandom(bool rand) {
        _random = rand;
    }
}
