using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Marooned.Shared;
using Marooned.Systems;
using Marooned.Systems.AI;
using NUnit.Framework;
using UnityEngine;

namespace Marooned.Tests.Editor
{
    /// <summary>
    /// Clue System v2 — Part (d): MCP Graph Response Layer (EditMode evidence)
    ///
    /// A: get_clue_board → List&lt;ClueBoardEntry&gt; (DisplayName/Reliability resolve จาก ClueDef)
    ///    พร้อม witness list ที่กรองแล้ว (npc ไม่รู้จักถูกตัด, player_local คงอยู่)
    /// B: ⚠️ witness เป็น npc ตายแล้ว → ไม่ปรากฏใน response (IsAlive filter)
    /// C: get_clue_graph → nodes (clue + npc, dedupe witness) + edges (witnessed) ถูกต้อง
    /// D: ⚠️ Information Hiding — SourceActorId ไม่โผล่ใน response เด็ดขาด
    ///    (type-level: ไม่มี field ชื่อนั้น + byte-level: scan MessagePack payload)
    /// </summary>
    public class ClueGraphEditModeTests
    {
        public const string EvidenceDir = "TestEvidence/clue-system-v2-d";

        private class FakePublisher<T> : MessagePipe.IPublisher<T>
        {
            public readonly List<T> Published = new();
            public void Publish(T value) => Published.Add(value);
        }

        LubanDataService _data;
        GameStateProvider _state;
        UtilityContext _ctx;
        NpcDirectorSystem _director;
        GetClueBoardHandler _board;
        GetClueGraphHandler _graph;

        [SetUp]
        public void SetUp()
        {
            _data = new LubanDataService();
            _state = new GameStateProvider();
            _ctx = new UtilityContext(_data, _state);

            var innocent = new InnocentUtilityAI(_ctx);
            var killer = new KillerPlanner(_ctx);
            _director = new NpcDirectorSystem(_data, new FakePublisher<NpcEliminatedMessage>(),
                new FakePublisher<NpcLocationChangedMessage>(), innocent, killer, _ctx, clueGeneration: null);
            _ctx.Bind(_director);

            _board = new GetClueBoardHandler(_state, _director, _data);
            _graph = new GetClueGraphHandler(_board, _state);
        }

        ClueInstance MakeInstance(string id, string defId, string location, string sourceActorId,
            params string[] witnesses)
        {
            var inst = new ClueInstance
            {
                InstanceId = id,
                DefId = defId,
                LocationId = location,
                GameTimestamp = 42f,
                Source = ClueTriggerSource.KillSabotage,
                SourceActorId = sourceActorId,
                WitnessNpcIds = new List<string>(witnesses),
            };
            _state.AllClueInstances[id] = inst;
            _state.GetPlayer().CollectedClueInstanceIds.Add(id);
            return inst;
        }

        [Test]
        public void A_Board_ReturnsEntriesWithFilteredWitnessList()
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (d) Test A — {DateTime.Now:HH:mm:ss} ===");

            // สร้าง npc_a/npc_b จริงใน director — ไม่งั้น IsAlive filter จะตัดทิ้งถูกต้องแล้วเทสล้มเอง
            _director.SetupRound(new[] { "npc_a", "npc_b" }, killerCount: 0);

            // player เก็บไว้แล้ว 3 id: 1 ตัวจริง + 1 dangling (ไม่มีใน registry) + 1 def แปลก
            _state.GetPlayer().CollectedClueInstanceIds.Add("inst_dangling");
            MakeInstance("inst_1", "clue_blood_stain", "beach", "npc_killer",
                "npc_a", "npc_b", "player_local");
            MakeInstance("inst_2", "clue_def_unknown", "cave_entrance", "npc_killer", "player_local");

            var res = _board.InvokeAsync(new GetClueBoardRequest()).GetAwaiter().GetResult();

            Assert.That(res.Entries, Is.Not.Null);
            Assert.That(res.Entries.Count, Is.EqualTo(2),
                "dangling id (ไม่มีใน registry) ต้องถูกข้าม — เหลือ 2 entries");

            var e1 = res.Entries.First(e => e.InstanceId == "inst_1");
            Assert.That(e1.DisplayName, Is.EqualTo("คราบเลือด"), "DisplayName resolve จาก ClueDef");
            Assert.That(e1.Reliability, Is.EqualTo("Strong"), "Reliability resolve จาก ClueDef enum");
            Assert.That(e1.LocationId, Is.EqualTo("beach"));
            Assert.That(e1.WitnessNpcIds, Is.EqualTo(new List<string> { "npc_a", "npc_b", "player_local" }),
                "witness ตรงตาม instance (ทุกคนยังมีชีวิต + player_local คงอยู่)");

            var e2 = res.Entries.First(e => e.InstanceId == "inst_2");
            Assert.That(e2.DisplayName, Is.EqualTo("clue_def_unknown"),
                "def ที่ไม่รู้จัก → fallback เป็น defId (ไม่ crash)");
            Assert.That(e2.Reliability, Is.Empty, "def ไม่รู้จัก → reliability ว่าง");

            log.AppendLine($"board: {res.Entries.Count} entries (จาก 3 collected ids, 1 dangling skipped)");
            foreach (var e in res.Entries)
                log.AppendLine($"  {e.InstanceId} | {e.DisplayName} | {e.Reliability} | {e.LocationId} | witnesses=[{string.Join(", ", e.WitnessNpcIds)}]");
            log.AppendLine("RESULT: PASS");
            WriteEvidence("A_Board_ReturnsEntriesWithFilteredWitnessList", log.ToString());
        }

        [Test]
        public void B_DeadWitness_IsFilteredOutOfResponse()
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (d) Test B — {DateTime.Now:HH:mm:ss} ===");

            _director.SetupRound(new[] { "npc_dead", "npc_live" }, killerCount: 0);
            _director.MoveNpc("npc_dead", "beach");
            _director.MoveNpc("npc_live", "beach");

            MakeInstance("inst_dead", "clue_blood_stain", "beach", "npc_killer",
                "npc_dead", "npc_live", "player_local");

            // ก่อนตาย: ทุกคนอยู่ครบ
            var before = _board.InvokeAsync(new GetClueBoardRequest()).GetAwaiter().GetResult();
            Assert.That(before.Entries[0].WitnessNpcIds, Is.EqualTo(
                new List<string> { "npc_dead", "npc_live", "player_local" }),
                "ก่อน npc_dead ตาย → witness ครบ 3");

            // npc_dead ตายแล้ว
            _director.Npcs["npc_dead"].IsAlive = false;
            log.AppendLine("npc_dead.IsAlive = false (victim was eliminated)");

            var after = _board.InvokeAsync(new GetClueBoardRequest()).GetAwaiter().GetResult();
            var w = after.Entries[0].WitnessNpcIds;

            Assert.That(w, Does.Not.Contain("npc_dead"),
                "⚠️ npc ตายแล้วต้องไม่ปรากฏใน witness list");
            Assert.That(w, Does.Contain("npc_live"), "npc มีชีวิตคงอยู่");
            Assert.That(w, Does.Contain("player_local"), "player_local คงอยู่เสมอ (IsAlive filter ไม่แตะ)");
            Assert.That(w, Is.EqualTo(new List<string> { "npc_live", "player_local" }));

            log.AppendLine($"witnesses after death filter: [{string.Join(", ", w)}] (npc_dead removed ⚠️)");
            log.AppendLine("RESULT: PASS");
            WriteEvidence("B_DeadWitness_IsFilteredOutOfResponse", log.ToString());
        }

        [Test]
        public void C_Graph_NodesAndEdges_AreCorrect()
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (d) Test C — {DateTime.Now:HH:mm:ss} ===");

            _director.SetupRound(new[] { "npc_a", "npc_b" }, killerCount: 0);

            MakeInstance("inst_1", "clue_blood_stain", "beach", "npc_killer", "npc_a", "npc_b");
            MakeInstance("inst_2", "clue_wet_clothes", "beach", "npc_killer", "npc_b", "player_local");

            var res = _graph.InvokeAsync(new GetClueGraphRequest()).GetAwaiter().GetResult();

            // Nodes: 2 clues + witnesses {npc_a, npc_b, player_local} (npc_b dedupe) = 5
            Assert.That(res.Nodes.Count, Is.EqualTo(5),
                $"2 clue nodes + 3 unique witness nodes (npc_b ซ้ำต้อง dedupe) — ได้ {res.Nodes.Count}");
            Assert.That(res.Nodes.Count(n => n.Type == "clue"), Is.EqualTo(2));
            Assert.That(res.Nodes.Count(n => n.Type == "npc"), Is.EqualTo(3));
            Assert.That(res.Nodes.First(n => n.Id == "inst_1").Label, Is.EqualTo("คราบเลือด"),
                "clue node label = DisplayName");
            Assert.That(res.Nodes.First(n => n.Id == "npc_a").Label, Is.EqualTo("npc_a"),
                "npc node label = id");

            // Edges: 4 — inst_1→npc_a, inst_1→npc_b, inst_2→npc_b, inst_2→player_local
            Assert.That(res.Edges.Count, Is.EqualTo(4));
            var expected = new HashSet<string>
            {
                "inst_1->npc_a", "inst_1->npc_b", "inst_2->npc_b", "inst_2->player_local",
            };
            foreach (var edge in res.Edges)
            {
                Assert.That(edge.Relation, Is.EqualTo("witnessed"), "relation ต้องเป็น witnessed เท่านั้น");
                Assert.That(expected.Remove($"{edge.From}->{edge.To}"),
                    Is.True, $"unexpected edge {edge.From}->{edge.To}");
            }
            Assert.That(expected, Is.Empty, "ทุก edge ที่คาดไว้ต้องเจอครบ");

            // ตัวอย่าง graph JSON (สำหรับ output requirement)
            var mp = MessagePack.MessagePackSerializer.Serialize(res);
            var json = MessagePack.MessagePackSerializer.ConvertToJson(mp);
            log.AppendLine("graph JSON sample:");
            log.AppendLine(json);
            log.AppendLine("RESULT: PASS");
            WriteEvidence("C_Graph_NodesAndEdges_AreCorrect", log.ToString());
        }

        [Test]
        public void D_InformationHiding_SourceActorId_NeverInResponses()
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (d) Test D — {DateTime.Now:HH:mm:ss} ===");

            _director.SetupRound(new[] { "npc_a" }, killerCount: 0);
            const string secretSource = "npc_secret_killer_x99";

            MakeInstance("inst_1", "clue_blood_stain", "beach", secretSource, "npc_a", "player_local");

            var board = _board.InvokeAsync(new GetClueBoardRequest()).GetAwaiter().GetResult();
            var graph = _graph.InvokeAsync(new GetClueGraphRequest()).GetAwaiter().GetResult();

            // 1) type-level: response DTO ทุกตัวไม่มี field/property ชื่อ SourceActorId/Witness ground truth
            foreach (var t in new[] { typeof(ClueBoardEntry), typeof(GraphNode), typeof(GraphEdge) })
            {
                var names = t.GetFields().Select(f => f.Name)
                    .Concat(t.GetProperties().Select(p => p.Name));
                Assert.That(names, Does.Not.Contain("SourceActorId"),
                    $"{t.Name} ห้ามมี field SourceActorId");
                log.AppendLine($"type check: {t.Name} → ไม่มี SourceActorId ✓");
            }

            // 2) byte-level: serialize response จริง → ห้ามมี id ผู้ก่อเหตุใน payload
            var needle = Encoding.UTF8.GetBytes(secretSource);
            foreach (var (name, payload) in new[]
            {
                ("get_clue_board", MessagePack.MessagePackSerializer.Serialize(board)),
                ("get_clue_graph", MessagePack.MessagePackSerializer.Serialize(graph)),
            })
            {
                var leak = IndexOf(payload, needle);
                Assert.That(leak, Is.False,
                    $"⚠️ {name} payload มี SourceActorId ({secretSource}) หลุดออกมา!");
                log.AppendLine($"byte scan: {name} ({payload.Length}B) → no source-actor leak ✓");
            }

            // 3) sanity: witness ที่ควรเห็นยังอยู่ใน payload (กัน false-positive ของ scan)
            var boardBytes = MessagePack.MessagePackSerializer.Serialize(board);
            Assert.That(IndexOf(boardBytes, Encoding.UTF8.GetBytes("npc_a")), Is.True,
                "scan ต้องเจอ witness ปกติ — พิสูจน์ว่า needle scan ทำงานจริง");
            log.AppendLine("scan sanity: witness npc_a พบใน payload (scan มีประสิทธิภาพจริง)");
            log.AppendLine("RESULT: PASS");
            WriteEvidence("D_InformationHiding_SourceActorId_NeverInResponses", log.ToString());
        }

        static bool IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                int j = 0;
                for (; j < needle.Length && haystack[i + j] == needle[j]; j++) { }
                if (j == needle.Length) return true;
            }
            return false;
        }

        private static void WriteEvidence(string test, string body)
        {
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", EvidenceDir));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{test}.txt"), body + Environment.NewLine);
        }
    }
}
