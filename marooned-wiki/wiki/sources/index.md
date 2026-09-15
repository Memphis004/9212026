---title: index
type: index
sources: []
related: []
folder: sources
created: 2026-09-05
tags:
  - index
  - marooned
  - lab-a
  - lab-b
  - lab-c

---

# 📚 Marooned Wiki — สารบัญ

> เอกสารทั้งหมดของโปรเจค Marooned (Card Survival × Social Deduction)
อัปเดตล่าสุด: 2026-09-15 (Clue System v2 (e) presentation layer: ClueBoardPresenter/View
MVP Lite — reuse GetClueGraphHandler, radial layout วงใน clue/วงนอก npc, get_clue_graph
คืน human-readable text ผ่าน ClueGraphTextFormat + PlayMode tests ผ่านครบ 9/9 A–I
(TestEvidence/clue-system-v2-e) + toggle กระดาน (Tab + ปุ่ม HUD บน Canvas) + edge glow
2 ชั้น + board chrome + คลิกทุก node ดูรายละเอียด (clue = reliability/witnesses ผ่าน
GetClueBoardHandler, npc witness = โซน + อาลิไบคร่าว ๆ จากเบาะแสที่เป็นพยาน — ไม่มี
ground truth หลุด) + ปักหมุดหลาย node เทียบกันได้ (pin สูงสุด 3 การ์ด side-by-side,
pin/ถอนจาก popup หรือปุ่ม × บนการ์ด, pin ของ node ที่หายถูกถอนอัตโนมัติ, pin รอดจาก
ปิด/เปิดกระดานใน session เดียวกันผ่าน CluePinState singleton) + MCP get_pinned_clues
(AI อ่าน pin เดียวกับ UI) + MCP set_pinned_clue (AI pin/unpin เอง — idempotent,
validate กับกราฟ, main-thread mutate, AI pin ขึ้น UI ทันที) + dedupe ×N display-only
สำหรับเบาะแสซ้ำใน alibi (MCP + view รูปเดียวกัน) — ดู [[clue-system-v2-presentation]])
ก่อนหน้า 2026-09-14 (Clue System v2 ครบ 4 ส่วน (a)–(d): clue เป็น instance object
(ClueInstance MessagePack + registry กลาง AllClueInstances) + pipeline กลาง ClueGenerationSystem
(roll อิสระต่อ trigger ตาม weight, พยาน exclude ผู้ก่อเหตุ + player ไม่เป็นพยานตัวเอง,
publish ClueGeneratedMessage ไม่มี ground truth) + hooks ครบ kill/explore(น้ำ/10%)/hunt(isHuntingTarget)
+ MCP tool investigate_clue (ไม่มีพารามิเตอร์ — server-side location กันสำรวจข้ามโซน) +
get_clue_board shape ใหม่เป็น ClueBoardEntry (⚠️ breaking — witness ผ่าน filter killer+IsAlive)
+ get_clue_graph (nodes/edges witnessed) — byte-scan ยืนยัน SourceActorId ไม่ leak,
EditMode 32/32 + PlayMode ผ่านหมด, หลักฐาน TestEvidence/clue-system-v2-{a,b,c,d} —
ดู [[clue-system-v2]] และ [[2026-09-14-clue-system-v2-instance-pipeline]])
ก่อนหน้า 2026-09-13 (UI TMP Migration: Legacy Text/TextMesh → TextMeshPro ทั้ง 6 ไฟล์
+ ฟอนต์ไทย THSarabunPSK SDF (Dynamic) ผ่าน WorldItemSystem.LoadLabelFont จุดโหลดกลาง +
CardSlot.prefab Text→TextMeshProUGUI + Canvas Scaler match 0.5 + เพิ่ม asmdef ref Unity.TextMeshPro —
Play session ไม่มี font error, ยังรอ screenshot เทส A–F) + แก้ bug script-execute
TypeLoadException vtable (Roslyn duplicate: ถอด com.unity.pipeline + เปิด import
Microsoft.CodeAnalysis.CSharp/System.* 8 dll ที่ LegacyMigration ปิดค้าง) — script-execute ใช้ได้แล้ว
+ เก็บกวาดต่อ: ลบ legacy `Assets/NuGet/` + ถอด UPM `com.github-glitchenzo.nugetforunity`
(ตัวการ GUID conflicts และ compile error ค้าง) ออกจาก manifest จนคอนโซลเคลียร์
+ เขียน convention nullable annotation สำหรับ Shared DTOs (Section 9 ของ conventions) —
ล้าง warning CS8618 ค้าง 79 จุดใน 7 ไฟล์ Shared/* (default initializer `string.Empty`/`new()`
+ `string?` scoped เฉพาะ field ที่ null=none จริง) จน McpBridge build สะอาด 0 warning

## 🎮 Game Design
- [[game_design_doc]] — Game Design Document หลัก (แหล่งความจริงของทุก design decision)

## 🏗️ Architecture
- [[overview]] — ภาพรวม architecture ทั้งหมด (Composition Root, DI, Message Bus, Data Flow)
- [[card-system]] — สถาปัตยกรรม Card System Phase 4 (Player-as-Killer, TryEliminate กลาง,
  Multiplayer-ready GameStateProvider, Safe UX เช็คก่อนหักการ์ด)
- [[mcp-bridge]] — MCP Bridge architecture (build/run, TCP connection, วิธีเพิ่ม tool)
- [[mcp-tool-table]] — ตาราง MCP tools ทั้งหมด พร้อมสถานะ
- [[chibi-visual-system]] — ระบบ spawn/คุม visual ตัวละคร Chibi แบบ event-driven
  (MessagePipe + VContainer + MVP Lite)
- [[player-system]] — ระบบผู้เล่น Lab B Phase 3: เดิน WASD 4 ทิศ + เก็บไอเท็มตามโซน
  (PlayerInputService/Movement/WorldItem/Pickup + zone loot mock table)
- [[biome-scatter-system]] — Lab C Phase 1: Hybrid Biome Scatter (System คำนวณ + publish event,
  View เป็นคน Instantiate) + HarvestableNode tool-gathering (durability/tool/regrow จาก Luban CSV)
- [[biome-scatter-editor-setup]] — คู่มือสร้าง BiomePrefabSet ใน Unity Editor +
  protocol เทส Test A–D พร้อมจุดพบบ่อย
- [[npc-embodiment-movement]] — Lab C Phase 2 Step 1-4 ครบ: NPC embodiment + AI Hook เทส A-F ผ่านหมด
  เป็น Ground Truth (NpcState Key 9-15, NpcInventory, NpcSurvivalState) +
  NpcMovementSystem (เดินอย่างเดียว) + สมอง AI แยก role InnocentUtilityAI/KillerPlanner
  ผ่าน MoveNpc + Position Seeding Rule + DI cycle ปลอดภัย (Bind pattern)
  กัน warp (0,0) + tick order Director ก่อน Movement + NpcCharacterView sync chibi
  กับ Position/Activity ทุกเฟรม (พร้อมผลเทส Test A–D และภาพหลักฐาน)
- [[npc-zone-transitions]] — Hybrid Transition Points (2026-09-11): NPC เดินข้ามโซนผ่าน
  จุดเชื่อมแทน teleport — ZoneConnectionDef.csv (กฎพิกัด 80% จากโซนต้นทาง) +
  LubanDataService.GetTransition + NpcState Key 16-17 (ground truth ห้าม leak) +
  Wander.MoveToZone (single source of truth, fallback teleport) +
  NpcZoneTransitionSystem FSM (WalkingToPoint→Exiting 0.5s→ข้ามโซน→Entering 0.3s)
  + tick order Director→Movement→ZoneTransition + EditMode 6/6 และ PlayMode visual A+B ผ่าน
- [[npc-survival-motives]] — Lab C Phase 2.5A Living NPCs (2026-09-11): NPC "รู้สึก" ถึงโลกจริง —
  NpcSurvivalSystem decay tick (Hunger +2/วิ, Fear −6/วิ ≈17s, Curiosity −3/วิ ≈33s, สเกล 0-100)
  + motive hooks subscribe NpcEliminatedMessage (witness Fear +50, hearer Curiosity +30
  + LastNoiseLocationId Key 3) + เปิด FleeToSafeZoneAction/InvestigateNoiseAction จริง
  (stateless ตามสัญญา IUtilityAction) + Wander.ClampTargetToZone (เฉพาะ TransitionPhase == None
  และเฉพาะ Target) + tick order Survival→Director→Movement→ZoneTransition +
  EditMode 8/8 และ PlayMode 2/2 ผ่าน
- [[player-auto-move-system]] — MoveToLocation fix (2026-09-12): Player เดินจริงไม่ teleport
  + redirect/cancel กลางทางได้ (รวม MCP tool `cancel_move`: หยุดเดินโดยไม่ต้องสั่งปลายทางใหม่
  idle คืน not_moving, คำสั่ง move ที่โดนยกเลิกคืน superseded) —
  PlayerAutoMoveSystem (เดินหา TargetX/Y Speed 3.5,
  Begin/Cancel + MoveGeneration token กัน waiter เก่า commit ทับ) +
  PlayerSurvivalState Key 15-17 (TargetX/TargetY/IsAutoMoving) + guard คีย์บอร์ดใน
  PlayerMovementSystem + MoveToLocationHandler 3 เฟส (ตั้งเป้า → รอเดินถึง/ถูกแทนที่ →
  commit เมื่อ gen ตรงเท่านั้น; superseded/move_timeout) +
  McpMainThreadDispatcher.WaitUntilAsync + tick order auto-move ก่อน movement +
  PlayMode เทส A–E + redirect/cancel + bridge round trip จริงผ่าน
- [[ui-tmp-migration]] — UI TMP Migration (2026-09-13): ย้ายข้อความทั้งเกมจาก Legacy
  Text/TextMesh → TextMeshPro รองรับฟอนต์ไทย THSarabunPSK SDF (Dynamic atlas) —
  WorldItemSystem.LoadLabelFont จุดโหลดกลาง (null check + LogError, fallback ไม่ crash) +
  world-space label fontSize 4 (item/harvestable/zone trigger) + UI TMP_Text fontSize 22-28
  (CardSlot/CardHand feedback/ZoneHud) + CardSlot.prefab Text→TextMeshProUGUI ผ่าน MCP
  prefab tools  + Canvas Scaler 1920×1080 match 0.5 + asmdef ref Unity.TextMeshPro
- [[clue-system-v2]] — Clue System v2 (2026-09-14): clue เป็น instance object มี metadata
  (timestamp/source/witnesses) — ClueGenerationSystem pipeline กลาง (roll อิสระต่อ trigger
  weight-based, witness snapshot exclude ผู้ก่อเหตุ, publish ClueGeneratedMessage ไม่มี ground truth)
  + hooks kill/explore/hunt (DI cycle-safe ผ่าน UtilityContext Bind pattern) +
  ActionClueTriggerDef.csv 7 triggers + registry กลาง GameStateProvider.AllClueInstances +
  investigate_clue (ไม่มี param, VisibleToBystanders→ทันที/50%) + get_clue_board ClueBoardEntry
  (breaking, witness filter killer+IsAlive) + get_clue_graph (nodes/edges) + byte-scan info-hiding
- [[clue-system-v2-presentation]] — Clue System v2 (e) (2026-09-15): Presentation layer ของ
  clue board — ClueBoardPresenter (reuse GetClueGraphHandler ผ่าน MessagePipe interface,
  ไม่สร้าง handler ใหม่) + ClueBoardView radial layout (clue วงใน r=150 / npc วงนอก r=300
  เฉพาะตัวที่มี edge, เส้นเชื่อม Image หมุน + circle sprite gen ใน code) + view data contracts
  ClueGraphNodeData/ClueGraphEdgeData (แยกจาก MessagePack GraphNode/GraphEdge) +
  get_clue_graph MCP output เป็น human-readable text (ClueGraphTextFormat ร่วม bridge+Unity)
  + ClueBoardPanel ใต้ Canvas ใน SampleScene (เริ่มซ่อน — Tab toggle ฝั่ง presenter ผ่าน
  UniTask PlayerLoop + ปุ่ม HUD บน Canvas สำหรับผู้เล่นเมาส์) + edge glow 2 ชั้น
  (radial-gradient beam เต้นช้า ๆ) + board chrome (backdrop/title/legend
  raycastTarget=false) + คลิกทุก node → popup รายละเอียด (GraphNodeClickProxy ส่ง id →
  presenter: clue ผ่าน GetClueBoardHandler reuse, npc witness = โซนปัจจุบัน
  (player-visible) + อาลิไบคร่าว ๆ จากเบาะแสที่เป็นพยาน — คลิกซ้ำ toggle ปิด, node อื่น
  สลับเนื้อหา, popup refresh ข้าม re-render, restore สีเดิมถูกตัวต่อ type) + PlayMode
  tests A–F 6/6 PASS + Game view screenshot ยืนยัน layout ด้วย pixel-scan
  (TestEvidence/clue-system-v2-e)

## 📄 Code Snippets

### Core
- [[GameLifetimeScope.cs]] — Composition Root: ประกอบร่าง DI, MessagePipe, TCP server ทั้งหมด
- [[GameTickDriver.cs]] — MonoBehaviour เรียก `Tick()` ของระบบ gameplay ทุกเฟรม (game loop)
- [[RoundInitializer.cs]] — เรียก `SetupRound()` อัตโนมัติตอน Scene โหลด ให้มี NPC เกิดในเกม

### Systems
- [[SurvivalStatSystem.cs]] — drain stat 4 ค่า + roll illness เมื่อ stat วิกฤต
- [[CardInventorySystem.cs]] — เพิ่ม/หัก/เช็คการ์ดใน inventory (พร้อม CraftingSystem ในไฟล์เดียว)
- [[CraftingSystem.cs]] — ตรวจเงื่อนไข recipe แล้วคราฟการ์ด
- [[ExplorationSystem.cs]] — สำรวจ location แบบ weighted loot ที่ deplete ได้
- [[NpcDirectorSystem.cs]] — เจ้าของ ground truth NPC + killer AI + spawn clue
  (Phase 4: `CanEliminate`/`TryEliminate` method กลางที่ player ใช้ร่วมกับ AI)
- [[Wander.cs]] — helper การเดินร่วมของ AI (single source of truth):
  `IsTransitioning` gate + `MoveToZone` (เริ่ม transition ผ่านจุดเชื่อม / fallback
  teleport) + `SetRandomTargetInZone`
- [[NpcZoneTransitionSystem.cs]] — FSM ข้ามโซนแบบเดินผ่านจุดเชื่อม
  (WalkingToPoint→Exiting→Entering→None) plain C# timer dictionary ไม่มี coroutine
  (2026-09-11)
- [[NpcSurvivalSystem.cs]] — decay tick stats NPC (สเกล 0-100) + motive hooks
  NpcEliminatedMessage (witness/hearer) + clamp target + hint lifecycle
  LastNoiseLocationId + status log ทุก 30 วิ (2026-09-11)
- [[InnocentUtilityAI.cs]] — สมองฝั่ง Innocent: utility scoring re-evaluate ทุก ~1.5 วิ
  — IdleWander/SeekFood/FleeToSafeZone (Fear)/InvestigateNoise (Curiosity×ไม่กลัว)
  stateless ตามสัญญา IUtilityAction, การเดินผ่าน Wander helper เท่านั้น (2026-09-11)
- [[DeductionSystem.cs]] — information-hiding layer + ตัดสิน accusation
- [[WorldEventSystem.cs]] — weighted random event queue (Survival/Social)
- [[McpRequestHandlers.cs]] — ปลายทาง request จาก MCP Bridge 10 ตัว
- [[LubanDataService.cs]] — GameStateProvider + LubanDataService (mock data รอ Luban จริง)

### UI
- [[UIRoot.cs]] — panel registry ของ UI (enum → Type mapping)
- [[CardHandPresenter.cs]] — Presenter ของมือการ์ด: render event-driven + click-to-use
  (`UseCardFromSlot`, target selection สำหรับ weapon ผ่าน ChibiSpawnerView) +
  `GetLocalizedReason` แปลง failure reason เป็นไทย (Phase 4–5)
- [[CardSlotUI.cs]] — View slot การ์ดเดี่ยว: rendering category color, label stack count, pop animation,
  และ IPointerClickHandler ส่ง event Clicked(cardId) ให้Presenter (MVP Lite —คลิกซ้ายบน slot ที่มีการ์ดเท่านั้น)
- [[ClueBoardPresenter.cs]] — Presenter ของกระดานเบาะแส (skeleton)
- [[MapExplorePresenter.cs]] — Presenter ของแผนที่สำรวจ (skeleton)
- [[MeetingVotePresenter.cs]] — Presenter ของหน้าโหวตกล่าวหา (skeleton)
- [[CardHandView.cs]] — View แสดงมือการ์ด (skeleton)
- [[ClueBoardView.cs]] — View กระดานเบาะแสแบบ detective board (skeleton)
- [[ConditionOverlayView.cs]] — HUD icon illness/injury ของผู้เล่น (skeleton)
- [[MapExploreView.cs]] — View แผนที่ 2D sandbox (skeleton)
- [[MeetingVoteView.cs]] — View หน้าประชุม + ปุ่ม Accuse/Abstain (skeleton)

### Visual Layer (Chibi)
- [[IChibiVisual.cs]] — interface กลางของ visual layer (backend-agnostic: Animator/Spine/paperdoll,
  Phase 4 เพิ่ม `PlayAction` one-shot)
- [[ChibiSpawnerView.cs]] — spawn/despawn chibi ของ NPC ตาม location แบบ event-driven
- [[NpcCharacterView.cs]] — Step 3: sync ตำแหน่ง/facing/animation ของ chibi NPC
  กับ NpcState ทุกเฟรม (direct read ไม่ใช้ MessagePipe ต่อเฟรม) + จับ phase
  เปลี่ยนของ transition เล่น `PlayAction("zone_exit"/"zone_enter")` one-shot
- [[GenericCuteVisualController.cs]] — คุม Animator ของ asset "Generic Cute 2D Student" (PSB skeletal)
- [[SpineVisualController.cs]] — Spine backend คุม SkeletonAnimation (Elena/Derek ใช้ร่วมกัน)

### Data
- [[ChibiAnimatedRenderer.cs]] — chibi sprite-swap paperdoll renderer เดิน 4 ทิศ

### Luban Generated (`Data/Gen/` — ห้ามแก้มือ)
- [[Data_Gen_Tables.cs]] — root table manager ของทั้ง 6 ตาราง
- [[Data_Gen_game_CardDef.cs]] — bean: การ์ด
- [[Data_Gen_game_ClueDef.cs]] — bean: เบาะแส
- [[Data_Gen_game_IllnessDef.cs]] — bean: โรค/บาดแผล
- [[Data_Gen_game_LocationDef.cs]] — bean: location node
- [[Data_Gen_game_RecipeDef.cs]] — bean: สูตรคราฟ
- [[Data_Gen_game_WorldEventDef.cs]] — bean: world event
- [[Data_Gen_game_TbCardDef.cs]] — table wrapper: การ์ด
- [[Data_Gen_game_TbClueDef.cs]] — table wrapper: เบาะแส
- [[Data_Gen_game_TbIllnessDef.cs]] — table wrapper: โรค
- [[Data_Gen_game_TbLocationDef.cs]] — table wrapper: location
- [[Data_Gen_game_TbRecipeDef.cs]] — table wrapper: recipe
- [[Data_Gen_game_TbWorldEventDef.cs]] — table wrapper: world event

## ⚙️ Game Mechanics
- [[survival-stats]] — ระบบ Survival Stats (Hunger/Thirst/Mood/Fatigue + illness)
- [[card-inventory]] — ระบบ Card Inventory และ hand management
- [[crafting]] — ระบบ Crafting และ recipes
- [[exploration]] — ระบบ Exploration และ map
- [[npc-director]] — ระบบ NPC Director (behavior, killer, no-witness rule)
- [[deduction]] — ระบบ Social Deduction (clues, accusation, win/lose) — มีส่วน
  "AI Deduction Workflow" แนะนำลำดับการใช้ MCP tools (observe → collect → review →
  pin/unpin ของ player+AI ผ่าน get_pinned_clues/set_pinned_clue → reason → accuse)
  + สถานะอัปเดตแล้วตาม Clue System v2 (2026-09-15)
- [[world-events]] — ระบบ World Events (Survival/Social groups)
- [[chibi-avatar]] — ระบบ Chibi sprite-swap avatar

## 🐛 Bug Log
- [[2026-09-14-test-runner-wedge-orphan-mcpbridge]] — PlayMode tests แขวน 360s / fail เป็นชุด
  / McpBridge ค้าง orphan — รากเหตุ 4 ชั้น (proxy timeout ≠ run abort → request ทับเขียน error
  log พิษ run ที่บินอยู่ → [Timeout] hard-abort ข้าม finally → orphan ถือ TCP slot) +
  protocol ก่อนรันทุกครั้ง (kill orphan / editor idle / filter testClass / poll แทนยิงซ้ำ)
  + บทเรียน SessionState ข้าม domain reload (2026-09-14)
- [[2026-09-13-script-execute-roslyn-vtable]] — script-execute พังทั้งตัว
  (TypeLoadException "invalid vtable method slot 4") — รากเหตุ 3 ชั้น: Roslyn 4.8 (NuGet
  ของ gamedev-mcp) ทับ Roslyn 3.11 (com.unity.pipeline) + LegacyMigration ปิด import
  Roslyn CSharp.dll/System.* 8 dll ค้างจาก Sep 6 + recompile แรกของวันทำงานเผยปัญหา —
  วิธีแก้ครบ 3 จุด + บทเรียนเรื่อง duplicate assembly identity และ DisableImporter ระเบิดเวลา (2026-09-13)
- [[2026-09-12-movetolocation-teleport]] — Bug แรก: move_to_location teleport
  (โซนเปลี่ยนแต่ PositionX/Y นิ่ง) — รากเหตุ handler ไม่มีระบบเดิน + ไม่มีใคร
  ขยับตำแหน่ง, ทางผ่านกลางที่ผิด (lerp ใน handler), วิธีแก้ครบ 6 จุด
  (PlayerAutoMoveSystem + WaitUntilAsync + tick order) + หลักฐานเทส + บทเรียน
  (event "เกิดแล้ว" ต้องยิงหลังถึงจริง / อย่าผสมสองแหล่งควบคุม / response
  timing เป็นหน้าที่ของเกม ไม่ใช่ proxy) (2026-09-12)

## 📋 Conventions & Logs
- [[conventions]] — Coding conventions จากโค้ดจริง (รวม Section 9: nullable annotation
  สำหรับ Shared DTOs — default initializer vs `string?` scoped, ข้อห้าม `required`,
  วิธี clean-build ตรวจ CS8618)
- [[2026-09-05]] — Dev Log วันแรก
- [[2026-09-07-lab-a-closure-b-phases-architecture-refactor]] — Dev Log: ปิด Lab A (MCP round trip) + Lab B Phase 1-4 (Chibi, Player, Player-as-Killer, refactor) + patch WorldItemSystem กัน item respawn ซ้ำ
- [[2026-09-09-lab-c-phase1-hybrid-biome-scatter-node-harvest-zone-transition]] — Dev Log: Lab C Phase 1 – Hybrid Biome Scatter + Node Harvest + Zone Transition
- [[2026-09-10-lab-c-phase2-npc-embodiment-ai-hook]] — Dev Log: Lab C Phase 2 – NPC Embodiment (data foundation, movement, visual sync, AI Hook InnocentUtilityAI/KillerPlanner) + runtime tests A–F ผ่านครบ + 2 bugs ที่เจอ/แก้
- [[2026-09-11-devlog]] — Dev Log: Hybrid Transition Points (Visual/ Spatial Fix) • Living NPCs (Survival Tick + Motive Events + Stub Actions) • Debug Overlay
- [[2026-09-14-clue-system-v2-instance-pipeline]] — Dev Log: Clue System v2 ครบ (a)–(d) —
  instance model + generation pipeline + kill/incidental hooks + investigate_clue +
  board/graph response layer (⚠️ breaking get_clue_board) — เทส A–F ทุกส่วนผ่าน +
  บทเรียน infra (test runner wedge / orphan McpBridge / testClass filter) (2026-09-14)
- [[npc-embodiment-movement]] — (architecture) Lab C Phase 2 Step 1-4 – NPC Embodiment + Basic AI Hook: data/movement/visual sync + InnocentUtilityAI/KillerPlanner (2026-09-10)

## 🗂️ โครงสร้าง Wiki

```
marooned-wiki/
├── game-design-doc/          # GDD (ไฟล์ game_design_doc.md)
└── wiki/
    ├── sources/
    │   ├── index.md              # สารบัญนี้
    │   ├── conventions.md        # coding conventions
    │   ├── architecture/         # overview, mcp-bridge, mcp-tool-table, chibi-visual-system
    │   ├── code-snippets/        # snippet รายไฟล์ (40 ไฟล์ — Systems/Core/Data/UI/Visual)
    │   ├── mechanics/            # เอกสารระบบเกม 8 ระบบ
    │   ├── game-design-doc/      # GDD (canonical location)
    │   ├── bug-log/              # บันทึก bug (รากเหตุ + วิธีแก้ + บทเรียน)
    │   └── devlog-history/       # dev log รายวัน
    ├── concepts/                 # auto-generated concept pages (Karpathy plugin)
    └── entities/                 # auto-generated entity pages (Karpathy plugin)
```

- โครงสร้างยึดแบบ **Karpathy LLM Wiki** — `sources/` เก็บเอกสารที่เขียน/สร้างจากโค้ดจริง
  `concepts/` กับ `entities/` เป็นของ plugin generate เอง
- ทุกเอกสารใน `sources/` มี YAML frontmatter รูปแบบเดียวกัน (ดู [[conventions]])
- เชื่อมโยงเนื้อหาด้วย `[[...]]` WikiLinks เสมอ เพื่อให้ Obsidian graph และ local LLM
  (Smart Connections) ค้นหาความสัมพันธ์ได้
