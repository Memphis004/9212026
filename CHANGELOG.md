# Changelog

## 2026-09-16 — Clue System v2 (f): Drag-Drop Workspace + Libraries + Dedupe + AI Pin Tool

### Added
- **Drag-drop workspace redesign**: Graph area at top (pinned nodes only, custom-positioned via drag), two-row library below (clue groups ×N + NPC portraits). Drag card from library → graph = pin all instances in group; drag node out of graph bounds = unpin. No click behavior remains — details via hover tooltips only.
- **Drag/Scroll arbitration**: `DraggableCardHandler` resolves Pending → Dragging | Scrolling on first significant movement (>10px). Vertical = card pickup (ghost), horizontal = ScrollRect forward. State decided once, never re-evaluated.
- **Hover tooltips**: `HoverTooltip` (per-card enter/exit handler) + `HoverTooltipView` (singleton under root canvas). Clue graph nodes show reliability + locations + witnesses (from M(G) only). NPC graph nodes + library cards show witnessed clue list. Both use `ClueGraphTextFormat.DedupeCounted` for display-only ×N deduplication.
- **ClueGroupUtil**: Groups clue instances by DefId (proxy = DisplayName). Produces `ClueGroup{DefId, DisplayName, Reliability, Instances}` for library rendering.
- **NpcPortraitResolver**: Loads `Resources/Portraits/{npcId}` sprite, falls back to deterministic colored circle from hash.
- **M(G) graph semantics**: Display group G when M(G) = {instances in G that are pinned} ∪ {instances in G witnessed by pinned NPCs} is non-empty. Label shows "×k" when k > 1. NPC N shown when N is pinned or N witnesses a pinned instance. Edge (G,N) shown when a witnessed instance links them and either the instance or NPC is pinned.
- **GraphNodeDragProxy** (replaces `GraphNodeClickProxy`): `IBeginDragHandler`/`IDragHandler`/`IEndDragHandler` only — no click. `Init(RectTransform dropZoneRect)` required at spawn time. Hides tooltip on drag start.
- **GraphDropZone**: `INodeDropHandler` for raycast-based drop detection + `IDropHandler` fallback.
- **CluePinState batch API**: `PinMany(List<string>)` / `UnpinMany(List<string>)` fire single `Changed` event. MaxPinned ceiling + eviction removed.
- **`set_pinned_clue` MCP tool**: Explicit `pinned: true/false` semantics (idempotent, retry-safe). Validates against live `get_clue_graph` node universe before mutation. Main-thread dispatch via `McpMainThreadDispatcher`. Presenter subscribes to `CluePinState.Changed` — one refresh path for player clicks, AI tools, and prunes.
- **`get_pinned_clues` MCP tool**: Returns current pin list with resolved data (clue facts / NPC zone+witnessed). Human-readable via `ClueGraphTextFormat.RenderPinned`. Empty state returns hint line.
- **DedupeCounted**: `ClueGraphTextFormat.DedupeCounted(list)` collapses repeated alibi entries into `<label> @ <location> ×N`. Used by both MCP `RenderPinned` and `ClueBoardView.FormatNpcBody` for parity.
- **Stale pin pruning**: `CluePinState.PruneDead(predicate)` called at start of every `RenderAsync`. Injects a fake id → next render sweeps it.

### Changed
- **MCP bridge descriptions updated**: `SetPinnedClue` and `GetPinnedClue` `[Description]` attributes no longer mention max pins or eviction language.
- **All tests converted to `InjectClueInstance`** (deterministic) instead of `SeedAndCollect` (TryGenerate snapshots witnesses at creation time, subject to ambient tick races producing empty `WitnessNpcIds`).
- **Test A–K rewrite**: All click/popup/pin-row tests deleted. New tests cover drag-drop, scroll arbitration, unpin-outside, M(G) NPC pin expansion, hover tooltips, MCP set_pinned_clue reactivity, node position persistence, regression, description audit, and stale pin pruning.

### Removed
- `GraphNodeClickProxy` (replaced by `GraphNodeDragProxy`)
- All `IPointerClickHandler` / `Clicked` event / `_wasDragged` from view
- Detail popup (popup GameObject, `EnsureDetailPopup`, pin-row/pin-cards/badges)
- `NodeClicked` event from `ClueBoardView`
- `MaxPinned` constant and oldest-first eviction loop from `CluePinState`
- `ClueNodeDetail` / `NpcNodeDetail` view-data classes from presenter
- Static formatters: `FormatClueTitle`, `FormatClueBody`, `FormatNpcTitle`, `FormatNpcBody` from view (replaced by `ClueGraphTextFormat.DedupeCounted`)

### Evidence
- 11/11 PlayMode tests PASS (A–K, evidence 15:51 in `TestEvidence/clue-system-v2-f/`)

---

## 2026-09-15 — Clue System v2 (e): Presentation layer (toggle + polish + clickable nodes + NPC alibi)

### Added
- **In-game clue board graph (MVP Lite)**: `ClueBoardPresenter` (new, plain C# entry point) reuses the existing `GetClueGraphHandler` via MessagePipe interface (no new handler — single source of truth), maps `GraphNode`/`GraphEdge` to new view-layer data contracts (`ClueGraphNodeData`/`ClueGraphEdgeData` — no MessagePack attributes, data separation from wire classes), and pushes renders to `ClueBoardView` on `ClueGeneratedMessage` + initial render. `ClueBoardView` (rewritten from skeleton): radial layout — clue nodes inner ring (radius 150), surviving witness NPC nodes outer ring (radius 300, linked ones only), edges drawn as thin rotated `Image` lines; circle sprite generated locally (`WorldItemSystem.CreateCircleSprite` is private — copied per spec). DI: `RegisterComponentInHierarchy<ClueBoardView>` + `RegisterEntryPoint<ClueBoardPresenter>` (handler registration untouched); scene: `ClueBoardPanel` (with `ClueBoardView`) added under Canvas in SampleScene.
- **Shared text renderer**: `ClueGraphTextFormat.Render(...)` — one human-readable line per collected clue (`<label> — seen near: <witnesses>`), used by both the MCP tool and the Unity view so the AI reads exactly what the player sees.

### Added (toggle + visual polish, later the same day)
- **Tab toggles the clue board**: `ClueBoardPresenter` polls `Input.GetKeyDown(KeyCode.Tab)` in a UniTask PlayerLoop (`ToggleLoopAsync`, cancelled via CTS on `Dispose`) and calls `TogglePanel()` — polled in the presenter by design because an inactive panel never runs `View.Update()`, so view-side polling could never re-open it. Panel starts hidden (`m_IsActive: 0` in SampleScene); opening re-renders fresh data. Graph root auto-parents under the panel itself so `SetActive` hides everything.
- **HUD toggle button (mouse-only Tab alternative)**: `ClueBoardView.EnsureToggleButton()` creates `ClueBoardToggleButton` on the **Canvas** (sibling of the panel, not a child) so it stays clickable while the board is hidden; fires `ToggleButtonPressed` → presenter binds it to `TogglePanel` (View passive, MVP Lite). Idempotent — created once from `Presenter.Initialize`.
- **Clickable nodes with detail popup (clue + NPC witness)**: every node gets `GraphNodeClickProxy` (`IPointerClickHandler`, `CardSlotUI` pattern — view forwards only the id, presenter decides) and `raycastTarget=true`. Clue nodes resolve details via the **existing `GetClueBoardHandler`** — reliability/location/privacy-filtered witnesses are the exact shape `get_clue_board` MCP returns (single source of truth, no new filter logic). **NPC witness nodes** show the NPC's current zone (`NpcState.CurrentLocationId` — player-visible, the chibi visibly walks there), alive status, and a loose alibi: the collected clues this NPC witnessed (`<label> @ <location>` from filtered board entries — asserting they saw X at Y implicitly places them there, per the GDD's anticipated "ระบบ alibi กลาง ๆ ให้ query", game_design_doc line 393). No ground truth leaks (killer role/agenda/cooldown never rendered; Test F guards on the word "killer"). Clicking the open node again closes the popup; selecting a different node switches content; the selected node highlights with per-type selected colors and original-color restore. Popup refreshes across re-renders (re-resolving by node type) and auto-closes if its node disappears from the graph; board close (`Tab`/button) hides it too.
- **Pin workspace (compare multiple nodes side by side)**: the detail popup gains a **[ปักหมุด]** button and pinned cards get a **×** unpin button — both fire the same `PinRequested` event; the presenter owns an ordered pin list (max 3, oldest dropped automatically). Pinned cards resolve fresh data every render through the same board handler / `NpcDirectorSystem` as the popup (no stale snapshots), pins of nodes that vanish from the graph are withdrawn automatically (`HasNode` guard), pinned nodes wear a gold dot badge, and cards lay out side by side at the bottom center (raycast-blocking so clicks don't pass through to nodes underneath).
- **New MCP tool `get_pinned_clues`** — the AI VTuber can read exactly what the player is comparing on the clue board. `CluePinState` (new singleton) is the single source of truth of the ordered pin list: the presenter writes through `Toggle`, the new `GetPinnedCluesHandler` reads it and resolves each pinned node through the **existing** `GetClueBoardHandler` (same privacy filter — dead NPCs and the perpetrator never appear) + `NpcDirectorSystem` (zone from `NpcState.CurrentLocationId`, loose alibi = witnessed collected clues). Response = `PinnedNodeEntry` list (Shared message, ordered oldest-first; per-entry `type` "clue"/"npc" with clue facts or zone/alive/witnessed list; no ground-truth fields). Bridge tool returns `ClueGraphTextFormat.RenderPinned` human-readable summary; empty state returns a hint line. **Pins persist across board close/reopen within the session** (state lives in the singleton, not the panel — closing only deactivates the panel; reopening redraws cards from the same pin list). Session-scoped by design (cleared on round reset, not serialized across play sessions).
- **Edge glow (witnessed links)**: each edge is one root with two child layers — a soft gold radial-gradient beam (`CreateEdgeGlowSprite`, quadratic falloff) pulsing slowly in `Update()` over a bright rounded core. One root per edge keeps Test B counting intact.
- **Board chrome**: idempotent dark backdrop + Thai title + `[Tab]` hint + 3-row legend (clue/witness/edge), all `raycastTarget=false` (display-only, never blocks card/world clicks).

### Changed
- **`get_clue_graph` MCP tool output is now human-readable** (was debug string `nodes: [...]/edges: [...]`): returns `Clue Graph — N clue(s), M witness link(s):` header + one line per clue with witness labels. No handler/message/shape changes.
- Test B: `GetComponentInParent<Canvas>()` → `GetComponentInParent<Canvas>(true)` — the panel now starts inactive and Unity's default lookup excludes inactive ancestors.
