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
            if (transform == ControlManager.Singleton.GetClosestTarget())
            {
                _collider.enabled = true;
                _meshRenderer.material = _disappearingMaterial;
            }
            else
            {
                _collider.enabled = false;
                _meshRenderer.material = _initialMaterial;
            }
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

    public void Capture() {
        if (transform == ControlManager.Singleton.GetClosestTarget())
        {
            _meshRenderer.material = _disappearingMaterial;
            _startDisappearing = true;
            _grabbable.enabled = false;
            _handGrabInteractable.enabled = false;
            _grabInteractable.enabled = false;
            _audioSource.PlayOneShot(_audioClip);

            if (_random)
            {
                ControlManager.Singleton.SendCaptureToServer(hash_for_random, transform.position);
            }
            else
            {
                ControlManager.Singleton.SendCaptureToServer(index, transform.position);
            }
        }
    }

    public void SetRandom(bool rand) {
        _random = rand;
    }
}
