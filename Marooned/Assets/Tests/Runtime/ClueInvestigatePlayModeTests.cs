using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Marooned.Core;
using Marooned.Shared;
using Marooned.Systems;
using MessagePipe;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VContainer;
using Debug = UnityEngine.Debug;

namespace Marooned.EditorTools
{
    /// <summary>
    /// Clue System v2 — Part (c): Incidental Hooks + investigate_clue (PlayMode evidence)
    ///
    /// B: harvest node_deer (isHuntingTarget=true) → TryHarvest สำเร็จแล้ว hook Hunting —
    ///    พิสูจน์แบบ statistical (40 ต้น: blood=25% + scratch=15%, P(0 ทั้งหมด)≈10⁻⁹)
    /// C: investigate_clue ที่โซนที่มี clue (blood VisibleToBystanders=true → เจอทันที)
    ///    → FoundInstanceId ถูกเพิ่มใน CollectedClueInstanceIds; โซนไม่มี clue →
    ///    no_clue_at_location
    /// D: ⚠️ ผ่าน bridge จริง (MCP stdio): tools/list ต้องมี investigate_clue ที่
    ///    ไม่มี parameter ใด ๆ (schema ไม่มี locationId) + tools/call ได้จริง
    /// E: ⚠️ investigate_clue ผ่าน TCP background thread ของ bridge (บทสนทนาจริง
    ///    รันบน threadpool) — handler ต้องไม่ throw threading/main-thread exception
    ///    (isError=false = handler บน main thread ทำงานจบสมบูรณ์)
    /// F: regression — kill-hook จาก commit (b) ยัง generate clue + publish ครบ
    /// </summary>
    public class ClueInvestigatePlayModeTests
    {
        public const string EvidenceDir = "TestEvidence/clue-system-v2-c";

        static readonly string RepoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        static readonly string BridgeDll = Path.Combine(RepoRoot, "McpBridge", "bin", "Debug", "net8.0", "McpBridge.dll");

        GameLifetimeScope _scope;
        GameStateProvider _stateProvider;
        NpcDirectorSystem _director;
        LubanDataService _data;

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
            _director = _scope.Container.Resolve<NpcDirectorSystem>();
            _data = _scope.Container.Resolve<LubanDataService>();
            await UniTask.Yield();
        });

        // ---------- Test B: harvest node_deer → Hunting hook ----------

        [UnityTest]
        public IEnumerator B_HarvestDeer_HooksHuntingSource() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (c) Test B — {DateTime.Now:HH:mm:ss} ===");

            var container = _scope.Container;
            var harvest = container.Resolve<NodeHarvestSystem>();
            var player = _stateProvider.GetPlayer();

            player.CurrentLocationId = "beach";
            player.Inventory["tool_axe"] = 1; // node_deer ต้องใช้ tool_axe (auto-pick เจอ)

            var before = _stateProvider.AllClueInstances.Count;
            var spawned = new List<GameObject>();
            var harvestOk = 0;
            try
            {
                // 40 ต้น (durability 2 ต่อต้น, regrow 600s จึงสร้างใหม่ทุกครั้ง) —
                // P(ไม่เจอ clue เลย) = 0.6^40 ≈ 1.3e-9
                for (var i = 0; i < 40; i++)
                {
                    var nodeGo = new GameObject($"test_deer_{i}");
                    spawned.Add(nodeGo);
                    var node = nodeGo.AddComponent<HarvestableNodeComponent>();
                    node.Init("node_deer", _data.HarvestableNodeDefs["node_deer"], harvest);
                    nodeGo.transform.position = new Vector3(player.PositionX, player.PositionY, 0);

                    var result = harvest.TryHarvest(node, toolItemId: null);
                    if (result.Success)
                    {
                        harvestOk++;
                        log.AppendLine($"harvest #{i + 1}: +{result.Count} {result.ItemId} (depleted={result.Depleted})");
                    }
                    else
                    {
                        log.AppendLine($"harvest #{i + 1}: FAIL {result.FailureReason}");
                    }
                }

                var hunting = _stateProvider.AllClueInstances.Values
                    .Where(c => c.Source == ClueTriggerSource.Hunting && c.SourceActorId == GameStateProvider.LocalPlayerId)
                    .ToList();
                log.AppendLine($"harvest สำเร็จ {harvestOk}/40 → Hunting clue เกิด {hunting.Count} ครั้ง (expect ≈40% ของรอบ)");
                foreach (var c in hunting.Take(5))
                    log.AppendLine($"  {c.DefId} @ {c.LocationId} witnesses=[{string.Join(",", c.WitnessNpcIds)}] (player = hunter → ไม่มี player_local)");

                Assert.That(harvestOk, Is.EqualTo(40), "node_deer ทุกต้นต้องเก็บสำเร็จ (tool_axe พร้อม)");
                Assert.That(hunting.Count, Is.GreaterThanOrEqualTo(1),
                    "40 ครั้ง (blood 25% + scratch 15%) ต้องเกิด Hunting clue อย่างน้อย 1");
                foreach (var c in hunting)
                {
                    Assert.That(c.LocationId, Is.EqualTo("beach"), "clue ต้องอยู่โซนที่ player เก็บ");
                    Assert.That(c.WitnessNpcIds, Does.Not.Contain(GameStateProvider.LocalPlayerId),
                        "player ไม่เป็นพยาน clue ของตัวเอง");
                }
                log.AppendLine("RESULT: PASS");
                WriteEvidence("B_HarvestDeer_HooksHuntingSource", log.ToString());
            }
            finally
            {
                foreach (var go in spawned) if (go != null) UnityEngine.Object.Destroy(go);
            }
            await UniTask.Yield();
        });

        // ---------- Test C: investigate_clue เก็บ clue เข้า board ----------

        [UnityTest]
        public IEnumerator C_Investigate_FindsClue_AtPlayerLocation() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (c) Test C — {DateTime.Now:HH:mm:ss} ===");
            var container = _scope.Container;
            var player = _stateProvider.GetPlayer();
            var investigate = container.Resolve<IAsyncRequestHandler<InvestigateClueRequest, InvestigateClueResponse>>();

            // ---- โซนที่ "ไม่มี clue" ก่อน → no_clue_at_location ----
            player.CurrentLocationId = "deep_jungle";
            var none = await investigate.InvokeAsync(new InvestigateClueRequest());
            log.AppendLine($"investigate @ deep_jungle (ไม่มี clue): Success={none.Success} reason={none.FailureReason}");
            Assert.IsFalse(none.Success);
            Assert.AreEqual("no_clue_at_location", none.FailureReason);
            Assert.AreEqual(string.Empty, none.FoundInstanceId);

            // ---- ย้ายไป beach แล้ว force-generate clue (KillSabotage: blood=100%) ----
            player.CurrentLocationId = "beach";
            var gen = container.Resolve<ClueGenerationSystem>();
            var generated = gen.TryGenerate(ClueTriggerSource.KillSabotage, "beach", "npc_test_killer");
            Assert.That(generated.Count, Is.GreaterThanOrEqualTo(1), "blood (100%) ต้องเกิด");
            log.AppendLine($"seed clue @ beach: [{string.Join(", ", generated.Select(g => g.DefId))}]");

            // blood_stain VisibleToBystanders=true → เจอทันทีไม่ง้อ 50% roll
            var res = await investigate.InvokeAsync(new InvestigateClueRequest());
            log.AppendLine($"investigate @ beach: Success={res.Success} found={res.FoundInstanceId[..Math.Min(8, res.FoundInstanceId.Length)]}… reason={res.FailureReason}");
            Assert.IsTrue(res.Success, $"ต้องเจอ clue (blood visible): {res.FailureReason}");
            Assert.That(player.CollectedClueInstanceIds, Does.Contain(res.FoundInstanceId),
                "FoundInstanceId ต้องถูกเพิ่มใน CollectedClueInstanceIds");
            var foundInstance = _stateProvider.AllClueInstances[res.FoundInstanceId];
            Assert.AreEqual("beach", foundInstance.LocationId, "ต้องเจอเฉพาะ clue ในโซน player");

            // ---- เก็บซ้ำ instance เดิมไม่ได้อีก (filter ตัดตัวที่เก็บแล้ว) ----
            var collectedBefore = player.CollectedClueInstanceIds.Count;
            await investigate.InvokeAsync(new InvestigateClueRequest());
            Assert.That(player.CollectedClueInstanceIds.Count, Is.GreaterThanOrEqualTo(collectedBefore));
            Assert.That(player.CollectedClueInstanceIds.Count(i => i == res.FoundInstanceId), Is.EqualTo(1),
                "instance เดียวเก็บได้ครั้งเดียว (no duplicates)");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("C_Investigate_FindsClue_AtPlayerLocation", log.ToString());
            await UniTask.Yield();
        });

        // ---------- Tests D + E: bridge จริง (tools/list schema + threading) ----------

        [UnityTest]
        [Timeout(360000)]
        public IEnumerator D_Bridge_InvestigateClue_NoParams_AndEndToEnd() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (c) Tests D+E (bridge) — {DateTime.Now:HH:mm:ss} ===");
            Assert.That(File.Exists(BridgeDll), $"bridge ยังไม่ build: {BridgeDll}");

            string result = null;
            Exception convError = null;
            await UniTask.Run(() =>
            {
                try { result = RunBridgeConversation(log); }
                catch (Exception ex) { convError = ex; }
            });
            if (convError != null) throw convError;
            Assert.IsFalse(result.Contains("\"isError\":true"),
                $"investigate_clue ผ่าน bridge ต้องไม่ error (E: threading OK): {result}");
            log.AppendLine("RESULT: PASS");
            WriteEvidence("D_Bridge_InvestigateClue_NoParams_AndEndToEnd", log.ToString());
            await UniTask.Yield();
        });

        /// <summary>บทสนทนา MCP stdio (รันบน threadpool — block ได้) คืน tools/call response ของ investigate_clue</summary>
        string RunBridgeConversation(StringBuilder log)
        {
            var psi = new ProcessStartInfo
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
            var bridge = new Process { StartInfo = psi };
            Assert.IsTrue(bridge.Start(), "spawn McpBridge ไม่สำเร็จ");
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
                    var sw = Stopwatch.StartNew();
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

                // ---- initialize ----
                Rpc("initialize", new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "clue-c-test", version = "0.1" } });
                writer.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
                writer.Flush();

                // ---- tools/list → หา investigate_clue + ตรวจ schema ไม่มี parameter ----
                var listRaw = Rpc("tools/list", new { });
                var toolName = FindToolName(listRaw, "investigateclue");
                Assert.IsNotNull(toolName, $"ไม่เจอ investigate_clue tool ใน: {Truncate(listRaw, 600)}");
                var schemaSlice = SliceUntilNextTool(listRaw, toolName);
                var hasLocationParam = schemaSlice.Contains("locationId") || schemaSlice.Contains("location_id");
                log.AppendLine($"[D] tools/list: '{toolName}' schema={Truncate(schemaSlice, 160)}");
                log.AppendLine($"[D] schema มี locationId/location_id: {hasLocationParam}");
                Assert.IsFalse(hasLocationParam,
                    "⚠️ investigate_clue ต้องไม่รับพารามิเตอร์ location ใด ๆ (ใช้ player location ฝั่ง server)");

                // ---- tools/call investigate_clue (ผ่าน TCP background thread จริง → Test E) ----
                var call = Rpc("tools/call", new { name = toolName, arguments = new { } });
                log.AppendLine($"[E] tools/call {toolName} (no args) ผ่าน bridge ↔ TCP: {Truncate(call, 220)}");
                Assert.IsFalse(call.Contains("\"isError\":true"),
                    "⚠️ handler ต้องรันบน main thread จบสมบูรณ์ (no threading exception)");
                return call;
            }
            finally
            {
                try { bridge.Kill(); } catch { }
                bridge.Dispose();
            }
        }

        // ---------- Test F: regression — kill-hook จาก commit (b) ----------

        [UnityTest]
        public IEnumerator F_KillHook_StillWorks_AfterPartC() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (c) Test F (regression) — {DateTime.Now:HH:mm:ss} ===");

            var subscriber = _scope.Container.Resolve<ISubscriber<ClueGeneratedMessage>>();
            var messageCount = 0;
            var subscription = subscriber.Subscribe(_ => messageCount++);

            try
            {
                var killerId = _director.Npcs.Keys.First();
                var victimId = _director.Npcs.Keys.Skip(1).First();
                var player = _stateProvider.GetPlayer();
                var before = _stateProvider.AllClueInstances.Count;

                // stage: killer + เหยื่ออยู่ cave_entrance สองต่อ, คนอื่น (รวม player) อยู่ deep_jungle
                player.CurrentLocationId = "deep_jungle";
                foreach (var npc in _director.Npcs.Values)
                    _director.MoveNpc(npc.Id, npc.Id == victimId || npc.Id == killerId ? "cave_entrance" : "deep_jungle");
                await UniTask.Yield();

                var (success, reason) = _director.TryEliminate(killerId, victimId, "cave_entrance");
                log.AppendLine($"TryEliminate({killerId} → {victimId} @ cave_entrance): {success} {reason}");
                Assert.IsTrue(success, $"kill ต้องสำเร็จ: {reason}");

                var generated = _stateProvider.AllClueInstances.Values
                    .Where(c => c.SourceActorId == killerId).ToList();
                log.AppendLine($"kill-hook → {generated.Count} clue(s): [{string.Join(", ", generated.Select(g => g.DefId))}], messages={messageCount}");
                Assert.That(generated.Count, Is.InRange(1, 2), "blood=100% → อย่างน้อย 1 (kill-hook ยังทำงาน)");
                Assert.That(_stateProvider.AllClueInstances.Count - before, Is.EqualTo(generated.Count));
                Assert.That(messageCount, Is.EqualTo(generated.Count),
                    "ClueGeneratedMessage ยิงครบ 1:1 (pipeline หลัง part (c) ไม่พัง)");
                log.AppendLine("RESULT: PASS");
                WriteEvidence("F_KillHook_StillWorks_AfterPartC", log.ToString());
            }
            finally
            {
                subscription.Dispose();
            }
            await UniTask.Yield();
        });

        // ---- helpers ----

        /// <summary>ตัด JSON ของ tool 1 ตัว: จาก "name":"&lt;tool&gt;" ไปจนถึง "name":" ตัวถัดไป (หรือจบ string)</summary>
        static string SliceUntilNextTool(string listRaw, string toolName)
        {
            var start = listRaw.IndexOf($"\"name\":\"{toolName}\"", StringComparison.Ordinal);
            if (start < 0) return string.Empty;
            var next = listRaw.IndexOf("\"name\":\"", start + toolName.Length + 8, StringComparison.Ordinal);
            return next < 0 ? listRaw.Substring(start) : listRaw.Substring(start, next - start);
        }

        static string FindToolName(string toolsListResponse, string keyword)
        {
            if (toolsListResponse == null) return null;
            var needle = keyword.Replace("_", "");
            var idx = 0;
            while (true)
            {
                idx = toolsListResponse.IndexOf("\"name\":\"", idx, StringComparison.Ordinal);
                if (idx < 0) return null;
                idx += 8;
                var end = toolsListResponse.IndexOf('"', idx);
                if (end < 0) return null;
                var name = toolsListResponse.Substring(idx, end - idx);
                if (name.ToLowerInvariant().Replace("_", "").Contains(needle)) return name;
            }
        }

        static string MiniSerialize(object obj)
        {
            switch (obj)
            {
                case null: return "null";
                case string s: return $"\"{s}\"";
                case bool b: return b ? "true" : "false";
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

        static string Truncate(string s, int max) => s != null && s.Length > max ? s.Substring(0, max) + "…" : s;

        private void WriteEvidence(string test, string body)
        {
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", EvidenceDir));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{test}.txt"), body + Environment.NewLine);
            Debug.Log($"[ClueInvestigatePlayModeTests] {test} PASS — evidence: {EvidenceDir}/");
        }
    }
}
