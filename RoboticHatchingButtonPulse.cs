using UnityEngine;
using UnityEngine.UI;

// Pulses the hatch button's color back and forth between its normal color
// and a target color (default yellow) in a smooth loop, for as long as
// the Robotic Hatching boost is active — instantly reverts to the
// button's original color the moment the boost ends.
[RequireComponent(typeof(Image))]
public class RoboticHatchingButtonPulse : MonoBehaviour
{
    [Tooltip("Color to pulse toward while the boost is active.")]
    public Color pulseColor = Color.yellow;

    [Tooltip("How fast the fade back-and-forth cycles, in seconds per full cycle.")]
    public float pulseSpeed = 1.5f;

    private Image buttonImage;
    private Color originalColor;

    void Awake()
    {
        buttonImage = GetComponent<Image>();
        originalColor = buttonImage.color;
    }

    void Update()
    {
        bool active = BoostActivationManager.Instance != null && BoostActivationManager.Instance.IsActive(RoboticHatchingBoost.ItemId);

        if (active)
        {
            // Smooth 0->1->0 ping-pong using a sine wave, so it eases at
            // both ends instead of linearly snapping back and forth.
            float t = (Mathf.Sin(Time.time * (2f * Mathf.PI / pulseSpeed)) + 1f) * 0.5f;
            buttonImage.color = Color.Lerp(originalColor, pulseColor, t);
        }
        else if (buttonImage.color != originalColor)
        {
            buttonImage.color = originalColor;
        }
    }
}
