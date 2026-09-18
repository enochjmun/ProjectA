using UnityEngine;

/// <summary>
/// Put on a Light that should look different between the CALM (tier 1) and TABLETOP (tier 2) lobby
/// states. It reads <see cref="LobbyLightingController.Blend"/> and interpolates this light's
/// intensity (and optionally colour) between the two authored values. Lights that look the same in
/// both tiers don't need this component at all.
///
/// The controller already smooths the blend over time, so this just MAPS blend -> values with no
/// easing of its own — all the timing lives in one place.
///
/// AUTHORING: set the light how you want it for tier 1, right-click the component header ->
/// "Capture Tier 1"; adjust the light for tier 2, "Capture Tier 2". No typing values by hand.
/// </summary>
[RequireComponent(typeof(Light))]
public class LightTierResponder : MonoBehaviour
{
    [Header("Tier 1 — calm / lobby")]
    [SerializeField] private float tier1Intensity = 1f;
    [SerializeField] private Color tier1Color = Color.white;

    [Header("Tier 2 — tabletop / minigame")]
    [SerializeField] private float tier2Intensity = 1f;
    [SerializeField] private Color tier2Color = Color.white;

    [Tooltip("Also interpolate colour, not just intensity. Off = colour left untouched.")]
    [SerializeField] private bool lerpColor = false;

    private Light _light;

    private void Awake() => _light = GetComponent<Light>();

    private void LateUpdate()
    {
        // No controller yet (or none in scene) -> rest at the calm look.
        float blend = LobbyLightingController.Instance != null
            ? LobbyLightingController.Instance.Blend
            : 0f;

        _light.intensity = Mathf.Lerp(tier1Intensity, tier2Intensity, blend);
        if (lerpColor)
            _light.color = Color.Lerp(tier1Color, tier2Color, blend);
    }

    [ContextMenu("Capture Tier 1")]
    private void CaptureTier1()
    {
        var l = GetComponent<Light>();
        tier1Intensity = l.intensity;
        tier1Color = l.color;
    }

    [ContextMenu("Capture Tier 2")]
    private void CaptureTier2()
    {
        var l = GetComponent<Light>();
        tier2Intensity = l.intensity;
        tier2Color = l.color;
    }
}
