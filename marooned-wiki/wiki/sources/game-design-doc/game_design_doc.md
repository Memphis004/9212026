# Marooned — Card Survival x Social Deduction
สรุปการออกแบบเกม + สถาปัตยกรรม (อ้างอิง tech stack จากโปรเจก Cultivation Together)

## 📋 Design Pivot Log

> บันทึกการเปลี่ยนทิศทางเกมครั้งใหญ่ — อ่านก่อนส่วนอื่นเพื่อเข้าใจ Context ว่าทำไมเนื้อหาบางอย่างจึงไม่ตรงกับระบบเกมแบบดั้งเดิม

### พบวันที่ 2026-09-06 — Design Pivot: จาก Point-and-Click → Real-time Walking Sandbox

**เหตุผลที่เปลี่ยน:**
- เกมเดิมออกแบบให้ผู้เล่นคลิกเลือกโซนเพื่อจัดการทรัพยากร (คล้าย Sect-management) — ไม่เหมาะกับ Social Deduction ที่ต้องมี "ตน้อง" ในโล Mage
- ต้องการให้ผู้เล่นควบคุม Chibi Character เดินไปมาบนแผนที่ 2D แบบ Real-time (WASD/Keyboard) คล้าย Don't Starve / Among Us
- การสำรวจเกิดจากการ "เดินเข้าไปในโซน" และ "เก็บไอเท็มบนพื้น" แทนการสุ่มจากการคลิก
- ทำให้ Killer Mechanic น่าสนใจขึ้น: การซุ่มซ่อน/กำจัดมีความเสี่ยงจริงๆ เพราะผู้เล่นขยับตัวได้เอง

**การเปลี่ยนแปลงที่ตามมา:**
- Player-as-Killer: ผู้เล่นสามารถเป็น Killer ได้ โดยใช้การ์ด Weapon
- Zone-based Loot: ไอเท็ม Spawn ตามโซน (ไม่สุ่มจาก UI)
- Physical Pickup: ผู้เล่นต้องเดินไปเก็บของด้วยตัวเอง
- Multiplayer-Ready Architecture: ระบบหลังบ้านรองรับหลาย Player (Dictionary-based GameStateProvider)

### พบวันที่ 2026-09-12 — MCP Auto-Move: AI สั่งเดินได้จริง (ไม่ Teleport)

**เหตุผลที่เปลี่ยน:**
- AI VTuber สั่ง `move_to_location` แล้วตัวละคร teleport ทันที (โซนเปลี่ยนแต่
  PositionX/Y นิ่ง) — ขัดกับหลัก Walking Sandbox ที่วางไว้เมื่อ 2026-09-06 เพราะ
  การเดินคือประสบการณ์หลักของเกม ไม่ใช่แค่ผลลัพธ์
- จังหวะการเล่าเรื่องของ VTuber ต้องตรงกับบนจอ: ตอนเดิน = กำลังเดินจริง,
  ถึงโซนใหม่ = เดินถึงจริงแล้ว (ดู [[player-auto-move-system]])

**การตัดสินใจ (ตกลงแล้ว):**
- การควบคุมผู้เล่นมี **2 ช่องทาง**: คีย์บอร์ด (WASD) และคำสั่ง MCP auto-move —
  แต่ต่อเฟรมมี "ผู้เขียนตำแหน่งเดียว" ผ่าน flag `IsAutoMoving` (ระหว่าง auto-move
  คีย์บอร์ดถูกล็อก, auto-move จบคีย์บอร์ดกลับมาทันทีในเฟรมเดียวกัน)
- **โซนเปลี่ยนเมื่อเดินถึงเท่านั้น**: `CurrentLocationId` + biome + chibi ต้อง
  รอจน Position เข้าใกล้ปลายทาง ≤ 0.1 unit แล้วค่อยเปลี่ยน — ห้ามเปลี่ยนก่อนเดิน
- **Response ของ `move_to_location` กลับหลังเดินถึง** — เวลาเดินเป็นของเกม
  (ระยะทาง / ความเร็ว 3.5) ไม่ใช่หน้าที่ของ proxy หน่วงแทน

---

## 1. แนวคิดเกม

เกมเอาชีวิตรอดแบบ **single-player 2D sandbox + card game** ที่ยืมโครงหลักจาก
*Card Survival: Tropical Island* และ *Cultivation Realm: Card Survival* (สำรวจ →
เก็บการ์ดทรัพยากร → คราฟ → บริหารสเตตัส) แล้วเติมชั้น **social deduction**
แบบ Among Us / มาเฟีย-หมาป่าเข้าไป: ผู้เล่นไม่ได้ติดเกาะคนเดียว แต่มี NPC
ติดอยู่ในสถานที่เดียวกัน และหนึ่งใน NPC เหล่านั้น (หรือมากกว่า) คือ "ฆาตกร"
ที่แอบกำจัด NPC ตัวอื่นทีละคน ผู้เล่นต้องเอาชีวิตรอดจากสิ่งแวดล้อม *และ*
ไล่หาตัวฆาตกรให้เจอ ก่อนที่ตัวเองจะกลายเป็นเป้าหมายถัดไป

**เสาหลักของเกม (3 pillars)**
1. **Survival Resource Loop** — หิว/กระหาย/อารมณ์/เหนื่อยล้า, สำรวจ, คราฟ, เจ็บป่วย/บาดเจ็บเป็นการ์ดติดตัว
2. **Social Deduction Loop** — สังเกตพฤติกรรม NPC, เก็บการ์ดเบาะแส, กล่าวหา/โหวต, หลีกเลี่ยงการตกเป็นเหยื่อ
3. **AI-Playable Layer** — ทุกกลไกต้อง query/ตัดสินใจได้ผ่าน MCP เพื่อให้ AI VTuber เล่นแทนสตรีมเมอร์ได้ (สืบทอดหลักคิดจากโปรเจกเดิม)

---

## 2. Core Gameplay Loop

```
เดินสำรวจแผนที่ 2D Sandbox (WASD + ปุ่ม Interact) → เจอไอเท็ม/เหตุการณ์/NPC บนพื้น
        ↓
เก็บของ (Pickup → เข้า Inventory → แปลงเป็นการ์ด) → คราฟ → จัดการ Stat (Hunger/Thirst/Mood/Fatigue)
        ↓
พบเบาะแส (Clue Card) หรือศพ → สืบสวน
        ↓
วัน/คืน ผ่านไป → Meeting Phase → กล่าวหา/โหวต หรือเงียบไว้ก่อน
        ↓
วนกลับสำรวจต่อ จนกว่า (ก) หาฆาตกรเจอครบ (ข) ผู้เล่นตาย/ถูกฆ่า (ค) รอดจนครบเงื่อนไขเอาชีวิตรอด
```

### 2.1 Stat Management (เหมือนโปรเจกอ้างอิง)
- `Hunger`, `Thirst`, `Mood`, `Fatigue` — ลดลงตามเวลา/การกระทำ, เติมด้วยการ์ดไอเท็ม
- Stat ตกต่ำ → เพิ่มโอกาสสุ่ม "Illness Card" (เจ็บป่วย) ติดตัวผู้เล่น เช่น ไข้, ท้องเสีย, นอนไม่หลับ
- Illness/Injury Card มีผล debuff ต่อ action (เช่น สำรวจช้าลง, คราฟพลาดง่ายขึ้น) และต้องใช้การ์ดยา/พักผ่อนเพื่อรักษา

### 2.2 Exploration & Zone-based Loot (Walking Sandbox)
- **แผนที่เป็น 2D Sandbox แบบ Real-time** — ผู้เล่นควบคุม Chibi Character เดินไปมาด้วย WASD/Keyboard (ไม่ใช่คลิกเลือกโซน)
- **การควบคุม 2 ช่องทาง (ตัดสินใจแล้ว 2026-09-12):**
  - **ช่องทางที่ 1 — คีย์บอร์ด (WASD):** ผู้เล่นมนุษย์ขับตรง ๆ (PlayerMovementSystem)
  - **ช่องทางที่ 2 — MCP Auto-Move:** AI VTuber สั่ง `move_to_location` →
    PlayerAutoMoveSystem เดินเข้าหาโซนเป้าหมายด้วยความเร็วเดิม (3.5 unit/วิ)
    ตามเส้นตรงไปยัง WorldX/WorldY ของโซน
  - **กันชนสองช่องทาง:** ต่อเฟรมมีผู้ควบคุมเดียว — ระหว่าง auto-move คีย์บอร์ด
    ไม่มีผล (IsAutoMoving lock), พอถึงปลายทางคีย์บอร์ดกลับมาทำงานทันที
  - **โซนเปลี่ยนเมื่อเดินถึงเท่านั้น** — biome/ไอเท็มบนพื้น/chibi NPC เปลี่ยน
    หลังเดินถึงจริง ไม่ใช่ตอนเริ่มออกเดินทาง (รายละเอียด: [[player-auto-move-system]])
- แผนที่แบ่งเป็น Location Zone แต่ละโซนมี **Loot Table (Luban table)** กำหนดว่ามีไอเท็มอะไรวางอยู่บนพื้น + weight
  - Beach → มะพร้าว, น้ำขวด
  - Jungle → เถาวัลย์, พืชสมุนไพร
  - Cave → หิน, แร่
- **การเก็บไอเท็ม (Physical Pickup):**
  - **โหมดที่ 1:** เดินไปเด็ด/เก็บของโดยตรง (เดินทับไอเท็มแล้วมันเข้า inventory อัตโนมัติ)
  - **โหมดที่ 2:** กดปุ่ม Interact เมื่ออยู่ใกล้ไอเท็ม → pickup
- ไอเท็มที่เก็บได้ → เข้า Inventory → แปลงเป็น Card (Card System)
- Crafting ใช้ Recipe Table (Luban): input card(s) → output card, มีเงื่อนไข tool/สถานที่/skill
- ทรัพยากรจำกัด (finite nodes ค่อยๆ หมด) เพื่อบังคับให้ต้องแย่งชิงกับ NPC หรือย้ายโซน

### 2.3 NPC & Social Deduction
- NPC มี Role: `Innocent`, `Killer`, อาจมี `Neutral` (เอาตัวรอดอย่างเดียว ไม่ช่วยใคร, เปิดใช้ทีหลังได้)
- **สัดส่วน Killer:Innocent = ยึดตาม Among Us (~1:4 ถึง 1:8)** เช่น NPC 5 ตัว → 1 killer, NPC 10 ตัว → 2 killer — คำนวณจาก `killerCount = clamp(round(npcCount / 6), 1, npcCount/4)` เป็นค่าเริ่มต้น ปรับ balance ได้ภายหลังจาก playtest
- NPC แต่ละตัวมี **Schedule/Behavior State** (กำลังหาน้ำ, นอนพัก, สำรวจโซน X) ที่ผู้เล่น query ได้บางส่วน (ไม่ full information — ต้องสังเกต/ตามดูจริง คล้าย Among Us "vent/task" pattern)
- **Killer NPC มี hidden action `Sabotage`/`Eliminate` ที่ทำได้เมื่อ (ก) อยู่กับเหยื่อสองต่อสองไม่มีพยาน (ข) ผ่าน cooldown**
- **กฎ "No Witness" ถูกบังคับใช้ทั้ง AI Killer และ Player (ผ่าน NpcDirectorSystem.TryEliminate ตัวกลาง)**
  - ผู้เล่นไม่สามารถฆ่า NPC ได้ ถ้ามีคนอื่นอยู่ใน location เดียวกัน
  - ระบบเช็คจากฟังก์ชันกลางเดียวกัน → ทั้ง AI Killer และ Player Killer ใช้กฎเดียวกัน
- เมื่อมีการฆาตกรรม → เกิด **Clue** ติดโซนที่เกิดเหตุ (v2, ตัดสินใจแล้ว 2026-09-14):
  clue เป็น **instance object** (`ClueInstance`) มี metadata ครบ — timestamp, แหล่งที่มา
  (`ClueTriggerSource`: KillSabotage/TaskSabotage/IncidentalAction/Hunting), พยาน snapshot
  (exclude ผู้ก่อเหตุ; player ไม่เป็นพยานตัวเอง) — เกิดผ่าน pipeline กลางเดียว
  `ClueGenerationSystem.TryGenerate` แบบ **roll อิสระต่อ trigger ตาม weight**
  (การฆ่า 1 ครั้ง = blood 100% + scratch 30% → ได้ 0/1/2 ชิ้น) จากตาราง
  `ActionClueTriggerDef`; hooks: kill / explore (น้ำ/10%) / harvest สัตว์ /
  task sabotage (รอระบบ) — สถาปัตยกรรม: [[clue-system-v2]]
- Meeting Phase: ผู้เล่นเลือก "กล่าวหา" NPC หรือ "งดออกเสียง" — ถ้ากล่าวหาผิด เสีย trust/mood ทั้งกลุ่ม, ถ้าปล่อยฆาตกรไว้นานเกินไป จำนวน NPC ที่รอดลดลงเรื่อยๆ

### 2.4 Player-as-Killer (Design Pivot)
- **เดิม:** มีแต่ NPC ที่เป็น Killer
- **ใหม่:** ผู้เล่นสามารถเป็น Killer ได้!
  - ใช้การ์ดประเภท **Weapon** (เช่น มีด, หินขว้าง) เพื่อ Eliminate NPC
  - ต้องอยู่ใน Location เดียวกับเป้าหมาย และไม่มีพยาน (No Witness)
  - ไอเท็ม Weapon ใช้แล้วไม่หาย (หรือหายตามเกมที่เลือก) — ถ้าใช้ไม่สำเร็จ (มีพยาน) การ์ดไม่เลือน
- **กฎ No Witness ใช้ร่วมกัน** ระหว่าง AI Killer และ Player — ผ่านระบบตรวจสอบกลาง `NpcDirectorSystem.TryEliminate`
  - ไม่ว่าจะใครก็ตามที่เรียก Eliminate, ระบบจะเช็คเงื่อนไขเดียวกัน: "ต้องมีแค่คนฆ่า + เหยื่อในรอบนั้น"
- Player Killer ยังต้องดูแล Stat ของตัวเอง (Hunger/Thirst) — ไม่ใช่ omniscient

### 2.5 Win / Lose Conditions
- **Win:** จับฆาตกรได้ครบ (โหวตถูกตัว) หรือเอาชีวิตรอดจนครบจำนวนวันที่กำหนด (survival timer) โดยไม่ตาย
- **Lose:** ผู้เล่นตายจาก stat ติดลบ/เจ็บป่วยรุนแรง/ถูกฆาตกรรม, หรือกล่าวหาผิดจนหมด "trust token" (โหวตผิดครบจำนวนครั้ง)

---

## 3. Event System

สืบทอด `WorldEventSystem` เดิม (weighted random pool ผ่าน Luban) แต่แยก event
เป็น 2 กลุ่มเพื่อไม่ให้ปนกัน:

| กลุ่ม Event | ตัวอย่าง | Trigger |
| --- | --- | --- |
| **Survival Event** | พายุเข้า (เพิ่ม fatigue drain), พบสัตว์ป่า, อาหารเป็นพิษ | Tick-based random, ขึ้นกับ location/เวลา |
| **Social Event** | NPC ทะเลาะกัน (เผย relationship), พบศพ, NPC ขอความช่วยเหลือ (อาจเป็นกับดัก), ขอแลกไอเท็ม | ขึ้นกับ NPC state machine + role |

Event ทั้งสองกลุ่มต้อง expose ผ่าน MCP เป็น queryable event queue เดียวกัน
(เหมือน `await_next_world_event` เดิม) เพื่อให้ AI Agent เห็นภาพรวมและตัดสินใจ
ตอบสนองได้โดยไม่ต้อง "สัญชาตญาณมนุษย์"

---

## 4. Tech Stack — ใช้ต่อจาก Cultivation Together

ตามที่ระบุไว้ ให้ scope เฉพาะส่วนที่จำเป็นให้ AI VTuber เล่นได้ ไม่ทำต่อถึง
Twitch extension/multiplayer voting

| ส่วน | ใช้ต่อจากโปรเจกเดิม | ปรับสำหรับเกมใหม่ |
| --- | --- | --- |
| Game engine | Unity (C#) | เปลี่ยนจาก UI แบบ sect-management เป็น 2D sandbox + card UI |
| DI | VContainer | เหมือนเดิม, register subsystem ใหม่ (SurvivalSystem, DeductionSystem, CardSystem) |
| Messaging | MessagePipe (in-process) | เหมือนเดิม |
| IPC | MessagePipe.Interprocess (TCP) | เหมือนเดิม, Unity host / Bridge client |
| MCP Bridge | .NET 8 Console (`ModelContextProtocol` SDK) | เพิ่ม tool set ใหม่ (ดูหัวข้อ 6) แทนที่ sect tools เดิม |
| Serialization | MessagePack | เหมือนเดิม |
| DataTable | **Luban** | ตารางใหม่: `CardDef`, `LocationDef`, `RecipeDef`, `NpcRoleDef`, `ClueDef`, `IllnessDef`, `WorldEventDef` |
| UI Framework | **Xianxia.UI.MVP Lite** (เปลี่ยนชื่อ namespace ได้ตามต้องการ) | View/Presenter/Service pattern เดิม, เพิ่ม panel ใหม่: `CardHandView`, `MapExploreView`, `MeetingVoteView`, `ClueBoardView` |
| Avatar | **เปลี่ยนใหม่ทั้งหมด** — ดูหัวข้อ 4.1 | ไม่ใช้ portrait-swap เดิมของโปรเจกอ้างอิง เปลี่ยนเป็น chibi sprite-swap paperdoll ที่เดิน/ขยับแขนขาได้จริงบน 2D sandbox |
| Additive Scene | (ตามแผนเดิมที่ยังเป็น draft) | เหมาะมากสำหรับเกมนี้: `CoreScene` (HUD/inventory persistent) + `GameplayScene` (island/location ที่กำลังสำรวจ) |

**สิ่งที่ตัดออกจาก scope เดิมโดยตั้งใจ** (ตามที่ระบุ): ไม่ทำ Twitch
extension, ไม่ทำระบบโหวตผู้ชม, ไม่ทำ multiplayer — ทุกอย่างจบที่ single
player + MCP ให้ AI VTuber เรียกเล่นได้เท่านั้น

### 4.0 Technical Constraints — Multiplayer-Ready Architecture

> แม้ปัจจุบันเกมเป็น Single-player แต่ระบบหลังบ้านออกแบบมาให้รองรับ Multiplayer ได้ในอนาคต

- `GameStateProvider` ใช้ **Dictionary-based** structure เก็บ player state หลายตัว → รองรับหลาย player ในอนาคตได้โดยไม่ต้อง refactor
- `CardDef` แยก `Target` และ `Effect` ออกจากกัน — รองรับทั้ง Self-target (ใช้กิน/ดื่ม) และ Single-target (ใช้ weapon โจมตี)
- `NpcDirectorSystem.TryEliminate` เป็นฟังก์ชันกลางตรวจสอบกฎ "No Witness" — ทั้ง AI Killer และ Player Killer เรียกใช้งานผ่านฟังก์ชันเดียวกัน
- `DeductionSystem` ทำ information-hiding ชั้นเดียวสำหรับทุก entity — ไม่ว่าจะเป็น AI player หรือ human player

**หมายเหตุ:** Multiplayer จริงยังไม่ implement (อยู่ใน scope ตัดออกโดยตั้งใจ) — แต่ architecture เตรียมไว้ให้ expand ได้ง่าย

### 4.1 Avatar System — Chibi Sprite-Swap Paperdoll (เปลี่ยนจากโปรเจกอ้างอิง)

โปรเจกอ้างอิงทำ `AvatarAppearance` เป็น **portrait-swap** เท่านั้น (โชว์หน้าตา/
ครึ่งตัวนิ่งๆ ในกล่องสนทนา) ซึ่งไม่พอสำหรับเกมนี้ที่ตัวละครต้องเดินไปมาบน
แผนที่ 2D sandbox จริง จึงออกแบบใหม่เป็น **ChibiAppearance**:

- ยังคง **หลักการ Dictionary-based เดิม** (`Parts: slot→partId`, เพิ่ม slot ใหม่แก้แค่ JSON) แต่แต่ละ slot ตอนนี้คือ**แขนขาแยกชิ้น** ไม่ใช่ภาพนิ่งภาพเดียว: `body, head, hair, arm_left, arm_right, leg_left, leg_right, accessory, overlay`
- แต่ละ part (`ChibiPartDef`) มี **sprite sheet ต่อทิศทาง+อนิเมชัน** (`FramesByAnimKey`: เช่น `"Down_Walk" → [เฟรม1, เฟรม2, เฟรม3, เฟรม4]`) แล้ว renderer สลับเฟรมตามจังหวะเดิน — เป็น **sprite-swap แบบคลาสสิก** (เหมือนเกม RPG top-down ทั่วไป) ไม่ใช่ skeletal/bone rig จึงทำง่ายกว่าและ art วาดแยกชิ้นได้ตรงไปตรงมา
- Layer stack ยึด draw order เดิมแต่ขยายรองรับแขนขา: `leg_back(0) → body(10) → leg_front(15) → arm_back(20) → head(30) → hair_back(35) → face_marking(38) → hair_front(40) → arm_front(45) → overlay/condition(50) → accessory(60)`
- **Condition overlay แยก dictionary ต่างหาก** (`ConditionOverlays`) จาก outfit `Parts` เพื่อให้คราบเลือด/ผ้าพันแผล/รอยขีดข่วน ติด-หลุดได้อิสระโดยไม่ต้องแก้ outfit ทั้งชุด
- `ChibiOutfitDef` แทน `OutfitDef` เดิม: เปลี่ยนทั้งชุด (ทุก slot) พร้อมกันแบบ atomic เหมือนเดิม แต่ต้องเช็คว่าทุก part มี `FramesByAnimKey` ที่ตรงกันครบ (เดินได้ทุกทิศ) ก่อน apply
- ผู้เล่นและ NPC ทุกตัวใช้ระบบเดียวกันหมด (NPC ก็เดินได้/มีคราบเลือดติดตัวเป็น clue ได้เหมือนที่ออกแบบไว้ในหัวข้อ 2.3)

---

## 4.2 Card System — Categories และ Targeting

#### Card Categories
- `Resource` — ไอเท็มที่เก็บได้จากโซน (มะพร้าว, น้ำขวด, เถาวัลย์)
- `Consumable` — ใช้กิน/ดื่ม เพื่อเติม Stat
- `Tool` — ใช้สำหรับ Crafting (เช่น `tool_campfire`)
- `Weapon` — ใช้โจมตี/Eliminate NPC (มีด, หินขว้าง)
  - **ใช้การ์ด Weapon ได้ต้องระบุ targetId** (Single-target) — ไม่สามารถใช้กับตัวเองได้
  - **Self-use ไม่ support** สำหรับ Weapon (ต่างจาก Consumable)
- `Illness` — เจ็บป่วยติดตัว (ไข้, ท้องเสีย)
- `Clue` — เบาะแสที่เก็บได้จากการสืบสวน
- `Craftable` — การ์ดที่สร้างได้จาก Recipe

#### Card Targeting Mechanism
- แต่ละ Card Def มี `TargetType`: `SelfOnly | SingleTarget | Any`
- `Weapon` cards ต้องมี `TargetType = SingleTarget` และระบุ `targetId` ในคำสั่งใช้การ์ด
- ระบบตรวจสอบกฎ No Witness ก่อนอนุญาตให้ใช้ Weapon:
  1. เช็ค targetId ว่ามี NPC อยู่ใน Location เดียวกันหรือไม่
  2. เช็คจำนวน NPC ใน Location — ต้องถ้ามีแค่ target + player เท่านั้น (No Witness)
  3. ถ้ามีพยาน → การ์ดไม่เลือน, eliminate ล้มเหลว
  4. ถ้าไม่มีพยาน → สำเร็จ, NPC ตาย, cooldown เริ่ม

---

## 5. Data Model (ร่าง, สไตล์เดียวกับ Shared/ เดิม)

```csharp
// Shared/CardDef.cs (Luban-generated, plain class)
public class CardDef
{
    public string Id;
    public CardCategory Category; // Resource, Consumable, Tool, Weapon, Illness, Clue, Craftable
    public string DisplayName;
    public string SpritePath;
    public int StackLimit;
    public Dictionary<string, float> StatEffect; // e.g. {"Hunger": +20}
    public CardTargetType TargetType; // SelfOnly, SingleTarget, Any
    public string? TargetRequired; // ถ้าเป็น Weapon จะระบุ targetId ที่จำเป็น
}

// Shared/CardTargetType.cs
public enum CardTargetType
{
    SelfOnly,    // ใช้กับตัวเองได้เท่านั้น (Consumable, Medicine)
    SingleTarget, // ใช้กับ NPC/Entity ตัวเดียวได้ (Weapon, Buff)
    Any          // ใช้ได้ทั้งตัวเองหรือคนอื่น
}

// Shared/PlayerSurvivalState.cs
[MessagePackObject]
public class PlayerSurvivalState
{
    [Key(0)] public float Hunger;
    [Key(1)] public float Thirst;
    [Key(2)] public float Mood;
    [Key(3)] public float Fatigue;
    [Key(4)] public List<string> ActiveConditionCardIds; // illness/injury ติดตัว
    [Key(5)] public Dictionary<string, int> Inventory;   // cardId -> count
}

// Shared/NpcState.cs
[MessagePackObject]
public class NpcState
{
    [Key(0)] public string Id;
    [Key(1)] public NpcRole Role;              // Innocent / Killer / Neutral
    [Key(2)] public bool IsAlive;
    [Key(3)] public string CurrentLocationId;
    [Key(4)] public List<string> VisibleConditionCardIds; // บาดแผล/เบาะแสที่ "เห็นได้" เท่านั้น
    [Key(5)] public ChibiAppearance Avatar;    // ดูหัวข้อ 4.1 — chibi sprite-swap, ไม่ใช่ portrait เดิม
}

// Shared/ClueDef.cs (v2: + ClueCategory)
public class ClueDef
{
    public string Id;
    public string DisplayName;       // "คราบเลือด", "รอยขีดข่วน"
    public ClueReliability Reliability; // Strong / Weak / RedHerring
    public string ClueCategory;      // wet / blood / footprint / general (v2)
    public string LinkedNpcIdHint;   // ใช้ฝั่ง server logic เท่านั้น ไม่ส่งตรงให้ client/AI
}

// Shared/ClueInstance.cs (v2, 2026-09-14) — clue ที่เกิดจริง 1 ชิ้น = 1 instance
[MessagePackObject]
public class ClueInstance
{
    [Key(0)] public string InstanceId;
    [Key(1)] public string DefId;
    [Key(2)] public string LocationId;
    [Key(3)] public float GameTimestamp;
    [Key(4)] public ClueTriggerSource Source;  // KillSabotage/TaskSabotage/IncidentalAction/Hunting
    [Key(5)] public string SourceActorId;      // ground truth — ห้าม expose
    [Key(6)] public List<string> WitnessNpcIds; // snapshot — ground truth — ห้าม expose
}
```

**v2 addition:** เก็บ instance ทั้งหมดใน `GameStateProvider.AllClueInstances` (registry กลาง,
multiplayer-ready); player เก็บเข้ามือผ่าน `investigate_clue` →
`CollectedClueInstanceIds` (Key(8) คงเดิมจากเดิม CollectedClueCardIds — wire-compatible)

**หลักการสำคัญ:** `LinkedNpcIdHint` และ ground-truth ของ `NpcRole.Killer`
ต้อง **ไม่** หลุดออกไปใน MCP response ใดๆ ที่ AI query ได้ตรงๆ (ไม่งั้น AI
จะโกงรู้ตัวคนร้ายทันที) — ต้องมี query layer แยกระหว่าง "GM/debug view"
(เห็นหมด) กับ "Player/Agent view" (เห็นเฉพาะสิ่งที่สังเกตได้จริงในเกม)
เหมือนกับ Among Us ที่ผู้เล่นไม่เห็น role คนอื่น

---

## 6. MCP Tool Design (สำหรับให้ AI VTuber เล่น)

### 6.1 การเพิ่มเติมจาก Design Pivot

| Tool | ประเภท | หน้าที่ | หมายเหตุ |
| --- | --- | --- | --- |
| `get_game_state` | Query | คืน player stat, inventory, location, เวลาปัจจุบัน, NPC ที่มองเห็นได้ในโซนเดียวกัน (พร้อม visible condition cards เท่านั้น) | — |
| `get_hand` / `get_inventory` | Query | รายการการ์ดที่ถืออยู่ | — |
| `explore_location` | Action (Request-Response) | **เดินไปยัง location + สำรวจ node** → คืนการ์ดที่พบ/เหตุการณ์ที่เจอ | รวมการเดินและเก็บของ |
| `pickup_item` | Action (ใหม่) | เก็บไอเท็มจากพื้น (Physical Pickup) | ใช้เมื่ออยู่ใกล้ไอเท็มบนพื้น |
| `craft_card` | Action | ใช้ recipe คราฟไอเท็ม, คืนผลสำเร็จ/ล้มเหลว | — |
| `use_card` | Action | กินอาหาร/ดื่มน้ำ/ใช้ยา **หรือใช้ Weapon โจมตี NPC** | Weapon ต้องระบุ targetId + ผ่าน No Witness check |
| `move_to_location` | Action | **เดิน**ไปยัง location ที่เชื่อมต่อถึง (auto-move ความเร็ว 3.5) — response กลับ**หลังเดินถึง** (~ระยะทาง/3.5 วิ), โซน/biome/chibi เปลี่ยนตอนถึง; สั่งซ้ำระหว่างเดิน = **redirect ทันที** (คำสั่งเก่าคืน superseded ไม่ commit โซน); ปฏิเสธ unknown_location / not_connected โดยไม่เริ่มเดิน; หมดเวลา 30 วิ คืน move_timeout | ตัดสินใจแล้ว 2026-09-12 (ดู [[player-auto-move-system]]) |
| `cancel_move` | Action | ยกเลิกการเดินอัตโนมัติทันที **โดยไม่ต้องสั่งปลายทางใหม่** — กำลังเดิน = หยุดตรงนั้น (ตำแหน่ง/โซนเดิม, คีย์บอร์ดกลับมาเฟรมถัดไป, คำสั่ง move ที่ถูกยกเลิกคืน superseded); ไม่ได้เดิน = คืน `not_moving` (ไม่ใช่ error); ใช้เมื่อเปลี่ยนใจกลางทาง เช่น เห็นศพ/โดนเห็น แล้วต้องการหยุดสังเกต | ตัดสินใจแล้ว 2026-09-12 (ดู [[player-auto-move-system]]) |
| `talk_to_npc` / `observe_npc` | Action/Query | สังเกตพฤติกรรม NPC เพื่อเก็บข้อมูลเชิงสังคม | — |
| `await_next_event` | Request-Response (คงแบบเดิมจาก Lab 6) | รอ event ถัดไป (survival หรือ social) แบบไม่ block TCP | — |
| `report_body` / `call_meeting` | Action | เริ่ม Meeting Phase หลังพบศพ/สงสัย | — |
| `accuse_npc` | Action | โหวตกล่าวหาใน Meeting Phase | — |
| `get_clue_board` | Query | รวมเบาะแสทั้งหมดที่ "เก็บสะสมมาแล้ว" ให้ AI ใช้นิรนัย — ⚠️ shape ใหม่ (2026-09-14): คืน `entries` ต่อ instance (`instance_id/display_name/reliability/location_id/witness_npc_ids` ที่ผ่าน filter แล้ว) | breaking change — สถาปัตยกรรม: [[clue-system-v2]] |
| `get_clue_graph` | Query (ใหม่) | เบาะแสชุดเดียวกันเป็นกราฟ: nodes (clue/npc) + edges clue→witness (witnessed) — reasoning "ใครเห็นอะไร" | 2026-09-14 |
| `investigate_clue` | Action (ใหม่) | สืบหา clue ในโซนปัจจุบัน — **ไม่มีพารามิเตอร์** (server บังคับใช้ตำแหน่งผู้เล่นเอง กันสำรวจข้ามโซน); VisibleToBystanders=true เจอทันที ไม่งั้น roll 50%; เจอ → เข้า clue board อัตโนมัติ | 2026-09-14 |

### 6.2 การ์ด Weapon และกฎ No Witness

เมื่อใช้ `use_card` กับ Weapon:
1. ระบบเช็ค targetId ว่ามี NPC อยู่หรือไม่
2. ระบบเช็ค No Witness: มี NPC คนอื่นอยู่ใน Location เดียวกันหรือไม่
3. ถ้ามีพยาน → ล้มเหลว (การ์ดไม่เลือน), คืน failureReason
4. ถ้าไม่มีพยาน → สำเร็จ, NPC ตาย, cooldown เริ่ม

### 6.3 Information Hiding (อัปเดต)

ทุก tool ต้องออกแบบให้ตอบด้วยข้อมูลที่ **AI ตัดสินใจได้จากสิ่งที่ query ได้จริง**
(หลักคิดกลางเดิมของโปรเจก) — ห้าม leak ground truth เช่น NPC role ที่แท้จริง
ผ่าน tool ไหนเลยนอกจาก debug/GM-only tool ที่แยก namespace ชัดเจน

เพิ่มเติมจากเดิม:
- Player-as-Killer ต้องไม่รู้ว่า NPC ตัวไหนเป็น Killer จริงๆ (เหมือน Among Us)
- AI ต้อง infer จากพฤติกรรมพวกลมหา (observe, clue, alibi)
- `GetVisibleNpcs` ยังคงเป็นทางเดียวที่ปลอดภัย — ไม่ expose `NpcRole`
- **Clue System v2 (2026-09-14)** — information hiding ทำ 3 ชั้น:
  1. `ClueGeneratedMessage` (broadcast) ไม่มี SourceActorId/WitnessNpcIds เลย
  2. `get_clue_board`/`get_clue_graph` กรอง witness เฉพาะ `player_local || IsAlive`
     (killer และ NPC ตายแล้วไม่มีวันโผล่) — ตรวจจริงด้วย byte-scan ของ response ที่ serialize
  3. `investigate_clue` ไม่รับ locationId จาก caller — server ใช้ตำแหน่งผู้เล่นเท่านั้น
     (กัน AI สำรวจข้ามโซน — ปัญหาเดิมของ ExplorationSystem)

---

## 7. UI.MVP Lite — Panel ใหม่ที่ต้องเพิ่ม

ใช้ pattern เดิม (View = MonoBehaviour, Presenter = Plain C#, Direct Method
Call สำหรับ in-process logic ตาม lesson จาก Lab 13):

- `CardHandView/Presenter` — แสดงมือการ์ด, drag เพื่อ craft/ใช้
  - **รองรับการ์ด Weapon** — แสดง target indicator เมื่อเลือก weapon
- `MapExploreView/Presenter` — 2D sandbox map, node ที่สำรวจได้/หมดแล้ว
  - **Display ไอเท็มบนพื้น** — แสดงตำแหน่ง item ที่สามารถ pickup ได้
- `ClueBoardView/Presenter` — กระดานปะติดปะต่อเบาะแส (คล้าย detective board)
- `MeetingVoteView/Presenter` — หน้าประชุม/โหวตกล่าวหา
- `ConditionOverlayView` — แสดง illness/injury card เป็น overlay บน Chibi (ใช้ `ChibiAnimatedRenderer` ตัวเดิม, เพิ่ม layer สำหรับคราบเลือด/ผ้าพันแผล)
- `MovementController` (ใหม่) — WASD movement สำหรับ Chibi Character
  - เดินไปมาบน 2D sandbox
  - **Auto-Move indicator** — เมื่อ AI VTuber สั่ง `move_to_location` ให้เห็น
    สถานะเดินอัตโนมัติ (Activity = Traveling บนจอแล้ว; แสดง target marker เพิ่ม
    เป็น optional) — ช่วงนี้คีย์บอร์ดไม่มีผลจนถึงปลายทาง
  - ปุ่ม Interact สำหรับ pickup ไอเท็ม
  - ปุ่ม Attack สำหรับใช้ Weapon

---

## 8. Roadmap ที่แนะนำ (ต่อยอดจากรูปแบบ "Lab Round" เดิม)

1. **Lab A — Survival Core (Walking Sandbox):** Stat system, Card/Inventory data model, Zone-based Loot, Physical Pickup, MCP tool อ่าน/คราฟพื้นฐาน, Movement Controller (WASD)
2. **Lab B — Illness/Injury Layer:** ConditionCard system + ChibiAppearance overlay integration + ChibiAnimatedRenderer เดิน 4 ทิศพร้อม overlay
3. **Lab C — NPC Skeleton:** NpcState, schedule/behavior stub, visibility rule (สิ่งที่ query เห็น vs ไม่เห็น)
4. **Lab D — Social Deduction Core:** Killer sabotage logic, Clue generation, Meeting/Vote flow, **Player-as-Killer (Weapon cards + No Witness)**
5. **Lab E — MCP Full Tool Set + AI VTuber Playtest:** ต่อ Bridge ให้ AI เล่นครบ loop ได้จริงตั้งแต่สำรวจจนโหวตจับฆาตกร (รวมทั้งเล่นเป็น Killer ได้)
6. **Lab F (ถ้ามีเวลา) — Additive Scene migration** ตามแผน CoreScene/GameplayScene ที่ค้างไว้ในโปรเจกเดิม

---

## 9. คำถามเปิด (Open Questions)

- ~~จำนวน NPC ต่อรอบ และสัดส่วน Killer:Innocent~~ → **ตัดสินใจแล้ว: 1:4 ถึง 1:8 ตาม Among Us** (ดูหัวข้อ 2.3)
- ควรมี "Neutral role" (เอาตัวรอดอย่างเดียว ไม่ใช่ทั้งฝ่ายดี/ร้าย) หรือไม่ — เพิ่มความซับซ้อนแต่เพิ่ม replayability
- Killer NPC ควรมี "hidden agenda card" ของตัวเอง (เช่นต้องฆ่าให้ครบ N คนก่อนวันที่ X) เพื่อให้ AI มี objective ที่ query ได้เหมือนฝั่งผู้เล่นหรือไม่ — ต้องเผื่อไว้ถ้าอนาคตอยากให้ AI สลับมาเล่นเป็นฆาตกรได้ด้วย
- ระดับ information ที่ Meeting Phase เปิดเผย (ใครอยู่กับใครตอนไหน) ควรมาจาก log ที่ผู้เล่นสังเกตเองระหว่างเกม หรือมีระบบ "alibi" กลาง ๆ ให้ query

---

## 10. สรุปการเปลี่ยนแปลง (Design Pivot Summary)

| หัวข้อ | ก่อน Design Pivot | หลัง Design Pivot |
| --- | --- | --- |
| Core Gameplay | Point-and-Click, คลิกเลือกโซนจัดการทรัพยากร | Real-time Walking Sandbox, WASD เดินไปมา + Physical Pickup |
| Item Gathering | สุ่มจากการ์ดจาก UI | Zone-based Loot (ไอเท็มวางตามโซน), เก็บด้วยการเดิน/Interact |
| Killer Role | มีแค่ NPC Killer | **ผู้เล่นเป็น Killer ได้ด้วย** ใช้การ์ด Weapon |
| No Witness Rule | ใช้กับ AI Killer เท่านั้น | ใช้ร่วมกันทั้ง AI Killer และ Player (ผ่าน NpcDirectorSystem.TryEliminate) |
| Card System | Categories พื้นฐาน | เพิ่ม **Weapon** category + CardTargetType (SelfOnly, SingleTarget) |
| Architecture | Single-player เท่านั้น | **Multiplayer-ready** (Dictionary-based GameStateProvider, แยก Target/Effect) |

**เหตุผลที่เปลี่ยน:**
- ทำให้เกมมี dimension ของ "เล่นเป็นฆาตกร" เพิ่มขึ้น — ผู้เล่นไม่ใช่แค่เอาตัวรอด แต่สามารถเลือกเป็น Killer ได้
- Walking Sandbox ทำให้การเดิน/สำรวจมีความเป็นเกมมากกว่าการคลิกเมาส์
- Multiplayer-ready architecture เตรียมไว้สำหรับอนาคต (แม้ปัจจุบันไม่ implement)
