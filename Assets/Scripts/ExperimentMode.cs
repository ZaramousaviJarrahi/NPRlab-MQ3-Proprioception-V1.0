using UnityEngine;

public class ExperimenterMode : MonoBehaviour
{
    [Header("Experimenter Settings")]
    public bool experimentStarted = false;

    void Start()
    {
        // App starts in experimenter mode
        experimentStarted = false;
        Debug.Log("Experimenter Mode: Ready for setup");
    }

    public void StartExperiment()
    {
        // Called when experimenter clicks Ready
        experimentStarted = true;
        Debug.Log("Experiment Started!");
    }

    public bool IsExperimentStarted()
    {
        return experimentStarted;
    }
}