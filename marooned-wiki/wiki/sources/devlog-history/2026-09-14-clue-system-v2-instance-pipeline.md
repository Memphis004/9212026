# Clue System v2 — Instance-Based Clue Pipeline (Parts a–d)

**Date:** 2026-09-14

## Overview
- เปลี่ยน clue จาก "card string" เป็น "instance object" ที่มี metadata ครบ (timestamp, source, witnesses) —
  พร้อม generation pipeline กลาง, hooks ครบทุก trigger (ฆ่า/สำรวจ/ล่าสัตว์), และ MCP response layer ใหม่
  (board + graph) สำหรับ AI VTuber
- Information hiding ทำทุกชั้น: message ที่ publish ไม่มี ground truth, response ที่ AI เห็นผ่าน filter
  แล้ว (killer และ NPC ตายไม่มีวันโผล่), `investigate_clue` ไม่รับ location จาก caller (กันสำรวจข้ามโซน)
- ทำเป็น 4 commit ต่อเนื่อง: (a) data model → (b) generation + kill hook → (c) incidental hooks +
  investigate → (d) response layer + graph

## Part (a) — Data Model + Registry
- `Shared/ClueInstance.cs` ใหม่: `[MessagePackObject]` Keys 0–6 (`InstanceId/DefId/LocationId/
  GameTimestamp/Source/SourceActorId/WitnessNpcIds`) — ไม่มี nullable suffix ตาม convention Shared DTO;
  `ClueTriggerSource` enum: `KillSabotage / TaskSabotage / IncidentalAction / Hunting`
  (`SourceActorId` + `WitnessNpcIds` = ground truth ห้าม expose ตรง ๆ)
- `PlayerSurvivalState`: เปลี่ยนชื่อ `CollectedClueCardIds` → `CollectedClueInstanceIds`
  **คง Key(8) เดิม** (wire-compatible, เปลี่ยนแค่ชื่อ)
- `ClueDef` เพิ่ม `ClueCategory` (wet/blood/footprint/general) — Luban column ใหม่ + แถว `clue_wet_clothes`
  (รวม 5 defs) + mapper copy field ใหม่ (ไม่งั้น field หายเงียบ ๆ)
- ตาราง `ActionClueTriggerDef.csv` ใหม่ 7 triggers (คู่ trigger×clue, weight อิสระ):
  kill_blood 100 / kill_scratch 30 / hunt_blood 25 / hunt_scratch 15 / water_wet 20 / water_mud 15 /
  task_sabotage 40 — `gen.sh` generate `TbActionClueTriggerDef` + `LubanDefMappers.MapActionClueTriggers`
  (pattern เดียวกับ MapCards, enum parse จาก string)
- `GameStateProvider.AllClueInstances` = registry กลาง (multiplayer-ready, ไม่ผูก player คนเดียว)

## Part (b) — Generation Pipeline + Kill Hook
- `ClueGenerationSystem.TryGenerate(ClueTriggerSource, locationId, sourceActorId)`:
  filter trigger ตาม source → **roll อิสระต่อ trigger** (`rng.Next(100) < weight`, weight=100 = เกิดเสมอ)
  → snapshot พยาน (NPC มีชีวิตในโซน, **exclude ผู้ก่อเหตุ**; player ถูกนับเฉพาะอยู่โซนเดียวกัน
  และไม่ใช่คนก่อเหตุ — player ไม่เป็นพยานตัวเอง) → เก็บ registry กลาง → publish `ClueGeneratedMessage`
  (Keys 0–2: InstanceId/DefId/LocationId — **ไม่มี ground truth บน message**) → คืน List (empty ได้)
- **DI cycle ปลอดภัย**: `ClueGenerationSystem` อ่าน NPC ผ่าน `UtilityContext` (ไม่ใช่ NpcDirectorSystem
  ตรง ๆ) — ใช้ Bind pattern เดียวกับ AI brains, ด้าน `NpcDirectorSystem` inject generator ผ่าน constructor
  ได้ปกติ; ลงทะเบียน `Lifetime.Singleton` ใน GameLifetimeScope
- `NpcDirectorSystem.SpawnClues` เดิม (hardcode blood + สุ่ม 30% scratch) → เรียก pipeline ใหม่
  และ append instance ids เข้า `victim.AllConditionCardIds` (รักษาความเข้ากันได้กับ DeductionSystem —
  `FilterVisible` ผ่าน id ที่ไม่ใช่ illness ออกทั้งหมด)
- การฆ่าหนึ่งครั้งได้ 0/1/2 clues ตาม weight (เทส 200 kills: 0 clue = 0%, 1 clue ≈ 66.5%, 2 clues ≈ 33.5%)

## Part (c) — Incidental Hooks + investigate_clue Tool
- `ExplorationSystem`: หลัง roll loot สำเร็จ — ถ้าได้ของเกี่ยวกับน้ำ (`water*` / `raw_fish`) หรือสุ่ม 10%
  → `TryGenerate(IncidentalAction, locationId)` (ไม่เดาจาก biome — ใช้ loot card id ที่ได้จริง)
- `NodeHarvestSystem`: หลัง harvest สำเร็จ — node ที่เป็น "การล่าสัตว์" → `TryGenerate(Hunting, …,
  LocalPlayerId)`; กำหนดด้วย column `isHuntingTarget` ใหม่ใน `HarvestableNodeDef.csv` (+ แถว
  `node_deer` ใหม่) — data-driven เพราะไม่มี location↔biome mapping ให้เช็ค
- **MCP tool `investigate_clue` — ไม่มีพารามิเตอร์เดียว**: handler ใช้ `player.CurrentLocationId`
  ฝั่ง server เท่านั้น (ปิดช่อง AI สำรวจข้ามโซน — บั๊กเดิมของ ExplorationSystem), wrap
  `McpMainThreadDispatcher.EnqueueAsync` (sync-func overload ที่ codebase ใช้จริง), เช็คทีละ clue:
  `VisibleToBystanders=true` เจอทันที / false roll 50%; เจอ → เพิ่มเข้า `CollectedClueInstanceIds`
  (dedupe), ไม่เจอ → `no_clue_at_location` / `investigation_failed`

## Part (d) — MCP Graph Response Layer (⚠️ Breaking)
- **`get_clue_board` shape เปลี่ยน**: เดิม flat list ของ clue id → ใหม่คืน `entries: List<ClueBoardEntry>`
  ต่อ instance (`instance_id/display_name/reliability/location_id/witness_npc_ids`) —
  AI VTuber ต้อง adapt (ดู [[mcp-tool-table]])
- **Witness filter (ฝั่ง response)**: `id == LocalPlayerId || Npcs[id].IsAlive` เท่านั้น —
  NPC ที่ตายแล้วหลุดจาก list ทุก tool; ไม่มีระบบ "ไม่เคย observe" (sighting log) —
  กรองแค่ killer + IsAlive ตามข้อตกลง design
- **`get_clue_graph` ใหม่**: nodes = clue ที่เก็บแล้ว (type=clue) + พยานที่มีชีวิต (type=npc),
  edges = clue→witness (relation=witnessed) — สำหรับ reasoning "ใครเห็นอะไร"
- Information hiding ตรวจด้วย **byte-scan** ของ response ที่ serialize จริง: `SourceActorId`
  ไม่โผล่แม้แต่ byte เดียว (ทั้ง board และ graph)
- สถาปัตยกรรมฝั่ง DI: `GetClueGraphHandler` พึ่ง `IAsyncRequestHandler<GetClueBoardRequest,…>`
  (MessagePipe interface) แทน concrete class — VContainer resolve ได้

## Runtime Tests (ผ่านหมด)
| ชุด | ขอบเขต | ผล |
|---|---|---|
| (a) A–D | โหลด 7 triggers / ClueCategory ครบ 5 defs / registry เริ่มว่าง / board handler compile+ค่าว่าง | ✅ |
| (b) A–F | kill จริงผ่าน UseCardHandler + message 1:1 / สถิติ roll 200× / พยาน exclude killer+ศพ+ต่างโซน / player ไม่เป็นพยานตัวเอง / MCP regression | ✅ |
| (c) A–F | explore ได้ wet/mud / harvest deer ได้ blood / investigate เจอ+เข้า inventory / tool ไม่มี location param / ไม่มี main-thread exception / kill-hook regression | ✅ |
| (d) A–E | board shape ใหม่ / พยานตายถูกกรอง / graph ถูกต้อง / byte-scan ไม่ leak / investigate regression | ✅ |
| Regression รวม | EditMode **32/32** + PlayMode ทุกคลาส (รวม bridge round-trip จริง) | ✅ |

หลักฐาน: `TestEvidence/clue-system-v2-{a,b,c,d}/` (+ graph JSON ตัวอย่างในไฟล์ Test C ของ (d))

## Bugs / Infra Lessons
1. **`LocationDef.csv` ไม่มี Weapon loot** — `KillerPlanner.TickSeekingWeapon` bounce กลับ Patrolling
   ทุก tick (เทส `GetCurrentPhase_AfterTick` fail แบบ deterministic ถ้าไม่มีโซนไหนมีการ์ด Weapon);
   ตัวเทสเองก็ non-deterministic ตาม RNG zone-cross — เป็นปัญหาเดิมก่อน clue work, ไม่เกี่ยวกับ v2
2. **PlayMode test runner wedge ต่อเนื่อง**: request ที่โดน proxy timeout ฝั่ง HTTP (≈5 นาที) ยังรันต่อ
   ใน Unity — request ทับมาถูกปฏิเสธ ("another test run is already in progress") และ error log นั้น
   **พิษให้ run ที่กำลังบิน** (LogAssert fail ทันที); run ที่ถูก `[Timeout]` hard-abort **ค้าง McpBridge
   process เป็น orphan** ที่กัน TCP slot ของ run ถัดไป (test แขวนเงียบ ๆ จนครบ 360s) —
   แก้: kill orphan (tasklist → taskkill), รอ SessionState lease หมดอายุ, แล้วรันทีละคลาส
3. **unity-mcp-cli filter ที่ถูกคือ `testClass`** — ใช้ `testNames` แล้วถูกเมียงเงียบ ๆ = รัน full suite ทุกครั้ง
   (ที่มาของปัญหาทับซ้อนเกือบทั้งหมดในรอบนี้); ใช้แล้ว run จบใน ~1 วิและผ่าน

## Next Steps
- Commit (a)–(d) ลง git (ยังอยู่ใน working tree ทั้งหมด)
- ฝั่ง AI VTuber: adapt response `get_clue_board` shape ใหม่ + เริ่มใช้ `get_clue_graph` /
  `investigate_clue` ใน reasoning loop
- `TaskSabotage` trigger ยังไม่มี hook เรียกจริง (รอระบบ sabotage ของ killer)
- RedHerring clue + reliability ยังไม่มีตัว generate (field พร้อมใน response แล้ว)
