using System.Collections.Generic;
using Dissonance;
using Unity.Netcode;
using UnityEngine;

namespace CasinoHorrorGame.Networking
{
    /// <summary>
    /// Rung 4 (current): live, event-driven, ASYMMETRIC voice routing -- the real
    /// GDD §7 mechanic, not the symmetric "team room" stand-in this file used to
    /// contain.
    ///
    /// NOTE on the name: this class is still called "VoiceRoomRouter" and still
    /// lives in this file purely because the Player prefab's component reference
    /// is wired to THIS FILE'S GUID. Renaming the .cs file or the class would
    /// generate a new GUID in Unity and the prefab would show "Missing Script"
    /// until someone re-added it by hand in the Editor. Don't rename either one
    /// without doing that.
    ///
    /// What changed from the Rung 3/4 spike and why:
    /// The old version had ONE NetworkVariable, "assigned room", and used it for
    /// BOTH broadcast and receipt -- i.e. it modeled "room" as a synonym for
    /// "team". That's wrong for this game. The real requirement is asymmetric:
    ///   - Prey broadcast into the "Prey" room. Other prey hear them (normal
    ///     talk) AND the monster hears them too -- voice is a beacon, talking
    ///     gives away your position to your own hunter.
    ///   - Monster broadcasts into the "Monster" room (body/breathing sounds).
    ///     Prey must never hear the monster's actual voice channel.
    ///   - Spectators (benched/caught players) broadcast nothing but must hear
    ///     BOTH rooms.
    ///
    /// Dissonance already decouples broadcast and receipt per player -- nothing
    /// requires them to match -- so the asymmetry itself isn't new engineering.
    /// The one real gap is that the stock VoiceReceiptTrigger component only
    /// holds a single room name, but Spectator needs to listen to more than one
    /// room at once. Dissonance room membership isn't exclusive (a listener can
    /// be in many rooms simultaneously), so this is solved by dropping
    /// VoiceReceiptTrigger entirely and managing Comms.Rooms.Join/Leave directly
    /// for an arbitrary SET of rooms. Broadcast still only ever needs one room
    /// per role, so VoiceBroadcastTrigger is kept and just retargeted.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class VoiceRoomRouter : NetworkBehaviour
    {
        public enum Role : byte
        {
            Prey = 0,
            Monster = 1,
            Spectator = 2
        }

        private const string PreyRoom = "Prey";
        private const string MonsterRoom = "Monster";

        // Server-side only. First player spawned becomes the Monster (this game
        // has one monster in the vertical slice -- see GDD); everyone after that
        // spawns as Prey. Static is safe because only the server process ever
        // reads/writes it -- clients never enter the IsServer branch below.
        private static bool _monsterAssigned;

        private readonly NetworkVariable<Role> _role = new NetworkVariable<Role>(
            Role.Prey,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private VoiceBroadcastTrigger _broadcast;
        private DissonanceComms _comms;

        // Listen side is no longer driven by a single-room Inspector component --
        // this tracks however many rooms the local player is currently joined to,
        // so Spectator can be a member of more than one at once.
        private readonly Dictionary<string, RoomMembership> _activeListenRooms = new Dictionary<string, RoomMembership>();

        // ---- Debug-only Inspector mirror -----------------------------------
        // The real state lives in the NetworkVariable above and the dictionary
        // above that -- neither shows up usefully in the Inspector (a Dictionary
        // doesn't render, and the old VoiceReceiptTrigger's RoomName field is
        // dead/disabled and frozen at whatever it last had). These three fields
        // are written every time ApplyRole runs, purely so you can select the
        // GameObject in the Hierarchy during Play mode and see what's actually
        // happening without digging through the Console. Don't edit them by
        // hand -- they get overwritten on the next role change.
        // These are written but never read back in code -- their only "reader"
        // is the Unity Inspector pulling the serialized value off the
        // GameObject, which the C# compiler can't see, so it flags them as
        // dead writes (CS0414). They're not dead; suppress the false positive.
#pragma warning disable 0414
        [Header("Debug (read-only, runtime only -- do not edit)")]
        [SerializeField] private Role _debugRole;
        [SerializeField] private string _debugBroadcastRoom;
        [SerializeField] private string _debugListenRooms;
#pragma warning restore 0414

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            _broadcast = GetComponent<VoiceBroadcastTrigger>();

            // The old single-room VoiceReceiptTrigger would fight with the
            // multi-room logic below (it doesn't know about rooms this script
            // joins, and would try to leave/join its own single room on top of
            // that). This script now owns ALL listen-room membership directly
            // via the Dissonance API, so the component is switched off for good.
            var legacyReceipt = GetComponent<VoiceReceiptTrigger>();
            if (legacyReceipt != null)
                legacyReceipt.enabled = false;

            if (IsServer)
            {
                var role = _monsterAssigned ? Role.Prey : Role.Monster;
                _monsterAssigned = true;
                _role.Value = role;

                Debug.Log($"[VoiceRoomRouter] Server assigned {OwnerClientId} -> role '{role}'");
            }

            if (IsOwner)
            {
                // This is MY OWN avatar -- safe to drive my local Dissonance
                // instance. Named method for +=/-= (not a lambda) so despawn
                // actually unsubscribes the same delegate it subscribed.
                _comms = DissonanceComms.GetSingleton();
                _role.OnValueChanged += OnRoleChanged;

                // The NetworkVariable may already hold its server-assigned value
                // by the time spawn reaches the owner client; apply whatever it
                // currently holds rather than assuming the default.
                ApplyRole(_role.Value);
            }
            else
            {
                // Not my avatar -- every spawned copy of this prefab (including
                // remote players' avatars rendered on MY machine) carries its own
                // copy of these components. If left enabled, a remote avatar's
                // trigger would pull MY local Dissonance instance into THAT
                // player's room, silently defeating the whole routing scheme.
                if (_broadcast != null)
                    _broadcast.enabled = false;
            }
        }

        public override void OnNetworkDespawn()
        {
            if (IsOwner)
            {
                _role.OnValueChanged -= OnRoleChanged;
                LeaveAllListenRooms();
            }

            base.OnNetworkDespawn();
        }

        private void Update()
        {
            if (!IsOwner)
                return;

            // Rung 4 stand-in for a real game event (caught by the monster,
            // benched/respawned, etc.) -- that system doesn't exist yet, so this
            // key press is a throwaway substitute that exercises the same
            // mechanism: server flips the NetworkVariable mid-session,
            // replication fires OnValueChanged, ApplyRole re-points broadcast
            // and re-syncs the listen-room set. Remove this Update() once a real
            // event source exists.
            if (Input.GetKeyDown(KeyCode.R))
                RequestCycleRoleServerRpc();
        }

        [ServerRpc]
        private void RequestCycleRoleServerRpc()
        {
            // Monster is reserved for whoever was assigned it at spawn -- there's
            // no real "monster gets caught/swapped" design yet, so this manual
            // trigger only toggles Prey <-> Spectator for testing.
            if (_role.Value == Role.Monster)
                return;

            var next = _role.Value == Role.Prey ? Role.Spectator : Role.Prey;
            _role.Value = next;

            Debug.Log($"[VoiceRoomRouter] Server reassigned {OwnerClientId} -> role '{next}' (Rung 4 manual trigger)");
        }

        private void OnRoleChanged(Role previous, Role current)
        {
            ApplyRole(current);
        }

        private void ApplyRole(Role role)
        {
            if (_comms == null)
            {
                Debug.LogWarning("[VoiceRoomRouter] No local DissonanceComms found -- cannot apply role.");
                return;
            }

            _debugRole = role;

            ApplyBroadcast(role);
            ApplyListenRooms(role);
        }

        private void ApplyBroadcast(Role role)
        {
            if (_broadcast == null)
                return;

            switch (role)
            {
                case Role.Prey:
                    _broadcast.enabled = true;
                    _broadcast.ChannelType = CommTriggerTarget.Room;
                    _broadcast.RoomName = PreyRoom;
                    _debugBroadcastRoom = PreyRoom;
                    break;

                case Role.Monster:
                    _broadcast.enabled = true;
                    _broadcast.ChannelType = CommTriggerTarget.Room;
                    _broadcast.RoomName = MonsterRoom;
                    _debugBroadcastRoom = MonsterRoom;
                    break;

                case Role.Spectator:
                    // GDD as written only specifies spectators HEAR everything --
                    // it doesn't say they can talk. Treating spectators as silent
                    // observers for now; flag to design if benched players should
                    // actually be able to talk to each other (would just need a
                    // third room).
                    _broadcast.enabled = false;
                    _debugBroadcastRoom = "(none -- silent)";
                    break;
            }
        }

        private void ApplyListenRooms(Role role)
        {
            var desired = new HashSet<string>();

            switch (role)
            {
                case Role.Prey:
                    // Hear other prey. This is the same room prey broadcast into,
                    // so this is also (by construction) the beacon room the
                    // monster is listening to -- prey do NOT get the monster's
                    // room added here, which is what keeps this one-directional.
                    desired.Add(PreyRoom);
                    break;

                case Role.Monster:
                    // The beacon: monster hears prey voices, prey don't hear back.
                    desired.Add(PreyRoom);
                    break;

                case Role.Spectator:
                    // Hears everything.
                    desired.Add(PreyRoom);
                    desired.Add(MonsterRoom);
                    break;
            }

            SyncListenRooms(desired);

            _debugListenRooms = string.Join(", ", desired);

            Debug.Log($"[VoiceRoomRouter] Local player now role '{role}', listening to [{_debugListenRooms}]");
        }

        private void SyncListenRooms(HashSet<string> desired)
        {
            // Leave anything we're currently in that we shouldn't be anymore.
            var toLeave = new List<string>();
            foreach (var room in _activeListenRooms.Keys)
            {
                if (!desired.Contains(room))
                    toLeave.Add(room);
            }

            foreach (var room in toLeave)
            {
                _comms.Rooms.Leave(_activeListenRooms[room]);
                _activeListenRooms.Remove(room);
            }

            // Join anything new.
            foreach (var room in desired)
            {
                if (!_activeListenRooms.ContainsKey(room))
                    _activeListenRooms[room] = _comms.Rooms.Join(new RoomName(room));
            }
        }

        private void LeaveAllListenRooms()
        {
            if (_comms == null)
                return;

            foreach (var membership in _activeListenRooms.Values)
                _comms.Rooms.Leave(membership);

            _activeListenRooms.Clear();
        }
    }
}
