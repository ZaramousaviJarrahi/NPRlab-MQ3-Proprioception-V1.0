using UnityEngine;

public class ExperimenterMode : MonoBehaviour
{
    [Header("Experimenter Settings")]
    public bool experimentStarted = false;

    void Start()
    {
        experimentStarted = false;
        Debug.Log("Experimenter Mode: Ready for setup");
    }

    void OnGUI()
    {
        if (!experimentStarted)
        {
            if (GUI.Button(new Rect(10, 10, 200, 50), 
                "Start Experiment"))
            {
                StartExperiment();
            }
        }
        else
        {
            GUI.Label(new Rect(10, 10, 200, 50), 
                "Experiment Running!");
        }
    }

    public void StartExperiment()
    {
        experimentStarted = true;
        Debug.Log("Experiment Started!");
    }

    public bool IsExperimentStarted()
    {
        return experimentStarted;
    }
}
