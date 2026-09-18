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
            Lobby = 3,
            LobbySpectator = 4,
            BenchedSpectator = 5
        }

        private const string PreyRoom = "Prey";
        private const string MonsterRoom = "Monster";
        private const string LobbyRoom = "Lobby";
        private const string BenchedSpectatorRoom = "BenchedSpectator";

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
                // Everyone starts in the shared Lobby voice room (proximity audio).
                // Match roles (Monster/Prey) are assigned later by MatchController at
                // the Lobby -> RoundStart transition via ServerAssignRole below.
                _role.Value = Role.Lobby;

                Debug.Log($"[VoiceRoomRouter] Server spawned {OwnerClientId} -> role 'Lobby'");
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

        private void OnRoleChanged(Role previous, Role current)
        {
            ApplyRole(current);
        }

        /// <summary>
        /// Server-only: set this player's voice role. MatchController calls this at the
        /// Lobby -> RoundStart transition to assign Monster/Prey -- the long-noted
        /// "MatchController drives _role" wiring. Writing _role replicates and the
        /// owner's OnRoleChanged -> ApplyRole pipeline reacts, same path as before.
        /// </summary>
        public void ServerAssignRole(Role role)
        {
            if (!IsServer)
                return;

            _role.Value = role;
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

            string room;
            switch (role)
            {
                case Role.Prey:
                    room = PreyRoom;
                    break;

                case Role.Monster:
                    room = MonsterRoom;
                    break;

                case Role.Lobby:
                    // Pre-match: everyone talks into the shared Lobby room. Proximity
                    // falloff comes from Dissonance positional playback, not room cuts.
                    room = LobbyRoom;
                    break;

                case Role.LobbySpectator:
                    // A surviving lobby player watching the dungeon still TALKS into the Lobby
                    // room (proximity), exactly like a normal lobby player. Spectating only
                    // changes what they HEAR (see ApplyListenRooms), not what they broadcast.
                    room = LobbyRoom;
                    break;

                case Role.BenchedSpectator:
                    // Caught/benched players talk into their OWN 2D room. No living role listens
                    // to it (see ApplyListenRooms), so their chatter never leaks to
                    // prey/monster/lobby -- but other benched spectators DO hear it, so the dead
                    // can talk amongst themselves.
                    room = BenchedSpectatorRoom;
                    break;

                default:
                    return;
            }

            // ⚠ FORCE A RE-TARGET BY TOGGLING `enabled` (fixed 2026-07-22).
            //
            // Assigning RoomName on a trigger that ALREADY HAS AN OPEN CHANNEL does not reliably
            // move that channel -- Dissonance keeps transmitting into the OLD room while this
            // script believes it switched. The listen side (which this class drives directly via
            // Comms.Rooms) updates correctly, so the two desynchronise.
            //
            // Symptom this caused: a player assigned Prey at round start, who then began
            // spectating, kept broadcasting into PreyRoom. Their listen set correctly became the
            // spectator set, so they could hear the chase -- and anyone listening to PreyRoom
            // could still hear THEM. It read as faint because spectator broadcast is positional
            // and their body was far away in the lobby, which is also why it went unnoticed:
            // the leak's volume depends on where people happen to be standing.
            //
            // Toggling enabled closes the existing channel and opens a fresh one on the new room.
            _broadcast.enabled = false;

            // Proximity (positional) playback for the LIVING rooms; the Benched room is
            // NON-positional -- flat, full-volume 2D voice -- so the dead hear each other clearly
            // no matter where their frozen bodies ended up.
            _broadcast.BroadcastPosition = role != Role.BenchedSpectator;
            _broadcast.ChannelType = CommTriggerTarget.Room;
            _broadcast.RoomName = room;

            _broadcast.enabled = true;

            _debugBroadcastRoom = room;
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
                    // Co-monsters hear each other (they broadcast into MonsterRoom). Prey never
                    // listen to MonsterRoom, so this stays one-directional.
                    desired.Add(MonsterRoom);
                    break;

                case Role.Lobby:
                    // Everyone hears everyone in the lobby (distance-attenuated by
                    // positional playback).
                    desired.Add(LobbyRoom);
                    break;

                case Role.LobbySpectator:
                    // A surviving lobby watcher hears the whole LIVING game -- lobby (proximity),
                    // prey and monster -- but NOT the benched 2D room, so the two spectator groups
                    // don't share a channel.
                    desired.Add(LobbyRoom);
                    desired.Add(PreyRoom);
                    desired.Add(MonsterRoom);
                    break;

                case Role.BenchedSpectator:
                    // Hears EVERYTHING living -- lobby, prey, monster -- plus its own benched room
                    // so the dead hear each other. There is no separate lobby-spectator channel to
                    // add (they talk into Lobby, already covered). No living role adds
                    // BenchedSpectatorRoom, keeping this channel one-way (they hear all; none hear
                    // them).
                    desired.Add(LobbyRoom);
                    desired.Add(PreyRoom);
                    desired.Add(MonsterRoom);
                    desired.Add(BenchedSpectatorRoom);
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
