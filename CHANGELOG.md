# Changelog

## 2026-09-14 — Clue System v2 (parts a–d)

### Breaking changes
- **`get_clue_board` response shape changed** (part d): previously returned a flat list of clue card id strings (`collectedClueCardIds`); now returns `entries: List<ClueBoardEntry>` where each entry carries `instanceId`, `displayName`, `reliability`, `locationId`, and a privacy-filtered `witnessNpcIds` list. AI VTuber clients must adapt.
- `PlayerSurvivalState.CollectedClueCardIds` renamed to `CollectedClueInstanceIds` (MessagePack Key(8) preserved — wire-compatible).

### Added
- **(a) Data model + registry**: `ClueInstance` (MessagePack, Keys 0–6) with `ClueTriggerSource` enum (KillSabotage / TaskSabotage / IncidentalAction / Hunting); `ClueDef.ClueCategory` (Luban column + mapper); central registry `GameStateProvider.AllClueInstances`; `ActionClueTriggerDef` table (7 weighted triggers).
- **(b) Generation pipeline + kill hook**: `ClueGenerationSystem.TryGenerate(...)` — independent per-trigger weighted rolls, witness snapshot excluding the source actor (player is never their own witness), instances stored centrally, `ClueGeneratedMessage` published 1:1. `NpcDirectorSystem.SpawnClues` now routes kills through the pipeline (0/1/2 clues per kill per weights).
- **(c) Incidental hooks + investigate tool**: `ExplorationSystem` generates IncidentalAction clues (water-related loot or 10% roll); `NodeHarvestSystem` generates Hunting clues (`HarvestableNodeDef.isHuntingTarget`, new `node_deer`); new `investigate_clue` MCP tool — **zero parameters**, server-side player location only (anti cross-zone scouting), main-thread dispatched, `VisibleToBystanders` → instant else 50% roll.
- **(d) MCP graph response layer**: `get_clue_board` returns filtered `ClueBoardEntry` list (witnesses filtered to alive NPCs + player); new `get_clue_graph` tool (nodes = collected clues + witnesses, edges = `witnessed`).

### Privacy / information hiding
- `ClueInstance.SourceActorId` and `WitnessNpcIds` never leave the server: `ClueGeneratedMessage` carries only id/def/location; board/graph responses expose only filtered witness ids. Verified by byte-scan of serialized responses (test D, part d).
- `investigate_clue` accepts no location parameter — the handler forces `player.CurrentLocationId`.

### Test evidence
- `TestEvidence/clue-system-v2-a/` — A–D (load 7 triggers, ClueCategory, empty registry, board handler).
- `TestEvidence/clue-system-v2-b/` — A–F (kill hook via real `use_card`, statistical roll distribution 200×, witness exclusion, player-not-own-witness, MCP regression).
- `TestEvidence/clue-system-v2-c/` — A–F (explore/hunt hooks, investigate round-trip via real McpBridge, no-location-param enforcement, kill-hook regression).
- `TestEvidence/clue-system-v2-d/` — A–D (board shape + filtered witnesses, dead-witness filter, graph JSON, SourceActorId byte-scan).
