using System;
using UnityEngine;

/// <summary>
/// Drives lobby lighting between two states — CALM (tier 1: lobby/intermission) and TABLETOP
/// (tier 2: a minigame is live) — off the MatchController phase.
///
/// It does NOT touch any light itself. Its whole output is a single smoothed <see cref="Blend"/>
/// (0 = calm, 1 = tabletop) that per-light <see cref="LightTierResponder"/>s (and any future
/// fog/ambient responder) read. One place owns the TIMING; everything else is a pure function of
/// Blend. That keeps this tiny and lets you tag only the lights that actually change.
///
/// Local/visual-only: reads the already-replicated phase and reacts on each client — no netcode,
/// same pattern as the trapdoor. Put ONE on a persistent scene object (the same manager that
/// holds AreaEnvironment is a fine home).
/// </summary>
public class LobbyLightingController : MonoBehaviour
{
    public static LobbyLightingController Instance { get; private set; }

    [Tooltip("Scene MatchController. Auto-found if left empty.")]
    [SerializeField] private MatchController match;

    [Tooltip("How fast the blend eases between states. Lower = a slower, tension-creeping " +
             "low->mid transition; higher = snappier.")]
    [SerializeField] private float blendSpeed = 2.5f;

    [Header("Debug / authoring")]
    [Tooltip("When ON, Blend is driven by the slider below (and the key toggle) instead of the " +
             "match phase — so you can preview/author the two tiers without reaching a real " +
             "minigame. Turn OFF to return to phase-driven.")]
    [SerializeField] private bool debugOverride;
    [SerializeField, Range(0f, 1f)] private float debugBlend;
    [Tooltip("In play, snaps debugOverride on and flips the slider between calm (0) and tabletop (1).")]
    [SerializeField] private KeyCode debugToggleKey = KeyCode.F8;

    /// <summary>0 = calm/tier-1, 1 = tabletop/tier-2. Responders read this. Smoothed HERE so
    /// responders don't each need their own easing.</summary>
    public float Blend { get; private set; }

    /// <summary>Fired once per real phase transition — hook for one-shot cues (the loss stinger).</summary>
    public event Action<MatchController.Phase> PhaseChanged;

    private MatchController.Phase _lastPhase;
    private bool _havePhase;

    private void Awake()
    {
        Instance = this;
        if (match == null) match = FindObjectOfType<MatchController>();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        // F8: force the debug override on and flip its target between calm and tabletop.
        if (Input.GetKeyDown(debugToggleKey))
        {
            debugOverride = true;
            debugBlend = debugBlend > 0.5f ? 0f : 1f;
        }

        // Decide the TARGET (0/1) — from the debug slider if overriding, else from the phase.
        float target;
        if (debugOverride)
        {
            target = debugBlend;
        }
        else
        {
            if (match == null) return;

            MatchController.Phase phase = match.CurrentPhase;

            // Fire the change event once per real transition (future loss-stinger hook).
            if (!_havePhase || phase != _lastPhase)
            {
                _havePhase = true;
                _lastPhase = phase;
                PhaseChanged?.Invoke(phase);
            }

            // TABLETOP (tier 2) while a game is live at the table, through its resolution.
            // Everything else — lobby, ready-up, the warm chase/betting intermission, round
            // end — is CALM (tier 1).
            bool tabletop = phase == MatchController.Phase.GameSelect
                         || phase == MatchController.Phase.GamePlay
                         || phase == MatchController.Phase.GameResolve;
            target = tabletop ? 1f : 0f;
        }

        // Single ease toward the target — so EVERY state change lerps (phase, F8, or slider),
        // never snaps. Framerate-independent exponential approach (same as AreaEnvironment).
        float t = 1f - Mathf.Exp(-blendSpeed * Time.deltaTime);
        Blend = Mathf.Lerp(Blend, target, t);
    }
}
