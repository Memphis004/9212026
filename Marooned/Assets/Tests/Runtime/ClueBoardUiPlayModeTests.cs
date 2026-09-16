using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cysharp.Threading.Tasks;
using Marooned.Core;
using Marooned.Shared;
using Marooned.Systems;
using Marooned.UI.Presenters;
using Marooned.UI.Views;
using MessagePipe;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using VContainer;
using Debug = UnityEngine.Debug;

namespace Marooned.EditorTools
{
    /// <summary>
    /// Clue System v2 — Part (f): drag-drop workspace redesign (PlayMode evidence)
    /// ตาม Locked decisions: ไม่มี click ทั้งหมด (drag + hover เท่านั้น), ลากการ์ดจาก
    /// คลังขึ้นกราฟ = pin ทั้งกลุ่ม, ลาก node ออกนอกกรอบ = unpin, ปัดแนวนอน = เลื่อนรายการ,
    /// ไม่มีเพดาน pin, tooltip = M(G) เท่านั้น
    ///
    /// A: ลากการ์ดเบาะแสขึ้นกราฟ → node + เส้น; get_pinned_clues ครบทุก instance ของกลุ่ม
    /// B: ลากกลุ่ม 5 instance → pin ครบ (ไม่มี eviction — ไม่มีเพดาน)
    /// C: ปัดแนวนอนบนการ์ด → ScrollRect เลื่อน, ไม่มี ghost, ไม่ pin (drag/scroll arbitration)
    /// D: ลาก group node ออกนอกกรอบ → unpin เฉพาะสมาชิกที่ pinned ใน M(G)
    /// E: pin NPC เดียวที่เป็นพยานหลายชนิด → groups โผล่ครบตาม M(G) (กราฟบวม = intended)
    /// F: hover NPC card + graph clue node → tooltip ถูกต้อง + สอดคล้อง ×k (M(G) เท่านั้น)
    /// G: MCP set_pinned_clue → กราฟอัปเดตเองผ่าน CluePinState.Changed (ไม่แตะ UI)
    /// H: ลากจัดตำแหน่ง node → ตำแหน่งคงอยู่ข้าม re-render
    /// I: regression — bridge text เดิม, ไม่มีซาก pin-row/popup/click, การ์ด NPC ไม่เทา
    /// J: [Description] ของ SetPinnedClue ไม่มีการอ้างเพดาน/oldest-drop เหลืออยู่
    /// K: stale pin (inject id ปลอมเข้า pin list ตรง ๆ) → RenderAsync รอบถัดไปกวาดทิ้ง
    /// </summary>
    public class ClueBoardUiPlayModeTests
    {
        public const string EvidenceDir = "TestEvidence/clue-system-v2-f";

        static readonly string RepoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        static readonly string BridgeDll = Path.Combine(RepoRoot, "McpBridge", "bin", "Debug", "net8.0", "McpBridge.dll");
        static readonly string BridgeSource = Path.Combine(RepoRoot, "McpBridge", "Program.cs");

        GameLifetimeScope _scope;
        GameStateProvider _stateProvider;
        ClueBoardView _view;
        NpcDirectorSystem _director;
        ClueGenerationSystem _gen;
        CluePinState _pins;
        ClueBoardPresenter _presenter;
        IAsyncRequestHandler<InvestigateClueRequest, InvestigateClueResponse> _investigate;

        [UnitySetUp]
        public IEnumerator SetUp() => UniTask.ToCoroutine(async () =>
        {
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "SampleScene")
            {
                var load = UnityEngine.SceneManagement.SceneManager.LoadSceneAsync("SampleScene");
                while (load != null && !load.isDone) await UniTask.Yield();
            }

            var deadline = Time.realtimeSinceStartup + 60f;
            while (Time.realtimeSinceStartup < deadline)
            {
                _scope = UnityEngine.Object.FindFirstObjectByType<GameLifetimeScope>();
                if (_scope != null && Application.isPlaying)
                {
                    _stateProvider = _scope.Container.Resolve<GameStateProvider>();
                    if (_stateProvider != null) break;
                }
                await UniTask.Yield();
            }
            Assert.That(_scope != null, "GameLifetimeScope ไม่เจอใน play mode");
            _view = _scope.Container.Resolve<ClueBoardView>();
            _director = _scope.Container.Resolve<NpcDirectorSystem>();
            _gen = _scope.Container.Resolve<ClueGenerationSystem>();
            _pins = _scope.Container.Resolve<CluePinState>();
            _presenter = _scope.Container.Resolve<ClueBoardPresenter>();
            _investigate = _scope.Container.Resolve<IAsyncRequestHandler<InvestigateClueRequest, InvestigateClueResponse>>();

            // test isolation: ปักหมุดค้างจาก test ก่อนหน้าต้องไม่ตกค้าง
            _pins.Clear();
            await UniTask.Yield();
        });

        // ---------- shared seed helpers ----------

        /// <summary>player + พยานมีชีวิต n ตัว ย้ายไป beach</summary>
        List<string> SeedPlayerAndWitnesses(int witnessCount)
        {
            var player = _stateProvider.GetPlayer();
            player.CurrentLocationId = "beach";
            var npcIds = _director.Npcs.Values.Where(n => n.IsAlive).Select(n => n.Id).Take(witnessCount).ToList();
            Assert.That(npcIds.Count, Is.GreaterThanOrEqualTo(1), "ต้องมี NPC มีชีวิตอย่างน้อย 1 (ทำ witness ได้)");
            foreach (var id in npcIds) _director.MoveNpc(id, "beach");
            return npcIds;
        }

        /// <summary>generate (blood 100%) + investigate จนได้ wanted ชิ้น — คืนรายการ id ที่เก็บได้รอบนี้</summary>
        async UniTask<List<string>> SeedAndCollect(int wanted, string tag)
        {
            var got = new List<string>();
            for (var i = 0; i < wanted + 3 && got.Count < wanted; i++)
            {
                _gen.TryGenerate(ClueTriggerSource.KillSabotage, "beach", $"npc_test_killer_{tag}{i}");
                var r = await _investigate.InvokeAsync(new InvestigateClueRequest());
                if (r.Success) got.Add(r.FoundInstanceId);
            }
            Assert.That(got.Count, Is.GreaterThanOrEqualTo(1), "blood (100%) ต้อง generate + เก็บได้");
            return got;
        }

        /// <summary>inject instance เข้า registry+inventory ตรง (deterministic — เลี่ยง weighted roll
        /// ของ TryGenerate; ใช้ DefId จริงจาก ClueDef เพื่อผ่าน DisplayName/Reliability resolve)</summary>
        string InjectClueInstance(string defId, string locationId, List<string> witnesses, string tag)
        {
            var inst = new ClueInstance
            {
                InstanceId = $"test_{defId}_{tag}_{Guid.NewGuid():N}".Replace("clue_", ""),
                DefId = defId,
                LocationId = locationId,
                GameTimestamp = Time.time,
                Source = ClueTriggerSource.KillSabotage,
                SourceActorId = "npc_test_killer_" + tag,
                WitnessNpcIds = witnesses,
            };
            _stateProvider.AllClueInstances[inst.InstanceId] = inst;
            _stateProvider.GetPlayer().CollectedClueInstanceIds.Add(inst.InstanceId);
            return inst.InstanceId;
        }

        /// <summary>เปิดกระดาน + render ผ่าน presenter (path เดียวกับเกม) — รอ layout/canvas
        /// พร้อมจริง (panel อาจเพิ่ง active ครั้งแรก — canvas update หลัง yield)</summary>
        async UniTask OpenBoardAndRender()
        {
            _view.gameObject.SetActive(true);
            await InvokeRenderAsync(_presenter);
            await UniTask.Yield();                       // layout pass ของ frame แรกหลัง active
            Canvas.ForceUpdateCanvases();
            await UniTask.Delay(50);                     // canvas geometry ต้องทันสมัยก่อน simulate drag/hover
            Canvas.ForceUpdateCanvases();
        }

        static Vector2 DropZoneCenterScreen()
        {
            var rt = _viewStatic.DropZoneRectForTests;
            return RectTransformUtility.WorldToScreenPoint(null, rt.position);
        }

        static ClueBoardView _viewStatic; // helper เข้าถึง view ใน static method (ตั้งใน OpenBoardAndRender)

        /// <summary>simulate ลากการ์ดคลังแบบแนวตั้ง (หยิบ) แล้วปล่อยที่จุด screen ที่กำหนด</summary>
        static void SimulateCardDrag(GameObject card, Vector2 startScreen, Vector2 endScreen, bool verticalPickup)
        {
            var ped = new PointerEventData(EventSystem.current)
            {
                position = startScreen,
                delta = Vector2.zero,
                // pressEventCamera อ่านอย่างเดียว (overlay canvas = null อยู่แล้ว)
            };
            ExecuteEvents.Execute(card, ped, ExecuteEvents.beginDragHandler);

            if (verticalPickup)
            {
                // แนวตั้งเด่นเกิน slop (10px) → ตัดสินเป็น Dragging (หยิบการ์ด)
                ped.delta = new Vector2(0f, -30f);
                ExecuteEvents.Execute(card, ped, ExecuteEvents.dragHandler);
                ped.position = endScreen;
                ped.delta = endScreen - startScreen + new Vector2(0f, -30f);
                ExecuteEvents.Execute(card, ped, ExecuteEvents.dragHandler);
            }
            else
            {
                // แนวนอนเด่นเกิน slop → Scrolling (ส่งต่อ ScrollRect — ใช้ *ตำแหน่ง* absolute
                // ไม่ใช่ delta จึงต้องขยับ position ตามแต่ละ step ด้วย)
                ped.position = startScreen + new Vector2(60f, 0f);
                ped.delta = new Vector2(60f, 0f);
                ExecuteEvents.Execute(card, ped, ExecuteEvents.dragHandler);
                ped.position = endScreen;
                ped.delta = endScreen - startScreen;
                ExecuteEvents.Execute(card, ped, ExecuteEvents.dragHandler);
            }

            ped.position = endScreen;
            ExecuteEvents.Execute(card, ped, ExecuteEvents.endDragHandler);
        }

        /// <summary>simulate ลาก node ในกราฟ (จัดตำแหน่ง) — endInside = ปล่อยใน/นอกกรอบ</summary>
        static void SimulateNodeDrag(GameObject nodeGo, Vector2 endScreen, bool endInside)
        {
            var ped = new PointerEventData(EventSystem.current)
            {
                position = DropZoneCenterScreen(),
                delta = Vector2.zero,
                // pressEventCamera อ่านอย่างเดียว (overlay canvas = null อยู่แล้ว)
            };
            ExecuteEvents.Execute(nodeGo, ped, ExecuteEvents.beginDragHandler);
            ped.position = endScreen;
            ped.delta = endScreen - DropZoneCenterScreen();
            ExecuteEvents.Execute(nodeGo, ped, ExecuteEvents.dragHandler);
            // ปล่อย: ถ้า endInside=false จุดปลายอยู่นอกกรอบอยู่แล้ว (ผู้เรียกส่งจุดนอกมา)
            ped.position = endInside ? DropZoneCenterScreen() : endScreen;
            ExecuteEvents.Execute(nodeGo, ped, ExecuteEvents.endDragHandler);
        }

        // ---------- Test A: drag clue card onto graph → node + edges + get_pinned_clues ----------

        [UnityTest]
        public IEnumerator A_DragClueCardOntoGraph_PinsWholeGroup_WithEdges() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (f) Test A — {DateTime.Now:HH:mm:ss} ===");
            _viewStatic = _view;

            // ใช้ InjectClueInstance (deterministic) แทน SeedAndCollect —
            // TryGenerate snapshots witnesses ณ เวลาสร้าง instance ซึ่ง ambient tick อาจ
            // ย้าย NPC ออกจาก location ก่อนสร้าง → rawWitnesses=[] ได้
            SeedPlayerAndWitnesses(2);
            var witnessIds = _director.Npcs.Values.Where(n => n.IsAlive)
                .Select(n => n.Id).Take(2).ToList();
            var collected = new List<string> { InjectClueInstance("clue_blood_stain", "beach", witnessIds, "a") };
            await OpenBoardAndRender();

            Assert.That(_view.ClueLibraryCardsForTests.Count, Is.GreaterThanOrEqualTo(1),
                "คลังเบาะแสต้องมีการ์ด (clue ที่เก็บแล้ว group ตาม DefId)");

            // การ์ดกลุ่มที่มี instance ที่เพิ่งเก็บ (ไม่ใช้ [0] — กัน residue จาก test ก่อนหน้า)
            var card = _view.ClueLibraryCardsForTests
                .FirstOrDefault(c => c.GetComponent<DraggableCardHandler>()?.GroupNodeIds.Contains(collected[0]) == true);
            Assert.That(card, Is.Not.Null, "ต้องมีการ์ดกลุ่มของ clue ที่เพิ่งเก็บ");
            var handler = card.GetComponent<DraggableCardHandler>();
            Assert.That(handler, Is.Not.Null, "การ์ดคลังต้องมี DraggableCardHandler");
            Assert.That(handler.GroupNodeIds.Count, Is.GreaterThanOrEqualTo(1),
                "การ์ดกลุ่มต้องพก instance ids ทั้งกลุ่ม");
            log.AppendLine($"[A] card '{card.name}' group instances = {handler.GroupNodeIds.Count}");

            // ลากขึ้นกราฟ (แนวตั้ง = หยิบ) ปล่อยกลาง GraphArea
            var before = _pins.PinnedNodeIds.Count;
            SimulateCardDrag(card, DropZoneCenterScreen() + new Vector2(-200f, -420f), DropZoneCenterScreen(), true);

            var dl = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < dl && _pins.PinnedNodeIds.Count == before)
                await UniTask.Yield();

            // pin ครบทุก instance ในกลุ่ม (decision 1)
            foreach (var id in handler.GroupNodeIds)
                Assert.That(_pins.IsPinned(id), Is.True, $"pin ทั้งกลุ่ม: {id} ต้องถูก pin");
            log.AppendLine($"[A] pinned after drop: [{string.Join(", ", _pins.PinnedNodeIds)}]");

            // render ที่ trigger โดย Changed อาจยัง in-flight — render ซ้ำจนกราฟครบ
            var graphDl = Time.realtimeSinceStartup + 5f;
            while (Time.realtimeSinceStartup < graphDl && _view.RenderedNodeCount < 2)
            {
                await InvokeRenderAsync(_presenter);
                await UniTask.Yield();
            }

            log.AppendLine($"[A] nodes={_view.RenderedNodeCount} edges={_view.RenderedEdgeCount}");

            // กราฟ: group node + npc witness nodes + เส้นเชื่อม
            Assert.That(_view.RenderedNodeCount, Is.GreaterThanOrEqualTo(2), "ต้องมี group node + npc node");
            Assert.That(_view.RenderedEdgeCount, Is.GreaterThanOrEqualTo(1), "ต้องมีเส้นเชื่อมอย่างน้อย 1");

            // get_pinned_clues (MCP chain ใน-process) ครบทุก instance
            var pinnedHandler = _scope.Container.Resolve<IAsyncRequestHandler<GetPinnedCluesRequest, GetPinnedCluesResponse>>();
            var pinnedRes = await pinnedHandler.InvokeAsync(new GetPinnedCluesRequest());
            var pinnedIds = pinnedRes.Pinned.Select(p => p.NodeId).ToHashSet();
            foreach (var id in handler.GroupNodeIds)
                Assert.That(pinnedIds, Does.Contain(id), "get_pinned_clues ต้องครบทุก instance ของกลุ่ม");
            log.AppendLine(ClueGraphTextFormat.RenderPinned(pinnedRes));

            log.AppendLine("RESULT: PASS");
            WriteEvidence("A_DragClueCardOntoGraph_PinsWholeGroup_WithEdges", log.ToString());
            await UniTask.Yield();
        });

        // ---------- Test B: 5-instance group drag → no eviction (no cap) ----------

        [UnityTest]
        public IEnumerator B_DragFiveInstanceGroup_AllPinned_NoEviction() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (f) Test B — {DateTime.Now:HH:mm:ss} ===");
            _viewStatic = _view;

            SeedPlayerAndWitnesses(2);
            // กลุ่ม 5 instance: blood 2 (generate จริง) + blood 3 (inject — 100% deterministic
            // แทนการพึ่ง weighted roll ของ TryGenerate ที่ไม่การันตี) — ต้องเป็น DefId เดียวกัน
            // จึงรวมเป็นกลุ่มเดียว (จับกลุ่มตาม DefId — Locked decision 2)
            var w = _director.Npcs.Values.Where(n => n.IsAlive).Select(n => n.Id).Take(2).ToList();
            Assert.That(w.Count, Is.GreaterThanOrEqualTo(1), "ต้องมีพยานมีชีวิตสำหรับ inject");
            // inject ทั้ง 5 instance (deterministic — ไม่พึ่ง TryGenerate)
            for (var i = 0; i < 5; i++)
                InjectClueInstance("clue_blood_stain", "beach", w, $"b{i}");
            log.AppendLine($"[B] collected total = {_stateProvider.GetPlayer().CollectedClueInstanceIds.Count}");

            await OpenBoardAndRender();

            // หาการ์ดกลุ่มที่มี ≥ 5 instance
            DraggableCardHandler target = null;
            foreach (var c in _view.ClueLibraryCardsForTests)
            {
                var h = c.GetComponent<DraggableCardHandler>();
                if (h != null && h.GroupNodeIds.Count >= 5) { target = h; break; }
            }
            Assert.That(target, Is.Not.Null, "ต้องมีกลุ่ม ≥ 5 instance (seed แล้ว)");
            var groupCount = target.GroupNodeIds.Count;
            log.AppendLine($"[B] group '{target.CardKey}' instances = {groupCount}");

            SimulateCardDrag(target.gameObject, DropZoneCenterScreen() + new Vector2(-200f, -420f),
                DropZoneCenterScreen(), true);

            var dl = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < dl && _pins.PinnedNodeIds.Count < groupCount)
                await UniTask.Yield();

            // ✅ assert ไม่มี eviction: pin ครบทุกตัว (เดิม cap 3 จะตัดเหลือ 3)
            Assert.That(_pins.PinnedNodeIds.Count, Is.EqualTo(groupCount),
                "ไม่มีเพดาน — pin ทั้งกลุ่มครบทุก instance (ไม่มีตัวเก่าสุดหลุด)");
            foreach (var id in target.GroupNodeIds)
                Assert.That(_pins.IsPinned(id), Is.True);
            log.AppendLine($"[B] all {groupCount} instances pinned — no cap eviction ✓");

            // กราฟแสดง group node เดียวของกลุ่ม (×k) — k = |M(G)| = ทั้งกลุ่ม
            var groupNode = _view.GetNodeProxyForTests(target.CardKey);
            Assert.That(groupNode, Is.Not.Null, "ต้องมี group node ในกราฟ");
            log.AppendLine($"[B] group node '{target.CardKey}' rendered");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("B_DragFiveInstanceGroup_AllPinned_NoEviction", log.ToString());
            await UniTask.Yield();
        });

        // ---------- Test C: horizontal swipe on card → scroll, no pin, no ghost ----------

        [UnityTest]
        public IEnumerator C_HorizontalSwipe_ScrollsRow_NoPin_NoGhost() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (f) Test C — {DateTime.Now:HH:mm:ss} ===");
            _viewStatic = _view;

            var wC = SeedPlayerAndWitnesses(1);
            InjectClueInstance("clue_blood_stain", "beach", wC, "c");
            await OpenBoardAndRender();

            // เติมการ์ดสมรบถ้วนให้ content ล้นแนวนอน (12 ใบ × 190px > viewport) —
            // render ครั้งถัดไปของ presenter จะล้างทิ้งเอง (ClearRow)
            var synthetic = new List<ClueLibraryCardData>();
            for (var i = 0; i < 12; i++)
                synthetic.Add(new ClueLibraryCardData
                {
                    Key = $"test_g_{i:D2}",
                    DisplayName = $"การ์ดทดสอบ {i:D2}",
                    Subtitle = "Weak",
                    GroupNodeIds = new List<string> { $"test_inst_{i:D2}" },
                });
            _view.RenderClueLibrary(synthetic);
            Canvas.ForceUpdateCanvases();
            await UniTask.Yield();

            var content = _view.ClueScrollContentForTests;
            Assert.That(content, Is.Not.Null, "ต้องมี scroll content ของแถวคลังเบาะแส");
            var beforeX = content.anchoredPosition.x;

            // ปัดจากขวาไปซ้าย (เนื้อหาโตทางขวา; Clamp เริ่มที่ x=0 — ปัดขวาไม่มีที่ไป)
            var card = content.GetChild(0).gameObject;
            var start = DropZoneCenterScreen() + new Vector2(300f, -420f);
            SimulateCardDrag(card, start, start - new Vector2(400f, 0f), verticalPickup: false);
            Canvas.ForceUpdateCanvases();
            await UniTask.Yield();

            var afterX = content.anchoredPosition.x;
            log.AppendLine($"[C] content.x before={beforeX:F1} after={afterX:F1}");
            Assert.That(afterX, Is.Not.EqualTo(beforeX).Within(0.5f),
                "ปัดแนวนอน → ScrollRect ต้องเลื่อน (forward ผ่าน DraggableCardHandler)");

            // ไม่ pin และไม่มี ghost เหลืออยู่
            Assert.That(_pins.PinnedNodeIds.Count, Is.EqualTo(0),
                "ปัดแนวนอนต้องไม่ pin (arbitration เลือก Scrolling)");
            Assert.That(GameObject.Find("DragGhost_test_g_00"), Is.Null,
                "โหมด Scrolling ต้องไม่สร้าง ghost");
            log.AppendLine("[C] no pin, no ghost — arbitration ✓");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("C_HorizontalSwipe_ScrollsRow_NoPin_NoGhost", log.ToString());
            await UniTask.Yield();
        });

        // ---------- Test D: drag group node out of frame → unpin only pinned M(G) members ----------

        [UnityTest]
        public IEnumerator D_DragGroupNodeOut_UnpinsOnlyPinnedMembers() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (f) Test D — {DateTime.Now:HH:mm:ss} ===");
            _viewStatic = _view;

            var wD = SeedPlayerAndWitnesses(2);
            var collected = new List<string>
            {
                InjectClueInstance("clue_blood_stain", "beach", wD, "d0"),
                InjectClueInstance("clue_blood_stain", "beach", wD, "d1"),
            };
            await OpenBoardAndRender();

            // หา group key ของ blood จาก node ที่ render (pin ผ่าน drag การ์ด)
            var card = _view.ClueLibraryCardsForTests[0];
            var handler = card.GetComponent<DraggableCardHandler>();
            var groupKey = handler.CardKey;
            Assert.That(groupKey, Does.StartWith("group:"), "การ์ดแรกต้องเป็นการ์ดกลุ่ม clue");

            // pin ทั้งกลุ่ม + pin npc 1 ตัว (พยานของกลุ่ม) — เพื่อพิสูจน์ unpin เฉพาะสมาชิกกลุ่ม
            SimulateCardDrag(card, DropZoneCenterScreen() + new Vector2(-200f, -420f), DropZoneCenterScreen(), true);
            var dl = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < dl && _pins.PinnedNodeIds.Count < handler.GroupNodeIds.Count)
                await UniTask.Yield();
            Assert.That(_pins.PinnedNodeIds.Count, Is.GreaterThanOrEqualTo(handler.GroupNodeIds.Count),
                "pin ทั้งกลุ่มก่อนทดสอบ");

            var witnessNpc = _director.Npcs.Values.First(n => n.IsAlive).Id;
            _pins.Pin(witnessNpc);
            await UniTask.Yield();
            await UniTask.Delay(100);
            await InvokeRenderAsync(_presenter);

            var groupNodeGo = _view.GetNodeProxyForTests(groupKey)?.gameObject;
            Assert.That(groupNodeGo, Is.Not.Null, "group node ต้องอยู่ในกราฟ");
            var nodeRect = (RectTransform)groupNodeGo.transform;
            var posBefore = nodeRect.anchoredPosition;
            log.AppendLine($"[D] before unpin: pinned = {_pins.PinnedNodeIds.Count}, group pos = {posBefore}");

            // ลากออกนอกกรอบ (มุมล่างซ้ายจอ — นอก GraphArea แน่นอน)
            SimulateNodeDrag(groupNodeGo, new Vector2(5f, 5f), endInside: false);

            dl = Time.realtimeSinceStartup + 10f;
            dl = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < dl && _view.HasNode(groupKey))
                await UniTask.Yield();
            await UniTask.Delay(200);
            await InvokeRenderAsync(_presenter);

            // ✅ unpin เฉพาะสมาชิกที่ pinned ของ M(G) — npc ที่ pin ต้องอยู่ครบ
            foreach (var id in handler.GroupNodeIds)
                Assert.That(_pins.IsPinned(id), Is.False, $"สมาชิกกลุ่ม {id} ต้องถูกถอน");
            Assert.That(_pins.IsPinned(witnessNpc), Is.True,
                "npc ที่ pin เองต้องไม่โดนถอน (unpin เฉพาะสมาชิกของกลุ่ม)");

            // หลัง render: ถ้า npc ที่ pin เป็นพยานของกลุ่ม → M(G) ยังดึงกลุ่มมา (intended)
            // ถ้าไม่ใช่พยาน → group node หาย 证明 M(G) semantics ถูกต้อง
            var boardHandler = _scope.Container.Resolve<IAsyncRequestHandler<GetClueBoardRequest, GetClueBoardResponse>>();
            var board = await boardHandler.InvokeAsync(new GetClueBoardRequest());
            var groupEntries = board.Entries.Where(e => handler.GroupNodeIds.Contains(e.InstanceId)).ToList();
            var npcWitnessesGroup = groupEntries.Any(e => e.WitnessNpcIds != null && e.WitnessNpcIds.Contains(witnessNpc));
            if (npcWitnessesGroup)
            {
                Assert.That(_view.HasNode(groupKey), Is.True,
                    "npc ที่ยัง pin เป็นพยานของกลุ่ม → M(G) ดึงกลุ่ม回来 (intended — locked decision)");
                log.AppendLine("[D] group node present via M(G) from pinned npc (intended)");
            }
            else
            {
                Assert.That(_view.HasNode(groupKey), Is.False,
                    "ไม่มีสมาชิก pinned + ไม่มี npc pin เป็นพยาน → group หาย");
            }
            log.AppendLine($"[D] after unpin: pinned = [{string.Join(", ", _pins.PinnedNodeIds)}] (npc ค้าง)");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("D_DragGroupNodeOut_UnpinsOnlyPinnedMembers", log.ToString());
            await UniTask.Yield();
        });

        // ---------- Test E: pin single NPC witnessing multiple types → groups per M(G) ----------

        [UnityTest]
        public IEnumerator E_PinSingleNpc_GroupsAppearPerWitnessedTypes() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (f) Test E — {DateTime.Now:HH:mm:ss} ===");
            _viewStatic = _view;

            var npcIds = SeedPlayerAndWitnesses(1);
            var witness = npcIds[0];

            // ชนิดที่ 1: blood (inject — deterministic)
            var collected = new List<string> { InjectClueInstance("clue_blood_stain", "beach", new List<string> { witness }, "e") };

            // ชนิดที่ 2: scratch mark — inject ตรง (TryGenerate เป็น weighted roll — ไม่ deterministic)
            // (state injection ที่นี่: เพื่อหา clue ชนิดที่สองที่คนเดียวกันเป็นพยานอย่างแน่นอน)
            var scratch = new ClueInstance
            {
                InstanceId = $"test_scratch_{Guid.NewGuid():N}",
                DefId = "clue_scratch_mark",
                LocationId = "beach",
                GameTimestamp = Time.time,
                Source = ClueTriggerSource.KillSabotage,
                SourceActorId = "npc_test_killer_e2",
                WitnessNpcIds = new List<string> { witness },
            };
            _stateProvider.AllClueInstances[scratch.InstanceId] = scratch;
            _stateProvider.GetPlayer().CollectedClueInstanceIds.Add(scratch.InstanceId);

            await OpenBoardAndRender();

            // pin npc ผ่าน drag การ์ดจากคลัง NPC (GroupNodeIds = [npcId])
            var npcCard = _view.NpcLibraryCardsForTests.FirstOrDefault(c => c.name == $"LibCard_{witness}");
            Assert.That(npcCard, Is.Not.Null, "คลัง NPC ต้องมีการ์ดของ witness");
            SimulateCardDrag(npcCard, DropZoneCenterScreen() + new Vector2(300f, -420f), DropZoneCenterScreen(), true);

            var dl = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < dl && !_pins.IsPinned(witness))
                await UniTask.Yield();
            await InvokeRenderAsync(_presenter); // ให้กราฟสะท้อน pin ล่าสุดแบบ deterministic

            // ✅ M(G): ทั้งสองกลุ่ม (blood + scratch) โผล่เพราะ npc ที่ pin เป็นพยานของ instance ในกลุ่ม
            // (กราฟบวมกรณี pin npc เดียวที่เป็นพยานหลายชนิด = intended behavior — บันทึกไว้ตาม spec)
            Assert.That(_pins.IsPinned(witness), Is.True, "npc ต้องถูก pin");
            Assert.That(_view.HasNode("group:คราบเลือด"), Is.True, "กลุ่ม blood ต้องโผล่ (M(G) ดึงจากพยาน)");
            Assert.That(_view.HasNode("group:รอยขีดข่วน"), Is.True, "กลุ่ม scratch ต้องโผล่ (M(G) ดึงจากพยาน)");
            Assert.That(_view.HasNode(witness), Is.True, "npc node ต้องโผล่");
            Assert.That(_view.RenderedEdgeCount, Is.GreaterThanOrEqualTo(2),
                "แต่ละกลุ่มต้องมีเส้นเชื่อมถึง npc");
            log.AppendLine($"[E] nodes = {_view.RenderedNodeCount}, edges = {_view.RenderedEdgeCount} " +
                           "(pin NPC เดียว → กราฟดึงทุกกลุ่มที่คนนี้เป็นพยาน — intended behavior)");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("E_PinSingleNpc_GroupsAppearPerWitnessedTypes", log.ToString());
            await UniTask.Yield();
        });

        // ---------- Test F: hover tooltips + ×k consistency (M(G)-only) ----------

        [UnityTest]
        public IEnumerator F_HoverTooltips_CorrectAndConsistentWithNodeLabel() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (f) Test F — {DateTime.Now:HH:mm:ss} ===");
            _viewStatic = _view;

            var npcIds = SeedPlayerAndWitnesses(1);
            var witness = npcIds[0];

            // กลุ่ม blood: instance ที่ 1 (beach — จะ pin), instance ที่ 2 (cave — ไม่ pin)
            // + ทั้งคู่ witness เดียวกัน → npc tooltip ต้อง dedupe เป็น ×2
            var inst1 = InjectClueInstance("clue_blood_stain", "beach", new List<string> { witness }, "f1");
            var collected = new List<string> { inst1 };
            var inst2 = new ClueInstance
            {
                InstanceId = $"test_blood2_{Guid.NewGuid():N}",
                DefId = "clue_blood_stain",
                LocationId = "cave", // ต่างที่ — พิสูจน์ว่า tooltip ไม่เอา instance ที่ไม่ pin มาโชว์
                GameTimestamp = Time.time,
                Source = ClueTriggerSource.KillSabotage,
                SourceActorId = "npc_test_killer_f2",
                WitnessNpcIds = new List<string> { witness },
            };
            _stateProvider.AllClueInstances[inst2.InstanceId] = inst2;
            _stateProvider.GetPlayer().CollectedClueInstanceIds.Add(inst2.InstanceId);

            // pin เฉพาะ instance ที่ 1 (pin ระดับ instance — กลุ่มมี 2 instance แต่ M(G) = 1)
            await OpenBoardAndRender();
            _pins.Pin(inst1);
            await UniTask.Delay(100);
            await InvokeRenderAsync(_presenter);

            // ---- hover graph clue node → tooltip ต้องใช้ M(G) เท่านั้น ----
            var groupKey = "group:คราบเลือด";
            Assert.That(_view.HasNode(groupKey), Is.True, "กลุ่ม blood ต้องอยู่ในกราฟ (มี instance pin)");
            var nodeGo = _view.GetNodeProxyForTests(groupKey).gameObject;
            var ped = new PointerEventData(EventSystem.current) { position = DropZoneCenterScreen() };
            ExecuteEvents.Execute(nodeGo, ped, ExecuteEvents.pointerEnterHandler);
            await UniTask.Yield();

            var clueTip = _view.TooltipTextForTests; // tooltip lazy-build ตอน Show แรก — อ่านหลัง hover
            Assert.That(clueTip, Is.Not.Null, "hover clue node แล้ว tooltip ต้องแสดง");
            log.AppendLine($"[F] clue node tooltip:\n{clueTip}");
            StringAssert.Contains("beach", clueTip, "tooltip ต้องโชว์ location ของ instance ที่ pin");
            StringAssert.DoesNotContain("cave", clueTip,
                "✅ M(G)-only: tooltip ห้ามเอา location ของ instance ที่ไม่ได้ pin มาโชว์ (กันตัวเลขเถียงกันเอง)");
            StringAssert.DoesNotContain("×2", clueTip,
                "k=1 (M(G) มี 1 instance) — ห้ามโชว์ ×2 จากทั้งกลุ่ม");
            ExecuteEvents.Execute(nodeGo, ped, ExecuteEvents.pointerExitHandler);

            // ---- hover NPC card ในคลัง → zone + witnessed (dedupe ×N) ----
            var npcCard = _view.NpcLibraryCardsForTests.FirstOrDefault(c => c.name == $"LibCard_{witness}");
            Assert.That(npcCard, Is.Not.Null, "คลัง NPC ต้องมีการ์ด witness");
            ExecuteEvents.Execute(npcCard, ped, ExecuteEvents.pointerEnterHandler);
            await UniTask.Yield();
            var npcTip = _view.TooltipTextForTests; // อ่านใหม่หลัง hover (Show แทนที่ข้อความ)
            log.AppendLine($"[F] npc card tooltip:\n{npcTip}");
            StringAssert.Contains(witness, npcTip, "tooltip npc ต้องมี id");
            StringAssert.Contains("beach", npcTip, "tooltip npc ต้องมีโซนปัจจุบัน");

            // ✅ dedupe invariant (computed — ทนต่อ residue): ผลรวม ×N + บรรทัดเดี่ยว
            // ต้องเท่ากับจำนวน instance จริงที่คนนี้เป็นพยานบน board
            var fBoardHandler = _scope.Container.Resolve<IAsyncRequestHandler<GetClueBoardRequest, GetClueBoardResponse>>();
            var fBoard = await fBoardHandler.InvokeAsync(new GetClueBoardRequest());
            var witnessLines = npcTip.Split('\n').ToList();
            var witnessIdx = witnessLines.FindIndex(l => l.TrimStart().StartsWith("เป็นพยาน"));
            var witnessedLines = witnessIdx >= 0 ? witnessLines.Skip(witnessIdx + 1).Where(l => !string.IsNullOrWhiteSpace(l)).ToList() : new List<string>();
            var actualWitnessCount = fBoard.Entries.Count(e => e != null && e.WitnessNpcIds != null && e.WitnessNpcIds.Contains(witness));
            var totalFromDisplay = 0;
            foreach (var line in witnessedLines)
            {
                var idx = line.LastIndexOf(" ×");
                totalFromDisplay += idx >= 0 && int.TryParse(line[(idx + 2)..].Trim(), out var n) ? n : 1;
            }
            Assert.That(totalFromDisplay, Is.EqualTo(actualWitnessCount),
                "display-only dedupe: ผลรวม ×N ต้องเท่ากับจำนวนเบาะแสจริงที่เป็นพยาน (ข้อมูลไม่หาย)");
            Assert.That(witnessedLines.Any(l => l.Contains(" ×")), Is.True,
                "มี instance ซ้ำชื่อ/ที่เดียวกัน → ต้องแสดงรูป ×N (ไม่ใช่บรรทัดซ้ำ)");
            log.AppendLine($"[F] npc dedupe: {actualWitnessCount} raw → {witnessedLines.Count} displayed (×N invariant ✓)");
            StringAssert.DoesNotContain("killer", npcTip, "ห้ามมี ground truth");
            ExecuteEvents.Execute(npcCard, ped, ExecuteEvents.pointerExitHandler);
            await UniTask.Yield();

            log.AppendLine("RESULT: PASS");
            WriteEvidence("F_HoverTooltips_CorrectAndConsistentWithNodeLabel", log.ToString());
            await UniTask.Yield();
        });

        // ---------- Test G: MCP set_pinned_clue → graph updates via Changed (no UI touch) ----------

        [UnityTest]
        public IEnumerator G_McpSetPinnedClue_GraphUpdatesWithoutUiInteraction() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (f) Test G — {DateTime.Now:HH:mm:ss} ===");
            _viewStatic = _view;

            var wG = SeedPlayerAndWitnesses(2);
            var collected = new List<string> { InjectClueInstance("clue_blood_stain", "beach", wG, "g") };
            await OpenBoardAndRender();
            var nodesBefore = _view.RenderedNodeCount;
            log.AppendLine($"[G] nodes before AI pin: {nodesBefore}");

            // ใช้ witness จาก inject แทน board query (board อาจ filtered)
            var witness = wG[0];
            var setHandler = _scope.Container.Resolve<IAsyncRequestHandler<SetPinnedClueRequest, SetPinnedClueResponse>>();
            var res = await setHandler.InvokeAsync(new SetPinnedClueRequest { NodeId = witness, Pinned = true });
            Assert.That(res.Success, Is.True, "AI pin ต้องสำเร็จ: " + res.FailureReason);
            Assert.That(res.PinnedNodeIds, Does.Contain(witness));

            // ✅ UI ต้องอัปเดตเองผ่าน CluePinState.Changed (ห้ามเรียก render เอง)
            var dl = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < dl && !_view.HasNode(witness))
                await UniTask.Yield();
            Assert.That(_view.HasNode(witness), Is.True, "npc node ต้องโผล่เองในกราฟ (Changed → render)");
            Assert.That(_view.RenderedNodeCount, Is.GreaterThan(nodesBefore),
                "M(G) ดึงกลุ่มที่คนนี้เป็นพยานเข้ากราฟเอง");
            log.AppendLine($"[G] nodes after AI pin: {_view.RenderedNodeCount} (npc '{witness}' + groups auto-pulled)");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("G_McpSetPinnedClue_GraphUpdatesWithoutUiInteraction", log.ToString());
            await UniTask.Yield();
        });

        // ---------- Test H: drag node position persists across re-render ----------

        [UnityTest]
        public IEnumerator H_NodeDragPosition_PersistsAcrossRerender() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (f) Test H — {DateTime.Now:HH:mm:ss} ===");
            _viewStatic = _view;

            var wH = SeedPlayerAndWitnesses(2);
            var collected = new List<string> { InjectClueInstance("clue_blood_stain", "beach", wH, "h") };
            await OpenBoardAndRender();

            var card = _view.ClueLibraryCardsForTests[0];
            var groupKey = card.GetComponent<DraggableCardHandler>().CardKey;
            SimulateCardDrag(card, DropZoneCenterScreen() + new Vector2(-200f, -420f), DropZoneCenterScreen(), true);
            var dl = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < dl && !_view.HasNode(groupKey)) await UniTask.Yield();

            var nodeGo = _view.GetNodeProxyForTests(groupKey).gameObject;
            var rt = (RectTransform)nodeGo.transform;
            var posBefore = rt.anchoredPosition;
            log.AppendLine($"[H] pos before drag: {posBefore}");

            // ลาก node ไปจุดใหม่ (ปล่อย "ใน" กรอบ — ไม่ unpin)
            var targetScreen = DropZoneCenterScreen() + new Vector2(180f, 120f);
            SimulateNodeDrag(nodeGo, targetScreen, endInside: true);
            await UniTask.Yield();
            var posAfterDrag = rt.anchoredPosition;
            Assert.That(posAfterDrag, Is.Not.EqualTo(posBefore).Within(1f), "ลากแล้วตำแหน่งต้องเปลี่ยน");
            log.AppendLine($"[H] pos after drag: {posAfterDrag}");

            // re-render → ตำแหน่งที่ผู้เล่นลากไว้ต้องคงอยู่ (custom position)
            await InvokeRenderAsync(_presenter);
            var nodeGo2 = _view.GetNodeProxyForTests(groupKey).gameObject;
            var posAfterRerender = ((RectTransform)nodeGo2.transform).anchoredPosition;
            Assert.That(posAfterRerender, Is.EqualTo(posAfterDrag).Within(1f),
                "re-render แล้วต้อง spawn ที่ตำแหน่งที่ลากไว้ (_customPositions)");
            log.AppendLine($"[H] pos after re-render: {posAfterRerender} ✓ persisted");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("H_NodeDragPosition_PersistsAcrossRerender", log.ToString());
            await UniTask.Yield();
        });

        // ---------- Test I: regression — bridge text, no remnants, npc cards not gray ----------

        [UnityTest]
        [Timeout(360000)]
        public IEnumerator I_Regression_BridgeTexts_NoRemnants_NpcCardsNotGray() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (f) Test I — {DateTime.Now:HH:mm:ss} ===");
            _viewStatic = _view;
            Assert.That(File.Exists(BridgeDll), "bridge ยังไม่ build: " + BridgeDll);

            var wI = SeedPlayerAndWitnesses(2);
            var collected = new List<string> { InjectClueInstance("clue_blood_stain", "beach", wI, "i") };
            await OpenBoardAndRender();

            // ---- bridge round-trips ยังคืน text เดิม ----
            string graphResult = null, boardResult = null;
            Exception convError = null;
            await UniTask.Run(() =>
            {
                try
                {
                    graphResult = RunBridgeConversation(log, "get_clue_graph");
                    boardResult = RunBridgeConversation(log, "get_clue_board");
                }
                catch (Exception ex) { convError = ex; }
            });
            if (convError != null) throw convError;

            var graphText = ExtractToolText(graphResult);
            Assert.IsFalse(graphResult.Contains("\"isError\":true"), "get_clue_graph ต้องไม่ error");
            StringAssert.Contains("Clue Graph", graphText, "get_clue_graph ต้องคง header เดิม");
            StringAssert.Contains("seen near:", graphText, "get_clue_graph ต้องคง format เดิม");
            log.AppendLine("[I] get_clue_graph text (ย่อ):");
            log.AppendLine(graphText.Split('\n').Take(3).Aggregate((a, b) => a + "\n" + b) + " …");

            var boardText = ExtractToolText(boardResult);
            Assert.IsFalse(boardResult.Contains("\"isError\":true"), "get_clue_board ต้องไม่ error");
            Assert.That(boardText.TrimStart().StartsWith("{"), Is.False, "ต้องไม่ใช่ JSON ดิบ");
            log.AppendLine("[I] get_clue_board text ✓");

            // ---- ไม่มีซาก click/popup/pin-row เหลืออยู่ (reflection บน view type) ----
            var viewType = typeof(ClueBoardView);
            Assert.That(viewType.GetEvent("NodeClicked"), Is.Null, "ลบ NodeClicked event แล้ว (decision 3)");
            Assert.That(viewType.GetProperty("PinnedCardCountForTests"), Is.Null, "ลบ pin-row hooks แล้ว");
            Assert.That(viewType.GetProperty("IsDetailVisible"), Is.Null, "ลบ popup hooks แล้ว");
            Assert.That(viewType.GetProperty("DetailNodeIdForTests"), Is.Null, "ลบ popup hooks แล้ว");
            Assert.That(viewType.GetProperty("PinButtonForTests"), Is.Null, "ลบ popup pin button แล้ว");
            Assert.That(AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetType("Marooned.UI.Views.GraphNodeClickProxy") != null), Is.False,
                "GraphNodeClickProxy ต้องถูกแทนด้วย GraphNodeDragProxy ทั้งหมด");
            log.AppendLine("[I] no click/popup/pin-row remnants ✓ (GraphNodeClickProxy → GraphNodeDragProxy)");

            // ---- การ์ด NPC ไม่เทา (fallback color มีพื้นความสว่าง ≥ 0.45 — decision 6) ----
            Assert.That(_view.NpcLibraryCardsForTests.Count, Is.GreaterThanOrEqualTo(1), "ต้องมีการ์ด NPC");
            var grayFound = 0;
            foreach (var npcCard in _view.NpcLibraryCardsForTests)
            {
                var portrait = npcCard.transform.Find("Portrait")?.GetComponent<Image>();
                Assert.That(portrait, Is.Not.Null, "การ์ด NPC ต้องมี Portrait");
                var c = portrait.color;
                var npcId = npcCard.name["LibCard_".Length..];
                var expected = NpcPortraitResolver.FallbackColor(npcId);
                Assert.That(c, Is.EqualTo(expected),
                    $"portrait ของ {npcId} ต้องใช้สี fallback เดิม (deterministic ต่อ npc)");
                if (c.r < 0.45f && c.g < 0.45f && c.b < 0.45f) grayFound++;
            }
            Assert.That(grayFound, Is.EqualTo(0), "ห้ามมี portrait เทา (สี fallback ยกพื้น 0.45+)");
            log.AppendLine("[I] npc portraits not gray ✓");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("I_Regression_BridgeTexts_NoRemnants_NpcCardsNotGray", log.ToString());
            await UniTask.Yield();
        });

        // ---------- Test J: SetPinnedClue description has no cap/oldest language ----------

        [UnityTest]
        public IEnumerator J_SetPinnedClueDescription_NoCapLanguage() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (f) Test J — {DateTime.Now:HH:mm:ss} ===");
            Assert.That(File.Exists(BridgeSource), "หา source ไม่เจอ: " + BridgeSource);

            var src = File.ReadAllText(BridgeSource);
            var setIdx = src.IndexOf("Pin or unpin a clue-board node", StringComparison.Ordinal);
            Assert.That(setIdx, Is.GreaterThanOrEqualTo(0), "SetPinnedClue description ต้องมีอยู่");
            var setDesc = src.Substring(setIdx, src.IndexOf("\")", setIdx, StringComparison.Ordinal) - setIdx);
            log.AppendLine("[J] SetPinnedClue description:");
            log.AppendLine(setDesc);

            StringAssert.DoesNotContain("oldest", setDesc, "ห้ามเหลือข้อความ 'Pinning a 4th node drops the oldest'");
            StringAssert.DoesNotContain("4th", setDesc, "ห้ามอ้างเพดาน 4 ตัว");
            StringAssert.Contains("No cap", setDesc, "ต้องบอกชัดว่าไม่มีเพดาน");
            StringAssert.Contains("drag-drop", setDesc, "ต้องสะท้อน workspace semantics");

            var getIdx = src.IndexOf("Get the clue-board nodes currently pinned", StringComparison.Ordinal);
            Assert.That(getIdx, Is.GreaterThanOrEqualTo(0), "GetPinnedClues description ต้องถูกแก้ด้วย");
            var getDesc = src.Substring(getIdx, src.IndexOf("\")", getIdx, StringComparison.Ordinal) - getIdx);
            StringAssert.DoesNotContain("cap of", getDesc, "GetPinnedClues ห้ามอ้างเพดาน");
            StringAssert.Contains("No cap", getDesc, "GetPinnedClues ต้องบอกว่าไม่มีเพดาน");
            log.AppendLine("[J] GetPinnedClues description ✓ (workspace semantics, no cap)");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("J_SetPinnedClueDescription_NoCapLanguage", log.ToString());
            await UniTask.Yield();
        });

        // ---------- Test K: stale pin pruned on next render ----------

        [UnityTest]
        public IEnumerator K_StalePin_PruneOnNextRender() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (f) Test K — {DateTime.Now:HH:mm:ss} ===");
            _viewStatic = _view;

            var wK = SeedPlayerAndWitnesses(1);
            var collected = new List<string> { InjectClueInstance("clue_blood_stain", "beach", wK, "k") };
            await OpenBoardAndRender();

            // inject id ปลอมเข้า pin list ตรง ๆ (เลี่ยงทุก path ปกติ — จำลอง state ตกค้าง)
            var fakeId = "stale_instance_gone_" + Guid.NewGuid().ToString("N")[..8];
            _pins.Pin(fakeId);
            await UniTask.Yield();                       // Changed → render → prune กวาด fake ทันที
            var dl = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < dl && _pins.IsPinned(fakeId)) await UniTask.Yield();
            Assert.That(_pins.IsPinned(fakeId), Is.False,
                "prune ต้องกวาด fake pin ใน render แรกหลัง pin (Changed → RenderAsync → PruneDead) — ");

            // เตรียม live pin: seed แล้ว pin ผ่าน path ปกติ (live ต้องรอด prune)
            var liveId = collected[0];
            _pins.Pin(liveId);
            var kGraphDl = Time.realtimeSinceStartup + 5f;
            while (Time.realtimeSinceStartup < kGraphDl && !_view.HasNode("group:คราบเลือด"))
            {
                await InvokeRenderAsync(_presenter);
                await UniTask.Yield();
            }
            Assert.That(_pins.IsPinned(liveId), Is.True, "pin ที่ยังมีจริงต้องไม่โดน prune");
            Assert.That(_view.HasNode("group:คราบเลือด"), Is.True, "กราฟยังวาดจาก pin ที่ live ✓");
            log.AppendLine($"[K] after render: pinned = [{string.Join(", ", _pins.PinnedNodeIds)}] — fake swept");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("K_StalePin_PruneOnNextRender", log.ToString());
            await UniTask.Yield();
        });

        // ---------- helpers ----------

        static async UniTask InvokeRenderAsync(ClueBoardPresenter presenter)
        {
            var task = (UniTask)typeof(ClueBoardPresenter)
                .GetMethod("RenderAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                !.Invoke(presenter, null)!;
            await task;
        }

        /// <summary>บทสนทนา MCP stdio (รันบน threadpool — block ได้) คืน tools/call response ของ tool ที่กำหนด
        /// (arguments = payload ของ tools/call — anonymous object, serialize ด้วย MiniSerialize)</summary>
        string RunBridgeConversation(StringBuilder log, string toolName, object arguments = null)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{BridgeDll}\"",
                WorkingDirectory = Path.Combine(RepoRoot, "McpBridge"),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var bridge = new System.Diagnostics.Process { StartInfo = psi };
            bridge.Start();
            var stderr = new StringBuilder();
            bridge.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            bridge.BeginErrorReadLine();

            try
            {
                using var writer = bridge.StandardInput;
                using var reader = bridge.StandardOutput;
                var id = 0;

                string Rpc(string method, object payload)
                {
                    var myId = ++id;
                    writer.WriteLine($"{{\"jsonrpc\":\"2.0\",\"id\":{myId},\"method\":\"{method}\",\"params\":{MiniSerialize(payload)}}}");
                    writer.Flush();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < 40)
                    {
                        var line = reader.ReadLine();
                        if (line == null) throw new Exception($"bridge ปิด stream ก่อนตอบ {method}\nstderr: {stderr}");
                        if (!line.Contains($"\"id\":{myId}")) continue;
                        if (line.Contains("\"error\""))
                            throw new Exception($"{method} คืน error: {Truncate(line, 400)}\nstderr: {stderr}");
                        return line;
                    }
                    throw new TimeoutException($"{method} ไม่ตอบใน 40 วิ\nstderr: {stderr}");
                }

                Rpc("initialize", new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "clue-f-test", version = "0.1" } });
                writer.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
                writer.Flush();

                return Rpc("tools/call", new { name = toolName, arguments = arguments ?? (object)new { } });
            }
            finally
            {
                try { bridge.Kill(); } catch { }
                bridge.Dispose();
            }
        }

        static string ExtractToolText(string toolsCallResponse)
        {
            var needle = "\"text\":\"";
            var start = toolsCallResponse.IndexOf(needle, StringComparison.Ordinal);
            if (start < 0) return string.Empty;
            start += needle.Length;
            var sb = new StringBuilder();
            for (var i = start; i < toolsCallResponse.Length; i++)
            {
                var c = toolsCallResponse[i];
                if (c == '"' && toolsCallResponse[i - 1] != '\\') break;
                sb.Append(c);
            }
            return sb.ToString().Replace("\\n", "\n").Replace("\\u002D", "-");
        }

        static string MiniSerialize(object obj)
        {
            switch (obj)
            {
                case null: return "null";
                case string s: return $"\"{s}\"";
                case bool b: return b ? "true" : "false"; // JSON lowercase
                case int i: return i.ToString();
                default:
                    var sb = new StringBuilder("{");
                    var first = true;
                    foreach (var p in obj.GetType().GetProperties())
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append($"\"{p.Name}\":{MiniSerialize(p.GetValue(obj))}");
                    }
                    sb.Append('}');
                    return sb.ToString();
            }
        }

        static string Truncate(string s, int max) => s != null && s.Length > max ? s[..max] + "…" : s;

        private void WriteEvidence(string test, string body)
        {
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", EvidenceDir));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{test}.txt"), body + Environment.NewLine);
            Debug.Log($"[ClueBoardUiPlayModeTests] {test} PASS — evidence: {EvidenceDir}/");
        }
    }
}
