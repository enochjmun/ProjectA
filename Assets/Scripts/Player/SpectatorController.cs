using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// On the Player. When the server flips IsSpectating on (the moment you're caught), the
/// OWNER's view switches to a Lethal-Company-style spectator: a third-person camera
/// ORBITING a surviving player, with the mouse rotating the camera around them and
/// mouse buttons cycling between survivors. Turned off again when the player rejoins,
/// which restores first-person control.
///
/// Purely a local view change -- IsSpectating is the only networked bit (so others can
/// tell who's a spectator and skip them as targets). The caught body is frozen in place
/// (MovementLocked); the owner never sees it, they see whoever they're spectating.
/// </summary>
public class SpectatorController : NetworkBehaviour
{
    [Tooltip("The player's own child camera (same one PlayerCamera drives).")]
    [SerializeField] private Camera playerCamera;

    [Tooltip("Character mesh root -- its renderers are hidden on ALL clients while spectating, so " +
             "others don't see a frozen body standing at the spawn.")]
    [SerializeField] private GameObject modelRoot;

    [Header("Orbit")]
    [SerializeField] private float orbitDistance = 4.5f;
    [Tooltip("Height above the watched player's pivot that the camera orbits around. Lower it (or " +
             "even set it to 0 / negative) so the orbit centre sits on the character, not above it.")]
    [SerializeField] private float orbitHeight = 1.0f;
    [SerializeField] private float sensitivity = 3f;
    [SerializeField] private float minPitch = -20f;
    [SerializeField] private float maxPitch = 70f;

    [Header("Wall collision")]
    [Tooltip("Layers the camera can't pass through (walls/floors). Best to exclude the player " +
             "layers and triggers so it only collides with real geometry.")]
    [SerializeField] private LayerMask wallMask = ~0;
    [Tooltip("Camera's collision sphere radius -- keeps it from clipping into a wall it's hugging.")]
    [SerializeField] private float cameraRadius = 0.25f;
    [Tooltip("Gap kept between the camera and a wall it pulled in against.")]
    [SerializeField] private float collisionBuffer = 0.2f;
    [Tooltip("Closest the camera may pull in to the watched player.")]
    [SerializeField] private float minDistance = 0.6f;
    [Tooltip("How fast the camera eases back out after a wall clears (pull-in is instant).")]
    [SerializeField] private float returnSpeed = 6f;

    /// <summary>Server-write. True = this player is a spectator. Everyone reads it so the
    /// target list can skip other spectators.</summary>
    public readonly NetworkVariable<bool> IsSpectating = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private PlayerCamera _fpCamera;
    private PlayerInteractor _interactor;
    private PlayerMovement _movement;
    private CameraFeel _cameraFeel;

    private bool _activeLocal;
    private float _yaw, _pitch;
    private int _targetIndex;
    private float _currentDist;
    private Vector3 _camRestLocalPos;
    private Quaternion _camRestLocalRot;

    /// <summary>Server: enter/leave spectator mode for this player. Pass benched=true for a
    /// caught/benched player (their own 2D room, hears everything); benched=false for a surviving
    /// lobby player opting to watch the dungeon (still talks into Lobby proximity, gains dungeon ears).</summary>
    public void ServerSetSpectating(bool on, bool benched = false)
    {
        if (!IsServer) return;
        IsSpectating.Value = on;

        // Voice: leaving spectate always returns to the shared Lobby room. Entering picks the
        // spectator flavour by origin -- caught -> BenchedSpectator (own 2D room, hears all),
        // lobby opt-in -> LobbySpectator (still broadcasts into Lobby, just gains dungeon listen).
        var voice = GetComponent<CasinoHorrorGame.Networking.VoiceRoomRouter>();
        voice?.ServerAssignRole(on
            ? (benched
                ? CasinoHorrorGame.Networking.VoiceRoomRouter.Role.BenchedSpectator
                : CasinoHorrorGame.Networking.VoiceRoomRouter.Role.LobbySpectator)
            : CasinoHorrorGame.Networking.VoiceRoomRouter.Role.Lobby);
    }

    /// <summary>Owner-side: ask the server to toggle spectator mode. The placeholder lobby Spectate
    /// button calls this; the server only grants it to an eligible lobby watcher (see
    /// MatchController.CanLobbySpectate), so a dungeon player can't ghost out of their own chase.</summary>
    public void RequestSpectate(bool on)
    {
        if (IsOwner) RequestSpectateRpc(on);
    }

    [Rpc(SendTo.Server)]
    private void RequestSpectateRpc(bool on)
    {
        var ps = GetComponent<PlayerState>();
        if (MatchController.Instance != null && MatchController.Instance.CanLobbySpectate(ps))
            ServerSetSpectating(on);
    }

    private void Awake()
    {
        _fpCamera = GetComponent<PlayerCamera>();
        _interactor = GetComponent<PlayerInteractor>();
        _movement = GetComponent<PlayerMovement>();
        _cameraFeel = GetComponent<CameraFeel>();
        if (playerCamera != null)
        {
            _camRestLocalPos = playerCamera.transform.localPosition;
            _camRestLocalRot = playerCamera.transform.localRotation;
        }
    }

    public override void OnNetworkSpawn()
    {
        // Runs on every client: keep the model's visibility in sync with the spectating flag,
        // so a spectator's body is hidden for remote players too (not a frozen figure at spawn).
        IsSpectating.OnValueChanged += OnSpectatingChanged;
        ApplyModelVisibility(IsSpectating.Value);
    }

    public override void OnNetworkDespawn()
    {
        IsSpectating.OnValueChanged -= OnSpectatingChanged;
    }

    private void OnSpectatingChanged(bool _, bool now) => ApplyModelVisibility(now);

    // Hide/show the character mesh on EVERY client while spectating / not.
    private void ApplyModelVisibility(bool spectating)
    {
        if (modelRoot == null) return;
        foreach (var r in modelRoot.GetComponentsInChildren<Renderer>(true))
            r.enabled = !spectating;
    }

    private void Update()
    {
        if (!IsOwner) return;

        if (IsSpectating.Value)
        {
            if (!_activeLocal) EnterSpectate();
            UpdateOrbit();
        }
        else if (_activeLocal)
        {
            ExitSpectate();
        }
    }

    private void EnterSpectate()
    {
        _activeLocal = true;
        // Hand the camera over: stop first-person look, interaction, and movement.
        if (_fpCamera != null) _fpCamera.enabled = false;
        if (_interactor != null) _interactor.enabled = false;
        if (_movement != null) _movement.MovementLocked = true;

        // CameraFeel rebuilds the camera's LOCAL transform every LateUpdate (bob/lean/shake, relative to
        // THIS player's body). Left running it clobbers the orbit each frame and snaps the view back to
        // our own head -- which pins a seated spectator's camera at the table. Disable it while spectating.
        if (_cameraFeel != null) _cameraFeel.enabled = false;
        _targetIndex = 0;
        _currentDist = orbitDistance;

        // Orbit is driven by raw mouse delta + mouse buttons (same as FPS look), so it
        // wants a LOCKED, hidden cursor. Nothing else locks it on this path -- a lobby
        // watcher entered via the IMGUI Spectate button with a FREE cursor, which is
        // why the pointer was left visible over the orbit view. Lock it here. Only ever
        // reached for the owner (Update gates on IsOwner), so touching the cursor is safe.
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void ExitSpectate()
    {
        _activeLocal = false;
        // Restore the camera to its first-person rest pose, then re-enable normal control.
        if (playerCamera != null)
        {
            playerCamera.transform.localPosition = _camRestLocalPos;
            playerCamera.transform.localRotation = _camRestLocalRot;
        }
        if (_fpCamera != null) _fpCamera.enabled = true;
        if (_interactor != null) _interactor.enabled = true;
        if (_movement != null) _movement.MovementLocked = false;
        if (_cameraFeel != null) _cameraFeel.enabled = true;   // restore head-bob/juice for first-person

        // Back to first-person control (lobby / walking) -- keep the cursor locked+hidden
        // for FPS look. (Bonus: this also cleans up the cursor for a benched/caught player
        // whose compulsory spectate just ended server-side, which nothing did before.)
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void UpdateOrbit()
    {
        var targets = GatherTargets();
        if (targets.Count == 0 || playerCamera == null) return;

        // Cycle target: left click = next survivor, right click = previous (Lethal-Company style).
        if (Input.GetMouseButtonDown(0)) _targetIndex++;
        if (Input.GetMouseButtonDown(1)) _targetIndex--;
        _targetIndex = ((_targetIndex % targets.Count) + targets.Count) % targets.Count;

        var target = targets[_targetIndex];
        if (target == null) return;

        // Mouse orbits the camera around the target.
        _yaw += Input.GetAxisRaw("Mouse X") * sensitivity;
        _pitch -= Input.GetAxisRaw("Mouse Y") * sensitivity;
        _pitch = Mathf.Clamp(_pitch, minPitch, maxPitch);

        Vector3 focus = target.position + Vector3.up * orbitHeight;
        Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
        Vector3 camDir = -(rot * Vector3.forward);   // from the focus outward to the camera

        // Camera collision: sphere-cast from the watched player out to where the camera wants to
        // sit, and pull it in if a wall is in the way -- so spectators can't see through or past
        // dungeon/lobby walls. Starting inside the target's collider means the cast ignores it.
        float targetDist = orbitDistance;
        if (Physics.SphereCast(focus, cameraRadius, camDir, out var hit, orbitDistance, wallMask, QueryTriggerInteraction.Ignore))
            targetDist = hit.distance - collisionBuffer;
        targetDist = Mathf.Max(targetDist, minDistance);

        // Snap IN instantly so we never clip through a wall, but ease back OUT once it clears --
        // so brushing past geometry doesn't jolt the camera.
        _currentDist = targetDist < _currentDist
            ? targetDist
            : Mathf.Lerp(_currentDist, targetDist, returnSpeed * Time.deltaTime);

        Vector3 camPos = focus + camDir * _currentDist;

        // Set world pose directly each frame so the caught body's transform can't drag it.
        playerCamera.transform.position = camPos;
        playerCamera.transform.rotation = Quaternion.LookRotation(focus - camPos, Vector3.up);
    }

    // Targets = everyone present who isn't me and isn't also a spectator. DURING A CHASE we additionally
    // lock to players actually inside the dungeon bounds, so a spectator (lobby or benched) can only
    // cycle through the chase participants, never an idle lobby player. Outside a chase (e.g. a benched
    // player watching the mini-game) we fall back to everyone, so their view isn't left empty.
    private List<Transform> GatherTargets()
    {
        var list = new List<Transform>();
        var mc = MatchController.Instance;
        var dungeon = DungeonGenerator.Instance;
        bool chaseLock = mc != null && mc.ChaseActive && dungeon != null;
        foreach (var ps in FindObjectsByType<PlayerState>(FindObjectsSortMode.None))
        {
            if (ps == null || ps.gameObject == gameObject) continue;
            var spec = ps.GetComponent<SpectatorController>();
            if (spec != null && spec.IsSpectating.Value) continue;
            if (chaseLock && !dungeon.WorldBounds.Contains(ps.transform.position)) continue;   // lobby idler -> skip
            list.Add(ps.transform);
        }
        return list;
    }
}
