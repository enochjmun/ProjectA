using UnityEngine;

/// <summary>
/// Tag dropped on every spawned card (Phase 2) so a raycast hit can tell
/// exactly which card it touched -- whose hand it belongs to and which slot in
/// the fan -- instead of inferring from position. Pure presentation glue: no
/// networking, no game state. OldMaidMiniGame.HandlePickInput reads OwnerClientId
/// to confirm the hit card belongs to the current neighbor before treating it as
/// a hover/draw target; OldMaidCardView reads Index and animates the runtime
/// fields below.
/// </summary>
public class OldMaidCardRef : MonoBehaviour
{
    public ulong OwnerClientId;
    public int Index;

    // The card's body renderer, cached so the peek reveal can swap its material
    // (back -> face) without re-finding it among the children.
    [System.NonSerialized] public MeshRenderer Body;

    // Runtime-only: current physical lift height (metres), lerped toward target
    // by OldMaidCardView so the hovered/revealed card rises smoothly.
    [System.NonSerialized] public float CurrentLift;

    // Peek state (local to the peeker's client only). Revealed = this card is
    // currently turned face-up toward the local viewer because they paid to peek
    // it; RevealLabel is the rank text object added for that reveal, removed when
    // the reveal clears.
    [System.NonSerialized] public bool Revealed;
    [System.NonSerialized] public GameObject RevealLabel;

    // Counts down while > 0 to play the "someone peeked this card" spin tell,
    // shown on every client (a back-faced card spinning leaks no rank).
    [System.NonSerialized] public float SpinTimer;
}
