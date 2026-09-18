# CasinoHorrorGame — project brief for Claude Code

Multiplayer casino-HORROR-comedy game (Unity 6 / URP 17, Netcode for GameObjects + Facepunch P2P, Dissonance voice). Solo dev (Enoch). Tonal spine: **"shoddy facade, sinister polish — the house always wins."** A warm, inviting casino/home-game facade over a cold machine that never lets you leave.

This file is the lean, always-loaded index. **Full rigor lives in the docs below — read the specific one for detail; don't assume from this summary.**

## Golden rules (always true)
- **Replicate the driver, apply the driver everywhere.** Server writes a minimal `NetworkVariable` (seed / phase / role / flag); every client reconstructs the visual locally in `Update()`. NO per-object NetworkObjects for geometry/animation. This pattern is everywhere — follow it.
- **Don't touch `Assets/Dissonance` or `Assets/Plugins`** (vendored voice plugin).
- **Don't rename `VoiceRoomRouter`** (prefab wired to its file GUID) — see docs/voice-routing.md.
- **FacepunchTransport owns Steam init** — nothing else may call SteamClient.Init.
- Server- vs client-authoritative `NetworkTransform` must match the mover; after an owner-targeted teleport RPC the server's position copy is stale — use the known spawn point.
- URP gotchas: runtime `CreatePrimitive` → magenta unless you assign a URP material; a LayerMask saved "Everything" before a layer existed silently excludes it.

## Where knowledge lives (source of truth = on-disk files)
**Engineering reference** — `docs/` (exported from Cowork memory 2026-09-19):
- `docs/code-map.md` — where every script lives (Match/ Player/ Networking/ UI/), what it does.
- `docs/engineering-state.md` — current build state, systems status, open threads.
- `docs/layers.md` — Unity layer map (Environment=12 lobby, Dungeon=13 runtime, etc.).
- `docs/voice-routing.md` — the asymmetric 5-role Dissonance voice model + gotchas.

**Design docs** — the separate folder `C:\Users\enoch\Claude\Projects\Game Design\` (add it as an extra working dir if not in scope). Key ones: `SESSION_HANDOFF.md` (live status, read first), `CasinoHorrorGame_GDD_v7_updated.md`, `VerticalSlice_Scope.md`, `Playtest_Plan_3Client_VerticalSlice.md`, `House_OS_UI_DesignSystem.md`, `Debt_FakeReplayability.md`, `Dungeon_Preload_And_Windowed_Generation.md`, `Menu_Lore_Spine.md`, plus per-system specs (dungeon, table, elevator, monster) and `house_os_*.html` UI mockups.

**Code itself** — scripts carry heavy XML-doc summary comments; they're real documentation, read them.

## Current focus / open threads
- **UI is basically done** (House OS amber terminal: menu security-room + connect facet, marker/debt, cursor, leaderboard, pause overlay). See `House_OS_UI_DesignSystem.md`.
- **The FUN is UNPROVEN at 3-client** — the deferred playtest is the real gate (see `Playtest_Plan_3Client_VerticalSlice.md`). Riskiest question: does the lobby's outcome-BETTING keep non-chased players engaged, or is it dead air?
- Parked/next: **dungeon modelling phase** (Blender kit) → then the pooling + windowed-generation refactor (`Dungeon_Preload_And_Windowed_Generation.md`); character **re-rig** (Mixamo, shoulder tearing); wiring the House-OS screens to real match phases (SetRound, ScreenDeploy, standings/debt reveal).
- Enoch flagged the **dungeon** as the weakest part of the slice.

## Note
The Cowork/claude.ai memory store (separate from this repo) holds additional curated notes; the important ones are exported here + in the Game Design folder. Keep on-disk docs as the single source of truth so Cowork and Claude Code stay in sync.
