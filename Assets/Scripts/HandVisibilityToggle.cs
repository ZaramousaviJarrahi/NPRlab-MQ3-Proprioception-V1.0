using UnityEngine;

public class HandVisibilityToggle : MonoBehaviour
{
    [Header("Hand Objects")]
    public GameObject leftHand;
    public GameObject rightHand;

    [Header("Status")]
    public bool handsVisible = true;

    void Start()
    {
        handsVisible = true;
        Debug.Log("Hands visible at start");
    }

    void OnGUI()
    {
        if (handsVisible)
        {
            if (GUI.Button(new Rect(10, 70, 200, 50), 
                "Hide Hands"))
            {
                HideHands();
            }
        }
        else
        {
            if (GUI.Button(new Rect(10, 70, 200, 50), 
                "Show Hands"))
            {
                ShowHands();
            }
        }
    }

    public void HideHands()
    {
        if (leftHand != null) leftHand.SetActive(false);
        if (rightHand != null) rightHand.SetActive(false);
        handsVisible = false;
        Debug.Log("Hands HIDDEN");
    }

    public void ShowHands()
    {
        if (leftHand != null) leftHand.SetActive(true);
        if (rightHand != null) rightHand.SetActive(true);
        handsVisible = true;
        Debug.Log("Hands VISIBLE");
    }
}
