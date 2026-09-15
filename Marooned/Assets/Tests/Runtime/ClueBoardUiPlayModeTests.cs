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
    /// Clue System v2 — Part (e): Presentation layer (PlayMode evidence)
    ///
    /// A: MCP tool get_clue_graph ต้องคืน human-readable text summary (ผ่าน bridge stdio จริง)
    ///    — ไม่ใช่ JSON/debug string ("nodes: [" / "edges: [" ต้องหายไป)
    /// B: ClueBoardView radial layout — clue nodes วงใน (r≈150), npc วงนอก (r≈300),
    ///    เฉพาะ npc ที่มี edge, มีเส้นเชื่อมครบทุก edge, container อยู่ใต้ Canvas
    /// C: Reactivity — เกิด ClueGeneratedMessage (ผ่าน ClueGenerationSystem.TryGenerate จริง)
    ///    → presenter re-render ทันที (จำนวน node เพิ่ม)
    /// </summary>
    public class ClueBoardUiPlayModeTests
    {
        public const string EvidenceDir = "TestEvidence/clue-system-v2-e";

        static readonly string RepoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        static readonly string BridgeDll = Path.Combine(RepoRoot, "McpBridge", "bin", "Debug", "net8.0", "McpBridge.dll");

        GameLifetimeScope _scope;
        GameStateProvider _stateProvider;
        ClueBoardView _view;

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
            Assert.That(_view != null, "ClueBoardView ต้อง resolve ได้ (RegisterComponentInHierarchy)");
            await UniTask.Yield();
        });

        // ---------- Test A: MCP tool returns human-readable summary ----------

        [UnityTest]
        [Timeout(360000)]
        public IEnumerator A_Bridge_GetClueGraph_ReturnsHumanReadableSummary() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (e) Test A (bridge) — {DateTime.Now:HH:mm:ss} ===");
            Assert.That(File.Exists(BridgeDll), "bridge ยังไม่ build: " + BridgeDll);

            // seed อย่างน้อย 1 clue เข้า board ก่อน (blood 100% — เจอทันที)
            var player = _stateProvider.GetPlayer();
            player.CurrentLocationId = "beach";
            var gen = _scope.Container.Resolve<ClueGenerationSystem>();
            var seeded = gen.TryGenerate(ClueTriggerSource.KillSabotage, "beach", "npc_test_killer");
            Assert.That(seeded.Count, Is.GreaterThanOrEqualTo(1), "blood (100%) ต้องเกิด");
            var investigate = _scope.Container.Resolve<IAsyncRequestHandler<InvestigateClueRequest, InvestigateClueResponse>>();
            var inv = await investigate.InvokeAsync(new InvestigateClueRequest());
            Assert.IsTrue(inv.Success, "seed clue ต้องเก็บได้: " + inv.FailureReason);
            log.AppendLine($"[seed] investigate @ beach → {inv.FoundInstanceId[..Math.Min(8, inv.FoundInstanceId.Length)]}…");

            string result = null;
            Exception convError = null;
            await UniTask.Run(() =>
            {
                try { result = RunBridgeConversation(log, "get_clue_graph"); }
                catch (Exception ex) { convError = ex; }
            });
            if (convError != null) throw convError;

            // ⚠️ ต้องเป็น text summary — ไม่ใช่ debug format เดิม
            Assert.IsFalse(result.Contains("\"isError\":true"), "tools/call ต้องไม่ error: " + result);
            var text = ExtractToolText(result);
            log.AppendLine("[A] get_clue_graph output:");
            log.AppendLine(text);
            Assert.IsFalse(text.Contains("nodes:"), "ต้องไม่ใช่ debug format เดิม (nodes: [...])");
            Assert.IsFalse(text.TrimStart().StartsWith("{"), "ต้องไม่ใช่ JSON ดิบ");
            StringAssert.Contains("seen near:", text, "ต้องมีบรรทัดรูปแบบ '<clue> — seen near: <witnesses>'");
            StringAssert.Contains("Clue Graph", text, "ต้องมี header 'Clue Graph — N clue(s)'");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("A_Bridge_GetClueGraph_ReturnsHumanReadableSummary", log.ToString());
            await UniTask.Yield();
        });

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

                Rpc("initialize", new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "clue-e-test", version = "0.1" } });
                writer.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
                writer.Flush();

                // tools/call (arguments ว่างได้ — query tool ไม่มี parameter)
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
            // text content ของ MCP: {"content":[{"type":"text","text":"..."}]}
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

        // ---------- Test B: radial layout ----------

        [UnityTest]
        public IEnumerator B_View_RadialLayout_InnerClues_OuterNpcs_WithEdges() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (e) Test B — {DateTime.Now:HH:mm:ss} ===");

            // seed 2 clue (investigate ทั้งคู่) + witness ในโซน — เลือกเฉพาะ NPC ที่ยังมีชีวิต
            // (NPC ตายแล้วจาก ambient kill ไม่ผ่าน witness filter → จะไม่มี edge เกิด)
            var player = _stateProvider.GetPlayer();
            var director = _scope.Container.Resolve<NpcDirectorSystem>();
            player.CurrentLocationId = "beach";
            var npcIds = director.Npcs.Values.Where(n => n.IsAlive).Select(n => n.Id).Take(2).ToList();
            Assert.That(npcIds.Count, Is.GreaterThanOrEqualTo(1), "ต้องมี NPC มีชีวิตอย่างน้อย 1 (ทำ witness ได้)");
            foreach (var id in npcIds) director.MoveNpc(id, "beach");

            var gen = _scope.Container.Resolve<ClueGenerationSystem>();
            var investigate = _scope.Container.Resolve<IAsyncRequestHandler<InvestigateClueRequest, InvestigateClueResponse>>();
            var seeded = 0;
            for (var round = 0; round < 3 && seeded < 2; round++)
            {
                gen.TryGenerate(ClueTriggerSource.KillSabotage, "beach", "npc_test_killer_" + round); // blood=100%
                var r = await investigate.InvokeAsync(new InvestigateClueRequest());
                if (r.Success) seeded++;
            }
            Assert.That(seeded, Is.GreaterThanOrEqualTo(1), "ต้อง seed clue เข้า board ได้อย่างน้อย 1");
            log.AppendLine($"[seed] collected clues: {player.CollectedClueInstanceIds.Count}");

            // render ผ่าน presenter จริง (เรียก handler ที่มีอยู่ — reuse path เดียวกับเกม)
            var presenter = _scope.Container.Resolve<ClueBoardPresenter>();
            Assert.That(presenter != null, "ClueBoardPresenter ต้อง resolve ได้ (RegisterEntryPoint)");
            var renderTask = (UniTask)typeof(ClueBoardPresenter)
                .GetMethod("RenderAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(presenter, null)!;
            await renderTask;

            Assert.That(_view.RenderedNodeCount, Is.GreaterThanOrEqualTo(1), "ต้องมี node อย่างน้อย 1 (clue ที่เก็บแล้ว)");
            Assert.That(_view.RenderedEdgeCount, Is.GreaterThanOrEqualTo(1), "clue ที่มี witness ต้องมี edge");
            Assert.That(_view.ContainerForTests.GetComponentInParent<Canvas>(true), Is.Not.Null,
                "graph container ต้องอยู่ใต้ Canvas (panel เริ่ม inactive — ต้อง includeInactive)");

            // ---- ตรวจ radial layout จาก RectTransform จริง ----
            // (แยก clue/npc ด้วย sizeDelta — clue=110, npc=80; ทดสอบ asmdef ไม่อ้าง TMPro)
            var clueNodes = new List<RectTransform>();
            var npcNodes = new List<RectTransform>();
            foreach (Transform child in _view.ContainerForTests)
            {
                if (!child.name.StartsWith("ClueNode_")) continue;
                if (IsNpcNode(child)) npcNodes.Add((RectTransform)child);
                else clueNodes.Add((RectTransform)child);
            }

            var innerDistances = clueNodes.Select(c => c.anchoredPosition.magnitude).ToList();
            var outerDistances = npcNodes.Select(c => c.anchoredPosition.magnitude).ToList();
            log.AppendLine($"[B] clue nodes (inner): {clueNodes.Count} @ r≈{string.Join(",", innerDistances.Select(d => d.ToString("F0")))}");
            log.AppendLine($"[B] npc nodes (outer): {npcNodes.Count} @ r≈{string.Join(",", outerDistances.Select(d => d.ToString("F0")))}");
            log.AppendLine($"[B] edges rendered: {_view.RenderedEdgeCount}");

            foreach (var d in innerDistances)
                Assert.That(d, Is.EqualTo(150f).Within(1f), "clue nodes ต้องอยู่วงใน radius 150");
            foreach (var d in outerDistances)
                Assert.That(d, Is.EqualTo(300f).Within(1f), "npc nodes ต้องอยู่วงนอก radius 300");

            // นับ edge GameObject จริง
            var edgeCount = 0;
            foreach (Transform child in _view.ContainerForTests)
                if (child.name == "ClueEdge") edgeCount++;
            Assert.That(edgeCount, Is.EqualTo(_view.RenderedEdgeCount), "จำนวนเส้นเชื่อมต้องตรงจำนวน edge");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("B_View_RadialLayout_InnerClues_OuterNpcs_WithEdges", log.ToString());
            await UniTask.Yield();
        });

        static bool IsNpcNode(Transform node)
        {
            // npc node ขนาดเล็กกว่า (80 กว่า 110) — ใช้ sizeDelta เป็น discriminator
            var rt = (RectTransform)node;
            return rt.sizeDelta.x < 100f;
        }

        // ---------- Test C: reactivity ----------

        [UnityTest]
        public IEnumerator C_View_Rerenders_OnClueGeneratedMessage() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (e) Test C — {DateTime.Now:HH:mm:ss} ===");

            var player = _stateProvider.GetPlayer();
            player.CurrentLocationId = "beach";

            // render ครั้งแรก (state ปัจจุบัน)
            var presenter = _scope.Container.Resolve<ClueBoardPresenter>();
            await InvokeRenderAsync(presenter);
            var beforeNodes = _view.RenderedNodeCount;
            log.AppendLine($"[C] nodes before: {beforeNodes}");

            var gen = _scope.Container.Resolve<ClueGenerationSystem>();
            var investigate = _scope.Container.Resolve<IAsyncRequestHandler<InvestigateClueRequest, InvestigateClueResponse>>();

            // board แสดงเฉพาะ clue ที่ "เก็บแล้ว" — เก็บ clue แรกเข้า board ก่อน
            // (ClueGeneratedMessage ยิงตอน generate — ก่อน collect ทำให้ render รอบนั้น
            // ยังไม่เห็น clue บน board; render ถัดไปจาก message ตัวใหม่ต้องเห็น)
            var first = gen.TryGenerate(ClueTriggerSource.KillSabotage, "beach", "npc_test_killer_c1");
            Assert.That(first.Count, Is.GreaterThanOrEqualTo(1), "blood (100%) ต้องเกิด");
            var inv = await investigate.InvokeAsync(new InvestigateClueRequest());
            Assert.IsTrue(inv.Success, "ต้องเก็บ clue เข้า board ได้: " + inv.FailureReason);
            log.AppendLine($"[C] collected clue {inv.FoundInstanceId[..Math.Min(8, inv.FoundInstanceId.Length)]}… (ยังไม่ re-render) — nodes now: {_view.RenderedNodeCount}");
            // (ห้าม assert strict ตรงนี้ — ambient kill จาก AI อาจยิง message ระหว่างทาง)

            // เกิด clue ใหม่ → ClueGeneratedMessage → presenter subscribe แล้ว re-render เอง
            // (render ครั้งนี้รวม clue ที่เก็บไว้แล้วด้วย → จำนวน node ต้องเพิ่ม)
            gen.TryGenerate(ClueTriggerSource.KillSabotage, "beach", "npc_test_killer_c2");

            // รอ render ที่ trigger โดย message (ไม่เรียกเอง — พิสูจน์ reactivity)
            var deadline = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < deadline && _view.RenderedNodeCount <= beforeNodes)
                await UniTask.Yield();

            log.AppendLine($"[C] nodes after: {_view.RenderedNodeCount}");
            Assert.That(_view.RenderedNodeCount, Is.GreaterThan(beforeNodes),
                "ClueGeneratedMessage ต้อง trigger re-render ทันที (กราฟรวม clue ที่เก็บแล้ว)");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("C_View_Rerenders_OnClueGeneratedMessage", log.ToString());
            await UniTask.Yield();
        });

        // ---------- Test D: clue node click → detail popup ----------

        [UnityTest]
        public IEnumerator D_NodeClick_ShowsDetailPopup_WithBoardFacts() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (e) Test D — {DateTime.Now:HH:mm:ss} ===");

            // ---- จัดฉาก: player @ beach + NPC มีชีวิต 2 ตัว (witness) + seed 1 clue + collect ----
            var player = _stateProvider.GetPlayer();
            var director = _scope.Container.Resolve<NpcDirectorSystem>();
            player.CurrentLocationId = "beach";
            var npcIds = director.Npcs.Values.Where(n => n.IsAlive).Select(n => n.Id).Take(2).ToList();
            Assert.That(npcIds.Count, Is.GreaterThanOrEqualTo(1), "ต้องมี NPC มีชีวิตอย่างน้อย 1");
            foreach (var id in npcIds) director.MoveNpc(id, "beach");

            var gen = _scope.Container.Resolve<ClueGenerationSystem>();
            var investigate = _scope.Container.Resolve<IAsyncRequestHandler<InvestigateClueRequest, InvestigateClueResponse>>();
            gen.TryGenerate(ClueTriggerSource.KillSabotage, "beach", "npc_test_killer_d");
            var inv = await investigate.InvokeAsync(new InvestigateClueRequest());
            Assert.IsTrue(inv.Success, "seed clue ต้องเก็บได้: " + inv.FailureReason);
            var targetId = inv.FoundInstanceId;

            // ---- render ผ่าน presenter (panel เปิด — popup ต้อง active-in-hierarchy จึงนับ) ----
            var presenter = _scope.Container.Resolve<ClueBoardPresenter>();
            _view.gameObject.SetActive(true);
            await InvokeRenderAsync(presenter);
            Assert.That(_view.RenderedNodeCount, Is.GreaterThanOrEqualTo(1), "ต้องมี node ก่อนคลิก");

            // ---- simulate คลิกจริงผ่าน EventSystem (ExecuteEvents — path เดียวกับเกม) ----
            Transform nodeTransform = null;
            foreach (Transform child in _view.ContainerForTests)
                if (child.name == $"ClueNode_{targetId}") { nodeTransform = child; break; }
            Assert.That(nodeTransform != null, "clue node ต้องถูก spawn ชื่อ ClueNode_<id>");

            var proxy = nodeTransform.GetComponent<GraphNodeClickProxy>();
            Assert.That(proxy != null, "clue node ต้องมี GraphNodeClickProxy");
            ExecuteEvents.Execute(nodeTransform.gameObject, new PointerEventData(EventSystem.current),
                ExecuteEvents.pointerClickHandler);

            // popup เปิดตอน ShowDetailAsync กลับจาก board handler — poll สั้น ๆ
            var deadline = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < deadline && !_view.IsDetailVisible)
                await UniTask.Yield();
            Assert.That(_view.IsDetailVisible, Is.True, "คลิก clue node ต้องเปิด popup");
            Assert.That(_view.DetailNodeIdForTests, Is.EqualTo(targetId));

            // ---- popup ต้องโชว์ facts จาก ClueBoardEntry (reuse GetClueBoardHandler) ----
            var board = await _scope.Container
                .Resolve<IAsyncRequestHandler<GetClueBoardRequest, GetClueBoardResponse>>()
                .InvokeAsync(new GetClueBoardRequest());
            var expected = board.Entries.First(e => e.InstanceId == targetId);
            var detailText = _view.DetailTextForTests;
            log.AppendLine($"[D] popup text: {detailText}");
            StringAssert.Contains(expected.DisplayName, detailText, "popup ต้องโชว์ชื่อ clue");
            StringAssert.Contains(expected.Reliability, detailText, "popup ต้องโชว์ความน่าเชื่อถือ");
            StringAssert.Contains(expected.LocationId, detailText, "popup ต้องโชว์สถานที่");
            foreach (var w in expected.WitnessNpcIds)
                StringAssert.Contains(w, detailText, $"popup ต้องโชว์พยาน {w} (ผ่าน filter แล้ว)");

            // ---- toggle ปิดด้วยการคลิกซ้ำ + ทุก node (รวม npc) ต้องมี click proxy ----
            ExecuteEvents.Execute(nodeTransform.gameObject, new PointerEventData(EventSystem.current),
                ExecuteEvents.pointerClickHandler);
            Assert.That(_view.IsDetailVisible, Is.False, "คลิก clue เดิมซ้ำ = ปิด popup");

            var npcNodeChecked = false;
            foreach (Transform child in _view.ContainerForTests)
            {
                if (!child.name.StartsWith("ClueNode_")) continue;
                var rt = (RectTransform)child;
                if (rt.sizeDelta.x < 100f) // npc discriminator เดียวกับ Test B
                {
                    npcNodeChecked = true;
                    Assert.That(child.GetComponent<GraphNodeClickProxy>(), Is.Not.Null,
                        "npc node ต้องรับคลิกได้ด้วย (โซน/อาลิไบ) — ไม่มี node display-only แล้ว");
                }
            }
            log.AppendLine($"[D] npc nodes verified clickable: {npcNodeChecked}");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("D_NodeClick_ShowsDetailPopup_WithBoardFacts", log.ToString());
            await UniTask.Yield();
        });

        // ---------- Test E: HUD toggle button ----------

        [UnityTest]
        public IEnumerator E_HudButton_TogglesBoard_PanelStartsHidden() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (e) Test E — {DateTime.Now:HH:mm:ss} ===");

            // ---- board เริ่มซ่อน (scene m_IsActive: 0) — ปิดค้างไว้ก่อนพิสูจน์ ----
            _view.gameObject.SetActive(false);
            _view.EnsureToggleButton(); // idempotent — ปุ่มอยู่บน Canvas (พ่อของ panel)

            var button = GameObject.Find("ClueBoardToggleButton");
            Assert.That(button != null, "ปุ่ม HUD ต้องถูกสร้างบน Canvas");
            Assert.That(button.transform.parent == _view.transform.parent,
                "ปุ่มต้องเป็น sibling ของ panel (บน Canvas) — ไม่งั้นถูกซ่อนพร้อม panel");

            // ---- กดปุ่ม (ผ่าน Button.onClick — path เดียวกับเกม) ----
            button.GetComponent<Button>().onClick.Invoke();
            await UniTask.Yield();
            Assert.That(_view.gameObject.activeSelf, Is.True, "กดปุ่มต้องเปิดกระดาน");

            // ---- กดซ้ำ = ปิด + ปุ่มยังอยู่แม้กระดานซ่อน ----
            button.GetComponent<Button>().onClick.Invoke();
            await UniTask.Yield();
            Assert.That(_view.gameObject.activeSelf, Is.False, "กดปุ่มซ้ำต้องปิดกระดาน");
            Assert.That(button != null && button.activeInHierarchy, Is.True, "ปุ่มต้องยัง active แม้กระดานซ่อน");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("E_HudButton_TogglesBoard_PanelStartsHidden", log.ToString());
            await UniTask.Yield();
        });

        // ---------- Test G: pin multiple nodes → side-by-side comparison cards ----------

        [UnityTest]
        public IEnumerator G_PinMultipleNodes_ShowsSideBySideCards_WithCapAndClose() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (e) Test G — {DateTime.Now:HH:mm:ss} ===");

            // ---- จัดฉาก: player + 2 NPC มีชีวิต @ beach + seed/collect 2 clue (→ 4 nodes) ----
            var player = _stateProvider.GetPlayer();
            var director = _scope.Container.Resolve<NpcDirectorSystem>();
            player.CurrentLocationId = "beach";
            var npcIds = director.Npcs.Values.Where(n => n.IsAlive).Select(n => n.Id).Take(2).ToList();
            Assert.That(npcIds.Count, Is.GreaterThanOrEqualTo(1), "ต้องมี NPC มีชีวิตอย่างน้อย 1");
            foreach (var id in npcIds) director.MoveNpc(id, "beach");

            var gen = _scope.Container.Resolve<ClueGenerationSystem>();
            var investigate = _scope.Container.Resolve<IAsyncRequestHandler<InvestigateClueRequest, InvestigateClueResponse>>();
            for (var i = 0; i < 3 && player.CollectedClueInstanceIds.Count < 2; i++)
            {
                gen.TryGenerate(ClueTriggerSource.KillSabotage, "beach", "npc_test_killer_g" + i);
                await investigate.InvokeAsync(new InvestigateClueRequest());
            }
            Assert.That(player.CollectedClueInstanceIds.Count, Is.GreaterThanOrEqualTo(2),
                "ต้องมี clue บน board อย่างน้อย 2 (จะ pin เทียบหลายตัว)");

            var presenter = _scope.Container.Resolve<ClueBoardPresenter>();
            _view.gameObject.SetActive(true);
            await InvokeRenderAsync(presenter);

            // ---- เก็บ node ทั้งหมด (clue ก่อน แล้ว npc — ตามลำดับ spawn) ----
            var clueIds = new List<string>();
            var npcNodeIds = new List<string>();
            var nodeTransforms = new Dictionary<string, Transform>();
            foreach (Transform child in _view.ContainerForTests)
            {
                if (!child.name.StartsWith("ClueNode_")) continue;
                var id = child.name["ClueNode_".Length..];
                nodeTransforms[id] = child;
                if (((RectTransform)child).sizeDelta.x >= 100f) clueIds.Add(id);
                else npcNodeIds.Add(id);
            }
            Assert.That(clueIds.Count, Is.GreaterThanOrEqualTo(1), "ต้องมี clue node อย่างน้อย 1");
            Assert.That(npcNodeIds.Count, Is.GreaterThanOrEqualTo(1), "ต้องมี npc node อย่างน้อย 1 (มี edge)");

            // ---- pin ตามลำดับผ่าน popup (คลิก node → ปุ่ม [ปักหมุด] — path เดียวกับเกม) ----
            var expected = 0;
            var pinOrder = new List<string>();
            async UniTask PinViaPopup(string nodeId)
            {
                ExecuteEvents.Execute(nodeTransforms[nodeId].gameObject,
                    new PointerEventData(EventSystem.current), ExecuteEvents.pointerClickHandler);
                var dl = Time.realtimeSinceStartup + 10f;
                while (Time.realtimeSinceStartup < dl && _view.DetailNodeIdForTests != nodeId) await UniTask.Yield();
                Assert.That(_view.DetailNodeIdForTests, Is.EqualTo(nodeId), "คลิก node ต้องสลับ popup ก่อนกด pin");
                Assert.That(_view.PinButtonForTests != null, "popup ต้องมีปุ่ม [ปักหมุด]");
                _view.PinButtonForTests.onClick.Invoke();
                expected = Mathf.Min(expected + 1, 3); // cap ฝั่ง presenter = 3
                dl = Time.realtimeSinceStartup + 10f;
                while (Time.realtimeSinceStartup < dl && _view.PinnedCardCountForTests != expected) await UniTask.Yield();
                Assert.That(_view.PinnedCardCountForTests, Is.EqualTo(expected), $"pin {nodeId} → {expected} การ์ด");
                pinOrder.Add(nodeId);
            }

            await PinViaPopup(clueIds[0]);
            await PinViaPopup(npcNodeIds[0]);
            Assert.That(_view.PinnedCardCountForTests, Is.EqualTo(2), "pin 2 → 2 การ์ด side-by-side");
            log.AppendLine($"[G] pinned: [{string.Join(", ", pinOrder)}]");

            // ---- เรียงซ้าย→ขวาตามลำดับ pin + ป้าย PinCard_<id> + badge ทอง ----
            var pinRow = _view.ContainerForTests.parent.Find("CluePinRow");
            Assert.That(pinRow != null, "ต้องมีแถว pin ใต้ panel");
            var left = pinRow.Find($"PinCard_{pinOrder[0]}");
            var right = pinRow.Find($"PinCard_{pinOrder[1]}");
            Assert.That(left != null && right != null, "การ์ดต้องตั้งชื่อ PinCard_<nodeId>");
            Assert.That(((RectTransform)left).anchoredPosition.x < ((RectTransform)right).anchoredPosition.x,
                "pin ก่อนต้องอยู่ซ้าย — เรียงตามลำดับ pin");
            Assert.That(nodeTransforms[pinOrder[0]].Find("PinBadge") != null
                && nodeTransforms[pinOrder[1]].Find("PinBadge") != null,
                "node ที่ pin ต้องมี badge จุดทอง");

            // ---- cap: pin จนครบ 4 (ถ้า node พอ) → ตัวเก่าสุดถูกตัด, การ์ดคง 3 ----
            var extra = clueIds.Concat(npcNodeIds).Where(id => !pinOrder.Contains(id)).ToList();
            if (extra.Count >= 2)
            {
                await PinViaPopup(extra[0]);
                var oldest = pinOrder[0];
                await PinViaPopup(extra[1]);
                Assert.That(_view.PinnedCardCountForTests, Is.EqualTo(3), "cap ที่ 3 การ์ด");
                var dl3 = Time.realtimeSinceStartup + 10f;
                while (Time.realtimeSinceStartup < dl3 && pinRow.Find($"PinCard_{oldest}") != null) await UniTask.Yield();
                Assert.That(pinRow.Find($"PinCard_{oldest}") == null,
                    "pin ตัวที่ 4 → ตัวเก่าสุดต้องถูกตัดออก");
                log.AppendLine("[G] cap test: pinned 4 → oldest dropped, cards=3");
            }
            else
            {
                log.AppendLine("[G] cap test skipped (nodes ไม่พอ)");
            }

            // ---- × บนการ์ดถอนหมุด ----
            var beforeClose = _view.PinnedCardCountForTests;
            string anyPinned = null;
            foreach (Transform child in pinRow)
                if (child.name.StartsWith("PinCard_")) { anyPinned = child.name["PinCard_".Length..]; break; }
            Assert.That(_view.TryGetPinCardCloseForTests(anyPinned, out var closeBtn), Is.True,
                "การ์ด pin ต้องมีปุ่ม ×");
            closeBtn.onClick.Invoke();
            var afterClose = beforeClose - 1;
            var dl2 = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < dl2 && _view.PinnedCardCountForTests != afterClose) await UniTask.Yield();
            Assert.That(_view.PinnedCardCountForTests, Is.EqualTo(afterClose), "กด × → การ์ดหาย 1");
            log.AppendLine($"[G] close button unpins → {afterClose}");

            // ---- refresh ข้าม re-render: pin ค้างไว้ — re-render ต้องวาดใหม่จาก pin list จำนวนเดิม ----
            await InvokeRenderAsync(presenter);
            Assert.That(_view.PinnedCardCountForTests, Is.EqualTo(afterClose),
                "re-render → แถว pin วาดใหม่ (ข้อมูลสด) จำนวนคงเดิม");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("G_PinMultipleNodes_ShowsSideBySideCards_WithCapAndClose", log.ToString());
            await UniTask.Yield();
        });

        // ---------- Test F: npc witness click → zone + witnessed clues (alibi) ----------

        [UnityTest]
        public IEnumerator F_NpcNodeClick_ShowsZone_AndWitnessedClues() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (e) Test F — {DateTime.Now:HH:mm:ss} ===");

            // ---- จัดฉาก: player + NPC มีชีวิต 2 ตัว @ beach, seed 1 clue + collect ----
            var player = _stateProvider.GetPlayer();
            var director = _scope.Container.Resolve<NpcDirectorSystem>();
            player.CurrentLocationId = "beach";
            var npcIds = director.Npcs.Values.Where(n => n.IsAlive).Select(n => n.Id).Take(2).ToList();
            Assert.That(npcIds.Count, Is.GreaterThanOrEqualTo(1), "ต้องมี NPC มีชีวิตอย่างน้อย 1");
            foreach (var id in npcIds) director.MoveNpc(id, "beach");

            var gen = _scope.Container.Resolve<ClueGenerationSystem>();
            var investigate = _scope.Container.Resolve<IAsyncRequestHandler<InvestigateClueRequest, InvestigateClueResponse>>();
            gen.TryGenerate(ClueTriggerSource.KillSabotage, "beach", "npc_test_killer_f");
            var inv = await investigate.InvokeAsync(new InvestigateClueRequest());
            Assert.IsTrue(inv.Success, "seed clue ต้องเก็บได้: " + inv.FailureReason);

            var boardHandler = _scope.Container.Resolve<IAsyncRequestHandler<GetClueBoardRequest, GetClueBoardResponse>>();
            var board = await boardHandler.InvokeAsync(new GetClueBoardRequest());

            var presenter = _scope.Container.Resolve<ClueBoardPresenter>();
            _view.gameObject.SetActive(true);
            await InvokeRenderAsync(presenter);

            // ---- หา npc witness node ในกราฟ (outer ring, มีชื่อใน witness list ที่ผ่าน filter) ----
            string npcId = null;
            Transform npcTransform = null;
            foreach (Transform child in _view.ContainerForTests)
            {
                if (!child.name.StartsWith("ClueNode_")) continue;
                var rt = (RectTransform)child;
                if (rt.sizeDelta.x >= 100f) continue; // npc = size 80 (discriminator เดียวกับ Test B)
                var id = child.name["ClueNode_".Length..];
                if (board.Entries.Any(e => e.WitnessNpcIds != null && e.WitnessNpcIds.Contains(id)))
                {
                    npcId = id;
                    npcTransform = child;
                    break;
                }
            }
            Assert.That(npcId != null, "ต้องมี npc witness node ในกราฟ (clue ที่ seed ต้องมีพยาน)");

            // pin โซนก่อนคลิก (กัน ambient move ระหว่าง test) — MoveNpc set location แบบ sync
            director.MoveNpc(npcId, "beach");
            var expectedZone = director.Npcs[npcId].CurrentLocationId;
            var expectedClue = board.Entries.First(e => e.WitnessNpcIds.Contains(npcId));

            // ---- คลิก npc node ผ่าน EventSystem (path เดียวกับเกม) ----
            ExecuteEvents.Execute(npcTransform.gameObject, new PointerEventData(EventSystem.current),
                ExecuteEvents.pointerClickHandler);

            var deadline = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < deadline && !_view.IsDetailVisible)
                await UniTask.Yield();
            Assert.That(_view.IsDetailVisible, Is.True, "คลิก npc node ต้องเปิด popup");
            Assert.That(_view.DetailNodeIdForTests, Is.EqualTo(npcId));

            // ---- popup ต้องโชว์ โซน (player-visible) + เบาะแสที่เป็นพยาน (อาลิไบคร่าว ๆ) ----
            var detailText = _view.DetailTextForTests;
            log.AppendLine($"[F] popup text: {detailText}");
            StringAssert.Contains(npcId, detailText, "popup ต้องโชว์ id ของ npc");
            StringAssert.Contains(expectedZone, detailText, "popup ต้องโชว์โซนปัจจุบัน (player-visible — chibi เดินอยู่จริง)");
            StringAssert.Contains(expectedClue.LocationId, detailText,
                "popup ต้องโชว์เบาะแสที่คนนี้เป็นพยาน (alibi — ยอมรับว่าอยู่แถวนั้นตอนนั้น)");

            // ---- ground-truth leak guard: popup ห้ามมีข้อมูล killer/role หลุดมา ----
            StringAssert.DoesNotContain("killer", detailText, "popup ห้ามมี ground truth ใด ๆ");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("F_NpcNodeClick_ShowsZone_AndWitnessedClues", log.ToString());
            await UniTask.Yield();
        });        // ---------- Test I: set_pinned_clue (AI pin/unpin) + dedupe ×N ใน alibi display ----------

        [UnityTest]
        public IEnumerator I_SetPinnedClue_AiSidePinning_AndDedupedAlibiDisplay() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (e) Test I — {DateTime.Now:HH:mm:ss} ===");
            Assert.That(File.Exists(BridgeDll), "bridge ยังไม่ build: " + BridgeDll);

            // ---- reset pin state + sync view (Test G/H อาจ pin ค้างไว้) ----
            var pinState = _scope.Container.Resolve<CluePinState>();
            pinState.Clear();
            var presenter = _scope.Container.Resolve<ClueBoardPresenter>();
            _view.gameObject.SetActive(true);
            await InvokeRenderAsync(presenter);
            Assert.That(_view.PinnedCardCountForTests, Is.EqualTo(0), "reset แล้ว UI ต้องไม่มีการ์ด pin");

            // ---- จัดฉาก: player + NPC มีชีวิต @ beach + seed/collect หลาย clue (เจตนาให้ชื่อซ้ำ) ----
            var player = _stateProvider.GetPlayer();
            var director = _scope.Container.Resolve<NpcDirectorSystem>();
            player.CurrentLocationId = "beach";
            var npcIds = director.Npcs.Values.Where(n => n.IsAlive).Select(n => n.Id).Take(2).ToList();
            Assert.That(npcIds.Count, Is.GreaterThanOrEqualTo(1), "ต้องมี NPC มีชีวิตอย่างน้อย 1");
            foreach (var id in npcIds) director.MoveNpc(id, "beach");

            var gen = _scope.Container.Resolve<ClueGenerationSystem>();
            var investigate = _scope.Container.Resolve<IAsyncRequestHandler<InvestigateClueRequest, InvestigateClueResponse>>();
            for (var i = 0; i < 4 && player.CollectedClueInstanceIds.Count < 3; i++)
            {
                gen.TryGenerate(ClueTriggerSource.KillSabotage, "beach", "npc_test_killer_i" + i);
                await investigate.InvokeAsync(new InvestigateClueRequest());
            }

            var boardHandler = _scope.Container.Resolve<IAsyncRequestHandler<GetClueBoardRequest, GetClueBoardResponse>>();
            var board = await boardHandler.InvokeAsync(new GetClueBoardRequest());
            Assert.That(player.CollectedClueInstanceIds.Count, Is.GreaterThanOrEqualTo(2), "ต้องมี clue ≥ 2");

            await InvokeRenderAsync(presenter);
            var graphHandler = _scope.Container.Resolve<IAsyncRequestHandler<GetClueGraphRequest, GetClueGraphResponse>>();
            var graph = await graphHandler.InvokeAsync(new GetClueGraphRequest());
            var clueNode = graph.Nodes.FirstOrDefault(n => n.Type == "clue");
            var npcNode = graph.Nodes.FirstOrDefault(n => n.Type == "npc");
            Assert.That(clueNode != null && npcNode != null, "กราฟต้องมีทั้ง clue และ npc node");

            var setHandler = _scope.Container.Resolve<IAsyncRequestHandler<SetPinnedClueRequest, SetPinnedClueResponse>>();

            // ---- AI pin clue → Success + state ตรง; แล้ว pin npc → 2 ----
            var r1 = await setHandler.InvokeAsync(new SetPinnedClueRequest { NodeId = clueNode.Id, Pinned = true });
            Assert.That(r1.Success, Is.True, "pin clue ต้องสำเร็จ: " + r1.FailureReason);
            Assert.That(r1.PinnedNodeIds, Is.EqualTo(new[] { clueNode.Id }));
            var r2 = await setHandler.InvokeAsync(new SetPinnedClueRequest { NodeId = npcNode.Id, Pinned = true });
            Assert.That(r2.Success, Is.True);
            Assert.That(r2.PinnedNodeIds.Count, Is.EqualTo(2), "2 pins (AI side)");
            log.AppendLine($"[I] AI pinned: [{string.Join(", ", r2.PinnedNodeIds)}]");

            // ---- idempotent: pin ซ้ำ → Success + no change; unpin ที่ไม่ได้ pin → Success + no change ----
            var r3 = await setHandler.InvokeAsync(new SetPinnedClueRequest { NodeId = clueNode.Id, Pinned = true });
            Assert.That(r3.Success && r3.FailureReason == "already_pinned", Is.True,
                "pin ซ้ำต้อง idempotent: " + r3.FailureReason);
            Assert.That(r3.PinnedNodeIds.Count, Is.EqualTo(2), "pin ซ้ำต้องไม่เปลี่ยน state");
            var r4 = await setHandler.InvokeAsync(new SetPinnedClueRequest { NodeId = clueNode.Id, Pinned = false });
            Assert.That(r4.Success && r4.FailureReason == string.Empty, Is.True, "unpin ต้องสำเร็จจริง");
            Assert.That(r4.PinnedNodeIds.Count, Is.EqualTo(1), "unpin แล้วเหลือ 1");
            var r4b = await setHandler.InvokeAsync(new SetPinnedClueRequest { NodeId = clueNode.Id, Pinned = false });
            Assert.That(r4b.Success && r4b.FailureReason == "not_pinned", Is.True, "unpin ซ้ำต้อง idempotent");
            log.AppendLine("[I] idempotent set semantics verified (already_pinned / not_pinned)");

            // ---- validation: unknown node + empty id ----
            var r5 = await setHandler.InvokeAsync(new SetPinnedClueRequest { NodeId = "npc_not_in_graph", Pinned = true });
            Assert.That(r5.Success, Is.False);
            Assert.That(r5.FailureReason, Is.EqualTo("unknown_node"), "ต้องปัด pin node ที่ไม่อยู่ในกราฟ");
            var r6 = await setHandler.InvokeAsync(new SetPinnedClueRequest { NodeId = "", Pinned = true });
            Assert.That(r6.FailureReason, Is.EqualTo("missing_node_id"));
            log.AppendLine("[I] validation: unknown_node + missing_node_id rejected");

            // ---- AI pin → UI ต้อง update ตาม (ผ่าน CluePinState.Changed subscription) ----
            var r7 = await setHandler.InvokeAsync(new SetPinnedClueRequest { NodeId = clueNode.Id, Pinned = true });
            Assert.That(r7.Success && r7.PinnedNodeIds.Count == 2, Is.True, "กลับไป 2 pins");
            var dl = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < dl && _view.PinnedCardCountForTests != 2) await UniTask.Yield();
            Assert.That(_view.PinnedCardCountForTests, Is.EqualTo(2),
                "AI pin ผ่าน MCP handler → การ์ดบน UI ต้องวาดตาม (Changed → RefreshPinnedAsync)");
            log.AppendLine("[I] AI-side pin reflected in UI: 2 cards");

            // ---- dedupe ×N (display-only): npc pin ที่เป็นพยานเบาะแสชื่อซ้ำ ----
            var npcWitnessed = board.Entries
                .Where(e => e.WitnessNpcIds != null && e.WitnessNpcIds.Contains(npcNode.Id))
                .Select(e => $"{e.DisplayName} @ {e.LocationId}").ToList();
            var grouped = npcWitnessed.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            var hasDupes = grouped.Any(kv => kv.Value > 1);
            var pinnedHandler = _scope.Container.Resolve<IAsyncRequestHandler<GetPinnedCluesRequest, GetPinnedCluesResponse>>();
            var pinnedRes = await pinnedHandler.InvokeAsync(new GetPinnedCluesRequest());
            var rendered = ClueGraphTextFormat.RenderPinned(pinnedRes);
            log.AppendLine("[I] RenderPinned output:");
            log.AppendLine(rendered);

            var npcLine = rendered.Split('\n').First(l => l.Contains("[npc]"));
            var witnessedPart = npcLine.Substring(npcLine.IndexOf("witnessed: ") + "witnessed: ".Length);
            var shownLines = witnessedPart.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            // display-only invariant: ผลรวมจำนวนจาก "×N" + บรรทัดเดี่ยว == จำนวน instance จริง
            var totalFromDisplay = 0;
            foreach (var s in shownLines)
            {
                var idx = s.LastIndexOf(" ×");
                if (idx >= 0 && int.TryParse(s[(idx + 2)..], out var n)) totalFromDisplay += n;
                else totalFromDisplay += 1;
            }
            Assert.That(totalFromDisplay, Is.EqualTo(npcWitnessed.Count),
                "display-only: ผลรวม ×N ต้องเท่ากับจำนวนเบาะแสจริง (ข้อมูลไม่หาย)");
            Assert.That(shownLines.Count, Is.LessThanOrEqualTo(npcWitnessed.Count));
            if (hasDupes)
            {
                Assert.That(witnessedPart, Does.Contain(" ×"), "มีเบาะแสชื่อซ้ำ → ต้องแสดงรูป ×N");
                Assert.That(shownLines.Count, Is.LessThan(npcWitnessed.Count), "dedupe ต้องลดจำนวนบรรทัดลง");
                log.AppendLine($"[I] dedupe: {npcWitnessed.Count} raw → {shownLines.Count} displayed (×N form)");
            }
            else
            {
                log.AppendLine("[I] dedupe precondition (ชื่อซ้ำ) ไม่เกิดรอบนี้ — invariant ผลรวมยังตรง");
            }
            StringAssert.DoesNotContain("killer", rendered, "ห้ามมี ground truth หลุด");

            // ---- bridge round-trip: set_pinned_clue ผ่าน stdio MCP (tool ใหม่มีจริง + args ถูกส่ง) ----
            pinState.Clear();
            await InvokeRenderAsync(presenter);
            string result = null;
            Exception convError = null;
            await UniTask.Run(() =>
            {
                try { result = RunBridgeConversation(log, "set_pinned_clue", new { nodeId = clueNode.Id, pinned = true }); }
                catch (Exception ex) { convError = ex; }
            });
            if (convError != null) throw convError;
            Assert.IsFalse(result.Contains("\"isError\":true"), "tools/call ต้องไม่ error: " + result);
            var text = ExtractToolText(result);
            log.AppendLine("[I] set_pinned_clue bridge output:");
            log.AppendLine(text);
            StringAssert.Contains("Pinned", text, "ต้องยืนยันการ pin");
            StringAssert.Contains(clueNode.Id, text, "ต้องระบุ node ที่ pin");
            StringAssert.Contains("Current pins", text, "response ต้องแนบ pin list ล่าสุด");
            Assert.That(pinState.IsPinned(clueNode.Id), Is.True,
                "bridge pin → CluePinState ต้องเปลี่ยนจริง (state เดียวกับ UI)");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("I_SetPinnedClue_AiSidePinning_AndDedupedAlibiDisplay", log.ToString());
            await UniTask.Yield();
        });

        static async UniTask InvokeRenderAsync(ClueBoardPresenter presenter)
        {
            var task = (UniTask)typeof(ClueBoardPresenter)
                .GetMethod("RenderAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                !.Invoke(presenter, null)!;
            await task;
        }

        // ---------- Test H: pin state → get_pinned_clues (MCP) + pins survive close/reopen ----------

        [UnityTest]
        public IEnumerator H_PinState_ExposedToMcp_AndSurvivesBoardReopen() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (e) Test H — {DateTime.Now:HH:mm:ss} ===");
            Assert.That(File.Exists(BridgeDll), "bridge ยังไม่ build: " + BridgeDll);

            // ---- reset pin state (test isolation — Test G อาจ pin ค้างไว้) ----
            var pinState = _scope.Container.Resolve<CluePinState>();
            pinState.Clear();
            var presenter = _scope.Container.Resolve<ClueBoardPresenter>();

            // ---- จัดฉาก: player + NPC มีชีวิต @ beach + seed/collect 2 clue (→ clue + npc nodes) ----
            var player = _stateProvider.GetPlayer();
            var director = _scope.Container.Resolve<NpcDirectorSystem>();
            player.CurrentLocationId = "beach";
            var npcIds = director.Npcs.Values.Where(n => n.IsAlive).Select(n => n.Id).Take(2).ToList();
            Assert.That(npcIds.Count, Is.GreaterThanOrEqualTo(1), "ต้องมี NPC มีชีวิตอย่างน้อย 1");
            foreach (var id in npcIds) director.MoveNpc(id, "beach");

            var gen = _scope.Container.Resolve<ClueGenerationSystem>();
            var investigate = _scope.Container.Resolve<IAsyncRequestHandler<InvestigateClueRequest, InvestigateClueResponse>>();
            for (var i = 0; i < 3 && player.CollectedClueInstanceIds.Count < 2; i++)
            {
                gen.TryGenerate(ClueTriggerSource.KillSabotage, "beach", "npc_test_killer_h" + i);
                await investigate.InvokeAsync(new InvestigateClueRequest());
            }
            Assert.That(player.CollectedClueInstanceIds.Count, Is.GreaterThanOrEqualTo(2),
                "ต้องมี clue บน board อย่างน้อย 2");

            _view.gameObject.SetActive(true);
            await InvokeRenderAsync(presenter);

            // ---- pin clue + npc ผ่าน popup (path เดียวกับเกม — เหมือน Test G) ----
            var clueIds = new List<string>();
            var npcNodeIds = new List<string>();
            var nodeTransforms = new Dictionary<string, Transform>();
            foreach (Transform child in _view.ContainerForTests)
            {
                if (!child.name.StartsWith("ClueNode_")) continue;
                var id = child.name["ClueNode_".Length..];
                nodeTransforms[id] = child;
                if (((RectTransform)child).sizeDelta.x >= 100f) clueIds.Add(id);
                else npcNodeIds.Add(id);
            }
            Assert.That(clueIds.Count, Is.GreaterThanOrEqualTo(1), "ต้องมี clue node");
            Assert.That(npcNodeIds.Count, Is.GreaterThanOrEqualTo(1), "ต้องมี npc node");

            async UniTask PinViaPopup(string nodeId)
            {
                ExecuteEvents.Execute(nodeTransforms[nodeId].gameObject,
                    new PointerEventData(EventSystem.current), ExecuteEvents.pointerClickHandler);
                var dl = Time.realtimeSinceStartup + 10f;
                while (Time.realtimeSinceStartup < dl && _view.DetailNodeIdForTests != nodeId) await UniTask.Yield();
                Assert.That(_view.DetailNodeIdForTests, Is.EqualTo(nodeId), "คลิก node ต้องเปิด popup ก่อน pin");
                _view.PinButtonForTests.onClick.Invoke();
                dl = Time.realtimeSinceStartup + 10f;
                while (Time.realtimeSinceStartup < dl && _view.PinnedCardCountForTests != pinState.PinnedNodeIds.Count)
                    await UniTask.Yield();
                Assert.That(_view.PinnedCardCountForTests, Is.EqualTo(pinState.PinnedNodeIds.Count),
                    $"pin {nodeId} → UI ต้องตรงกับ CluePinState");
            }

            await PinViaPopup(clueIds[0]);
            await PinViaPopup(npcNodeIds[0]);
            Assert.That(pinState.PinnedNodeIds.Count, Is.EqualTo(2), "pin แล้วต้องมี 2");
            var expectedClueId = pinState.PinnedNodeIds[0];
            var expectedNpcId = pinState.PinnedNodeIds[1];
            log.AppendLine($"[H] pinned: [{string.Join(", ", pinState.PinnedNodeIds)}]");

            // ---- MCP chain ใน-process: GetPinnedCluesHandler อ่าน state เดียวกับ UI ----
            var pinnedHandler = _scope.Container.Resolve<IAsyncRequestHandler<GetPinnedCluesRequest, GetPinnedCluesResponse>>();
            var pinnedRes = await pinnedHandler.InvokeAsync(new GetPinnedCluesRequest());
            Assert.That(pinnedRes.Pinned.Count, Is.EqualTo(2), "handler ต้องเห็น pin ทั้ง 2 (state เดียวกับ UI)");
            Assert.That(pinnedRes.Pinned[0].NodeId, Is.EqualTo(expectedClueId), "ลำดับ pin (เก่าสุดก่อน)");
            Assert.That(pinnedRes.Pinned[0].Type, Is.EqualTo("clue"), "pin แรก = clue");
            Assert.That(pinnedRes.Pinned[0].DisplayName, Is.Not.Empty, "clue pin ต้องมี DisplayName");
            Assert.That(pinnedRes.Pinned[0].Reliability, Is.Not.Empty, "clue pin ต้องมี Reliability (ผ่าน board handler เดิม)");
            Assert.That(pinnedRes.Pinned[1].Type, Is.EqualTo("npc"), "pin ที่สอง = npc");
            Assert.That(pinnedRes.Pinned[1].Zone, Is.EqualTo("beach"), "npc pin ต้องโชว์โซนที่ MoveNpc ตั้งไว้");
            Assert.That(pinnedRes.Pinned[1].WitnessedClues.Count, Is.GreaterThanOrEqualTo(1),
                "npc pin ต้องมีอาลิไบ (เบาะแสที่เป็นพยาน)");
            var humanReadable = ClueGraphTextFormat.RenderPinned(pinnedRes);
            log.AppendLine("[H] RenderPinned output:");
            log.AppendLine(humanReadable);
            StringAssert.Contains("Pinned Clues", humanReadable, "ต้องมี header");
            StringAssert.Contains("[clue]", humanReadable);
            StringAssert.Contains("[npc]", humanReadable);
            StringAssert.DoesNotContain("killer", humanReadable, "ห้ามมี ground truth หลุด");

            // ---- single-node query (presenter reuse path): ใส่ NodeId = node เดียว ----
            var single = await pinnedHandler.InvokeAsync(new GetPinnedCluesRequest { NodeId = expectedClueId });
            Assert.That(single.Pinned.Count, Is.EqualTo(1), "query เจาะจง id ต้องได้ 1 รายการ");
            Assert.That(single.Pinned[0].NodeId, Is.EqualTo(expectedClueId));

            // ---- pins survive board close/reopen (same session) ----
            _view.gameObject.SetActive(false); // ปิดกระดาน (เหมือน Tab toggle close)
            await UniTask.Yield();
            Assert.That(pinState.PinnedNodeIds.Count, Is.EqualTo(2), "ปิดกระดานแล้ว pin ต้องค้าง (state ฝั่ง presenter/singleton)");
            _view.gameObject.SetActive(true); // เปิดใหม่
            await InvokeRenderAsync(presenter); // TogglePanel เรียก RenderAsync ตอนเปิด — เรียกตรงเพื่อ await
            Assert.That(_view.PinnedCardCountForTests, Is.EqualTo(2),
                "เปิดกระดานใหม่ → แถว pin ต้องวาดครบ 2 การ์ดจาก pin list เดิม");
            log.AppendLine("[H] pins survived close/reopen: 2 cards redrawn from CluePinState");

            // ---- bridge round-trip: get_pinned_clues ผ่าน stdio MCP (tool ใหม่มีจริงบน bridge) ----
            string result = null;
            Exception convError = null;
            await UniTask.Run(() =>
            {
                try { result = RunBridgeConversation(log, "get_pinned_clues"); }
                catch (Exception ex) { convError = ex; }
            });
            if (convError != null) throw convError;
            Assert.IsFalse(result.Contains("\"isError\":true"), "tools/call ต้องไม่ error: " + result);
            var text = ExtractToolText(result);
            log.AppendLine("[H] get_pinned_clues bridge output:");
            log.AppendLine(text);
            StringAssert.Contains("Pinned Clues", text, "bridge ต้องเห็น pin เดียวกัน (state singleton ร่วม)");
            StringAssert.Contains("[clue]", text);
            StringAssert.Contains("[npc]", text);

            log.AppendLine("RESULT: PASS");
            WriteEvidence("H_PinState_ExposedToMcp_AndSurvivesBoardReopen", log.ToString());
            await UniTask.Yield();
        });

        // ---- helpers ----

        static string MiniSerialize(object obj)
        {
            switch (obj)
            {
                case null: return "null";
                case string s: return $"\"{s}\"";
                case bool b: return b ? "true" : "false"; // JSON lowercase — bool.ToString() ให้ True/False ซึ่งไม่ใช่ JSON
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
