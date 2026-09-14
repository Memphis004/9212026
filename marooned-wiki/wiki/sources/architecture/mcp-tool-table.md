---
title: mcp-tool-table
type: architecture
sources:
  - McpBridge/Program.cs
  - Marooned/Assets/Scripts/Systems/McpRequestHandlers.cs
  - Marooned/Assets/Scripts/Shared/GameMessages.cs
  - marooned-wiki/wiki/sources/game-design-doc/game_design_doc.md
related:
  - "[[mcp-bridge]]"
  - "[[overview]]"
  - "[[game_design_doc]]"
  - "[[McpRequestHandlers.cs]]"
  - "[[Information-Hiding]]"
folder: architecture
created: 2026-09-05
tags:
  - architecture
  - mcp
  - marooned
  - lab-a
---

# MCP Tool Table

ตาราง MCP tools ทั้งหมด — รวบรวมจากโค้ดจริงใน `McpBridge/Program.cs` (14 tools ที่
implement แล้ว) เทียบกับแผนใน game_design_doc.md §6 สถานะฝั่ง Unity ดูรายละเอียดใน
[[mcp-bridge]] — ระบบ clue ทั้งหมดดู [[clue-system-v2]]

## ตารางหลัก

| Tool Name | Category | Input | Output | Description | Status |
|-----------|----------|-------|--------|-------------|--------|
| GetGameState | SurvivalQuery | - | PlayerSurvivalState (ข้อความสรุป) | ดึง stat, inventory, ตำแหน่ง, condition, จำนวนโหวตผิด | ✅ |
| GetVisibleNpcs | SurvivalQuery | - | รายการ NpcObservableView | NPC ที่มองเห็นในโซนเดียวกัน (ไม่มี role จริง) | ✅ |
| GetClueBoard | SurvivalQuery | - | entries: List<ClueBoardEntry> (instance_id/display_name/reliability/location_id/witness_npc_ids) | เบาะแสที่ผู้เล่นเก็บสะสม — ⚠️ breaking (2026-09-14): จาก flat clue id → entry ต่อ instance, witness ผ่าน filter (killer/ศพไม่โผล่) — สถาปัตยกรรม: [[clue-system-v2]] | ✅ |
| GetClueGraph | SurvivalQuery | - | nodes (clue/npc) + edges (witnessed) | เบาะแสเดียวกันเป็นกราฟ: clue→witness ที่มีชีวิต — ใช้ reasoning "ใครเห็นอะไร" (2026-09-14) | ✅ |
| ExploreLocation | SurvivalAction | locationId | success + รายการ card id ที่พบ | สำรวจ node — สุ่ม loot แบบ deplete | ⚠️ |
| CraftCard | SurvivalAction | recipeId | success/failureReason + outputCardId | คราฟการ์ดตามสูตร | ⚠️ |
| MoveToLocation | SurvivalAction | locationId | success/failureReason | ย้ายไป location ที่เชื่อมถึงเท่านั้น | ✅ |
| UseCard | SurvivalAction | cardId, targetId (optional) | success/failureReason + ResultText (เมื่อ eliminate) | ใช้การ์ด — self-use เติม stat; weapon (Phase 4) ต้องระบุ targetId และผ่านเงื่อนไข no-witness จึงจะฆ่าสำเร็จ (การ์ดไม่หายถ้า fail) | ✅ |
| HarvestNode | SurvivalAction | toolItemId (optional) | Success + NodeId/ItemId/Count/Depleted/RegrowSeconds หรือ failureReason | เก็บเกี่ยว node ใกล้ผู้เล่น (Lab C Phase 1.5) — auto-pick tool จาก inventory; รายละเอียดใน [[biome-scatter-system]] | ✅ |
| AwaitNextEvent | SurvivalAction | timeoutSeconds (default 30) | TimedOut / Group + EventId + DisplayText | รอ world event ถัดไป (Survival หรือ Social) | ⚠️ |
| CallMeeting | Deduction | - | success + รายชื่อ NPC เข้าร่วม | เรียกประชุมฉุกเฉินเมื่อพบศพ/สงสัย | ⚠️ |
| AccuseNpc | Deduction | targetNpcId | WasCorrect + GameOverWin/Loss + ResultText | กล่าวหา NPC เป็น Killer — ตัดสินชนะ/แพ้ | ✅ |
| InvestigateClue | Deduction | **- (ไม่มีพารามิเตอร์)** | FoundInstanceId / failureReason (no_clue_at_location, investigation_failed) | สืบหา clue ในโซนปัจจุบัน — server บังคับใช้ตำแหน่งผู้เล่นเอง (กันสำรวจข้ามโซน); VisibleToBystanders=true เจอทันที ไม่งั้น roll 50%; เจอแล้วเพิ่มเข้า clue board อัตโนมัติ (2026-09-14) | ✅ |

**สถานะ:** ✅ = ใช้งานได้ครบ • ⚠️ = ใช้ได้แต่ logic ฝั่ง Unity ยังเป็น placeholder • ❌ = ยังไม่มี

## Tools ตามแผน GDD §6 ที่ยังไม่มีในโค้ด

| Tool | Category | แผนตาม GDD | Status |
|-----------|----------|-------------|--------|
| get_hand / get_inventory | Query | รายการการ์ดในมือ (แยกจาก GetGameState) | ❌ |
| talk_to_npc | Action | สนทนากับ NPC เพื่อข้อมูลเชิงสังคม | ❌ |
| observe_npc | Query | สังเกตพฤติกรรม NPC เพิ่มเติม | ❌ |
| report_body | Action | แยก "รายงานศพ" ออกจาก CallMeeting | ❌ |

## คำอธิบายแต่ละ Tool

- **GetGameState** — คำถามแรกที่ AI ควรเรียกทุก turn: รู้ว่าตัวเองอยู่ไหน หิวแค่ไหน
  มีอะไรในมือ และใกล้แพ้จากโหวตผิดกี่ครั้ง ใช้ตัดสินใจว่าจะกิน/สำรวจ/หาน้ำก่อน
- **GetVisibleNpcs** — ใช้เมื่ออยากรู้ว่า "ใครอยู่ด้วยตอนนี้" — ดู activity และ
  condition ที่มองเห็นได้ (เช่น คราบเลือด) เพื่ออนุมานความน่าสงสัย; ห้ามคาดหวัง role จริง
- **GetClueBoard** — ทบทวนเบาะแสที่สะสมไว้ก่อนตัดสินใจกล่าวหา — ⚠️ **shape ใหม่**
  (2026-09-14): คืน entry ต่อ clue instance พร้อม display_name/reliability/location_id และ
  witness_npc_ids ที่ผ่าน filter แล้ว (killer และ NPC ตายแล้วไม่มีวันโผล่); เบาะแสได้จาก
  investigate_clue + ถูก generate อัตโนมัติจากเหตุการณ์ (ฆ่า/สำรวจ/ล่า) — สถาปัตยกรรม:
  [[clue-system-v2]]
- **GetClueGraph** — ข้อมูลชุดเดียวกับ GetClueBoard ในรูปกราฟ: nodes = clue ที่เก็บแล้ว
  (type=clue) + พยานที่มีชีวิต (type=npc), edges = clue→witness (relation=witnessed) —
  เหมาะกับ reasoning เชิงสังคม "ใครเห็นอะไร ใครอยู่ใกล้เหตุ" (2026-09-14)
- **InvestigateClue** — สืบหาเบาะแสในโซนที่ **ยืนอยู่ปัจจุบัน** (ห้ามส่ง location — tool
  ไม่มีพารามิเตอร์เลย เพื่อกันการสำรวจข้ามโซน); โซนไม่มี clue = `no_clue_at_location`,
  มีแต่ roll ไม่ติด = `investigation_failed` (ลองใหม่ได้); เจอแล้วเข้า clue board ทันที —
  ใช้หลังย้ายไปโซนที่มีเหตุการณ์ (เช่น เห็นศพ/รอยเลือดจาก GetVisibleNpcs)
- **ExploreLocation** — ใช้เมื่ออยู่ location แล้ว หรือต้องการทรัพยากร; ผลลัพธ์ว่างได้ถ้า
  node หมด (depleted) — ไม่ใช่ error
- **CraftCard** — ใช้เมื่อมีวัตถุดิบครบตาม recipe (เช่น ปลาดิบ → ปลาย่าง); ถ้า fail จะบอก
  เหตุผล machine-readable (`missing_ingredients`, `missing_tool`, `wrong_location`)
- **MoveToLocation** — ใช้ย้ายโซนเพื่อหา loot ใหม่หรือตาม/หลีก NPC; ผิดกฎการเดินทางจะได้
  `not_connected` — **เดินจริง** (2026-09-12, ไม่ teleport): response กลับหลังเดินถึง
  (~ระยะทาง/3.5 วิ); สั่งซ้ำระหว่างเดิน = redirect ทันที คำสั่งเก่าคืน `superseded`
  (ไม่ commit โซน); ปลายทางถูก clamp เข้า WorldBounds; หมดเวลา 30 วิ = `move_timeout`
  (สถาปัตยกรรม: [[player-auto-move-system]])
- **CancelMove** — ยกเลิกการเดินอัตโนมัติทันทีโดยไม่ต้องสั่งปลายทางใหม่ (2026-09-12);
  กำลังเดิน = หยุดตรงนั้น คืน `Move cancelled` + ตำแหน่ง/โซนเดิม (คีย์บอร์ดกลับมาเฟรมถัดไป,
  คำสั่ง move ที่ถูกยกเลิกคืน `superseded`); ไม่ได้เดินอยู่ = คืน `not_moving` (ไม่ใช่ error);
  ใช้เมื่อเปลี่ยนใจกลางทาง เช่น เห็นศพ/โดนเห็น แล้วอยากหยุดเพื่อสังเกตการณ์
- **UseCard** — ใช้เมื่อ stat ต่ำ (ดูจาก GetGameState); การ์ดอาหารสุกให้ค่ามากกว่าของดิบ;
  การ์ด weapon (เช่น `knife_basic`) ต้องส่ง `targetId` — ล้มเหลวแบบการ์ดไม่หายถ้า target
  ไม่อยู่โซนเดียวกัน (`target_not_same_location`), มีคนเห็น (`witnessed`), ตายไปแล้ว
  (`target_already_dead`) หรือไม่ระบุ target (`missing_target`); สำเร็จคืน
  `eliminated_<npcId>` (สถาปัตยกรรม: [[card-system]])
- **AwaitNextEvent** — ใช้เมื่อ "ไม่มีอะไรจะทำ" เพื่อรอเหตุการณ์แล้ววางแผนตอบสนอง;
  ปัจจุบันตอบ timeout ทันทีถ้าคิวว่าง
- **HarvestNode** — เก็บเกี่ยว harvestable node ในรัศมี 1.6 หน่วยรอบผู้เล่น (Lab C:
  [[biome-scatter-system]]) — เลือก tool จากมือการ์ดให้เอง (เช่น `tool_axe` สำหรับ
  `tree_wood`), ไม่มี tool = `missing_tool`, ไม่มี node ใกล้ = `no_node_in_range`,
  node กำลังงอกใหม่ = `regrowing`; สำเร็จคืนไอเท็มเข้า inventory + บอกว่า node หมด
  หรือยัง (Depleted/RegrowSeconds); ระบุ `toolItemId` เองได้ถ้าอยากบังคับ tool ตัวใดตัวหนึ่ง
- **CallMeeting** — ใช้เมื่อพบศพหรือเก็บหลักฐานพอแล้ว; ตอนนี้แค่ list ใครอยู่ตรงนั้น
- **AccuseNpc** — ใช้เมื่อมีหลักฐานพอ (ผิด 3 ครั้ง = แพ้, Mood หาย 15/ครั้ง) — เป็น tool
  เดียวที่ตัดสิน win/lose ของเกม

## การเรียกใช้จาก AI VTuber

AI เรียกผ่าน MCP client (stdio) — Bridge เป็น MCP server ที่ spawn ด้วย command
`dotnet run` ภายใน `McpBridge/` (ต้องเปิด Unity Play mode ก่อน ดู [[mcp-bridge]])

**รูปแบบทั่วไป (MCP protocol):**
```json
{
  "method": "tools/call",
  "params": {
    "name": "ExploreLocation",
    "arguments": { "locationId": "beach" }
  }
}
```

**ตัวอย่างที่ AI พิมพ์จริงใน tool-call:**
- `GetGameState()` — ไม่มี argument
- `ExploreLocation(locationId: "beach")`
- `CraftCard(recipeId: "recipe_cook_fish")`
- `MoveToLocation(locationId: "jungle_edge")`
- `UseCard(cardId: "food_coconut")`
- `UseCard(cardId: "knife_basic", targetId: "npc_03")` — weapon (Phase 4)
- `AwaitNextEvent(timeoutSeconds: 30)`
- `AccuseNpc(targetNpcId: "npc_03")`
- `GetClueBoard()` / `GetClueGraph()` — ไม่มี argument (shape ใหม่ 2026-09-14)
- `InvestigateClue()` — ไม่มี argument (ใช้โซนปัจจุบันเสมอ)

**ข้อควรรู้สำหรับ AI:**
- ทุก tool คืนค่าเป็น **ข้อความภาษาคน** (ไม่ใช่ JSON ดิบ) — อ่านแล้วใช้ตัดสินใจได้ทันที
- ชื่อ tool ในโค้ดเป็น PascalCase (`GetGameState`) — บาง MCP client จะ normalize เป็น
  snake_case (`get_game_state`) ตาม convention ของ client ฝั่งนั้น
- ข้อมูลที่ได้คือ "สิ่งที่ผู้เล่นสังเกตได้จริง" เท่านั้น — ground truth (role Killer)
  ถูกซ่อนโดยการออกแบบ ดู [[Information-Hiding]]
- argument ผิด (เช่น locationId ไม่มีจริง) ไม่ throw — tool คืนข้อความ failure ที่อ่าน
  เหตุผลได้ ให้ลองใหม่ด้วยค่าที่ถูก
