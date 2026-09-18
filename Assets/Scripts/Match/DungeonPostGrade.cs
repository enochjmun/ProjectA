using UnityEngine;

/// <summary>
/// Sizes the BuckshotPost dither grid to the current resolution. That is now this component's ONLY
/// job.
///
/// HISTORY (2026-07-20): this used to lerp _Exposure / _Contrast / _ShadowLift / _QuantizeGamma
/// between a LOBBY profile and a DUNGEON profile based on where the local camera was, because one
/// global post material couldn't flatter a bright lobby and a near-black dungeon at once. That whole
/// premise was retired when the project moved to "neutral post grade, get consistency from LIGHTING
/// instead" -- if both areas are lit to the same standard there is nothing to compensate for.
///
/// It was also actively harmful. _ShadowLift is applied BEFORE posterize, so raising it doesn't just
/// brighten darks, it RELOCATES them up into quantisation levels 1-3 where the level-to-level ratio
/// is 2^_QuantizeGamma (~4.6x) and banding is at its most visible. The dungeon's dark-band artifact
/// was caused by exactly that. Tone knobs now live on the material at neutral values and stay there.
///
/// Local/visual-only: writes one float on the post material, reads nothing networked. No longer has
/// any dependency on DungeonGenerator or Camera.main.
/// </summary>
public class DungeonPostGrade : MonoBehaviour
{
    [Tooltip("The BuckshotPost fullscreen material (Hidden_BuckshotPost.mat).")]
    [SerializeField] private Material postMaterial;

    [Header("Shader property name (BuckshotPost default)")]
    [SerializeField] private string ditherScaleProp = "_DitherPixelScale";

    [Header("Dither grain scaling")]
    [Tooltip("Screen height the dither was tuned at. Cell size scales up in INTEGER steps above this " +
             "so grain keeps a constant physical size on higher-res monitors.")]
    [SerializeField] private float ditherReferenceHeight = 1080f;
    [Tooltip("Upper clamp on cell size, matching the shader slider's range.")]
    [SerializeField] private int maxDitherScale = 4;

    private int _dsId;
    private int _lastScreenHeight = -1;

    private void Awake() => _dsId = Shader.PropertyToID(ditherScaleProp);

    /// <summary>
    /// Size one Bayer cell in REAL pixels, as an INTEGER multiple, so grain holds a constant physical
    /// size as resolution rises (1080p -> 1, 4K -> 2) instead of shrinking.
    ///
    /// Integer is the load-bearing constraint, not a rounding convenience: a fractional cell size is a
    /// non-integer multiple of the display grid, so each Bayer tile lands on a different sub-pixel
    /// phase and the two grids interfere into regular bars. That beating is what the old virtual-row
    /// approach produced. Integer cells are always phase-aligned and cannot beat.
    ///
    /// The cost is that intermediate resolutions round down -- 1440p gets scale 1 and runs ~25% finer
    /// than 1080p. Correct trade: a dither pattern's whole job is to go unnoticed, and a 25% size
    /// drift is invisible where moire is not.
    /// </summary>
    private void LateUpdate()
    {
        if (postMaterial == null) return;
        if (Screen.height == _lastScreenHeight) return;   // early-out: only reacts to actual changes
        _lastScreenHeight = Screen.height;

        int scale = Mathf.Clamp(
            Mathf.RoundToInt(Screen.height / Mathf.Max(ditherReferenceHeight, 1f)), 1, maxDitherScale);
        postMaterial.SetFloat(_dsId, scale);
    }
}
