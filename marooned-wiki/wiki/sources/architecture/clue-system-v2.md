---
title: clue-system-v2
type: architecture
sources:
  - Shared/ClueInstance.cs
  - Shared/ActionClueTriggerDef.cs
  - Shared/GameMessages.cs
  - Marooned/Assets/Scripts/Systems/ClueGenerationSystem.cs
  - Marooned/Assets/Scripts/Systems/GameStateProvider.cs
  - Marooned/Assets/Scripts/Systems/NpcDirectorSystem.cs
  - Marooned/Assets/Scripts/Systems/ExplorationSystem.cs
  - Marooned/Assets/Scripts/Systems/NodeHarvestSystem.cs
  - Marooned/Assets/Scripts/Systems/McpRequestHandlers.cs
  - DataTables/Data/ActionClueTriggerDef.csv
related:
  - "[[mcp-tool-table]]"
  - "[[card-system]]"
  - "[[npc-embodiment-movement]]"
  - "[[npc-survival-motives]]"
  - "[[deduction]]"
  - "[[mcp-bridge]]"
  - "[[ClueGenerationSystem.cs]]"
  - "[[NpcDirectorSystem.cs]]"
  - "[[Information-Hiding]]"
folder: architecture
created: 2026-09-14
tags:
  - architecture
  - clue
  - deduction
  - information-hiding
  - messagepipe
  - mcp
  - marooned
---

# Clue System v2 — Instance-Based Clue Pipeline

> เบาะแสทุกชิ้นคือ **instance object** ที่มี metadata ครบ (timestamp, แหล่งที่มา, พยาน) —
> เกิดจาก pipeline กลางเดียว (`ClueGenerationSystem`) ที่รับ hook จากทุก trigger ในเกม,
> เก็บใน registry กลาง (multiplayer-ready), และออกสู่ AI เฉพาะรูปแบบที่ผ่าน filter แล้ว

## ภาพรวม Data Flow

```
Trigger events                      ClueGenerationSystem.TryGenerate()
────────────────  ───────────────►  1. filter triggers ตาม source
· kill (ผ่าน                        2. roll อิสระต่อ trigger (weight%)
  SpawnClues)                       3. snapshot พยาน (exclude ผู้ก่อเหตุ)
· explore (น้ำ/10%)                 4. เก็บ → GameStateProvider.AllClueInstances
· harvest (isHuntingTarget)         5. publish ClueGeneratedMessage (1:1)
· (task sabotage — รอ hook)             └──► witness/hearer motives, อนาคต: UI toast

GameStateProvider.AllClueInstances ──► investigate_clue ──► CollectedClueInstanceIds (player)
                                                              └──► get_clue_board / get_clue_graph
                                                                   (filtered — killer/ศพ ไม่โผล่)
```

## Data Model

- **`ClueInstance`** (`Shared/ClueInstance.cs`, MessagePack Keys 0–6): `InstanceId` (Guid),
  `DefId`, `LocationId`, `GameTimestamp` (Time.time), `Source` (enum), และ ground truth
  2 ตัว — `SourceActorId` + `WitnessNpcIds` (snapshot ตอน generate) — **ห้าม expose ตรง ๆ**
  ออกนอก server เด็ดขาด
- **`ClueTriggerSource`**: `KillSabotage / TaskSabotage / IncidentalAction / Hunting`
- **`ClueDef`** เพิ่ม `ClueCategory` (wet/blood/footprint/general) ผ่าน Luban column
  + defs ปัจจุบัน 5 ตัว (เพิ่ม `clue_wet_clothes`)
- **`PlayerSurvivalState.CollectedClueInstanceIds`** (เดิม `CollectedClueCardIds`,
  Key(8) คงเดิม — wire-compatible) = clue ที่ player "เก็บแล้ว" เท่านั้น (ผ่าน investigate)
- **`ActionClueTriggerDef.csv`** — คู่ trigger×clue พร้อม weight:

| trigger | source | clue | weight |
|---|---|---|---|
| trig_kill_blood | KillSabotage | clue_blood_stain | 100 (เกิดเสมอ) |
| trig_kill_scratch | KillSabotage | clue_scratch_mark | 30 |
| trig_hunt_blood | Hunting | clue_blood_stain | 25 |
| trig_hunt_scratch | Hunting | clue_scratch_mark | 15 |
| trig_water_wet | IncidentalAction | clue_wet_clothes | 20 |
| trig_water_mud | IncidentalAction | clue_footprint_mud | 15 |
| trig_task_sabotage | TaskSabotage | clue_torn_cloth | 40 |

โหลดผ่าน Luban (`TbActionClueTriggerDef`) + `LubanDefMappers.MapActionClueTriggers`
(pattern เดียวกับ [[card-system]]) → เข้า `LubanDataService.ActionClueTriggerDefs`

## Generation Pipeline (`ClueGenerationSystem`)

- **Roll อิสระต่อ trigger** — ไม่ใช่ single-pick: การฆ่า 1 ครั้ง = blood 100% + scratch 30%
  → ได้ 0/1/2 ชิ้น (ทฤษฎี 0%: 0% / 1: 70% / 2: 30% — เทส 200 ครั้งตรง ±3.5%)
- **Witness snapshot**: NPC ที่ `IsAlive` + อยู่ `locationId` เดียวกัน, **exclude
  `sourceActorId` เสมอ**; player ถูกใส่เฉพาะอยู่โซนเดียวกัน **และไม่ใช่คนก่อเหตุ**
  (player ไม่เป็นพยานตัวเอง)
- Publish **`ClueGeneratedMessage`** (Keys 0–2: InstanceId/DefId/LocationId) —
  message เป็น "ประกาศเหตุการณ์" เท่านั้น **ไม่มี ground truth** — consumer เช่น
  NpcSurvivalSystem motives ต่อยอดจาก location ได้โดยไม่รู้ว่าใครทำ
- `NpcDirectorSystem.SpawnClues` (kill hook) เรียก pipeline แล้ว append instance ids เข้า
  `victim.AllConditionCardIds` — [[deduction]] `FilterVisible` ยังทำงานเหมือนเดิม
  (id ที่ไม่ใช่ illness ผ่านทั้งหมด)

### DI Wiring (cycle-safe)

- `ClueGenerationSystem` อ่าน NPC ผ่าน **`UtilityContext`** ไม่ใช่ `NpcDirectorSystem` —
  ทำให้ director inject generator ผ่าน constructor ได้ตรง ๆ โดยไม่เกิด
  `Director → Generator → Director` cycle (Bind pattern เดียวกับ AI brains ดู
  [[npc-embodiment-movement]])
- ลงทะเบียน `builder.Register<ClueGenerationSystem>(Lifetime.Singleton).AsSelf()` ใน
  GameLifetimeScope; handlers ใหม่ ๆ ลงทะเบียนตาม pattern เดิม

## Hooks ที่ยิง TryGenerate

| Hook | ไฟล์ | เงื่อนไข |
|---|---|---|
| KillSabotage | `NpcDirectorSystem.SpawnClues` | ทุกการฆ่าที่สำเร็จ (player หรือ killer AI) |
| IncidentalAction | `ExplorationSystem` (หลัง roll loot) | loot ที่ได้เกี่ยวกับน้ำ (`water*`, `raw_fish`) หรือสุ่ม 10% |
| Hunting | `NodeHarvestSystem` (หลัง harvest) | node มี `isHuntingTarget=true` (เช่น `node_deer`) — data-driven จาก CSV |
| TaskSabotage | — ยังไม่มี hook | รอระบบ sabotage ของ killer |

## MCP Response Layer (สำหรับ AI VTuber)

รายละเอียด tool ทั้งหมดใน [[mcp-tool-table]] — สรุปเฉพาะส่วน clue:

- **`investigate_clue`** — **ไม่มีพารามิเตอร์** (ป้องกันการสำรวจข้ามโซน — บั๊กเดิม
  ExplorationSystem ที่รับ locationId จาก caller): handler ใช้ `player.CurrentLocationId`
  ฝั่ง server เท่านั้น + wrap main-thread dispatcher; ต่อ clue ในโซน:
  `VisibleToBystanders=true` → เจอทันที, `false` → roll 50%; เจอ → เพิ่มเข้า
  `CollectedClueInstanceIds` (dedupe); fail คืน `no_clue_at_location` / `investigation_failed`
- **`get_clue_board`** (⚠️ breaking change 2026-09-14) — คืน `entries: List<ClueBoardEntry>`
  ต่อ instance: `instance_id / display_name / reliability / location_id / witness_npc_ids`
- **`get_clue_graph`** — nodes (type=clue ที่เก็บแล้ว / type=npc พยานที่มีชีวิต) +
  edges clue→witness (`relation=witnessed`) สำหรับ reasoning "ใครเห็นอะไร"

### Information Hiding Layers

1. **Message layer**: `ClueGeneratedMessage` ไม่มี SourceActorId/WitnessNpcIds
2. **Response layer**: `ClueBoardEntry.witness_npc_ids` ผ่าน filter
   `id == LocalPlayerId || Npcs[id].IsAlive` — killer และ NPC ที่ตายแล้วไม่มีวันโผล่
   (ไม่มีระบบ "ไม่เคย observe" — กรองแค่ 2 เงื่อนไขนี้ตามข้อตกลง design)
3. **ตรวจจริงด้วย byte-scan**: serialize response จริงแล้วหา substring
   `SourceActorId` — ไม่พบใน board/graph (sanity check ว่า scan เจอ witness id จริง)

## Tests & Evidence

- เทสครบ A–F ทุก commit (a)–(d) — สรุปรายชุดใน devlog
  [[2026-09-14-clue-system-v2-instance-pipeline]]; หลักฐานใน
  `TestEvidence/clue-system-v2-{a,b,c,d}/`
- Regression รวม: EditMode 32/32 + PlayMode ทุกคลาส (รวม bridge round-trip จริง)
- บทเรียน infra (test runner wedge / orphan McpBridge / filter `testClass`)
  อยู่ใน devlog เดียวกัน

## Next Steps

- `TaskSabotage` hook (รอระบบ sabotage), RedHerring + reliability generation,
  ฝั่ง AI VTuber adapt board shape ใหม่ + ใช้ graph/investigate ใน reasoning loop
