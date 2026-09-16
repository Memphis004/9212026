using System;
using System.Collections.Generic;
using Marooned.Systems;
using Marooned.Shared; // ClueGraphTextFormat.DedupeCounted (display-only ×N dedupe)
using UnityEngine.EventSystems;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Marooned.UI.Views
{
    /// <summary>
    /// View-layer node data — แยกจาก MessagePack classes (GraphNode) โดยเจตนา
    /// (Data Separation) — ไม่มี [MessagePackObject]
    /// (f) redesign: node key = instance id แทน / npc id / "group:{DisplayName}"
    /// (clue วาดเป็นตัวแทนกลุ่ม — 1 node ต่อกลุ่ม DefId)
    /// </summary>
    public class ClueGraphNodeData
    {
        public string Id;      // node key (ดูข้างบน)
        public string Type;    // "clue" หรือ "npc"
        public string Label;   // Text ที่จะแสดงบน Node
    }

    /// <summary>View-layer edge data — (f): เชื่อมกลุ่ม clue → npc (dedupe คู่ (G,N) แล้ว)</summary>
    public class ClueGraphEdgeData
    {
        public string FromClueKey;  // "group:{DisplayName}"
        public string ToNpcId;
        public string Relation = "witnessed";
    }

    /// <summary>View-layer การ์ดคลัง 1 ใบ — นิยามใน NpcPortraitResolver.cs (ClueLibraryCardData)
    /// — ไม่มี [MessagePackObject] (Data Separation)</summary>

    /// <summary>
    /// Detective drag-drop workspace (Clue System v2 (f) — FULL REDESIGN):
    /// บน: GraphArea = เฉพาะ node ที่ pin (ลากจัดตำแหน่ง, ลากออกนอกกรอบ = unpin)
    /// ล่าง: ScrollView แนวนอน 2 แถว = คลังการ์ดเบาะแส (group ×N) + คลัง NPC (portrait)
    /// รายละเอียดทั้งหมด = hover tooltip กลางตัวเดียว (ไม่มี popup/click เหลืออยู่)
    ///
    /// MVP Lite: passive — Presenter เรียก RenderGraph/RenderClueLibrary/RenderNpcLibrary
    /// (ไม่รู้จัก handler / MessagePack — รับแต่ view data)
    ///
    /// ⚠️ Sprite: CreateCircleSprite ของ WorldItemSystem เป็น private — copy logic
    /// สร้าง Texture2D วงกลมมาเป็น private method ของคลาสนี้ (spec Step 4 เดิม)
    /// </summary>
    public class ClueBoardView : MonoBehaviour
    {
        [SerializeField] private Transform clueBoardContainer; // root ของ graph (auto-create ถ้าไม่ผูก)

        private readonly Dictionary<string, RectTransform> _nodeRects = new(); // node key → rect
        private readonly List<GameObject> _spawned = new();                    // nodes+edges (clear ตอน re-render)
        private readonly List<Image> _edgeGlowImages = new();                  // glow layer ของแต่ละ edge (pulse ใน Update)
        private readonly List<GameObject> _libraryCards = new();               // การ์ดคลังทั้ง 2 แถว

        private Sprite _nodeSprite;
        private Sprite _edgeGlowSprite;
        private TMP_FontAsset _labelFont;
        private Transform _container;
        private bool _backgroundReady;
        private bool _libraryReady;
        private bool _tooltipReady;

        // ---- MVP Lite events — View ส่งต่อ key/id เท่านั้น, Presenter ตัดสินใจ ----
        /// <summary>การ์ดคลังถูกลากมาปล่อยในกราฟ → pin ทุก instance ในกลุ่ม</summary>
        public event Action<List<string>> LibraryCardDropped;
        /// <summary>node ถูกลากออกนอกกรอบกราฟ → unpin (กลุ่ม ถ้า clue)</summary>
        public event Action<string> GraphNodeUnpinRequested;
        /// <summary>hover เข้า การ์ดคลัง/node → Presenter ประกอบเนื้อหา tooltip (key, kind)</summary>
        public event Action<string, string> NodeHoverEnter;
        /// <summary>hover ออก → ซ่อน tooltip</summary>
        public event Action<string, string> NodeHoverExit;
        /// <summary>MVP Lite: ปุ่ม HUD (มุมจอ) ถูกกด → Presenter เป็นคน TogglePanel</summary>
        public event Action ToggleButtonPressed;

        private bool _toggleButtonReady; // HUD button สร้างแล้ว (idempotent)
        private HoverTooltipView _tooltip;
        private GraphDropZone _dropZone;

        // ---- Layout constants ----
        private const float GraphTopInset = 90f;        // พื้นที่กราฟ (บน) — เว้นหัวเรื่อง
        private const float LibraryRowHeight = 130f;    // แถวคลัง (ล่าง)
        private const float LibraryBottomOffset = 64f;
        private const float GroupNodeSize = 110f;
        private const float NpcNodeSize = 80f;
        private const float DefaultSpreadX = 420f;      // default radial ในกราฟ (ก่อนผู้เล่นลากเอง)
        private const float DefaultSpreadY = 130f;
        private const float EdgeThickness = 4f;
        private const float EdgeGlowSpread = 10f;
        private const float GlowPulseSpeed = 2.2f;
        private const float GlowPulseAmplitude = 0.15f;
        private static readonly Color ClueNodeColor = new Color(0.85f, 0.35f, 0.30f);
        private static readonly Color NpcNodeColor = new Color(0.35f, 0.60f, 0.85f);
        private static readonly Color EdgeCoreColor = new Color(1f, 0.95f, 0.78f, 0.95f);
        private static readonly Color EdgeGlowColor = new Color(1f, 0.82f, 0.35f, 0.35f);
        private static readonly Color BoardColor = new Color(0.12f, 0.10f, 0.09f, 0.92f);
        private static readonly Color TitleColor = new Color(0.92f, 0.88f, 0.80f, 1f);
        private static readonly Color CardBgColor = new Color(0.16f, 0.14f, 0.12f, 0.95f);
        private static readonly Color LibraryBgColor = new Color(0.10f, 0.09f, 0.08f, 0.85f);

        /// <summary>node key → ตำแหน่งที่ผู้เล่นลากไว้ (คงอยู่ข้าม re-render — Test H)</summary>
        private readonly Dictionary<string, Vector2> _customPositions = new();

        /// <summary>จำนวน node ที่ render ล่าสุด (runtime tests)</summary>
        public int RenderedNodeCount => _nodeRects.Count;

        /// <summary>จำนวน edge ที่ render ล่าสุด (runtime tests)</summary>
        public int RenderedEdgeCount { get; private set; }

        /// <summary>container ที่ใช้จริง (runtime tests)</summary>
        public Transform ContainerForTests => _container;

        /// <summary>dropzone ของ GraphArea (runtime tests — simulate ลากออกนอกกรอบ)</summary>
        public RectTransform DropZoneRectForTests => _dropZoneRect;
        private RectTransform _dropZoneRect;

        /// <summary>การ์ดคลัง clue ทั้งหมด (runtime tests)</summary>
        public IReadOnlyList<GameObject> ClueLibraryCardsForTests => _clueLibraryCards;
        private readonly List<GameObject> _clueLibraryCards = new();

        /// <summary>การ์ดคลัง NPC ทั้งหมด (runtime tests)</summary>
        public IReadOnlyList<GameObject> NpcLibraryCardsForTests => _npcLibraryCards;
        private readonly List<GameObject> _npcLibraryCards = new();

        /// <summary>node นี้อยู่ในกราฟที่ render ล่าสุดไหม (presenter ใช้ประกอบ tooltip ฯลฯ)</summary>
        public bool HasNode(string nodeKey) => _nodeRects.ContainsKey(nodeKey);

        /// <summary>ข้อความ tooltip กลาง (runtime tests — assert เนื้อหา; string เพื่อไม่ผูก test asmdef กับ TMPro)</summary>
        public string TooltipTextForTests => _tooltip != null ? _tooltip.GetTextForTests() : null;
        private TMP_Text _tooltipText;

        /// <summary>scroll content ของแถวคลัง (runtime tests — Test C ปัดเลื่อน)</summary>
        public RectTransform ClueScrollContentForTests => _clueScrollContent;
        private RectTransform _clueScrollContent;
        public RectTransform NpcScrollContentForTests => _npcScrollContent;
        private RectTransform _npcScrollContent;

        /// <summary>การ์ดคลังจาก key (runtime tests — simulate drag/hover)</summary>
        public GameObject FindLibraryCardForTests(string key)
        {
            foreach (var go in _clueLibraryCards) if (go != null && go.name == $"LibCard_{key}") return go;
            foreach (var go in _npcLibraryCards) if (go != null && go.name == $"LibCard_{key}") return go;
            return null;
        }

        /// <summary>GraphNodeDragProxy ของ node ในกราฟ (runtime tests — simulate ลาก/ลากออก)</summary>
        public GraphNodeDragProxy GetNodeProxyForTests(string nodeKey)
        {
            if (_nodeRects.TryGetValue(nodeKey, out var rt))
                return rt.GetComponent<GraphNodeDragProxy>();
            return null;
        }

        /// <summary>Re-render ทั้งกราฟ: ClearAll → spawn node ที่ pin (custom pos ก่อน radial)
        /// → วาดเส้น (dedupe แล้ว) — เรียกซ้ำได้ปลอดภัย (reactivity จาก ClueGeneratedMessage)</summary>
        public void RenderGraph(List<ClueGraphNodeData> nodes, List<ClueGraphEdgeData> edges)
        {
            EnsureBackground();
            EnsureGraphArea();
            ClearAll();
            if (nodes == null || nodes.Count == 0) { RenderedEdgeCount = 0; return; }

            var clues = new List<ClueGraphNodeData>();
            var npcs = new List<ClueGraphNodeData>();
            foreach (var n in nodes)
            {
                if (n == null || string.IsNullOrEmpty(n.Id)) continue;
                if (n.Type == "npc") npcs.Add(n);
                else clues.Add(n);
            }

            // ---- clue group nodes: custom position ก่อน (ผู้เล่นลากไว้) → default เป็นแถวโค้ง ----
            for (var i = 0; i < clues.Count; i++)
            {
                var pos = _customPositions.TryGetValue(clues[i].Id, out var saved)
                    ? saved
                    : DefaultCluePosition(i, clues.Count);
                SpawnNode(clues[i], pos, GroupNodeSize, ClueNodeColor);
            }

            // ---- npc nodes: จัดมุมตาม clue แรกที่เชื่อมถึง (radial default) ----
            var npcAngle = new Dictionary<string, float>();
            if (edges != null)
                foreach (var e in edges)
                {
                    if (e?.FromClueKey == null) continue;
                    if (_nodeRects.TryGetValue(e.FromClueKey, out var clueRect) && !npcAngle.ContainsKey(e.ToNpcId))
                        npcAngle[e.ToNpcId] = Mathf.Atan2(
                            clueRect.anchoredPosition.y, clueRect.anchoredPosition.x) * Mathf.Rad2Deg;
                }

            for (var i = 0; i < npcs.Count; i++)
            {
                var angle = npcAngle.TryGetValue(npcs[i].Id, out var a)
                    ? a
                    : 360f * i / Mathf.Max(npcs.Count, 1) - 90f;
                var pos = _customPositions.TryGetValue(npcs[i].Id, out var saved)
                    ? saved
                    : new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad)) * 260f;
                SpawnNode(npcs[i], pos, NpcNodeSize, NpcNodeColor);
            }

            // ---- เส้นเชื่อม (dedupe คู่ (G,N) ทำที่ Presenter แล้ว — วาดตรงนี้) ----
            var drawn = 0;
            if (edges != null)
                foreach (var e in edges)
                    if (e != null
                        && _nodeRects.TryGetValue(e.FromClueKey, out var from)
                        && _nodeRects.TryGetValue(e.ToNpcId, out var to))
                    {
                        DrawEdge(from.anchoredPosition, to.anchoredPosition);
                        drawn++;
                    }
            RenderedEdgeCount = drawn;

            TrimCustomPositions(nodes);
        }

        /// <summary>ตำแหน่ง default ของ clue node: เรียงแถวโค้งเบา ๆ กลางกราฟ (ก่อนผู้เล่นจัดเอง)</summary>
        private static Vector2 DefaultCluePosition(int index, int total)
        {
            if (total == 1) return Vector2.zero;
            var t = total == 2 ? -0.5f + index : (float)index / (total - 1) - 0.5f; // -0.5..0.5
            return new Vector2(t * 2f * DefaultSpreadX,
                Mathf.Cos(t * Mathf.PI) * -DefaultSpreadY * 0.4f);
        }

        /// <summary>ลบ custom position ของ node ที่ไม่อยู่ในกราฟแล้ว (กัน dict โตค้าง)</summary>
        private void TrimCustomPositions(List<ClueGraphNodeData> nodes)
        {
            var live = new HashSet<string>();
            foreach (var n in nodes) if (n != null) live.Add(n.Id);
            var dead = new List<string>();
            foreach (var k in _customPositions.Keys) if (!live.Contains(k)) dead.Add(k);
            foreach (var k in dead) _customPositions.Remove(k);
        }

        /// <summary>
        /// ทำลาย node/edge เก่าทั้งหมด — SetActive(false) ก่อน Destroy (OnDisable ของ
        /// Graphic/TMP unregister ออกจาก canvas batch ทันที — กัน MissingReferenceException
        /// ที่เคยพา test run ลง) + detach ออกจาก container ทันที (Destroy เป็น deferred)
        /// </summary>
        public void ClearAll()
        {
            foreach (var go in _spawned)
                if (go != null)
                {
                    go.SetActive(false);
                    go.transform.SetParent(null);
                    Destroy(go);
                }
            _spawned.Clear();
            _nodeRects.Clear();
            _edgeGlowImages.Clear();
            RenderedEdgeCount = 0;
        }

        private void SpawnNode(ClueGraphNodeData node, Vector2 position, float size, Color color)
        {
            var go = new GameObject($"ClueNode_{node.Id}", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(_container, false);

            var rt = go.GetComponent<RectTransform>();
            rt.anchoredPosition = position;
            rt.sizeDelta = new Vector2(size, size);
            rt.localRotation = Quaternion.identity;
            rt.localScale = Vector3.one;

            var img = go.GetComponent<Image>();
            img.sprite = GetNodeSprite();
            img.color = color;
            img.raycastTarget = true; // รับ drag + hover

            // ---- drag proxy (เดิม GraphNodeClickProxy — click ถูกลบทิ้ง, decision 3) ----
            var proxy = go.AddComponent<GraphNodeDragProxy>();
            proxy.NodeKey = node.Id;
            proxy.Init(_dropZoneRect); // ✏️ v4: ต้อง Init ทุก spawn — ห้าม rect null
            proxy.Dragged += OnNodeDragged;
            proxy.DraggedOutOfGraph += key => GraphNodeUnpinRequested?.Invoke(key);

            // ---- hover tooltip (รายละเอียด — decision 3) ----
            var hover = go.AddComponent<HoverTooltip>();
            hover.NodeKey = node.Id;
            hover.TooltipKind = node.Type;
            hover.HoverEnter += (key, kind) => NodeHoverEnter?.Invoke(key, kind);
            hover.HoverExit += (key, kind) => NodeHoverExit?.Invoke(key, kind);

            // ---- Label ----
            var textGo = new GameObject("Label", typeof(RectTransform));
            textGo.transform.SetParent(go.transform, false);
            var textRt = textGo.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;

            var label = textGo.AddComponent<TextMeshProUGUI>();
            var font = GetLabelFont();
            if (font != null) label.font = font;
            label.text = node.Label;
            label.enableAutoSizing = true;
            label.fontSizeMin = 12;
            label.fontSizeMax = 24;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            label.raycastTarget = false;

            _nodeRects[node.Id] = rt;
            _spawned.Add(go);
        }

        /// <summary>node ถูกลาก: เก็บตำแหน่ง (คงอยู่ข้าม re-render) + วาดเส้นใหม่ตามปลายใหม่</summary>
        private void OnNodeDragged(string key, Vector2 pos)
        {
            _customPositions[key] = pos;
            RedrawEdgesOnly();
        }

        /// <summary>วาดเส้นใหม่จากตำแหน่ง node ปัจจุบัน (เรียกระหว่างลาก — node ไม่ rebuild)</summary>
        public void RedrawEdgesOnly()
        {
            // (view เก็บ edges ล่าสุดไว้ให้วาดซ้ำ)
            RenderEdgesFromCache();
        }

        private readonly List<ClueGraphEdgeData> _lastEdges = new();
        /// <summary>Presenter เรียกหลัง RenderGraph เพื่อให้ RedrawEdgesOnly มีข้อมูลล่าสุด</summary>
        public void SetEdgeCache(List<ClueGraphEdgeData> edges)
        {
            _lastEdges.Clear();
            if (edges != null) _lastEdges.AddRange(edges);
        }

        private void RenderEdgesFromCache()
        {
            // ลบเส้นเก่า (ชื่อ ClueEdge) แล้ววาดใหม่จาก cache
            for (var i = _spawned.Count - 1; i >= 0; i--)
            {
                var go = _spawned[i];
                if (go != null && go.name == "ClueEdge")
                {
                    _spawned.RemoveAt(i);
                    go.SetActive(false);
                    go.transform.SetParent(null);
                    Destroy(go);
                }
            }
            _edgeGlowImages.Clear();
            var drawn = 0;
            foreach (var e in _lastEdges)
                if (e != null
                    && _nodeRects.TryGetValue(e.FromClueKey, out var from)
                    && _nodeRects.TryGetValue(e.ToNpcId, out var to))
                {
                    DrawEdge(from.anchoredPosition, to.anchoredPosition);
                    drawn++;
                }
            RenderedEdgeCount = drawn;
        }

        /// <summary>
        /// เส้นเชื่อม witnessed แบบ 2 ชั้น: glow ทองนุ่ม (pulse ใน Update) ครอบ core เส้นสว่าง
        /// </summary>
        private void DrawEdge(Vector2 from, Vector2 to)
        {
            var delta = to - from;
            var length = delta.magnitude;
            if (length < 1f) return;

            var go = new GameObject("ClueEdge", typeof(RectTransform));
            go.transform.SetParent(_container, false);

            var rt = go.GetComponent<RectTransform>();
            rt.anchoredPosition = (from + to) * 0.5f;
            rt.sizeDelta = new Vector2(EdgeThickness, length);
            rt.localRotation = Quaternion.Euler(0f, 0f,
                Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg - 90f);
            rt.localScale = Vector3.one;

            var glowRt = CreateLayerRect("Glow", rt, EdgeGlowSpread);
            var glowImg = glowRt.gameObject.AddComponent<Image>();
            glowImg.sprite = GetEdgeGlowSprite();
            glowImg.color = EdgeGlowColor;
            glowImg.raycastTarget = false;
            _edgeGlowImages.Add(glowImg);

            var coreRt = CreateLayerRect("Core", rt, 0f);
            var coreImg = coreRt.gameObject.AddComponent<Image>();
            coreImg.sprite = GetNodeSprite();
            coreImg.color = EdgeCoreColor;
            coreImg.raycastTarget = false;

            _spawned.Add(go);
        }

        private static RectTransform CreateLayerRect(string name, RectTransform parent, float spread)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(-spread, -spread);
            rt.offsetMax = new Vector2(spread, spread);
            rt.localScale = Vector3.one;
            return rt;
        }

        /// <summary>จังหวะเต้นของ edge glow (cosmetic — ไม่รันตอน panel ซ่อน)</summary>
        private void Update()
        {
            if (_edgeGlowImages.Count == 0) return;
            var pulse = EdgeGlowColor;
            pulse.a = Mathf.Clamp01(EdgeGlowColor.a
                + (Mathf.Sin(Time.unscaledTime * GlowPulseSpeed) * 0.5f + 0.5f) * GlowPulseAmplitude);
            foreach (var img in _edgeGlowImages)
                if (img != null) img.color = pulse;
        }

        // ---- โครงพื้นที่: GraphArea (บน, มี GraphDropZone) + แถวคลัง 2 แถว (ล่าง) ----

        /// <summary>GraphArea ครึ่งบนของ panel — dropzone ของการ์ดจากคลัง (idempotent)</summary>
        private void EnsureGraphArea()
        {
            if (_container != null && _dropZoneRect != null) return;

            if (clueBoardContainer != null && _dropZoneRect != null)
            {
                _container = clueBoardContainer;
                return;
            }

            // พื้นที่กราฟ: กึ่งกลางแนวนอน, ย่นล่างไว้ให้แถวคลัง 2 แถว
            if (_container == null)
            {
                Transform host;
                if (clueBoardContainer != null)
                {
                    host = clueBoardContainer;
                }
                else
                {
                    var go = new GameObject("ClueBoardGraphRoot", typeof(RectTransform));
                    go.transform.SetParent(transform, false);
                    host = go.transform;
                }
                var rt = (RectTransform)host;
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(0f, 40f);
                _container = host;
            }

            // dropzone = กรอบจริงของพื้นที่กราฟ (ขยายเกิน container เล็กน้อย — ขอบหลวม ๆ)
            if (_dropZoneRect == null)
            {
                var dzGo = new GameObject("GraphArea", typeof(RectTransform), typeof(Image));
                dzGo.transform.SetParent(transform, false);
                dzGo.transform.SetAsFirstSibling(); // อยู่หลังสุด (ไม่บัง event ของ node/แถวคลัง)
                _dropZoneRect = (RectTransform)dzGo.transform;
                _dropZoneRect.anchorMin = new Vector2(0.5f, 0.5f);
                _dropZoneRect.anchorMax = new Vector2(0.5f, 0.5f);
                _dropZoneRect.pivot = new Vector2(0.5f, 0.5f);
                _dropZoneRect.anchoredPosition = new Vector2(0f, 40f);
                _dropZoneRect.sizeDelta = new Vector2(1560f, 640f);
                var dzImg = dzGo.GetComponent<Image>();
                dzImg.sprite = null;
                dzImg.color = new Color(0f, 0f, 0f, 0f); // โปร่งใส — แค่ raycast พื้นที่
                dzImg.raycastTarget = true;              // ⚠️ ต้อง true — DraggableCardHandler หา drop
                                                         // ด้วย EventSystem.RaycastAll (โปร่งใสแต่ยังต้อง
                                                         // เป็น raycast target ถึงจะโดน); ตั้ง first sibling
                                                         // จึงไม่บัง node/card ที่อยู่หน้ามัน
                _dropZone = dzGo.AddComponent<GraphDropZone>();
                _dropZone.NodesDropped += ids => LibraryCardDropped?.Invoke(ids);
                _dropZone.NodeDraggedOut += key => GraphNodeUnpinRequested?.Invoke(key);
            }
        }

        /// <summary>แถวคลัง 2 แถวล่าง (idempotent): ScrollRect แนวนอน + Viewport + Content + layout</summary>
        private void EnsureLibraryRows()
        {
            if (_libraryReady) return;
            _libraryReady = true;

            _clueRowLabel = CreateBoardText("ClueLibraryLabel", "คลังเบาะแส", 24,
                TextAlignmentOptions.Left, new Color(0.85f, 0.82f, 0.75f, 0.9f));
            var clRt = _clueRowLabel.rectTransform;
            clRt.anchorMin = new Vector2(0f, 0f); clRt.anchorMax = new Vector2(0f, 0f);
            clRt.pivot = new Vector2(0f, 0f);
            clRt.anchoredPosition = new Vector2(24f, LibraryBottomOffset + 2 * LibraryRowHeight + 6f);
            clRt.sizeDelta = new Vector2(300f, 28f);

            _clueScrollContent = CreateScrollRow("ClueLibraryRow",
                LibraryBottomOffset + LibraryRowHeight + 14f);
            _npcScrollContent = CreateScrollRow("NpcLibraryRow", LibraryBottomOffset - 10f);
        }

        private TMP_Text _clueRowLabel;

        /// <summary>สร้างแถว ScrollRect แนวนอน 1 แถว — คืน Content rect</summary>
        private RectTransform CreateScrollRow(string name, float bottomY)
        {
            var rowGo = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(RectTransform));
            rowGo.transform.SetParent(transform, false);
            var rowRt = (RectTransform)rowGo.transform;
            rowRt.anchorMin = new Vector2(0f, 0f);
            rowRt.anchorMax = new Vector2(1f, 0f);
            rowRt.pivot = new Vector2(0.5f, 0f);
            rowRt.anchoredPosition = new Vector2(0f, bottomY);
            rowRt.sizeDelta = new Vector2(0f, LibraryRowHeight);
            var rowBg = rowGo.GetComponent<Image>();
            rowBg.sprite = null;
            rowBg.color = LibraryBgColor;
            rowBg.raycastTarget = true; // ScrollRect ต้องรับ drag เพื่อเลื่อน

            var scroll = rowGo.GetComponent<ScrollRect>();
            scroll.horizontal = true;
            scroll.vertical = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 0f;

            // Viewport (Mask)
            var vpGo = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            vpGo.transform.SetParent(rowRt, false);
            var vpRt = (RectTransform)vpGo.transform;
            vpRt.anchorMin = Vector2.zero; vpRt.anchorMax = Vector2.one;
            vpRt.offsetMin = Vector2.zero; vpRt.offsetMax = Vector2.zero;
            var vpImg = vpGo.GetComponent<Image>();
            vpImg.sprite = null;
            vpImg.color = Color.white;
            vpImg.raycastTarget = false;
            vpGo.GetComponent<Mask>().showMaskGraphic = false;

            // Content (HorizontalLayoutGroup + ContentSizeFitter)
            var contentGo = new GameObject("Content", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
            contentGo.transform.SetParent(vpRt, false);
            var contentRt = (RectTransform)contentGo.transform;
            contentRt.anchorMin = new Vector2(0f, 0f);
            contentRt.anchorMax = new Vector2(0f, 0.5f); // ซ้าย-กลางแนวตั้ง — โตทางขวา
            contentRt.pivot = new Vector2(0f, 0.5f);
            var hlg = contentGo.GetComponent<HorizontalLayoutGroup>();
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.spacing = 14f;
            hlg.padding = new RectOffset(12, 12, 8, 8);
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childControlWidth = false;
            hlg.childControlHeight = false;
            var fitter = contentGo.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

            scroll.viewport = vpRt;
            scroll.content = contentRt;

            return contentRt;
        }

        /// <summary>
        /// Render คลังการ์ดเบาะแส (group ตาม DefId แล้ว — "×N" รวมใน Subtitle)
        /// — การ์ดเดียวแทนกลุ่ม, GroupNodeIds = instance ทั้งกลุ่ม (ลากวาง = pin หมด)
        /// </summary>
        public void RenderClueLibrary(List<ClueLibraryCardData> cards)
        {
            EnsureBackground();
            EnsureLibraryRows();
            ClearRow(_clueLibraryCards, _clueScrollContent);
            if (cards == null) return;

            foreach (var c in cards)
            {
                if (c == null || string.IsNullOrEmpty(c.Key)) continue;
                var go = CreateLibraryCard(c, _clueScrollContent, ClueNodeColor);
                _clueLibraryCards.Add(go);
            }
        }

        /// <summary>Render คลัง NPC (portrait + ชื่อ — ไม่เทา, Locked decision 6)</summary>
        public void RenderNpcLibrary(List<ClueLibraryCardData> cards)
        {
            EnsureBackground();
            EnsureLibraryRows();
            ClearRow(_npcLibraryCards, _npcScrollContent);
            if (cards == null) return;

            foreach (var c in cards)
            {
                if (c == null || string.IsNullOrEmpty(c.Key)) continue;
                var go = CreateLibraryCard(c, _npcScrollContent, NpcNodeColor,
                    isNpcCard: true, npcId: c.Key);
                _npcLibraryCards.Add(go);
            }
        }

        private void ClearRow(List<GameObject> cards, RectTransform content)
        {
            foreach (var go in cards)
                if (go != null)
                {
                    go.SetActive(false);
                    Destroy(go);
                }
            cards.Clear();
            // Content ที่ผ่าน HorizontalLayoutGroup — layout rebuild เองเมื่อ child เปลี่ยน
            if (content != null) LayoutRebuilder.ForceRebuildLayoutImmediate(content);
        }

        /// <summary>การ์ดคลัง 1 ใบ: พื้น + ชื่อ + subtitle + DraggableCardHandler + HoverTooltip</summary>
        private GameObject CreateLibraryCard(ClueLibraryCardData c, RectTransform content,
            Color accent, bool isNpcCard = false, string npcId = null)
        {
            var go = new GameObject($"LibCard_{c.Key}", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(content, false);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(190f, LibraryRowHeight - 24f);
            rt.localScale = Vector3.one;

            var bg = go.GetComponent<Image>();
            bg.sprite = null;
            bg.color = CardBgColor;
            bg.raycastTarget = true;

            // แถบสี accent ซ้าย (clue=แดง, npc=น้ำเงิน — แยกแถวให้เห็นชัด)
            var stripeGo = new GameObject("Stripe", typeof(RectTransform), typeof(Image));
            stripeGo.transform.SetParent(go.transform, false);
            var sRt = (RectTransform)stripeGo.transform;
            sRt.anchorMin = Vector2.zero; sRt.anchorMax = Vector2.zero;
            sRt.pivot = new Vector2(0f, 0.5f);
            sRt.anchoredPosition = new Vector2(0f, rt.sizeDelta.y / 2f);
            sRt.sizeDelta = new Vector2(8f, rt.sizeDelta.y);
            var sImg = stripeGo.GetComponent<Image>();
            sImg.sprite = GetNodeSprite();
            sImg.color = accent;
            sImg.raycastTarget = false;

            if (isNpcCard)
            {
                // ---- portrait (ครึ่งซ้าย) ----
                var portraitGo = new GameObject("Portrait", typeof(RectTransform), typeof(Image));
                portraitGo.transform.SetParent(go.transform, false);
                var pRt = (RectTransform)portraitGo.transform;
                pRt.anchorMin = new Vector2(0f, 0.5f); pRt.anchorMax = new Vector2(0f, 0.5f);
                pRt.pivot = new Vector2(0f, 0.5f);
                pRt.anchoredPosition = new Vector2(20f, 0f);
                pRt.sizeDelta = new Vector2(78f, 78f);
                var pImg = portraitGo.GetComponent<Image>();
                // NpcPortraitResolver: Resources/Portraits/{npcId} → fallback สี hash (ไม่เทา)
                var portrait = NpcPortraitResolver.LoadPortrait(npcId);
                if (portrait != null)
                {
                    pImg.sprite = portrait;
                    pImg.color = Color.white;
                    pImg.preserveAspect = true;
                }
                else
                {
                    pImg.sprite = GetNodeSprite();
                    pImg.color = NpcPortraitResolver.FallbackColor(npcId);
                }
                pImg.raycastTarget = false;
            }

            // ---- ชื่อ + subtitle (ครึ่งขวา) ----
            var nameX = isNpcCard ? 110f : 22f;
            var name = CreateBoardText("Name", c.DisplayName, 24, TextAlignmentOptions.Left,
                TitleColor, go.transform);
            var nRt = name.rectTransform;
            nRt.anchorMin = new Vector2(0f, 1f); nRt.anchorMax = new Vector2(1f, 1f);
            nRt.pivot = new Vector2(0f, 1f);
            nRt.anchoredPosition = new Vector2(nameX, -10f);
            nRt.sizeDelta = new Vector2(-(nameX + 10f), 40f);
            name.enableAutoSizing = true;
            name.fontSizeMin = 16; name.fontSizeMax = 24;

            var sub = CreateBoardText("Sub", c.Subtitle ?? "", 20, TextAlignmentOptions.Left,
                new Color(0.85f, 0.82f, 0.75f, 0.85f), go.transform);
            var subRt = sub.rectTransform;
            subRt.anchorMin = new Vector2(0f, 0f); subRt.anchorMax = new Vector2(1f, 0f);
            subRt.pivot = new Vector2(0f, 0f);
            subRt.anchoredPosition = new Vector2(nameX, 10f);
            subRt.sizeDelta = new Vector2(-(nameX + 10f), 30f);

            // ---- drag + hover ----
            var drag = go.AddComponent<DraggableCardHandler>();
            drag.CardKey = c.Key;
            drag.GroupNodeIds = c.GroupNodeIds ?? new List<string>();
            drag.TooltipView = EnsureTooltipView();
            drag.DroppedOnGraph += ids =>
            {
                // ปล่อยในกราฟ = pin ทุก instance ในกลุ่ม (decision 1)
                _dropZone.HandleDrop(ids);
            };

            var hover = go.AddComponent<HoverTooltip>();
            hover.NodeKey = c.Key;
            hover.TooltipKind = isNpcCard ? "npc" : "clue_library"; // การ์ดคลัง ≠ graph node (M(G))
            hover.HoverEnter += (key, kind) => NodeHoverEnter?.Invoke(key, kind);
            hover.HoverExit += (key, kind) => NodeHoverExit?.Invoke(key, kind);

            LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            return go;
        }

        /// <summary>tooltip กลางตัวเดียว (สร้างครั้งเดียว — ใช้ร่วมทุกการ์ด/node; Presenter เรียกผ่าน view)</summary>
        public HoverTooltipView EnsureTooltipView()
        {
            if (_tooltip != null) return _tooltip;
            EnsureBackground();
            _tooltip = gameObject.AddComponent<HoverTooltipView>();
            // เก็บ ref ของ text ไว้ assert ใน test (ผ่าน reflection-free getter ด้านล่าง)
            _tooltipText = (TMP_Text)_tooltip.GetType()
                .GetField("_text", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(_tooltip);
            _tooltipReady = true;
            return _tooltip;
        }

        /// <summary>ซ่อน tooltip กลาง (เรียกจาก Presenter ตอน drag เริ่ม/panel ปิด)</summary>
        public void HideTooltip() => _tooltip?.Hide();

        // ---- chrome: พื้น/หัวเรื่อง/hint/legend (เดิม — hint ปรับให้ตรง drag/hover) ----

        private void EnsureBackground()
        {
            if (_backgroundReady) return;
            _backgroundReady = true;

            var bg = GetComponent<Image>();
            if (bg == null) bg = gameObject.AddComponent<Image>();
            bg.sprite = null;
            bg.color = BoardColor;
            bg.raycastTarget = false;

            var title = CreateBoardText("ClueBoardTitle", "ผังเบาะแส — Clue Board", 40,
                TextAlignmentOptions.Center, TitleColor);
            var titleRt = title.rectTransform;
            titleRt.anchorMin = new Vector2(0.5f, 1f);
            titleRt.anchorMax = new Vector2(0.5f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.anchoredPosition = new Vector2(0f, -18f);
            titleRt.sizeDelta = new Vector2(600f, 60f);

            // ✅ hint ปรับให้สะท้อน drag/hover (ศัพท์เดิม: "กระดาน", "เบาะแส", "พยาน")
            var hint = CreateBoardText("ClueBoardHint",
                "[Tab] เปิด/ปิดกระดาน • ลากการ์ดจากคลังขึ้นกราฟ = ปักหมุด • ลากออกนอกกรอบ = ถอน • ชี้การ์ด = รายละเอียด",
                22, TextAlignmentOptions.Right, new Color(0.85f, 0.82f, 0.75f, 0.75f));
            var hintRt = hint.rectTransform;
            hintRt.anchorMin = new Vector2(1f, 0f);
            hintRt.anchorMax = new Vector2(1f, 0f);
            hintRt.pivot = new Vector2(1f, 0f);
            hintRt.anchoredPosition = new Vector2(-20f, 14f);
            hintRt.sizeDelta = new Vector2(980f, 32f);

            CreateLegendItem("LegendClue", ClueNodeColor, "เบาะแส (clue)", 0);
            CreateLegendItem("LegendNpc", NpcNodeColor, "พยาน (witness)", 1);
            CreateLegendItem("LegendEdge", new Color(1f, 0.82f, 0.35f), "เส้นเชื่อม witnessed", 2);
        }

        private TextMeshProUGUI CreateBoardText(string name, string text, float fontSize,
            TextAlignmentOptions alignment, Color color, Transform parent = null)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent != null ? parent : transform, false);
            var rt = go.GetComponent<RectTransform>();
            rt.localScale = Vector3.one;
            var label = go.AddComponent<TextMeshProUGUI>();
            var font = GetLabelFont();
            if (font != null) label.font = font;
            label.text = text;
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = color;
            label.raycastTarget = false;
            return label;
        }

        private void CreateLegendItem(string name, Color dotColor, string label, int row)
        {
            var dotGo = new GameObject(name, typeof(RectTransform), typeof(Image));
            dotGo.transform.SetParent(transform, false);
            var dotRt = dotGo.GetComponent<RectTransform>();
            dotRt.anchorMin = new Vector2(0f, 0f);
            dotRt.anchorMax = new Vector2(0f, 0f);
            dotRt.pivot = new Vector2(0f, 0f);
            dotRt.anchoredPosition = new Vector2(24f, 100f - row * 34f);
            dotRt.sizeDelta = new Vector2(18f, 18f);
            var dotImg = dotGo.GetComponent<Image>();
            dotImg.sprite = GetNodeSprite();
            dotImg.color = dotColor;
            dotImg.raycastTarget = false;

            var text = CreateBoardText(name + "_Text", label, 24, TextAlignmentOptions.Left, TitleColor);
            var textRt = text.rectTransform;
            textRt.anchorMin = new Vector2(0f, 0f);
            textRt.anchorMax = new Vector2(0f, 0f);
            textRt.pivot = new Vector2(0f, 0f);
            textRt.anchoredPosition = new Vector2(52f, 96f - row * 34f);
            textRt.sizeDelta = new Vector2(260f, 30f);
        }

        /// <summary>
        /// ปุ่ม HUD มุมซ้ายบน (alternative ของ Tab) — บน Canvas พ่อของ panel
        /// (board ซ่อน ปุ่มต้องยังอยู่)
        /// </summary>
        public void EnsureToggleButton()
        {
            if (_toggleButtonReady) return;
            _toggleButtonReady = true;

            var host = transform.parent != null ? transform.parent : transform;
            var go = new GameObject("ClueBoardToggleButton", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(host, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(24f, -24f);
            rt.sizeDelta = new Vector2(210f, 56f);
            rt.localScale = Vector3.one;

            var img = go.GetComponent<Image>();
            img.sprite = null;
            img.color = new Color(0.16f, 0.14f, 0.12f, 0.9f);

            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => ToggleButtonPressed?.Invoke());

            var label = CreateBoardText("ToggleLabel", "ผังเบาะแส [Tab]", 26,
                TextAlignmentOptions.Center, TitleColor, go.transform);
            var lRt = label.rectTransform;
            lRt.anchorMin = Vector2.zero;
            lRt.anchorMax = Vector2.one;
            lRt.offsetMin = Vector2.zero;
            lRt.offsetMax = Vector2.zero;
            label.raycastTarget = false;
        }

        // ---- sprite helpers (เดิม) ----

        private Sprite GetEdgeGlowSprite()
        {
            if (_edgeGlowSprite == null) _edgeGlowSprite = CreateEdgeGlowSprite();
            return _edgeGlowSprite;
        }

        private static Sprite CreateEdgeGlowSprite()
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var center = (size - 1) / 2f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var d = Vector2.Distance(new Vector2(x, y), new Vector2(center, center)) / center;
                    var a = Mathf.Clamp01(1f - d);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 64f);
        }

        private Sprite GetNodeSprite()
        {
            if (_nodeSprite == null) _nodeSprite = CreateCircleSprite();
            return _nodeSprite;
        }

        private TMP_FontAsset GetLabelFont()
        {
            if (_labelFont == null) _labelFont = WorldItemSystem.LoadLabelFont();
            return _labelFont;
        }

        /// <summary>font ร่วม (HoverTooltipView ใช้ — WorldItemSystem.LoadLabelFont เป็น internal)</summary>
        public TMP_FontAsset GetLabelFontPublic() => GetLabelFont();

        /// <summary>วงกลม placeholder สร้างจาก Texture2D ใน code (copy จาก WorldItemSystem)</summary>
        private static Sprite CreateCircleSprite()
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var center = (size - 1) / 2f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var inside = new Vector2(x - center, y - center).sqrMagnitude <= center * center;
                    tex.SetPixel(x, y, inside ? Color.white : Color.clear);
                }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 64f);
        }
    }
}
