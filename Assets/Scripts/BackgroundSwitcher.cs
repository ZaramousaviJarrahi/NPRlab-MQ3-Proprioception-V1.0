
using UnityEngine;

public class BackgroundSwitcher : MonoBehaviour
{
    [Header("Meta XR")]
    [SerializeField] private OVRPassthroughLayer passthroughLayer;
    [SerializeField] private Camera centerEyeCamera;

    [Header("360 Photo")]
    [SerializeField] private Material photo360Material;

    private bool showingPassthrough = true;
    private Material originalSkybox;

    private void Start()
    {
        originalSkybox = RenderSettings.skybox;

        // Start in real-world passthrough mode.
        ShowPassthrough();
    }

    public void ToggleBackground()
    {
        if (showingPassthrough)
        {
            Show360Photo();
        }
        else
        {
            ShowPassthrough();
        }
    }

    public void Show360Photo()
    {
        showingPassthrough = false;

        // Hide the real-world camera feed.
        if (passthroughLayer != null)
        {
            passthroughLayer.enabled = false;
        }

        // Display the panoramic photo as the background.
        RenderSettings.skybox = photo360Material;
        centerEyeCamera.clearFlags = CameraClearFlags.Skybox;
    }

    public void ShowPassthrough()
    {
        showingPassthrough = true;

        // Restore the transparent background needed for passthrough.
        centerEyeCamera.clearFlags = CameraClearFlags.SolidColor;
        centerEyeCamera.backgroundColor = Color.clear;

        // Restore the original skybox reference.
        RenderSettings.skybox = originalSkybox;

        // Show the real-world camera feed.
        if (passthroughLayer != null)
        {
            passthroughLayer.enabled = true;
        }
    }
}
