# Unity layer map

*(Exported from Cowork memory 2026-09-19. Traced from SampleScene 2026-08-04.)*

Layers defined in `ProjectA/ProjectSettings/TagManager.asset` (index → name): 0 Default, 1 TransparentFX, 2 Ignore Raycast, 3 HoleMask, 4 Water, 5 UI, 6 OwnerHidden, 7 Player, 8 HoleEraser, 9 Falling, 10 LobbyFloor, 11 LocalPlayer, 12 Environment, 13 Dungeon.

**Solid-geometry layers:**
- **Environment (12)** = hand-modeled, textured LOBBY geometry — shell, bar, pillars (`LobbyShell.fbx`, `Bar.fbx`, `BarInside.fbx`, prefab instances). ~29 objects. NOTE: their layer is set via prefab-instance OVERRIDES (`propertyPath: m_Layer, value: 12`), NOT plain `m_Layer:` lines — so `grep 'm_Layer: 12'` finds 0 and misleads. The lobby is NOT greybox anymore.
- **Dungeon (13)** = the runtime procedural dungeon. `DungeonGenerator` sits on this layer; EVERYTHING it spawns inherits it via `SetLayerRecursively(go, gameObject.layer)`. Load-bearing: the NavMesh bake is masked to exactly this layer (`1 << gameObject.layer`), and `StalkerAI.sightBlockers` must include it.
- **HoleEraser (8)** = the table floor + plinth the trapdoor stencil erases (~2 objects). The surface a loser drops THROUGH.
- **LobbyFloor (10)** = currently 0 objects (floor reads as part of the shell on Environment). Reserved.
- Player (7) / LocalPlayer (11) / OwnerHidden (6) / Falling (9) = player bodies & the falling chair — NOT solid-world.

**So: Environment = authored lobby solids, Dungeon = runtime dungeon solids** (no overlap; both populated). For `VoiceSpatialProcessor.occluderMask` tick **Environment + Dungeon + HoleEraser** → covers lobby walls, dungeon walls, and the trapdoor floor (so the fall-scream muffles).
