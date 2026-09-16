---
title: deduction
type: mechanics
sources:
  - Marooned/Assets/Scripts/Systems/DeductionSystem.cs
  - Marooned/Assets/Scripts/Systems/NpcDirectorSystem.cs
  - Marooned/Assets/Scripts/Shared/NpcState.cs
  - Marooned/Assets/Scripts/Shared/ClueDef.cs
  - marooned-wiki/wiki/sources/game-design-doc/game_design_doc.md
related:
  - "[[DeductionSystem.cs]]"
  - "[[npc-director]]"
  - "[[MeetingVoteView.cs]]"
  - "[[ClueBoardView.cs]]"
  - "[[clue-system-v2-presentation]]"
  - "[[Information-Hiding]]"
  - "[[overview]]"
folder: mechanics
created: 2026-09-05
tags:
  - mechanics
  - deduction
  - marooned
  - lab-a
---

# Social Deduction

## ภาพรวม (Gameplay Perspective)
เมื่อเกิดฆาตกรรม ผู้เล่นจะพบเบาะแส (Clue Card) เช่น คราบเลือด รอยขีดข่วน — บางอันเชื่อถือได้
(Strong) บางอันเป็นกับดัก (RedHerring) ผู้เล่นสะสมเบาะแสไว้บน Clue Board แล้วเรียกประชุม
เพื่อ **กล่าวหา (Accuse)** NPC ที่สงสัย — โหวตถูกตัวครบทุก Killer = ชนะ แต่โหวตผิด 3 ครั้ง
= แพ้ทันที (และ Mood ถูกหักทุกครั้ง) หลักการสำคัญ: ผู้เล่น/AI ไม่มีทางรู้ role จริงนอกจาก
อนุมานจากสิ่งที่สังเกตได้ เหมือน Among Us

## การ Implement (Developer Perspective)
- **Class หลัก:** `DeductionSystem` (`Marooned/Assets/Scripts/Systems/DeductionSystem.cs`) —
  **ชั้นเดียวที่แปลง ground truth → safe view** ([[Information-Hiding]])
- **Public API สำคัญ:**
  - `List<NpcObservableView> GetObservableNpcsAt(string locationId)` — NPC ที่มองเห็นได้ใน
    location เดียวกัน (ไม่มี Role/Cooldown หลุดออก; กรอง condition ด้วย `IllnessDef.Visible`)
  - `(bool wasCorrect, bool win, bool loss, string resultText) Accuse(string targetNpcId)` —
    ถูก: killer ออกจากเกม (หมดทุกตัว = win); ผิด: `WrongAccusations++`, Mood −15,
    ครบ `MaxWrongAccusations = 3` = loss
- **Flow การเก็บเบาะแส:** `NpcDirectorSystem.SpawnClues` ใส่ clue id ใน
  `victim.AllConditionCardIds` → ตัวกรอง visibility กำหนดว่าเห็นได้จากภายนอกหรือต้อง
  investigate → เก็บเข้า `CollectedClueInstanceIds` ผ่าน `investigate_clue` / pipeline
  ของ [[clue-system-v2-presentation]] (ClueGenerationSystem + hooks)
- **MCP tools:** `get_visible_npcs` / `get_clue_board` / `get_clue_graph` /
  `get_pinned_clues` / `investigate_clue` / `call_meeting` / `accuse_npc`
  (handler ทั้งหมดผ่าน DeductionSystem/GetClueBoardHandler เท่านั้น — ground truth
  ไม่ข้ามเส้น by construction)
- **Meeting Phase:** `CallMeetingHandler` ยังแค่ snapshot คนที่มองเห็น — ยังไม่มี pause/summon
  จริง (GDD §2.3)

## Data Tables
- `DataTables/Data/ClueDef.csv` — เบาะแส 4 ชนิด: `clue_blood_stain` (Strong),
  `clue_scratch_mark` (Weak), `clue_footprint_mud` (Weak, ต้อง investigate),
  `clue_torn_cloth` (RedHerring)
- `DataTables/Data/IllnessDef.csv` — field `visible` ใช้กรองว่าผู้สังเกตเห็นได้หรือไม่
- ⚠️ ทั้งหมดยังโหลดผ่าน mock ใน [[LubanDataService.cs]]

## ความเชื่อมโยงกับระบบอื่น
- [[npc-director]] — แหล่ง ground truth และเหตุการณ์ฆาตกรรม
- [[MeetingVoteView.cs]] / `MeetingVotePresenter` — UI โหวตกล่าวหา
- [[ClueBoardView.cs]] / `ClueBoardPresenter` — UI กระดานเบาะแส
- [[survival-stats]] — โหวตผิดหัก Mood
- GDD §9 (Open Questions) — Neutral role และระบบ alibi ยังไม่ตัดสินใจ

## AI Deduction Workflow (รวม Pin Actions)

Workflow ที่แนะนำสำหรับ AI VTuber (MCP) — อ่านอย่างเดียวทุก tool, สั่งงานผ่าน mutating
tools เท่าที่มี และ **ไม่มี tool ใดคืน ground truth** (killer role/agenda ไม่ข้ามเส้น):

1. **Observe** — `get_game_state` (โซน/สถานะตัวเอง) + `get_visible_npcs` (ใครอยู่รอบตัว,
   activity, condition ที่มองเห็นได้)
2. **Collect** — `investigate_clue` (ไม่มีพารามิเตอร์ — server ใช้ตำแหน่ง player เอง,
   กันสำรวจข้ามโซน)
3. **Review board** — `get_clue_board` (ราย instance + reliability/witnesses ที่ผ่าน filter)
   หรือ `get_clue_graph` (มุมมอง node/edge สรุปใครเห็นอะไร)
4. **Pin Actions — ของ player และของ AI เอง** — `get_pinned_clues` (อ่าน) +
   `set_pinned_clue` (pin/unpin เอง):
   pin เป็น action ร่วมของทั้งสองฝั่งบน state เดียว (CluePinState) — player คลิก node
   บนกระดาน → [ปักหมุด] (× บนการ์ด = ถอน), AI เรียก `set_pinned_clue` พร้อม
   `nodeId` (จาก `get_clue_graph`) และ `pinned` (true/false); ไม่มีเพดาน (workspace
   semantics — ลากเข้า/ออก = pin/unpin); idempotent — pin ซ้ำ/unpin ซ้ำ = no change
   (ปลอดภัยกับ retry); pin รอดจากปิด/เปิดกระดานใน session เดียวกัน — ดู
   [[clue-system-v2-presentation]]
   - **AI pin เพื่อวิเคราะห์**: pin คู่ที่สงสัไว้เทียบกัน (clue + npc witness) แล้วอ่าน
     `get_pinned_clues` เพื่อเห็นข้อมูลสรุป — pin ที่ player ทำไว้ = สัญญาณว่า player
     สงสัยอะไร (AI ควรอ่านก่อนพูด):
     - reliability ขัดกัน (Strong vs RedHerring ชี้ที่ npc เดียวกัน?)
     - พยานร่วม (2 clue โดน npc เดียวกันเห็นทั้งคู่ → alibi ของ npc นั้นน่าสงสัย)
     - โซนที่ชนกัน (clue @ beach + npc โซน beach → จุดเชื่อมเชิงเวลา)
   - เบาะแสซ้ำใน alibi แสดงรูป `×N` (เช่น `คราบเลือด @ beach ×4`) — N = จำนวน
     instance จริงที่เก็บได้ (display-only)
5. **Reason + narrate ก่อนกล่าวหา** — `accuse_npc` ผิด 3 ครั้ง = แพ้ทันที

ตัวอย่าง output ของ `get_pinned_clues` (human-readable เสมอ ตาม MCP convention):

```
Pinned Clues — 2 node(s) pinned (oldest first):
  [clue] คราบเลือด (Strong) @ beach — seen near: npc_02, player_local
  [npc] npc_04 | zone=beach • alive | witnessed: คราบเลือด @ beach; รอยขีดข่วน @ beach
```

คืน "No nodes pinned on the clue board …" เมื่อไม่มี pin — ไม่ใช่ error (อ่านได้ปกติ
แม้กระดานปิดอยู่ — state อยู่ที่ CluePinState singleton ไม่ผูกกับ panel)

## สถานะปัจจุบัน
- ✅ Accuse (win/loss) + observable view filtering + โทษโหวตผิด เสร็จแล้ว
- ✅ กลไกเก็บ clue ครบ (Clue System v2 a–d): `CollectedClueInstanceIds` เติมผ่าน
  investigate/kill/explore/hunt pipeline — `get_clue_board` มีข้อมูลจริง
- ✅ `investigate_clue` มีแล้ว (empty request — server-side location)
- ❌ ไม่มี Meeting Phase state machine — accuse ได้ทุกเมื่อ
- ❌ `MaxWrongAccusations = 3` hardcode
- ✅ AI pin/unpin เองได้แล้วผ่าน `set_pinned_clue` (2026-09-15 — idempotent,
  validate กับกราฟก่อน mutate, main-thread dispatch, AI pin ขึ้น UI ทันที)
