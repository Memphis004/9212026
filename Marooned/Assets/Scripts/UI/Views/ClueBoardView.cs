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
    /// (Data Separation: Presenter map GraphNode → ClueGraphNodeData, View ไม่รู้จัก
    /// MessagePack shape) — ไม่มี [MessagePackObject]
    /// </summary>
    public class ClueGraphNodeData
    {
        public string Id;
        public string Type;    // "clue" หรือ "npc" (View ใช้แยกวงใน/วงนอก)
        public string Label;   // Text ที่จะแสดงบน Node
    }

    /// <summary>View-layer edge data — map จาก GraphEdge.From/To ฝั่ง Presenter</summary>
    public class ClueGraphEdgeData
    {
        public string FromClueId;
        public string ToNpcId;
        public string Relation = "witnessed";
    }

    /// <summary>
    /// View-layer detail data ของ clue (คลิก node → popup) — map จาก ClueBoardEntry
    /// ฝั่ง Presenter (handler เป็นคน filter witness แล้ว) — ไม่มี [MessagePackObject]
    /// เหมือน data contract ตัวอื่นของ view (Data Separation)
    /// </summary>
    public class ClueNodeDetail
    {
        public string InstanceId;
        public string Label;
        public string Reliability;
        public string LocationId;
        public List<string> Witnesses = new();
    }

    /// <summary>
    /// View-layer detail data ของ npc witness (คลิก node → popup) — map ฝั่ง Presenter:
    /// โซน/สถานะมาจาก NpcState (player เห็น chibi เดินอยู่จริง — ไม่ใช่ ground truth แอบแฝง)
    /// ส่วน "อาลิไบ" = เบาะแสที่ npc คนนี้เป็นพยาน (จาก ClueBoardEntry ที่ผ่าน filter แล้ว —
    /// บอกได้ว่า "ยืนยันว่าเห็น X ที่ Y" ซึ่งสอดคล้อง GDD แนวคิด alibi แบบกลาง ๆ)
    /// </summary>
    public class NpcNodeDetail
    {
        public string NpcId;
        public string Zone;                    // CurrentLocationId ปัจจุบัน (player-visible)
        public bool IsAlive;
        public List<string> WitnessedClues = new(); // ข้อความ "<label> @ <location>" ต่อเบาะแส
    }

    /// <summary>
    /// View-layer ข้อมูลการ์ดปักหมุด (pin) — Presenter เรียงจาก pinned ids แล้ว map
    /// เป็น Title/Body ผ่าน formatter ของ View (format เดียวกับ popup) — ไม่มี
    /// [MessagePackObject] เหมือน data contract ตัวอื่นของ view (Data Separation)
    /// </summary>
    public class PinnedCardData
    {
        public string NodeId;
        public string Title;
        public string Body;
    }

    /// <summary>
    /// Detective-board style layout ของ clue graph (Clue System v2 (e) — presentation layer):
    /// Clue nodes เรียงวงกลมชั้นใน, NPC witness nodes วงนอก (เฉพาะตัวที่มี edge เชื่อม),
    /// เส้นเชื่อม clue → npc (Image บางๆ หมุนให้ชี้จาก clue ไปหา npc)
    ///
    /// MVP Lite: passive — Presenter เรียก RenderGraph(nodes, edges) เท่านั้น
    /// (ไม่รู้จัก GetClueGraphHandler / MessagePack — รับแต่ view data)
    ///
    /// ⚠️ Sprite: CreateCircleSprite ของ WorldItemSystem เป็น private — copy logic
    /// สร้าง Texture2D วงกลมมาเป็น private method ของคลาสนี้ (spec Step 4)
    /// </summary>
    public class ClueBoardView : MonoBehaviour
    {
        [SerializeField] private Transform clueBoardContainer; // root ของ graph (auto-create ถ้าไม่ผูก)

        private readonly Dictionary<string, RectTransform> _nodeRects = new(); // node id → rect
        private readonly Dictionary<string, Image> _nodeImages = new();        // node id → image (highlight ตอนเลือก)
        private readonly List<GameObject> _spawned = new();                    // nodes+edges ทั้งหมด (clear ตอน re-render)
        private readonly List<Image> _edgeGlowImages = new();                  // glow layer ของแต่ละ edge (pulse ใน Update)

        private Sprite _nodeSprite;
        private Sprite _edgeGlowSprite;
        private TMP_FontAsset _labelFont;
        private Transform _container;
        private bool _backgroundReady;

        /// <summary>MVP Lite: คลิก node (clue หรือ npc) → ส่งต่อ id ให้ Presenter ตัดสินใจ (ผูกจาก GraphNodeClickProxy)</summary>
        public event Action<string> NodeClicked;

        /// <summary>MVP Lite: ปุ่ม HUD (มุมจอ) ถูกกด → Presenter เป็นคน TogglePanel (View passive)</summary>
        public event Action ToggleButtonPressed;

        /// <summary>MVP Lite: ขอปัก/ถอนหมุด node → Presenter เป็นคน toggle รายการ (จากปุ่มใน popup + ปุ่ม × บนการ์ด)</summary>
        public event Action<string> PinRequested;

        private RectTransform _detailRoot;   // popup card (สร้าง lazy ครั้งแรกที่เปิด — sibling ท้าย = วาดทับ node)
        private TMP_Text _detailTitle;
        private TMP_Text _detailBody;
        private string _detailNodeId;        // node ที่ popup กำลังโชว์ (null = ปิด)
        private Image _selectedImage;        // node ที่ถูก highlight ตอน popup เปิด
        private Color _selectedOriginalColor; // สีเดิมของ node ที่เลือก (clue=แดง, npc=น้ำเงิน — restore ถูกตัว)
        private Color _selectedNodeColor;     // สี selected ปัจจุบัน (re-apply ข้าม re-render)
        private bool _toggleButtonReady;     // HUD button สร้างแล้ว (idempotent)

        private RectTransform _pinRow;                              // แถวการ์ดปักหมุด (ล่างกลาง panel)
        private readonly Dictionary<string, GameObject> _pinCards = new();   // nodeId → card GO
        private readonly Dictionary<string, Image> _pinBadges = new();       // nodeId → badge จุดทองบน node
        private Button _pinButton;                                  // ปุ่ม [ปักหมุด] ใน popup

        // ---- Layout constants ----
        private const float InnerRadius = 150f;         // วงใน: clue nodes
        private const float OuterRadius = 300f;         // วงนอก: npc witness nodes
        private const float ClueNodeSize = 110f;
        private const float NpcNodeSize = 80f;
        private const float EdgeThickness = 4f;
        private const float EdgeGlowSpread = 10f;       // glow ยื่นออกรอบ core (px)
        private const float GlowPulseSpeed = 2.2f;      // จังหวะเต้นของ glow (rad/s)
        private const float GlowPulseAmplitude = 0.15f; // ช่วง alpha ที่เต้น
        private static readonly Color ClueNodeColor = new Color(0.85f, 0.35f, 0.30f);      // แดงอิฐ (clue)
        private static readonly Color ClueNodeSelectedColor = new Color(1f, 0.62f, 0.55f); // แดงอิฐสว่าง (node ที่เลือก)
        private static readonly Color NpcNodeSelectedColor = new Color(0.62f, 0.82f, 1f);  // น้ำเงินสว่าง (npc ที่เลือก)
        private static readonly Color NpcNodeColor = new Color(0.35f, 0.60f, 0.85f);  // น้ำเงิน (npc)
        private static readonly Color EdgeCoreColor = new Color(1f, 0.95f, 0.78f, 0.95f); // core เส้นสว่าง
        private static readonly Color EdgeGlowColor = new Color(1f, 0.82f, 0.35f, 0.35f); // ทอง (alpha เต้นตามเวลา)
        private static readonly Color BoardColor = new Color(0.12f, 0.10f, 0.09f, 0.92f); // พื้นกระดานเข้ม
        private static readonly Color TitleColor = new Color(0.92f, 0.88f, 0.80f, 1f);    // parchment

        /// <summary>จำนวน node ที่ render ล่าสุด (ใช้โดย runtime tests เป็นหลักฐาน)</summary>
        public int RenderedNodeCount => _nodeRects.Count;

        /// <summary>จำนวน edge ที่ render ล่าสุด (runtime tests)</summary>
        public int RenderedEdgeCount { get; private set; }

        /// <summary>popup รายละเอียดกำลังเปิดจริงใน hierarchy ไหม (board ซ่อน = false)</summary>
        public bool IsDetailVisible => _detailRoot != null && _detailRoot.gameObject.activeInHierarchy;

        /// <summary>node id ที่ popup เปิดอยู่ (clue instance id หรือ npc id; null = ปิด) — runtime tests</summary>
        public string DetailNodeIdForTests => _detailNodeId;

        /// <summary>ข้อความใน popup แบบรวม (title | body) — runtime tests assert โดยไม่แตะ TMP</summary>
        public string DetailTextForTests => _detailTitle != null && _detailBody != null
            ? _detailTitle.text + " | " + _detailBody.text
            : string.Empty;

        /// <summary>node นี้อยู่ในกราฟที่ render ล่าสุดไหม (presenter ใช้ตัด pin ที่ node หายไป)</summary>
        public bool HasNode(string nodeId) => _nodeRects.ContainsKey(nodeId);

        /// <summary>container ที่ใช้จริง (runtime tests — ตรวจว่าอยู่ใต้ Canvas)</summary>
        public Transform ContainerForTests => _container;

        /// <summary>จำนวนการ์ดปักหมุดที่แสดงอยู่ — runtime tests</summary>
        public int PinnedCardCountForTests => _pinCards.Count;

        /// <summary>ปุ่ม [ปักหมุด] ใน popup (test กดผ่าน ExecuteEvents ตาม path เดียวกับเกม)</summary>
        public Button PinButtonForTests => _pinButton;

        /// <summary>ปุ่ม × ปิดของการ์ดปักหมุดที่ระบุ — runtime tests (คืน false ถ้าไม่มีการ์ดนั้น)</summary>
        public bool TryGetPinCardCloseForTests(string nodeId, out Button close)
        {
            close = null;
            return _pinCards.TryGetValue(nodeId, out var card)
                && card != null
                && card.transform.Find("CloseBtn") != null
                && card.transform.Find("CloseBtn").TryGetComponent(out close);
        }

        /// <summary>
        /// Re-render ทั้งกราฟ: ClearAll → spawn node วงใน/วงนอก → วาดเส้นเชื่อม
        /// (เรียกซ้ำได้ปลอดภัย — ใช้เป็นกลไก reactivity เมื่อเกิด clue ใหม่)
        /// </summary>
        public void RenderGraph(List<ClueGraphNodeData> nodes, List<ClueGraphEdgeData> edges)
        {
            EnsureBackground(); // chrome (พื้น/หัวเรื่อง/legend) ต้องมีเสมอ — แม้กราฟว่าง
            ClearAll();
            if (nodes == null || nodes.Count == 0) return;

            EnsureContainer();

            var clues = new List<ClueGraphNodeData>();
            var npcs = new List<ClueGraphNodeData>();
            foreach (var n in nodes)
            {
                if (n == null || string.IsNullOrEmpty(n.Id)) continue;
                if (n.Type == "npc") npcs.Add(n);
                else clues.Add(n); // "clue" หรือ type แปลกๆ → วงใน (safe default)
            }

            // NPC เฉพาะตัวที่มี edge เชื่อม (spec Step 4.2)
            var linkedNpcIds = new HashSet<string>();
            if (edges != null)
                foreach (var e in edges)
                    if (!string.IsNullOrEmpty(e?.ToNpcId)) linkedNpcIds.Add(e.ToNpcId);
            npcs.RemoveAll(n => !linkedNpcIds.Contains(n.Id));

            // ---- วงใน: clue nodes (radius 150) ----
            for (var i = 0; i < clues.Count; i++)
            {
                var angle = 360f * i / Mathf.Max(clues.Count, 1) - 90f;
                var pos = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad)) * InnerRadius;
                SpawnNode(clues[i], pos, ClueNodeSize, ClueNodeColor);
            }

            // ---- วงนอก: npc nodes (radius 300) — จัดมุมตาม clue แรกที่เชื่อมถึง ----
            var npcAngle = new Dictionary<string, float>();
            if (edges != null)
                foreach (var e in edges)
                {
                    if (e?.FromClueId == null || !linkedNpcIds.Contains(e.ToNpcId)) continue;
                    if (_nodeRects.TryGetValue(e.FromClueId, out var clueRect) && !npcAngle.ContainsKey(e.ToNpcId))
                        npcAngle[e.ToNpcId] = Mathf.Atan2(
                            clueRect.anchoredPosition.y, clueRect.anchoredPosition.x) * Mathf.Rad2Deg;
                }

            for (var i = 0; i < npcs.Count; i++)
            {
                var angle = npcAngle.TryGetValue(npcs[i].Id, out var a)
                    ? a
                    : 360f * i / Mathf.Max(npcs.Count, 1) - 90f;
                var pos = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad)) * OuterRadius;
                SpawnNode(npcs[i], pos, NpcNodeSize, NpcNodeColor);
            }

            // ---- เส้นเชื่อม clue → npc ----
            if (edges != null)
                foreach (var e in edges)
                    if (e != null
                        && _nodeRects.TryGetValue(e.FromClueId, out var from)
                        && _nodeRects.TryGetValue(e.ToNpcId, out var to))
                        DrawEdge(from.anchoredPosition, to.anchoredPosition);

            RenderedEdgeCount = edges?.Count ?? 0;

            // popup เปิดค้างไว้: node หายไปจากกราฟใหม่ → ปิดเอง; ยังอยู่ → re-apply highlight
            // (node เก่าถูก destroy ตอน ClearAll — ตัวใหม่เพิ่ง spawn ต้อง highlight ใหม่)
            if (_detailNodeId != null && !_nodeRects.ContainsKey(_detailNodeId))
                HideNodeDetail();
            else if (_detailNodeId != null && _nodeImages.TryGetValue(_detailNodeId, out var selImg) && selImg != null)
            {
                if (_selectedImage != null) _selectedImage.color = _selectedOriginalColor;
                _selectedImage = selImg;
                selImg.color = _selectedNodeColor;
            }
        }

        /// <summary>
        /// ทำลาย child objects เก่าทั้งหมด — detach ออกจาก container ทันที
        /// (Destroy เป็น deferred ถึงจบเฟรม — ถ้า re-render 2 ครั้งในเฟรมเดียว เช่น
        /// message-triggered render ชนกับ explicit render, เด็กเก่าต้องหายจาก
        /// container ก่อน ไม่งั้นนับซ้ำ/ซ้อนทับ) แล้วค่อย Destroy ท้ายเฟรมตามปกติ
        /// </summary>
        public void ClearAll()
        {
            foreach (var go in _spawned)
                if (go != null)
                {
                    // SetActive(false) ก่อน destroy — OnDisable ของ Graphic/TMP ทำงานทันที
                    // (unregister ออกจาก canvas batch) ไม่งั้นถ้า object ถูก destroy ในสถานะ
                    // ที่ OnDisable ไม่รัน กราฟฟิกที่ถูกลบไปแล้วจะยังติดค้างใน batch และ
                    // โยน MissingReferenceException ทุกเฟรม (พาทั้ง test run ลง)
                    go.SetActive(false);
                    go.transform.SetParent(null);
                    Destroy(go);
                }
            _spawned.Clear();
            _nodeRects.Clear();
            _nodeImages.Clear();
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
            // ทุก node รับคลิกได้ (CardSlotUI pattern — IPointerClickHandler บน view component
            // ส่งต่อ id ผ่าน event, Presenter เป็นคนตัดสินใจ): clue → รายละเอียดเบาะแส,
            // npc → โซน/อาลิไบ — ไม่มี node แบบ display-only แล้ว
            img.raycastTarget = true;
            var proxy = go.AddComponent<GraphNodeClickProxy>();
            proxy.NodeId = node.Id;
            proxy.Clicked += id => NodeClicked?.Invoke(id);

            // Label บน node (TMP เหมือน UI อื่น — THSarabunPSK SDF)
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
            _nodeImages[node.Id] = img;
            _spawned.Add(go);
        }

        /// <summary>
        /// เส้นเชื่อม witnessed แบบ 2 ชั้นจาก clue → npc: glow ทองนุ่ม (radial-gradient
        /// sprite ยืดเป็น beam — เต้นช้า ๆ ใน Update) ครอบ core เส้นสว่างหัวท้ายมน
        /// (root 1 อันต่อ 1 edge — Glow/Core เป็นแค่ layer ลูก ทำให้นับ edge ง่ายเหมือนเดิม)
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

            // glow layer — sprite ไล่ alpha เชิงรัศมี ยืดเกินขอบ core ทุกด้าน = beam นุ่ม
            var glowRt = CreateLayerRect("Glow", rt, EdgeGlowSpread);
            var glowImg = glowRt.gameObject.AddComponent<Image>();
            glowImg.sprite = GetEdgeGlowSprite();
            glowImg.color = EdgeGlowColor;
            glowImg.raycastTarget = false;
            _edgeGlowImages.Add(glowImg);

            // core — เส้นสว่างบาง (circle sprite ยืด → หัวท้ายมน)
            var coreRt = CreateLayerRect("Core", rt, 0f);
            var coreImg = coreRt.gameObject.AddComponent<Image>();
            coreImg.sprite = GetNodeSprite();
            coreImg.color = EdgeCoreColor;
            coreImg.raycastTarget = false;

            _spawned.Add(go);
        }

        /// <summary>child rect ยืดเต็ม parent + ขยายเกินขอบทุกด้านตาม spread (px)</summary>
        private static RectTransform CreateLayerRect(string name, RectTransform parent, float spread)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(-spread, -spread); // ขยายเกินขอบซ้าย-ล่าง
            rt.offsetMax = new Vector2(spread, spread);   // ขยายเกินขอบขวา-บน
            rt.localScale = Vector3.one;
            return rt;
        }

        /// <summary>จังหวะเต้นของ edge glow (cosmetic — เบา ไม่แตะ logic; ไม่รันตอน panel ซ่อน)</summary>
        private void Update()
        {
            if (_edgeGlowImages.Count == 0) return;
            var pulse = EdgeGlowColor;
            pulse.a = Mathf.Clamp01(EdgeGlowColor.a
                + (Mathf.Sin(Time.unscaledTime * GlowPulseSpeed) * 0.5f + 0.5f) * GlowPulseAmplitude);
            foreach (var img in _edgeGlowImages)
                if (img != null) img.color = pulse;
        }

        /// <summary>
        /// พื้นกระดาน detective board (สร้างครั้งเดียว — idempotent): backdrop เข้มโปร่ง
        /// บนตัว panel + หัวเรื่อง + hint ปุ่ม Tab + legend 3 แถว ทั้งหมด raycastTarget=false
        /// (กระดาน display-only — ไม่บังคลิกการ์ด/โลก) ฟอนต์ THSarabunPSK เหมือน UI อื่น
        /// </summary>
        private void EnsureBackground()
        {
            if (_backgroundReady) return;
            _backgroundReady = true;

            var bg = GetComponent<Image>();
            if (bg == null) bg = gameObject.AddComponent<Image>();
            bg.sprite = null;          // solid color — เรียบ ไม่แย่ง attention
            bg.color = BoardColor;
            bg.raycastTarget = false;

            // ---- หัวเรื่อง (บนกลาง) ----
            var title = CreateBoardText("ClueBoardTitle", "ผังเบาะแส — Clue Board", 40,
                TextAlignmentOptions.Center, TitleColor);
            var titleRt = title.rectTransform;
            titleRt.anchorMin = new Vector2(0.5f, 1f);
            titleRt.anchorMax = new Vector2(0.5f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.anchoredPosition = new Vector2(0f, -18f);
            titleRt.sizeDelta = new Vector2(600f, 60f);

            // ---- hint ปุ่ม toggle (ล่างขวา) ----
            var hint = CreateBoardText("ClueBoardHint", "[Tab] เปิด/ปิดกระดาน • คลิกเบาะแส/พยาน = รายละเอียด", 26,
                TextAlignmentOptions.Right, new Color(0.85f, 0.82f, 0.75f, 0.75f));
            var hintRt = hint.rectTransform;
            hintRt.anchorMin = new Vector2(1f, 0f);
            hintRt.anchorMax = new Vector2(1f, 0f);
            hintRt.pivot = new Vector2(1f, 0f);
            hintRt.anchoredPosition = new Vector2(-20f, 14f);
            hintRt.sizeDelta = new Vector2(620f, 36f);

            // ---- legend (ล่างซ้าย) ----
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
        /// ปุ่ม HUD มุมซ้ายบน (alternative ของ Tab สำหรับผู้เล่นเมาส์) — สร้างบนตัว Canvas
        /// (พ่อของ panel) ไม่ใช่บน panel เพราะตอน board ซ่อน ปุ่มต้องยังอยู่ให้กดเปิดกลับ
        /// เรียกจาก Presenter.Initialize ได้แม้ panel inactive (แค่สร้าง hierarchy)
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
            img.sprite = null; // solid — เรียบเหมือน chrome ของ board
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
            label.raycastTarget = false; // ให้คลิกทะลุ label ลงปุ่ม
        }

        // ---- Detail popup (คลิก node) — clue: ข้อมูล map จาก ClueBoardEntry, npc: โซน+เบาะแสที่เป็นพยาน
        //      (ทั้งหมดผ่าน filter เดียวกับ get_clue_board — Presenter reuse handler เดิม) ----

        // formatter ร่วม popup + การ์ดปักหมุด (format ที่เดียว — Presenter เรียกใช้ตอนเรียง pin)

        /// <summary>หัวข้อ popup/การ์ดของ clue</summary>
        public static string FormatClueTitle(ClueNodeDetail d) => d.Label;

        /// <summary>เนื้อหา popup/การ์ดของ clue (reliability/location/witnesses)</summary>
        public static string FormatClueBody(ClueNodeDetail d)
        {
            var witnessText = d.Witnesses != null && d.Witnesses.Count > 0
                ? string.Join(", ", d.Witnesses)
                : "ไม่มีผู้เห็น";
            return $"ความน่าเชื่อถือ: {d.Reliability}\nพบที่: {d.LocationId}\nพยาน: {witnessText}";
        }

        /// <summary>หัวข้อ popup/การ์ดของ npc witness</summary>
        public static string FormatNpcTitle(NpcNodeDetail d) => d.NpcId;

        /// <summary>เนื้อหา popup/การ์ดของ npc witness (โซน + อาลิไบคร่าว ๆ —
        /// เบาะแสซ้ำรวมเป็น "×N" ผ่าน ClueGraphTextFormat.DedupeCounted — display-only)</summary>
        public static string FormatNpcBody(NpcNodeDetail d)
        {
            var status = d.IsAlive ? "มีชีวิต" : "ตายแล้ว";
            var clueText = d.WitnessedClues != null && d.WitnessedClues.Count > 0
                ? "\n- " + string.Join("\n- ", ClueGraphTextFormat.DedupeCounted(d.WitnessedClues))
                : " ไม่พบ (จากเบาะแสที่เก็บได้)";
            return $"โซน: {d.Zone} • {status}\nพยานให้เบาะแสที่เก็บได้:{clueText}";
        }

        /// <summary>เปิด/อัปเดต popup รายละเอียดของ clue + highlight node ที่เลือก</summary>
        public void ShowClueDetail(ClueNodeDetail detail)
        {
            if (detail == null) { HideNodeDetail(); return; }
            ShowDetail(detail.InstanceId, FormatClueTitle(detail), FormatClueBody(detail),
                ClueNodeSelectedColor);
        }

        /// <summary>
        /// เปิด/อัปเดต popup รายละเอียดของ npc witness: โซนปัจจุบัน + อาลิไบคร่าว ๆ
        /// (เบาะแสที่คนนี้เป็นพยาน — คือ "ยอมรับว่าอยู่แถวนั้นตอนนั้น" โดยปริยาย)
        /// </summary>
        public void ShowNpcDetail(NpcNodeDetail detail)
        {
            if (detail == null) { HideNodeDetail(); return; }
            ShowDetail(detail.NpcId, FormatNpcTitle(detail), FormatNpcBody(detail),
                NpcNodeSelectedColor);
        }

        /// <summary>แกนกลาง popup: เขียนข้อความ + highlight node (restore สีเดิมของตัวก่อนเสมอ)</summary>
        private void ShowDetail(string nodeId, string title, string body, Color selectedColor)
        {
            EnsureDetailPopup();
            _detailNodeId = nodeId;
            _detailTitle.text = title;
            _detailBody.text = body;
            _detailRoot.gameObject.SetActive(true);

            if (_selectedImage != null) _selectedImage.color = _selectedOriginalColor;
            _selectedOriginalColor = _nodeImages.TryGetValue(nodeId, out var img) && img != null
                ? img.color
                : ClueNodeColor;
            _selectedNodeColor = selectedColor;
            _selectedImage = _nodeImages.TryGetValue(nodeId, out var sel) ? sel : null;
            if (_selectedImage != null) _selectedImage.color = selectedColor;
        }

        /// <summary>ปิด popup + คืนสีเดิมของ node (เรียกได้ปลอดภัยแม้ยังไม่เคยเปิด)</summary>
        public void HideNodeDetail()
        {
            _detailNodeId = null;
            if (_detailRoot != null) _detailRoot.gameObject.SetActive(false);
            if (_selectedImage != null) _selectedImage.color = _selectedOriginalColor;
            _selectedImage = null;
        }

        /// <summary>การ์ดรายละเอียด — สร้าง lazy ครั้งแรก (sibling ท้ายสุดจึงวาดทับ node ทุกตัว)</summary>
        private void EnsureDetailPopup()
        {
            if (_detailRoot != null) return;

            var go = new GameObject("ClueDetailPopup", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(transform, false);
            _detailRoot = (RectTransform)go.transform;
            _detailRoot.anchorMin = new Vector2(0.5f, 0.5f);
            _detailRoot.anchorMax = new Vector2(0.5f, 0.5f);
            _detailRoot.pivot = new Vector2(0.5f, 0.5f);
            _detailRoot.anchoredPosition = new Vector2(0f, -40f);
            _detailRoot.sizeDelta = new Vector2(620f, 280f);
            _detailRoot.localScale = Vector3.one;

            var card = go.GetComponent<Image>();
            card.sprite = null;         // solid dark card — เรียบ เน้นตัวหนังสือ
            card.color = new Color(0.09f, 0.08f, 0.07f, 0.97f);
            card.raycastTarget = true;  // บังคลิกทะลุ — กันคลิกโดน node ใต้การ์ด

            _detailTitle = CreateBoardText("DetailTitle", string.Empty, 34,
                TextAlignmentOptions.Center, TitleColor, _detailRoot);
            var tRt = _detailTitle.rectTransform;
            tRt.anchorMin = new Vector2(0f, 1f);
            tRt.anchorMax = new Vector2(1f, 1f);
            tRt.pivot = new Vector2(0.5f, 1f);
            tRt.anchoredPosition = new Vector2(0f, -12f);
            tRt.sizeDelta = new Vector2(-32f, 44f); // stretch-x, ขอบซ้ายขวา 16

            _detailBody = CreateBoardText("DetailBody", string.Empty, 27,
                TextAlignmentOptions.Left, new Color(0.95f, 0.93f, 0.88f), _detailRoot);
            var bRt = _detailBody.rectTransform;
            bRt.anchorMin = Vector2.zero;
            bRt.anchorMax = Vector2.one;
            bRt.offsetMin = new Vector2(24f, 40f);
            bRt.offsetMax = new Vector2(-24f, -60f);

            var hint = CreateBoardText("DetailHint", "[คลิก node เดิมอีกครั้งเพื่อปิด]", 20,
                TextAlignmentOptions.Center, new Color(0.85f, 0.82f, 0.75f, 0.7f), _detailRoot);
            var hRt = hint.rectTransform;
            hRt.anchorMin = new Vector2(0.5f, 0f);
            hRt.anchorMax = new Vector2(0.5f, 0f);
            hRt.pivot = new Vector2(0.5f, 0f);
            hRt.anchoredPosition = new Vector2(70f, 8f);
            hRt.sizeDelta = new Vector2(330f, 28f);

            // ---- ปุ่มปักหมุด (ล่างซ้ายของ popup) — ยิง PinRequested(nodeId ที่ popup เปิดอยู่)
            //      Presenter เป็นคน toggle รายการ pin (View passive ส่งแค่ id) ----
            var pinGo = new GameObject("PinButton", typeof(RectTransform), typeof(Image), typeof(Button));
            pinGo.transform.SetParent(_detailRoot, false);
            var pinRt = (RectTransform)pinGo.transform;
            pinRt.anchorMin = new Vector2(0f, 0f);
            pinRt.anchorMax = new Vector2(0f, 0f);
            pinRt.pivot = new Vector2(0f, 0f);
            pinRt.anchoredPosition = new Vector2(12f, 6f);
            pinRt.sizeDelta = new Vector2(150f, 34f);
            pinRt.localScale = Vector3.one;
            var pinImg = pinGo.GetComponent<Image>();
            pinImg.sprite = null;
            pinImg.color = new Color(0.24f, 0.20f, 0.14f, 0.95f);
            _pinButton = pinGo.GetComponent<Button>();
            _pinButton.targetGraphic = pinImg;
            _pinButton.onClick.AddListener(() =>
            {
                if (_detailNodeId != null) PinRequested?.Invoke(_detailNodeId);
            });
            var pinLabel = CreateBoardText("PinLabel", "[ปักหมุด]", 22,
                TextAlignmentOptions.Center, new Color(1f, 0.82f, 0.35f), pinGo.transform);
            var pRt = pinLabel.rectTransform;
            pRt.anchorMin = Vector2.zero;
            pRt.anchorMax = Vector2.one;
            pRt.offsetMin = Vector2.zero;
            pRt.offsetMax = Vector2.zero;
            pinLabel.raycastTarget = false;

            go.SetActive(false);
        }

        // ---- Pin workspace (ปักหมุดหลาย node เทียบกัน) — Presenter เรียงรายการแล้วส่ง
        //      PinnedCardData (format ผ่าน formatter ด้านบน) — view วาดแถวล่างกลาง + badge ทองบน node

        /// <summary>
        /// วาดแถวการ์ดปักหมุดใหม่ทั้งหมด (idempotent — ลบของเก่าแล้วสร้างตามลิสต์)
        /// เรียงซ้าย→ขวาตามลำดับ pin; node ที่ถูก pin มีจุดทองมุมขวาบนเป็นสัญลักษณ์
        /// </summary>
        public void RenderPinned(List<PinnedCardData> cards)
        {
            EnsurePinRow();

            // เคลียร์การ์ด + badge เก่า (badge ผูกกับ node ที่ re-render อาจถูก destroy ไปแล้ว)
            // — เหมือน ClearAll: SetActive(false) ก่อน Destroy เพื่อให้ Graphic/TMP unregister ทันที
            foreach (var card in _pinCards.Values)
                if (card != null)
                {
                    card.SetActive(false);
                    Destroy(card);
                }
            _pinCards.Clear();
            foreach (var badge in _pinBadges.Values)
                if (badge != null)
                {
                    badge.gameObject.SetActive(false);
                    Destroy(badge.gameObject);
                }
            _pinBadges.Clear();

            if (cards == null || cards.Count == 0) return;

            const float cardW = 380f, cardH = 220f, gap = 20f;
            var rowW = cards.Count * cardW + (cards.Count - 1) * gap;
            _pinRow.sizeDelta = new Vector2(rowW, cardH);

            for (var i = 0; i < cards.Count; i++)
            {
                var c = cards[i];
                if (c == null || string.IsNullOrEmpty(c.NodeId)) continue;

                var go = new GameObject($"PinCard_{c.NodeId}", typeof(RectTransform), typeof(Image));
                go.transform.SetParent(_pinRow, false);
                var rt = (RectTransform)go.transform;
                rt.anchoredPosition = new Vector2(i * (cardW + gap) + cardW / 2f - rowW / 2f, 0f);
                rt.sizeDelta = new Vector2(cardW, cardH);
                rt.localScale = Vector3.one;
                var bg = go.GetComponent<Image>();
                bg.sprite = null;
                bg.color = new Color(0.10f, 0.09f, 0.08f, 0.95f);
                bg.raycastTarget = true; // บังคลิกทะลุ — กันคลิกโดน node ใต้การ์ด

                var title = CreateBoardText("Title", c.Title, 26,
                    TextAlignmentOptions.Center, TitleColor, go.transform);
                var tRt = title.rectTransform;
                tRt.anchorMin = new Vector2(0f, 1f);
                tRt.anchorMax = new Vector2(1f, 1f);
                tRt.pivot = new Vector2(0.5f, 1f);
                tRt.anchoredPosition = new Vector2(0f, -8f);
                tRt.sizeDelta = new Vector2(-70f, 36f); // เว้นขวาไว้ให้ปุ่ม ×

                var body = CreateBoardText("Body", c.Body, 21,
                    TextAlignmentOptions.Left, new Color(0.95f, 0.93f, 0.88f), go.transform);
                var bRt = body.rectTransform;
                bRt.anchorMin = Vector2.zero;
                bRt.anchorMax = Vector2.one;
                bRt.offsetMin = new Vector2(16f, 8f);
                bRt.offsetMax = new Vector2(-16f, -48f);

                // ปุ่ม × ถอนหมุด — ยิง PinRequested เดียวกับปุ่มใน popup (toggle ฝั่ง presenter)
                var closeGo = new GameObject("CloseBtn", typeof(RectTransform), typeof(Image), typeof(Button));
                closeGo.transform.SetParent(go.transform, false);
                var cRt = (RectTransform)closeGo.transform;
                cRt.anchorMin = new Vector2(1f, 1f);
                cRt.anchorMax = new Vector2(1f, 1f);
                cRt.pivot = new Vector2(1f, 1f);
                cRt.anchoredPosition = new Vector2(-8f, -8f);
                cRt.sizeDelta = new Vector2(30f, 30f);
                cRt.localScale = Vector3.one;
                var cImg = closeGo.GetComponent<Image>();
                cImg.sprite = GetNodeSprite();
                cImg.color = new Color(0.55f, 0.28f, 0.24f);
                var closeBtn = closeGo.GetComponent<Button>();
                closeBtn.targetGraphic = cImg;
                var nodeId = c.NodeId;
                closeBtn.onClick.AddListener(() => PinRequested?.Invoke(nodeId));
                var closeLabel = CreateBoardText("X", "×", 24,
                    TextAlignmentOptions.Center, Color.white, closeGo.transform);
                var xRt = closeLabel.rectTransform;
                xRt.anchorMin = Vector2.zero;
                xRt.anchorMax = Vector2.one;
                xRt.offsetMin = Vector2.zero;
                xRt.offsetMax = Vector2.zero;
                closeLabel.raycastTarget = false;

                _pinCards[c.NodeId] = go;

                // badge จุดทองบน node ที่ถูก pin (node หายไประหว่าง render → ข้าม)
                if (_nodeImages.TryGetValue(c.NodeId, out var nodeImg) && nodeImg != null)
                {
                    var badgeGo = new GameObject("PinBadge", typeof(RectTransform), typeof(Image));
                    badgeGo.transform.SetParent(nodeImg.transform, false);
                    var bRt2 = (RectTransform)badgeGo.transform;
                    bRt2.anchorMin = new Vector2(1f, 1f);
                    bRt2.anchorMax = new Vector2(1f, 1f);
                    bRt2.pivot = new Vector2(1f, 1f);
                    bRt2.anchoredPosition = new Vector2(-4f, -4f);
                    bRt2.sizeDelta = new Vector2(26f, 26f);
                    bRt2.localScale = Vector3.one;
                    var badgeImg = badgeGo.GetComponent<Image>();
                    badgeImg.sprite = GetNodeSprite();
                    badgeImg.color = new Color(1f, 0.82f, 0.35f);
                    badgeImg.raycastTarget = false; // คลิกทะลุลง node ตัวหลัก
                    _pinBadges[c.NodeId] = badgeImg;
                }
            }
        }

        /// <summary>แถว pin ล่างกลาง panel (สร้างครั้งแรก — sibling ใต้ popup)</summary>
        private void EnsurePinRow()
        {
            if (_pinRow != null) return;
            var go = new GameObject("CluePinRow", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            _pinRow = (RectTransform)go.transform;
            _pinRow.anchorMin = new Vector2(0.5f, 0f);
            _pinRow.anchorMax = new Vector2(0.5f, 0f);
            _pinRow.pivot = new Vector2(0.5f, 0f);
            _pinRow.anchoredPosition = new Vector2(0f, 56f);
            _pinRow.localScale = Vector3.one;
        }

        private Sprite GetEdgeGlowSprite()
        {
            if (_edgeGlowSprite == null) _edgeGlowSprite = CreateEdgeGlowSprite();
            return _edgeGlowSprite;
        }

        /// <summary>radial alpha gradient (quadratic falloff) — ยืดเป็น beam แล้วดูนุ่มเป็น glow</summary>
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

        private void EnsureContainer()
        {
            if (_container != null) return;
            if (clueBoardContainer != null)
            {
                _container = clueBoardContainer;
                return;
            }

            // Auto-create ใต้ตัว panel เอง (transform ของ view) — ไม่ใช่ Canvas ตรง ๆ
            // เพราะ ClueBoardPresenter.TogglePanel ทำ SetActive ที่ panel ที่เดียว
            // (panel inactive → ทั้งกราฟถูกซ่อนตาม) panel เป็น full-stretch ใต้ Canvas
            // จึงมีพิกัดตรงกับ Canvas space — child กึ่งกลาง = กึ่งกลางจอเท่าเดิม
            // (ถ้าผูก clueBoardContainer เองใน Inspector ต้องวางใต้ panel ด้วย
            // ไม่งั้น toggle จะไม่ซ่อน container นั้น)
            var go = new GameObject("ClueBoardGraphRoot", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero; // กึ่งกลาง panel (= กึ่งกลางจอ)
            _container = go.transform;
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

        /// <summary>
        /// วงกลม placeholder สร้างจาก Texture2D ใน code (copy logic จาก
        /// WorldItemSystem.CreateCircleSprite ที่เป็น private — ตาม spec Step 4)
        /// </summary>
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

    /// <summary>
    /// ติดบน node root (clue + npc — Image raycastTarget=true) — IPointerClickHandler คลิกซ้าย
    /// → ยิง Clicked(NodeId) ให้ ClueBoardView ส่งต่อ Presenter (pattern เดียวกับ
    /// CardSlotUI — View passive ส่งแค่ id, Presenter ตัดสินใจว่าจะโชว์อะไร)
    /// </summary>
    public class GraphNodeClickProxy : MonoBehaviour, IPointerClickHandler
    {
        public string NodeId { get; set; }
        public event Action<string> Clicked;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            if (string.IsNullOrEmpty(NodeId)) return;
            Clicked?.Invoke(NodeId);
        }
    }
}
