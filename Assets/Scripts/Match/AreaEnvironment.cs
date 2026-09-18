using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Single runtime owner of the GLOBAL environment settings that must differ between the pre-session
/// menu (dark security room), the warm lobby, and the near-black dungeon: flat ambient colour +
/// reflection intensity.
///
/// WHY THIS EXISTS: RenderSettings.ambientLight / reflectionIntensity are SCENE-GLOBAL, but the menu,
/// lobby and the runtime-generated dungeon all share one scene. One baked value can't serve them — a
/// warm lobby floor would light up the dungeon and the dark menu. So we swap them at runtime by game
/// state and by where the ACTIVE VIEW is.
///
/// THREE STATES:
///   - No networked session (the menu / security room) -> menuAmbient (near-black). This is checked
///     FIRST, so the menu never inherits the warm lobby ambient regardless of camera position.
///   - In a session, camera outside the dungeon bounds -> lobbyAmbient (warm).
///   - In a session, camera inside the dungeon bounds  -> dungeonAmbient (near-black).
///
/// WHY CAMERA, NOT PLAYER BODY: in-session we key off Camera.main, so a lobby spectator orbiting a
/// dungeon player still gets the dungeon environment — it's about what's being RENDERED.
///
/// PLACEMENT (manual step): put exactly ONE of these on a persistent scene object (NOT the player
/// prefab) so there is a single writer of the global RenderSettings.
/// </summary>
public class AreaEnvironment : MonoBehaviour
{
    [Header("Menu (pre-session security room — dark)")]
    [Tooltip("Applied whenever NO networked session is active (i.e. the menu). Keep near-black so the " +
             "security room is lit only by its screens + desk lamp.")]
    [SerializeField] private Color menuAmbient = new Color(0.02f, 0.02f, 0.03f);
    [SerializeField] private float menuReflection = 0f;

    [Header("Lobby (warm floor)")]
    [Tooltip("Flat ambient colour in the lobby. Keep it LOW — in Flat mode the colour's magnitude " +
             "IS its intensity, so a hot value flattens all the pool/shadow drama. Start dim, raise " +
             "to taste. Default ~#241A11.")]
    [SerializeField] private Color lobbyAmbient = new Color(0.141f, 0.102f, 0.067f);
    [SerializeField] private float lobbyReflection = 1f;

    [Header("Dungeon (near-black, flashlight-required)")]
    [Tooltip("Matches the value LobbyGenerator used to bake in — keeps the dungeon flashlight-dark.")]
    [SerializeField] private Color dungeonAmbient = new Color(0.03f, 0.03f, 0.035f);
    [SerializeField] private float dungeonReflection = 0f;

    [Header("Transition")]
    [Tooltip("How fast ambient/reflection ease toward the target on an area change. High = " +
             "near-instant; smoothing avoids a hard pop at the teleport.")]
    [SerializeField] private float lerpSpeed = 6f;

    private void Awake()
    {
        // ambientLight is only honoured in Flat ambient mode.
        RenderSettings.ambientMode = AmbientMode.Flat;
    }

    // LateUpdate: read the camera AFTER it has moved this frame, so the environment matches what is
    // about to be rendered.
    private void LateUpdate()
    {
        Color targetAmbient;
        float targetReflection;

        // Menu FIRST: before any session, force the dark security-room environment regardless of where
        // the menu camera sits, so it doesn't inherit the warm lobby ambient.
        var nm = NetworkManager.Singleton;
        bool inSession = nm != null && (nm.IsClient || nm.IsServer);

        if (!inSession)
        {
            targetAmbient = menuAmbient;
            targetReflection = menuReflection;
        }
        else
        {
            Camera cam = Camera.main;
            if (cam == null) return;   // mid-transition / no active view — leave settings untouched.

            // Is the active view inside the generated dungeon's world bounds?
            bool inDungeon = DungeonGenerator.Instance != null
                && DungeonGenerator.Instance.WorldBounds.Contains(cam.transform.position);

            targetAmbient = inDungeon ? dungeonAmbient : lobbyAmbient;
            targetReflection = inDungeon ? dungeonReflection : lobbyReflection;
        }

        // Frame-rate-independent exponential ease toward the target: 1 - e^(-k·dt) approaches
        // cleanly without overshoot and behaves identically at any framerate.
        float t = 1f - Mathf.Exp(-lerpSpeed * Time.deltaTime);
        RenderSettings.ambientLight = Color.Lerp(RenderSettings.ambientLight, targetAmbient, t);
        RenderSettings.reflectionIntensity =
            Mathf.Lerp(RenderSettings.reflectionIntensity, targetReflection, t);
    }
}
