# Clue System v2 (f) — Drag-Drop Workspace + Libraries

วันที่: 2026-09-16
สถานะ: **implemented + PlayMode evidence ผ่านครบ 11/11** (A–K — TestEvidence/clue-system-v2-f/)

UI redesign: Graph workspace (drag-drop pin/unpin) + 2-row library (clue groups ×N + NPC portraits) + hover tooltips. Click/popup/pin-row ถูกลบหมด.

บทความนี้ต่อจาก [[clue-system-v2]] (backend parts a–d) — รอบนี้คือ presentation layer
เท่านั้น **ไม่มี handler/message ใหม่** ทุกอย่าง reuse จากของเดิม

## ขอบเขต (Reuse-only constraints)

| ห้ามแตะ | เหตุผล |
|---|---|
| `GetClueGraphHandler` | มีอยู่แล้ว — filter witness (รวม `player_local`) เกิดที่นี่ผ่าน `GetClueBoardHandler` แล้ว |
| `GraphNode` / `GraphEdge` | field จริง: `node.Label`, `edge.From`/`edge.To` (ไม่ใช่ DisplayName/SourceId/TargetId) |
| `RegisterAsyncRequestHandler<...>` ใน GameLifetimeScope | register แล้ว (พร้อม options) — เพิ่มซ้ำ = build fail |

## สถาปัตยกรรม (MVP Lite)

```
ClueGeneratedMessage (MessagePipe bus)
        │
        ▼
ClueBoardPresenter (plain C#, IInitializable/IDisposable — RegisterEntryPoint)
   │  subscribe → RenderAsync().Forget()
   │  เรียก IAsyncRequestHandler<GetClueGraphRequest, GetClueGraphResponse>  ← reuse handler เดิม
   │  map: GraphNode(Id/Type/Label) → ClueGraphNodeData
   │        GraphEdge(From/To)      → ClueGraphEdgeData(FromClueId/ToNpcId)
   ▼
ClueBoardView (passive MonoBehaviour บน ClueBoardPanel ใต้ Canvas — SampleScene)
   RenderGraph(nodes, edges):
     1. ClearAll() — destroy children เก่า
     2. clue nodes วงใน radius 150 / npc วงนอก radius 300 (เฉพาะตัวที่มี edge)
     3. เส้นเชื่อม = Image บางๆ (กว้าง 4) หมุน RectTransform ให้ชี้ clue → npc
        + circle sprite สร้างจาก Texture2D ใน code (copy จาก WorldItemSystem
          CreateCircleSprite ที่เป็น private — ตาม spec)
```

### Data Separation
- `ClueGraphNodeData { Id, Type, Label }` / `ClueGraphEdgeData { FromClueId, ToNpcId, Relation="witnessed" }`
  — **ไม่ใส่ [MessagePackObject]** — เป็น view-layer contract ล้วน (อยู่ในไฟล์ ClueBoardView.cs)
- Presenter เป็นคนกลางเดียวที่แปลง wire shape ↔ view shape

### DI (GameLifetimeScope — เพิ่ม 2 บรรทัดเท่านั้น)
```csharp
builder.RegisterComponentInHierarchy<ClueBoardView>();
builder.RegisterEntryPoint<ClueBoardPresenter>(Lifetime.Singleton).AsSelf();
```
(pattern เดียวกับ CardHandPresenter — `Lifetime.Singleton` กัน presenter ซ้ำจาก
RegisterEntryPoint ที่ default transient, `.AsSelf()` เพื่อให้ test resolve ตรง ๆ)

## MCP get_clue_graph — output ใหม่ (human-readable)

`McpBridge/Program.cs` → `GetClueGraph()` เดิมคืน debug string (`nodes: [...]/edges: [...]`)
→ เปลี่ยนไปเรียก `ClueGraphTextFormat.Render(res)` (shared ไฟล์เดียว ใช้ทั้ง bridge และ
Unity ทำให้ AI อ่าน format เดียวกับที่ผู้เล่นเห็น):

```
Clue Graph — 2 clue(s), 3 witness link(s):
  รอยเลือด — seen near: npc_01, npc_02
  รอยเท้า — seen near: no one observed
```

- กราฟว่าง → `"No clues collected yet (empty graph)."`
- witness label = id ที่ผ่าน filter แล้ว (killer/NPC ตายไม่มีวันโผล่ — หลัก information
  hiding เดิมของ response layer ไม่เปลี่ยน)

## Toggle กระดาน (Tab) — ออกแบบไว้ที่ Presenter

**ห้าม poll ปุ่มใน View** — ตอน panel `SetActive(false)` `View.Update()` ไม่รันเลย
Tab จะ "เปิดกลับ" ไม่ได้ตลอดชาติ → ฝั่ง presenter poll ผ่าน UniTask PlayerLoop
(`ToggleLoopAsync` — plain C# ไม่มี MonoBehaviour.Update, ยกเลิกผ่าน
CancellationTokenSource ใน Dispose):

```csharp
// ClueBoardPresenter
if (Input.GetKeyDown(KeyCode.Tab)) TogglePanel();
// TogglePanel: opening = !view.activeSelf → SetActive(opening) + ถ้าเปิด → RenderAsync() ให้ข้อมูลสด
```

- panel **เริ่มซ่อน** (`m_IsActive: 0` ใน SampleScene — detective board UX)
- container ของกราฟถูก auto-create **ใต้ตัว panel** (ไม่ใช่ Canvas ตรง ๆ) เพื่อให้
  toggle ซ่อนครบทั้งกราฟ — panel full-stretch ใต้ Canvas จึงพิกัดเท่าเดิม
- ค้นพบตอน verify: VContainer `FindComponentProvider` ใช้
  `GetComponentInChildren(type, includeInactive: true)` → register บน inactive GO ได้สบาย
  แต่ **test ที่ assert `GetComponentInParent<Canvas>()` ต้องส่ง `includeInactive: true`**
  (Unity 6 overload) ไม่งั้น null ตอน panel ซ่อน
- input: project ใช้ `activeInputHandler: 2` (Both) — `Input.GetKeyDown` ปลอดภัย
  (pattern เดียวกับ NpcDebugOverlay/PlayerInputService)

## Visual Polish (edge glow + board chrome)

- **Edge glow 2 ชั้นต่อ 1 edge root** (`ClueEdge/Glow` + `ClueEdge/Core`) — glow =
  radial-gradient sprite (quadratic falloff, สร้างจาก Texture2D) ยืดเป็น beam ทองนุ่ม
  ครอบ core สว่างหัวท้ายมน, alpha เต้นช้า ๆ ใน `Update()` (cosmetic, หยุดเองตอน panel
  ซ่อน) — **จำนวน GameObject ต่อ edge ยังเป็น 1 root** ทำให้ Test B นับเหมือนเดิม
- **Board chrome** (idempotent ใน `EnsureBackground`): backdrop เข้มโปร่ง (0.12,0.10,0.09,
  a=0.92) + หัวเรื่อง "ผังเบาะแส" + hint `[Tab]` + legend 3 แถว (clue/witness/edge) —
  ทุกอย่าง `raycastTarget=false` กระดาน display-only ไม่บังคลิกการ์ด/โลก
- ⚠️ บั๊กย่อยที่จับตอน self-review: `CreateLayerRect` ใส่ `offsetMax = (-spread,-spread)`
  (ควรเป็น `+spread`) — glow จะหดเท่า core และเลื่อนลงซ้าย ไม่ใช่แผ่ทับ — แก้ก่อน verify

## HUD Toggle Button + Clickable Clue Nodes (รายละเอียดเบาะแส)

**ปุ่ม HUD (ทางเลือกของ Tab สำหรับผู้เล่นเมาส์)** — `ClueBoardView.EnsureToggleButton()`
สร้าง `ClueBoardToggleButton` บน **Canvas (พ่อของ panel)** ไม่ใช่บน panel เพราะตอน
board ซ่อน ปุ่มต้องยังกดได้ (ตรงข้ามกับ graph root ที่ต้องอยู่ใต้ panel เพื่อถูกซ่อนตาม)
กดปุ่ม → event `ToggleButtonPressed` → presenter ผูกกับ `TogglePanel` (MVP Lite —
View passive ยิง event อย่างเดียว) — สร้าง idempotent จาก `Presenter.Initialize`

**คลิก node → popup รายละเอียด** (ทุก node คลิกได้ — clue และ npc):

```
GraphNodeClickProxy (IPointerClickHandler — pattern เดียวกับ CardSlotUI)
   └─ Clicked(nodeId) → ClueBoardView.NodeClicked (event — View passive)
        └─ ClueBoardPresenter.OnNodeClicked
             ├─ คลิกซ้ำ node เดิม → HideNodeDetail() (toggle ปิด)
             └─ ShowDetailAsync(nodeId) แยก type:
                  ├─ id มีใน board entries → ClueNodeDetail (GetClueBoardHandler reuse)
                  │    → View.ShowClueDetail: reliability/location/witnesses
                  └─ ไม่มี → NpcDirectorSystem.Npcs[nodeId] → NpcNodeDetail
                       → View.ShowNpcDetail: โซน + อาลิไบคร่าว ๆ
```

- **ทำไม GetClueBoardHandler**: popup ต้องโชว์ reliability/location/witnesses ซึ่ง
  `GetClueGraphResponse` ไม่มี — reuse board handler แปลว่า popup ได้ข้อมูล shape เดียวกับ
  MCP `get_clue_board` และอยู่ใต้ witness filter เดียวกัน (single source of truth —
  ห้ามเขียน filter ใหม่ ตรงตามข้อ 2 ของ architecture constraints)
- **อาลิไบของ npc witness (แนวคิด GDD "ระบบ alibi กลาง ๆ ให้ query" — game_design_doc
  line 393)**: ยังไม่มีระบบ alibi จริง จึง derive จากข้อมูลที่ player เห็นได้อยู่แล้ว —
  โซน = `NpcState.CurrentLocationId` (chibi เดินให้เห็นจริง) + "พยานให้เบาะแสที่เก็บได้"
  = entries ที่คนนี้อยู่ใน `WitnessNpcIds` (ผ่าน filter แล้ว) — การยืนยันว่าเห็น X @ Y
  = ยอมรับโดยปริยายว่าตัวเองอยู่แถวนั้นตอนนั้น ⚠️ ห้ามโชว์ Role/HiddenAgenda/
  KillCooldown — ground truth ของ killer ต้องไม่หลุด popup (Test F มี guard ตรวจ)
- popup เป็น sibling ท้ายสุดของ panel (วาดทับ node ทุกตัว) + `raycastTarget=true`
  บังคลิกทะลุ; node ที่เลือก highlight — **จำสีเดิมของแต่ละ node ไว้ restore ถูกตัว**
  (บั๊กเดิม: `HideClueDetail` restore เป็นสี clue เสมอ → npc ที่เคยถูกเลือกจะกลายเป็นสีแดง)
- ข้าม re-render: popup เปิดค้าง → refresh ตาม type ใหม่ทุก render; node หายจากกราฟ
  → ปิด popup เอง (`RenderGraph` ตรวจ `_detailNodeId`)
- asmdef ของ tests ต้องเพิ่ม reference `UnityEngine.UI` (Button/ExecuteEvents ใน D/E)
- หมายเหตุ UI (รอ polish): ถ้าคนเดียวเป็นพยานหลาย instance ชื่อเดียวกัน ปุจฉาการแสดง
  บรรทัดซ้ำ — พิจารณาทำ "ชื่อ @ โซน ×N" (dedup เฉพาะ display ไม่ยุบ data)

### ผลรันล่าสุด (09:23) — A–I 9/9 PASS
- **D** ✅: คลิก clue ผ่าน `ExecuteEvents.Execute` (path เดียวกับเกม) → popup โชว์
  reliability/location/witnesses ตรงกับ `get_clue_board` เป๊ะ (พิสูจน์ reuse chain)
  + คลิกซ้ำปิด + ทุก node (รวม npc) มี click proxy
- **E** ✅: ปุ่มอยู่บน Canvas (sibling panel) — กระดานซ่อนปุ่มยัง active; กดเปิด/กดปิด
- **F** ✅: คลิก npc witness node → popup `npc_02 | โซน: beach • มีชีวิต / พยานให้เบาะแสที่เก็บได้:
  คราบเลือด @ beach …` + guard ตรวจว่าไม่มีคำว่า killer (ground truth) หลุดมา
- **G** ✅: pin clue + npc_02 → การ์ด 2 ใบเรียง side-by-side; pin ตัวที่ 4 → ตัวเก่าสุดหลุด
  เหลือ 3; กด × บนการ์ด → ถอนหมุดเหลือ 2 (บั๊ก unpin ไม่ render ซ้ำ ถูกเทสรอบก่อนจับแล้วแก้)
- **H** ✅: pin ผ่าน popup → CluePinState → `GetPinnedCluesHandler` คืนข้อมูลตรง
  (ลำดับ pin, clue = DisplayName/Reliability ผ่าน board handler เดิม, npc = โซน + อาลิไบ)
  + pin รอดจากปิด/เปิดกระดาน (การ์ด 2 ใบวาดใหม่จาก state เดิม)
  + bridge round-trip จริง: `get_pinned_clues` ผ่าน stdio MCP คืน state เดียวกับ UI
- **I** ✅: AI pin ผ่าน `set_pinned_clue` (in-process + bridge round-trip พร้อม args) →
  idempotent (pin ซ้ำ/unpin ซ้ำ = no change), validation ปัด `unknown_node`/`missing_node_id`,
  pin ของ AI ขึ้นบน UI ทันที (Changed → RefreshPinnedAsync), dedupe ×N ถูกต้อง
  (5 raw → 2 displayed, ผลรวม ×N == จำนวนจริง)
- หมายเหตุ: รอบก่อนหน้าหนึ่งเจอ NRE storm จาก Spine `ActivateBasedOnFlipDirection`
  (third-party) ระหว่าง test-runner session — ไม่เกี่ยวกับโค้ด clue board (ไม่มี scene/
  prefab อ้าง component นี้, manual Play สะอาด, โค้ดเราไม่แตะ Spine) ถ้าเจอซ้ำให้แยก
  หาสาเหตุที่ visual backend ก่อน อย่าเพิ่งสรุปว่าเป็นของ UI ใหม่

## ปักหมุดหลาย node เทียบกัน (Pin Workspace)

- ปุ่ม **[ปักหมุด]** ใน popup และปุ่ม **×** บนการ์ด pin ยิง event เดียวกัน (`PinRequested`)   — pin list อยู่ฝั่ง presenter (ordered, ไม่มีเพดาน) ตาม MVP Lite
- การ์ด pin resolve ข้อมูลสดทุกครั้งผ่าน board handler / NpcDirectorSystem เหมือน popup
  — ไม่มี snapshot stale; pin ของ node ที่หายจากกราฟถูกถอนอัตโนมัติก่อนวาด (`HasNode` guard)
- node ที่ถูก pin มี badge จุดทอง; การ์ดวางเรียงกันล่างกลาง (side-by-side) กันคลิกทะลุ
- **บั๊กที่เทส G จับได้**: ฝั่ง unpin ของ `TogglePin` เดิม `Remove()` สำเร็จแล้ว `return`
  ทันทีโดยไม่ render ซ้ำ → การ์ดค้างบนจอจนมี render อื่นมาแทน (เทสเจอ: กด × แล้ว
  นับการ์ดได้ 3 แทน 2) — แก้โดยเรียก `RefreshPinnedAsync()` ก่อน return
- **บั๊ก UI จากรอบรันก่อนหน้า**: `MissingReferenceException` จาก `TextMeshProUGUI`
  ถูก destroy แบบ deferred ขณะยังลงทะเบียนกับ canvas — แก้โดย `SetActive(false)` ก่อน
  `Destroy` ทุกจุดใน `ClearAll`/`RenderPinned` (บังคับ `OnDisable` → unregister ทันที)

## get_pinned_clues (MCP) + CluePinState singleton + pin persistence

- **CluePinState** (singleton, `Register<CluePinState>(Lifetime.Singleton)`): single source   of truth ของ ordered pin list — presenter เขียนผ่าน `Toggle`/`PinMany`/`UnpinMany`,
   handler อ่าน `PinnedNodeIds`; มี `PruneDead` (ถอน pin ของ node ที่หายจากกราฟ) +
   `Clear` (round reset); ไม่มีเพดาน (workspace semantics)
- **Persistence across close/reopen**: pin list อยู่ใน singleton (ไม่ใช่ field ของ panel) —
  ปิดกระดาน (Tab/ปุ่ม) แค่ `SetActive(false)` + ซ่อน popup, เปิดใหม่ → `RenderAsync` →
  `RefreshPinnedAsync` วาดการ์ดจาก pin list เดิมให้เอง (Test H ยืนยัน)
- **GetPinnedCluesHandler** (ใหม่, ใน McpRequestHandlers.cs): NodeId ว่าง = ทั้งหมดตาม
  ลำดับ pin (MCP), ใส่ id = node เดียว (reuse path สำหรับ popup) — clue ผ่าน
  GetClueBoardHandler เดิม (filter/resolve เกิดที่เดียว), npc ผ่าน NpcDirectorSystem
- **get_pinned_clues bridge tool** (ใหม่, SurvivalQueryTools): คืน
  `ClueGraphTextFormat.RenderPinned` — format:
  ```
  Pinned Clues — 2 node(s) pinned (oldest first):
    [clue] คราบเลือด (Strong) @ beach — seen near: npc_02, player_local
    [npc] npc_04 | zone=beach • alive | witnessed: คราบเลือด @ beach; รอยขีดข่วน @ beach
  ```
- ⚠️ Information Hiding: response เป็นเนื้อหาเดียวกับ popup บนกระดาน — ไม่มี
  SourceActorId/killer Role หลุด (Test H guard ด้วยคำว่า killer เหมือน F)
- ข้อจำกัดตั้งใจ: pin เป็น **session state** ฝั่ง Unity (จงใจไม่ persist ข้าม play session —
  round reset = ล้าง) — ถ้าอนาคตต้องเก็บข้าม session ให้ serialize CluePinState ตอน OnDestroy

## set_pinned_clue (AI pin/unpin เองได้) + รวม display ซ้ำ ×N

- **SetPinnedClueHandler** (ใหม่): explicit set semantics (`Pinned = true/false`) ไม่ใช่
  toggle — idempotent ปลอดภัยกับ retry (pin ซ้ำ/unpin ที่ไม่ได้ pin = Success + no change,
  FailureReason เชิงข้อมูล `already_pinned`/`not_pinned`)
- ⚠️ **Validation**: NodeId ต้องเป็น node ในกราฟจริง (ผ่าน GetClueGraphHandler — universe
  เดียวกับที่ board วาดหลัง filter) — pin id ปลอม/หมดอายุถูกปัด (`unknown_node`,
  `missing_node_id`) ก่อนแตะ state
- ⚠️ **Main-thread mutate** ผ่าน McpMainThreadDispatcher เหมือน mutating handlers อื่น —
  presenter subscribe `CluePinState.Changed` แล้ว render แถว pin ใหม่เสมอ → pin ของ AI
  ขึ้นบนกระดานทันทีเหมือน player คลิก (source เดียวสำหรับทุกทางเข้า; `TogglePin` ของ
  presenter เหลือแค่ mutate state อย่างเดียว)
- Response แนบ `PinnedNodeIds` ล่าสุดทุกครั้ง — AI แก้ model ได้ใน call เดียว ไม่ต้อง
  ตามด้วย get_pinned_clues; bridge tool คืน human-readable
  `Pinned <id>. Current pins (oldest first): [...]`
- **Dedupe ×N (display-only)**: `ClueGraphTextFormat.DedupeCounted` รวมเบาะแสชื่อซ้ำ
  เป็น `คราบเลือด @ beach ×4` — ใช้ทั้ง `RenderPinned` (MCP) และ `FormatNpcBody`
  (popup/การ์ด pin) เพื่อให้ AI กับ player เห็นรูปเดียวกัน; ตัวเลข N = จำนวน clue
  instance จริง (ข้อมูลดิบใน WitnessedClues ไม่ถูกแตะ), เรียงตาม first-occurrence,
  ผลรวมของ ×N ต้องเท่าจำนวนจริงเสมอ (Test I assert)

`SampleScene.unity`: GameObject `ClueBoardPanel` (Layer UI, RectTransform stretch เต็มจอ)
ใต้ `Canvas` พร้อม component `ClueBoardView` (guid f6a494d31f23d66428586e2329fb396a) —
`clueBoardContainer` ปล่อยว่างได้ (View auto-create `ClueBoardGraphRoot` ใต้ Canvas เอง
กลางจอ — pattern เดียวกับ CardHandView.EnsureFeedbackText)

## Runtime Tests (ClueBoardUiPlayModeTests — TestEvidence/clue-system-v2-e/)

ผลรันจริง 2026-09-15 (ผ่าน Unity MCP tests-run, PlayMode, class filter ClueBoardUiPlayModeTests)
— **3/3 PASS**:

- **A** ✅ (1.77s): bridge stdio จริง → tools/call get_clue_graph คืน text summary —
  evidence: `Clue Graph — 1 clue(s), 6 witness link(s): คราบเลือด — seen near:
  npc_01..npc_05, player_local` (พิสูจน์ player_local ผ่าน filter ถูกต้อง + ไม่มี debug prefix)
- **B** ✅ (0.05s): 3 clue nodes @ r=150, 6 npc nodes @ r=300, 18 edges ครบ —
  ตรวจจาก RectTransform จริง
- **C** ✅ (0.03s): nodes 9 → 10 หลัง ClueGeneratedMessage — reactivity พิสูจน์แล้ว
- **Visual** ✅ (Game view screenshot — `board-game-view.png` + `visual-seed-render.txt`,
  รันใหม่ 05:39 หลังใส่ toggle+polish): runner เมนู `Marooned/Clue Board/Seed Clues + Render`
  (เปิด panel เองก่อน render เพราะเริ่มซ่อน) → pixel-scan สีจริงใน screenshot ที่ scale
  0.426 (viewport 646×582, CanvasScaler match=0.5 → geometric mean — ไม่ใช่ width ratio!):
  - clue nodes (แดงอิฐ) พบ 41–86px (คาด 64±23 = 40.6–87.4) ✅
  - npc nodes (น้ำเงิน) พบ 111–143px (คาด 128±17 = 111–145) ✅
  - gold glow พบ 256px ตามแนวเส้นเชื่อม + backdrop เข้ม 98.2% ✅
  - บทเรียน pixel-scan: legend มุมซ้ายล่างใช้สีเดียวกับ node → ต้อง exclude มุมนั้น
    ไม่งั้น outlier ที่รัศมี 397–415px ปนมา

### Bug ที่ Test B จับได้ (แก้แล้ว)

`ClearAll()` เดิมใช้ `Destroy()` อย่างเดียว — Destroy เป็น **deferred to end-of-frame**
พอ re-render 2 ครั้งในเฟรมเดียว (message-triggered render ชน explicit render) เด็กเก่า
ยังค้างใน container → นับ edge ได้ 36 จาก 18 (ซ้ำเป๊ะ 2 เท่า) — แก้โดย `SetParent(null)`
(detach ทันที) ก่อน Destroy ท้ายเฟรม — บทเรียน: อย่านับ children หลัง Destroy ในเฟรมเดียวกัน

⚠️ หมายเหตุ timing: `ClueGeneratedMessage` ยิงตอน **generate** ไม่ใช่ตอน collect —
board แสดงเฉพาะ clue ที่เก็บแล้ว จึงต้อง collect ก่อนแล้วให้ message ตัวใหม่ trigger
render ที่ "เห็น" clue เดิม (ออกแบบ test C ตามนี้ — assert strict ก่อน message ใหม่
ไม่ได้ เพราะ ambient kill จาก AI อาจยิง message ระหว่างทาง)

## ไฟล์ที่แตะต้อง

| ไฟล์ | การเปลี่ยนแปลง |
|---|---|
| `Shared/ClueGraphTextFormat.cs` | ใหม่ (formatter ร่วม) |
| `McpBridge/Program.cs` | แก้ GetClueGraph() เรียก formatter |
| `Marooned/Assets/Scripts/UI/Views/ClueBoardView.cs` | rewrite จาก skeleton |
| `Marooned/Assets/Scripts/UI/Presenters/ClueBoardPresenter.cs` | ใหม่ |
| `Marooned/Assets/Scripts/Core/GameLifetimeScope.cs` | +2 บรรทัด (View/Presenter) |
| `Marooned/Assets/Scenes/SampleScene.unity` | +ClueBoardPanel ใต้ Canvas |
| `Marooned/Assets/Tests/Runtime/ClueBoardUiPlayModeTests.cs` | ใหม่ (+`GetComponentInParent<Canvas>(true)`, +Tests D/E) |
| `Marooned/Assets/Tests/Runtime/Marooned.Tests.Runtime.asmdef` | +reference UnityEngine.UI |
| `Marooned/Assets/Editor/ClueBoardVisualRunner.cs` | ใหม่ (เปิด panel ก่อน render ตอน seed) |
