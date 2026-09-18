using UnityEngine;

/// <summary>
/// STATIC reverb for the dungeon (audio pass — the "pit" reverb). One AudioReverbZone sized to the
/// generated dungeon, set to a fixed cold preset ONCE. Unity's own distance blend (min/max) fades it in
/// as a listener enters the pit and out again toward the lobby, so no per-frame driving is needed.
///
/// ⚠ WHY NOT ROOM-DRIVEN ANY MORE (reverted 2026-08-04): the earlier version drove the zone's decayTime/
/// level EVERY frame to tighten the tail in corridors and open it in rooms. Unity's reverb DSP re-inits
/// on parameter changes and CLICKS -- 60 of those a second while the values eased sounded like gunshots.
/// AudioReverbZone simply isn't built to be modulated continuously. If room-size variation is wanted
/// later, the pop-free ways are: (a) several STATIC zones placed per room-size that Unity crossfades by
/// listener position, or (b) an FMOD snapshot per area. For now: one clean static pit reverb.
///
/// SCOPE: Unity reverb zones affect the UNITY audio path = Dissonance VOICE. FMOD SFX/music reverb is
/// separate (Friend A). Local, no netcode. The lobby stays DRY because the dungeon is ~276m from it,
/// well outside this zone's radius (see the lobby-dry decision).
///
/// EDITOR SETUP: put this + an AudioReverbZone on one persistent scene object (the manager that holds
/// AreaEnvironment / LobbyLightingController). Nothing else to assign -- it finds the generator and
/// sizes itself. Pick the preset below to taste.
/// </summary>
[RequireComponent(typeof(AudioReverbZone))]
public class DungeonReverbZone : MonoBehaviour
{
    [Tooltip("Cold/large preset for the concrete pit. Hallway / StoneCorridor = tighter concrete; " +
             "StoneRoom = a moderate stone space; Cave = big and very reverberant. Set once, no runtime " +
             "modulation, so it can't pop.")]
    [SerializeField] private AudioReverbPreset preset = AudioReverbPreset.Hallway;

    [Tooltip("Extra metres past the dungeon's far corner where the reverb fades to nothing. This fade " +
             "shell sits in EMPTY space outside the geometry, so it's never heard inside the dungeon.")]
    [SerializeField] private float outerPadding = 4f;

    private AudioReverbZone _zone;
    private bool _sized;

    private void Awake()
    {
        _zone = GetComponent<AudioReverbZone>();
        _zone.reverbPreset = preset;   // set ONCE — a built-in preset sets all params to coherent values
    }

    private void Update()
    {
        // Size once to the (constant) dungeon bounds as soon as the generator exists, then never touch
        // the zone again — no per-frame parameter writes, so no reverb-DSP pops.
        if (_sized || DungeonGenerator.Instance == null)
            return;

        Bounds b = DungeonGenerator.Instance.WorldBounds;
        transform.position = b.center;

        // AudioReverbZone is a SPHERE (min/max = radii from the centre), but the dungeon is a flat, wide
        // box. So size the FULL-reverb core (minDistance) to reach the box's farthest CORNER — then every
        // point in the dungeon sits inside the uniform full-reverb sphere, and the radial falloff happens
        // only in the empty shell OUTSIDE the geometry (harmless — the lobby is ~276 m away). That's how a
        // single sphere still gives even reverb across a planar dungeon: enclose the whole box in the core,
        // never rely on the radial gradient. (Per-ROOM variation would need multiple blended zones — the
        // deferred multi-zone approach — not one sphere.)
        float halfDiagonal = b.extents.magnitude;   // reaches the farthest corner of the box
        _zone.minDistance = halfDiagonal;            // full reverb everywhere inside the dungeon
        _zone.maxDistance = halfDiagonal + outerPadding;
        _sized = true;
    }
}
