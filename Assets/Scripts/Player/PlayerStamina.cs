using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Sprint stamina for the local player, plus the bar that shows it.
///
/// LOCAL ONLY, BY DESIGN. Movement is already client-authoritative (the owner moves itself and
/// ClientNetworkTransform replicates the result), so a networked stamina value would add RPC traffic
/// and latency without closing any hole a determined cheat couldn't already walk through. Remotes see
/// the *consequence* -- you moving at walk speed -- through the existing transform sync, which is all
/// they need. If stamina ever has to be authoritative, it moves server-side with the rest of movement,
/// not on its own.
///
/// MODEL (chosen 2026-07-20): hard stop with a recovery threshold, and a delay before regen starts.
///   - Draining to zero forces you to walk, and sprint stays locked until stamina climbs back past
///     `recoverFraction`. Without that threshold players stutter-sprint -- tapping shift to burn each
///     tick of stamina the instant it exists -- which feels bad and destroys the tension of the meter.
///   - Regen waits `regenDelay` after you stop sprinting, so stopping to catch your breath is a real
///     decision with a cost. That's what makes hiding a tradeoff rather than a free reset.
///
/// SETUP: add this component to the Player prefab (no serialized refs). Throwaway IMGUI -- real UI
/// later, alongside ObjectiveHud and SpectateButtonHud when the whole HUD gets rebuilt properly.
/// </summary>
public class PlayerStamina : NetworkBehaviour
{
    [Header("Tuning (in seconds -- easier to reason about than abstract points)")]
    [Tooltip("How long you can sprint from full.")]
    [SerializeField] private float sprintSeconds = 5f;
    [Tooltip("How long a full refill takes from empty. Longer than sprintSeconds makes stamina a " +
             "resource you spend rather than a rhythm you cycle.")]
    [SerializeField] private float regenSeconds = 7f;
    [Tooltip("Pause after you stop sprinting before regen begins.")]
    [SerializeField] private float regenDelay = 1f;
    [Tooltip("Fraction of full stamina you must recover before sprint unlocks after exhausting. This " +
             "is the anti-stutter-sprint knob -- 0 would let you re-sprint on a single frame's worth.")]
    [Range(0f, 1f)] [SerializeField] private float recoverFraction = 0.3f;

    [Header("HUD -- throwaway IMGUI, matching ObjectiveHud/SpectateButtonHud. No refs to wire.")]
    [Tooltip("Bar size + vertical offset from the BOTTOM. Drawn horizontally centred.")]
    [SerializeField] private Rect rect = new Rect(0f, 90f, 220f, 8f);
    [Tooltip("How fast the bar fades in/out. It's hidden at full stamina so the screen stays clean.")]
    [SerializeField] private float fadeSpeed = 6f;
    [SerializeField] private Color barColor = new Color(0.85f, 0.85f, 0.8f);
    [Tooltip("Colour while sprint is locked out, so exhaustion reads instantly without having to judge " +
             "the bar's length.")]
    [SerializeField] private Color exhaustedColor = new Color(0.8f, 0.25f, 0.2f);
    [SerializeField] private Color backdropColor = new Color(0f, 0f, 0f, 0.55f);

    private float _alpha;                 // current fade, driven in Tick
    private static Texture2D _px;         // 1x1 white, tinted per draw via GUI.color

    // 0..1. Kept normalised so the tuning fields stay in seconds and the HUD needs no conversion.
    private float _current = 1f;
    private float _delayTimer;
    private bool _exhausted;

    /// <summary>Whether sprint is currently permitted. PlayerMovement asks this each frame.</summary>
    public bool CanSprint => !_exhausted && _current > 0f;

    /// <summary>Current stamina, 0..1. ScreenFeedback reads this to drive the low-stamina vignette.</summary>
    public float Fraction => _current;
    /// <summary>True while sprint is locked out (ran dry, recovering). Lets the vignette read exhaustion.</summary>
    public bool IsExhausted => _exhausted;

    public override void OnNetworkSpawn()
    {
        // Non-owners have no stamina state worth simulating and must never draw a bar -- their movement
        // arrives pre-resolved over the network. Disabling outright is cheaper than guarding every frame.
        if (!IsOwner) enabled = false;
    }

    /// <summary>
    /// Called by PlayerMovement once per frame with whether the player is ACTUALLY sprinting -- that is,
    /// holding sprint AND moving. Passing mere input would drain stamina while standing still holding
    /// shift, which players read as a bug.
    /// </summary>
    public void Tick(bool sprinting)
    {
        float dt = Time.deltaTime;

        if (sprinting)
        {
            _current -= dt / Mathf.Max(sprintSeconds, 0.01f);
            _delayTimer = regenDelay;
            if (_current <= 0f)
            {
                _current = 0f;
                _exhausted = true;   // locks sprint until recoverFraction is reached
            }
        }
        else
        {
            if (_delayTimer > 0f) _delayTimer -= dt;
            else _current = Mathf.Min(1f, _current + dt / Mathf.Max(regenSeconds, 0.01f));

            if (_exhausted && _current >= recoverFraction) _exhausted = false;
        }

        // Visible whenever stamina isn't full, or while locked out. Hiding it at full keeps the screen
        // clean for the horror atmosphere, and makes the bar APPEARING a signal in itself.
        float target = (_current < 0.999f || _exhausted) ? 1f : 0f;
        _alpha = Mathf.MoveTowards(_alpha, target, fadeSpeed * dt);
    }

    private void OnGUI()
    {
        if (!IsOwner || _alpha <= 0.001f) return;

        // One shared 1x1 white texture, tinted per draw with GUI.color. Allocating a Texture2D per
        // frame in OnGUI is the classic IMGUI leak -- OnGUI runs multiple times per frame (layout AND
        // repaint), so anything allocated here is allocated several times over.
        if (_px == null)
        {
            _px = new Texture2D(1, 1);
            _px.SetPixel(0, 0, Color.white);
            _px.Apply();
        }

        var r = new Rect((Screen.width - rect.width) * 0.5f,
                         Screen.height - rect.y - rect.height,
                         rect.width, rect.height);

        Color prev = GUI.color;

        GUI.color = new Color(backdropColor.r, backdropColor.g, backdropColor.b, backdropColor.a * _alpha);
        GUI.DrawTexture(new Rect(r.x - 1f, r.y - 1f, r.width + 2f, r.height + 2f), _px);

        Color fill = _exhausted ? exhaustedColor : barColor;
        GUI.color = new Color(fill.r, fill.g, fill.b, fill.a * _alpha);
        GUI.DrawTexture(new Rect(r.x, r.y, r.width * _current, r.height), _px);

        GUI.color = prev;   // OnGUI state is global -- leaving it tinted would colour every later HUD
    }
}
