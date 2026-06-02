using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using UnityEngine;

public class TargetController : MonoBehaviour
{
    [HideInInspector] public int index;
    [HideInInspector] public string hash_for_random;
    [SerializeField] private float _disappearingDuration = 2f;
    private float _disappearTime = 0;
    private bool _startDisappearing = false;
    private Material _material;
    private Grabbable _grabbable;
    private HandGrabInteractable _handGrabInteractable;
    private GrabInteractable _grabInteractable;
    private bool _random = false;

    void Start()
    {
        _material = gameObject.GetComponent<MeshRenderer>().material;
        _grabbable = GetComponent<Grabbable>();
        _handGrabInteractable = transform.GetChild(0).GetComponent<HandGrabInteractable>();
        _grabInteractable = transform.GetChild(0).GetComponent<GrabInteractable>();
        _disappearTime = 0;
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
                _material.color = new Color(_material.color.r, _material.color.g, _material.color.b, (_disappearingDuration - _disappearTime) / _disappearingDuration);
                gameObject.GetComponent<MeshRenderer>().material = _material;
            }
        }
    }

    public void Disappear() {
        _material = gameObject.GetComponent<MeshRenderer>().material;
        _startDisappearing = true;
        _grabbable.enabled = false;
        _handGrabInteractable.enabled = false;
        _grabInteractable.enabled = false;
    }

    public void DisappearNow() {
        ControlManager.Singleton.DestroyThisTarget(transform);
        GameObject.Destroy(gameObject);
    }

    public void Capture() {
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
