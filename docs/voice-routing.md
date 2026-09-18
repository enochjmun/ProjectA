# Voice routing — VoiceRoomRouter (asymmetric 5-role Dissonance model)

*(Exported from Cowork memory 2026-09-19. Verified from source 2026-08-04.)*

`VoiceRoomRouter` (Networking/) implements GDD §7 asymmetric voice. **FIVE roles**: `Prey`, `Monster`, `Lobby`, `LobbySpectator`, `BenchedSpectator`. Broadcast + listen are decoupled per player (Dissonance allows it):

- **Prey** — broadcast Prey room; listen Prey. (Monster also listens to Prey → talking is a beacon that gives away position; prey never hear Monster back.)
- **Monster** — broadcast Monster; listen Prey+Monster. (No player gets this in the slice — the monster is the AI Stalker. Role kept for a future player-monster; nothing hands it out.)
- **Lobby** — broadcast+listen Lobby (positional proximity). Everyone starts here.
- **LobbySpectator** — a surviving lobby player opting to watch: still broadcasts into Lobby (proximity), but listens Lobby+Prey+Monster (gains dungeon ears). NOT the benched room.
- **BenchedSpectator** — caught/benched: broadcasts into own **non-positional 2D** BenchedSpectator room; listens to everything living + benched. No living role listens to BenchedSpectator → one-way (dead hear all, none hear them; dead hear each other).

Role is a server-written NetworkVariable; owner's `OnRoleChanged → ApplyRole` drives local Dissonance. `MatchController` / `SpectatorController` call `ServerAssignRole`.

**⚠ Two gotchas baked into the code:**
1. **Don't rename the class or file** — the Player prefab's component ref is wired to this file's GUID; renaming → "Missing Script".
2. **Broadcast re-target requires toggling `_broadcast.enabled`** (fixed 2026-07-22). Assigning `RoomName` on a trigger with an already-open channel does NOT move it — it keeps transmitting into the OLD room while the listen side updates correctly, so the two desync (symptom: a Prey→spectator player leaking into PreyRoom). Toggling enabled closes the old channel and opens a fresh one.
