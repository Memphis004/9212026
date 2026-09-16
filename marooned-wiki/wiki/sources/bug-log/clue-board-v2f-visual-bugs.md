# Clue Board v2(f) — Visual Bugs (2026-09-16)

วันที่: 2026-09-16  
สถานะ: **Open**  
Introduced by: Clue System v2(f) drag-drop workspace redesign  

## BUG-1: HoverTooltip แสดงผลที่มุมล่างซ้ายแทนที่จะตามเมาส์

**Symptoms:**  
ชี้เมาส์ที่ graph node (เช่น npc_03 ตรงกลางจอ) → tooltip แสดงผลที่มุมล่างซ้ายจอ  
แทนที่จะแสดงใกล้ตำแหน่งเมาส์

**Evidence:**  
Screenshot 2026-09-16 15:53 — ลูกศรชี้จาก node npc_03 ตรงกลางไปที่ tooltip  
ที่มุมล่างซ้าย ("npc_03 อยู่ที่: beach ไม่มีเบาะแส...")

**Root cause (suspected):**  
`HoverTooltipView.FollowMouse()` ใช้ `RectTransformUtility.ScreenPointToLocalPointInRectangle`  
กับ canvas parent ของ tooltip — แต่ tooltip ถูก parent ใต้ root Canvas  
ซึ่งอาจมี coordinate mapping ต่างจาก screen coords  
(เช่น CanvasScaler `Scale With Screen Size` หรือ Canvas renderMode ไม่ใช่ overlay)

**Files involved:**  
- `Marooned/Assets/Scripts/UI/Views/HoverTooltip.cs` — `FollowMouse()` + `EnsureBuilt()`

**Attempted fix (reverted):**  
เพิ่ม `Canvas rootCanvas` lookup — ไม่ช่วย + ทำให้ drag ยากขึ้น (เพิ่ม GraphicRaycaster)

---

## BUG-2: ZoneHudText แสดงผลทับ Clue Board

**Symptoms:**  
ข้อความ "ชายหาด" (ZoneHudText) แสดงผลทับ title "ผังเบาะแส — Clue Board"  
Clue Board ควรแสดงผลทับ ZoneHudText ไม่ใช่กลับกัน

**Evidence:**  
Screenshot 2026-09-16 15:53 — "ชายหาด" ซ้อนทับ "ผังเบาะแส" ที่ด้านบนจอ

**Root cause (suspected):**  
`ZoneHudView.EnsureZoneText()` สร้าง ZoneHudText เป็น child ของ root Canvas ใน `Start()`  
ซึ่งรันหลัง ClueBoard panel → ZoneHudText เป็น later sibling → render ทับ board  
(Unity UI render order = sibling order ใน hierarchy เดียวกัน)

**Files involved:**  
- `Marooned/Assets/Scripts/UI/Views/ZoneHudView.cs` — `EnsureZoneText()`
- Scene: `SampleScene.unity` — Canvas hierarchy ordering

**Attempted fix (reverted):**  
เพิ่ม Canvas + sortingOrder=10 + GraphicRaycaster บน ClueBoardView — ไม่ช่วย  
+ ทำให้ drag ยากขึ้น (GraphicRaycaster block raycasts ผ่าน board)

---

## Fix approach needed

ทั้งสองบัคเกี่ยวกับ **rendering order** และ **coordinate mapping** ของ Unity UI Canvas:

1. **BUG-1 (tooltip):** ต้อง debug ว่า canvas จริงที่ใช้เป็น overlay หรือ camera mode,  
   และ CanvasScaler reference resolution คือเท่าไร — แล้วจึงเลือก approach ที่ถูก
2. **BUG-2 (Z-order):** แก้ที่ **scene hierarchy** (ย้าย ZoneHudText ไว้ก่อน board ใน Canvas)  
   หรือเพิ่ม Canvas sortingOrder โดย **ไม่เพิ่ม GraphicRaycaster** (กัน block drag)

⚠️ ห้ามเพิ่ม GraphicRaycaster ลง ClueBoardView — จะ block raycasts ทำให้ drag/drop ใช้ไม่ได้
