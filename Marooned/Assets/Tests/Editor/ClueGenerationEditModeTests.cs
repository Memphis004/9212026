using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Marooned.Shared;
using Marooned.Systems;
using Marooned.Systems.AI;
using MessagePipe;
using NUnit.Framework;
using UnityEngine;

namespace Marooned.Tests.Editor
{
    /// <summary>
    /// Clue System v2 — Part (b): Generation Pipeline + Kill-Hook (EditMode evidence)
    ///
    /// B: kill hook → pipeline จริง: registry เพิ่มทุกครั้ง, blood (weight=100) เกิด
    ///    เสมอ, ClueGeneratedMessage ยิง 1 ต่อ 1 instance, victim.AllConditionCardIds
    ///    ได้ instance ids (DeductionSystem เดิมยังทำงาน)
    /// C: roll อิสระต่อ trigger — kill 200 ครั้ง: ได้ 1 หรือ 2 clues เท่านั้น
    ///    (blood=100% จึงไม่มีทางได้ 0; scratch=30% → ได้ 2 ≈ 30% ของรอบ)
    ///    + พิสูจน์กรณี 0/1/2 ครบผ่าน Hunting (25/15 → ทั้งคู่ไม่ติดได้จริง)
    ///    (นับผ่าน ClueGeneratedMessage delta — publish 1:1 กับ instance)
    /// D: WitnessNpcIds hygiene — exclude ผู้ก่อเหตุ, exclude NPC ตาย/ต่างโซน,
    ///    รวม NPC มีชีวิตในโซน + player; และผ่าน kill hook จริง killer ก็ห้ามปน
    ///    (หมายเหตุ: กติกาเดิม CanEliminate บังคับ kill ต้องไม่มี NPC พยานอยู่แล้ว —
    ///    snapshot มีพยานครบ ๆ จึงต้องเรียก TryGenerate ตรง ๆ เหมือน source อื่น
    ///    เช่น Hunting/IncidentalAction ที่ไม่มีกฎ no-witness)
    /// E: player เป็น killer → WitnessNpcIds ไม่มี "player_local" เด็ดขาด
    /// </summary>
    public class ClueGenerationEditModeTests
    {
        public const string EvidenceDir = "TestEvidence/clue-system-v2-b";

        private class FakePublisher<T> : IPublisher<T>
        {
            public List<T> Published { get; } = new();
            public void Publish(T message) => Published.Add(message);
        }

        private LubanDataService _data;
        private GameStateProvider _state;
        private UtilityContext _ctx;
        private NpcDirectorSystem _director;
        private ClueGenerationSystem _gen;
        private FakePublisher<ClueGeneratedMessage> _clueMessages;

        [SetUp]
        public void SetUp()
        {
            _data = new LubanDataService(); // ตารางจริงจาก Resources
            _state = new GameStateProvider();
            _ctx = new UtilityContext(_data, _state);
            _clueMessages = new FakePublisher<ClueGeneratedMessage>();
            _gen = new ClueGenerationSystem(_data, _state, _ctx, _clueMessages);

            var innocent = new InnocentUtilityAI(_ctx);
            var killer = new KillerPlanner(_ctx);
            _director = new NpcDirectorSystem(_data, new FakePublisher<NpcEliminatedMessage>(),
                new FakePublisher<NpcLocationChangedMessage>(), innocent, killer, _ctx, _gen);
            _ctx.Bind(_director);
        }

        private NpcState Npc(string id) => _director.Npcs[id];

        /// <summary>สร้าง NPC actor 1 ตัว (ชื่อตาม role ของเทส เช่น npc_killer เมื่อ NPC เป็น killer,
        /// npc_by_1 เมื่อเป็นแค่ bystander) + คนอื่นตามพารามิเตอร์</summary>
        private void StageNpcs(string actorId, string actorLocation, params (string id, string location)[] others)
        {
            _director.SetupRound(others.Select(o => o.id).Prepend(actorId).ToArray(), killerCount: 0);
            _director.MoveNpc(actorId, actorLocation);
            foreach (var (id, location) in others) _director.MoveNpc(id, location);
        }

        // ---------- Test B: kill hook → generation pipeline ----------

        [Test]
        public void B_KillHook_GeneratesViaPipeline_RegistryAndMessages()
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (b) Test B — {DateTime.Now:HH:mm:ss} ===");
            StageNpcs("npc_killer", "beach", ("npc_victim", "beach"), ("npc_far", "jungle_edge"));
            _state.GetPlayer().CurrentLocationId = "beach"; // player อยู่ด้วย (ไม่ใช่ killer)

            var (success, reason) = _director.TryEliminate("npc_killer", "npc_victim", "beach");
            Assert.IsTrue(success, $"kill ต้องสำเร็จ (NPC พยานไม่มี — npc_far อยู่ jungle_edge): {reason}");

            var generated = _state.AllClueInstances.Values
                .Where(i => i.SourceActorId == "npc_killer")
                .ToList();

            log.AppendLine($"kill (npc_killer → npc_victim @ beach) → {generated.Count} instance(s):");
            foreach (var inst in generated)
                log.AppendLine($"  {inst.DefId} (Source={inst.Source}, ts={inst.GameTimestamp:F2}, " +
                               $"witnesses=[{string.Join(",", inst.WitnessNpcIds)}])");

            Assert.That(generated.Count, Is.InRange(1, 2),
                "blood (weight=100) เกิดเสมอ → อย่างน้อย 1; scratch (30%) อาจเพิ่มเป็น 2");
            foreach (var inst in generated)
            {
                Assert.That(inst.Source, Is.EqualTo(ClueTriggerSource.KillSabotage));
                Assert.That(inst.LocationId, Is.EqualTo("beach"));
            }
            Assert.That(_state.AllClueInstances.Values.Count, Is.EqualTo(generated.Count),
                "registry กลางต้องมีทุก instance ที่เพิ่งเกิด (เริ่มรอบว่าง)");

            Assert.That(_clueMessages.Published.Count, Is.EqualTo(generated.Count),
                "ClueGeneratedMessage ยิง 1 ครั้งต่อ 1 clue ที่เกิด");
            foreach (var msg in _clueMessages.Published)
            {
                Assert.That(_state.AllClueInstances.TryGetValue(msg.InstanceId, out var inst2), Is.True,
                    "message.InstanceId ต้องชี้ instance ใน registry");
                Assert.That(inst2.DefId, Is.EqualTo(msg.DefId));
                Assert.That(inst2.LocationId, Is.EqualTo(msg.LocationId));
            }

            Assert.That(Npc("npc_victim").AllConditionCardIds,
                Is.EquivalentTo(generated.Select(i => i.InstanceId).ToList()),
                "victim.AllConditionCardIds ต้องมี instance ids (DeductionSystem เดิม)");

            log.AppendLine($"messages published: {_clueMessages.Published.Count} (1:1 with instances)");
            log.AppendLine("RESULT: PASS");
            WriteEvidence("B_KillHook_GeneratesViaPipeline_RegistryAndMessages", log.ToString());
        }

        // ---------- Test C: 0/1/2 clues ตาม weight อิสระ ----------

        [Test]
        public void C_IndependentRolls_PerTrigger_WeightsHonored()
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (b) Test C — {DateTime.Now:HH:mm:ss} ===");

            // ---- kill 200 ครั้ง (KillSabotage: blood=100, scratch=30) — นับจาก message delta ----
            const int kills = 200;
            var killCounts = new Dictionary<int, int> { [0] = 0, [1] = 0, [2] = 0 };
            var publishedBefore = 0;
            for (var i = 0; i < kills; i++)
            {
                StageNpcs("npc_killer", "beach", ("npc_victim", "beach"), ("npc_far", "jungle_edge"));
                _director.TryEliminate("npc_killer", "npc_victim", "beach");

                var produced = _clueMessages.Published.Count - publishedBefore;
                publishedBefore = _clueMessages.Published.Count;
                killCounts[Math.Min(produced, 2)]++;
            }
            log.AppendLine($"kill x{kills} (blood=100% + scratch=30%):");
            log.AppendLine($"  0 clues: {killCounts[0]} ({100.0 * killCounts[0] / kills:F1}%) — P=0 (blood 100% ห้ามพลาด)");
            log.AppendLine($"  1 clue : {killCounts[1]} ({100.0 * killCounts[1] / kills:F1}%) — blood-only, expect ≈70%");
            log.AppendLine($"  2 clues: {killCounts[2]} ({100.0 * killCounts[2] / kills:F1}%) — blood+scratch, expect ≈30%");

            Assert.That(killCounts[0], Is.EqualTo(0), "blood weight=100 → kill ห้ามได้ 0 clue");
            Assert.That(killCounts[1], Is.GreaterThan(0), "ต้องเคยได้ 1 (blood-only)");
            Assert.That(killCounts[2], Is.GreaterThan(0), "ต้องเคยได้ 2 (ทั้งคู่)");
            Assert.That(killCounts[2], Is.InRange(40, 80),
                "scratch=30% ของ 200 รอบ → ~60 (σ≈6.5, ±3σ) — ออกนอกย่าง = roll ไม่ independent ต่อ trigger");

            // ---- พิสูจน์ 0/1/2 ครบด้วย Hunting (blood=25 + scratch=15) ----
            var huntCounts = new Dictionary<int, int> { [0] = 0, [1] = 0, [2] = 0 };
            const int hunts = 150;
            for (var i = 0; i < hunts; i++)
            {
                var got = _gen.TryGenerate(ClueTriggerSource.Hunting, "beach", "npc_killer").Count;
                huntCounts[Math.Min(got, 2)]++;
            }
            log.AppendLine($"TryGenerate(Hunting) x{hunts} (blood=25% + scratch=15%):");
            log.AppendLine($"  0 clues: {huntCounts[0]} ({100.0 * huntCounts[0] / hunts:F1}%) — expect ≈63.75%");
            log.AppendLine($"  1 clue : {huntCounts[1]} ({100.0 * huntCounts[1] / hunts:F1}%) — expect ≈32.5%");
            log.AppendLine($"  2 clues: {huntCounts[2]} ({100.0 * huntCounts[2] / hunts:F1}%) — expect ≈3.75%");

            Assert.That(huntCounts[0], Is.GreaterThan(0), "ทั้งคู่ไม่ติด (0 clue) ต้องเกิดได้จริง");
            Assert.That(huntCounts[1], Is.GreaterThan(0), "ได้ตัวเดียว ต้องเกิดได้จริง");
            Assert.That(huntCounts[2], Is.GreaterThan(0), "ได้ครบ 2 ต้องเกิดได้จริง (P≈3.75% ของ 150 รอบ)");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("C_IndependentRolls_PerTrigger_WeightsHonored", log.ToString());
        }

        // ---------- Test D: witness hygiene ----------

        [Test]
        public void D_WitnessSnapshot_ExcludesKiller_AndOthers()
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (b) Test D — {DateTime.Now:HH:mm:ss} ===");
            StageNpcs("npc_killer", "beach",
                ("npc_witness", "beach"),   // พยานจริง
                ("npc_dead", "beach"),      // ตายแล้ว — ห้ามเป็นพยาน
                ("npc_far", "jungle_edge")); // โซนอื่น — ไม่เห็น
            Npc("npc_dead").IsAlive = false;
            _state.GetPlayer().CurrentLocationId = "beach";

            // เรียก TryGenerate ตรง (source อื่นเช่น Hunting/Incidental ไม่มีกฎ no-witness
            // จึงมีพยานครบ ๆ ได้ — kill hook เองถูกกฎเดิมบังคับให้ฆ่าตอนไม่มี NPC พยาน)
            var instances = _gen.TryGenerate(ClueTriggerSource.KillSabotage, "beach", "npc_killer");
            Assert.That(instances.Count, Is.GreaterThanOrEqualTo(1), "blood (100%) ต้องเกิด");

            foreach (var inst in instances)
            {
                Assert.That(inst.WitnessNpcIds, Does.Not.Contain("npc_killer"),
                    "⚠️ killer (ผู้ก่อเหตุ) ห้ามเป็นพยานเหตุการณ์ตัวเอง");
                Assert.That(inst.WitnessNpcIds, Does.Not.Contain("npc_dead"),
                    "NPC ตายแล้วไม่นับเป็นพยาน");
                Assert.That(inst.WitnessNpcIds, Does.Not.Contain("npc_far"),
                    "NPC โซนอื่นไม่เห็นเหตุการณ์");
                Assert.That(inst.WitnessNpcIds, Does.Contain("npc_witness"),
                    "NPC มีชีวิตในโซนเดียวกันต้องถูก snapshot");
                Assert.That(inst.WitnessNpcIds, Does.Contain("player_local"),
                    "player อยู่โซนเดียวกัน (ไม่ใช่ killer) ต้องถูก snapshot");
                Assert.That(inst.WitnessNpcIds, Is.Unique, "snapshot ห้ามมี id ซ้ำ");
                log.AppendLine($"{inst.DefId}: witnesses=[{string.Join(",", inst.WitnessNpcIds)}]");
            }

            // ผ่าน kill hook จริง (TryEliminate — กติกาเดิมบังคับไม่มี NPC พยาน):
            // killer ก็ห้ามปนใน snapshot เสมอ แม้ list จะเป็น []/มีแต่ player
            StageNpcs("npc_killer", "beach", ("npc_victim", "beach"), ("npc_far", "jungle_edge"));
            _state.GetPlayer().CurrentLocationId = "jungle_edge"; // player ไม่อยู่โซน kill
            var (success, reason) = _director.TryEliminate("npc_killer", "npc_victim", "beach");
            Assert.IsTrue(success, $"kill ต้องสำเร็จ: {reason}");
            foreach (var inst in _state.AllClueInstances.Values.Where(i => i.SourceActorId == "npc_killer"))
            {
                Assert.That(inst.WitnessNpcIds, Does.Not.Contain("npc_killer"),
                    "⚠️ end-to-end kill: killer ห้ามอยู่ใน WitnessNpcIds");
                log.AppendLine($"kill-hook {inst.DefId}: witnesses=[{string.Join(",", inst.WitnessNpcIds)}] (no killer)");
            }

            log.AppendLine("RESULT: PASS");
            WriteEvidence("D_WitnessSnapshot_ExcludesKiller_AndOthers", log.ToString());
        }

        // ---------- Test E: player เป็น killer ----------

        [Test]
        public void E_PlayerAsKiller_PlayerNeverOwnWitness()
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (b) Test E — {DateTime.Now:HH:mm:ss} ===");
            // actor ที่นี่เป็นแค่ bystander — killer คือ player (ชื่อ npc_by_1 กันสับสนกับ evidence)
            StageNpcs("npc_by_1", "beach", ("npc_a", "beach"), ("npc_b", "beach"));
            _state.GetPlayer().CurrentLocationId = "beach";

            // ผ่าน TryGenerate ตรง — พยานต้องมี NPC ในโซนแต่ "ห้ามมี player_local"
            var instances = _gen.TryGenerate(ClueTriggerSource.KillSabotage, "beach", GameStateProvider.LocalPlayerId);
            Assert.That(instances.Count, Is.GreaterThanOrEqualTo(1), "blood (100%) ต้องเกิด");
            foreach (var inst in instances)
            {
                Assert.That(inst.SourceActorId, Is.EqualTo(GameStateProvider.LocalPlayerId));
                Assert.That(inst.WitnessNpcIds, Does.Not.Contain(GameStateProvider.LocalPlayerId),
                    "⚠️ player ไม่เป็นพยานตัวเอง");
                Assert.That(inst.WitnessNpcIds, Does.Contain("npc_a"), "NPC ในโซนยังเป็นพยานตามปกติ");
                Assert.That(inst.WitnessNpcIds, Does.Contain("npc_b"));
                log.AppendLine($"player-kill snapshot: witnesses=[{string.Join(",", inst.WitnessNpcIds)}] (no player_local)");
            }

            // ผ่าน kill hook เต็ม (TryEliminate) — player ฆ่า npc_by_1 ในโซนที่มีแค่เหยื่อ
            // (CanEliminate เดิมบังคับไม่มี NPC พยาน — จึงย้าย npc_a/b ออกก่อน)
            _director.MoveNpc("npc_a", "jungle_edge");
            _director.MoveNpc("npc_b", "jungle_edge");
            var (success, reason) = _director.TryEliminate(GameStateProvider.LocalPlayerId, "npc_by_1", "beach");
            Assert.IsTrue(success, $"player kill ต้องสำเร็จ: {reason}");
            foreach (var inst in _state.AllClueInstances.Values.Where(i => i.SourceActorId == GameStateProvider.LocalPlayerId))
            {
                Assert.That(inst.WitnessNpcIds, Does.Not.Contain(GameStateProvider.LocalPlayerId),
                    "⚠️ end-to-end: player-killer ห้ามอยู่ใน WitnessNpcIds");
                log.AppendLine($"end-to-end {inst.DefId}: witnesses=[{string.Join(",", inst.WitnessNpcIds)}]");
            }

            log.AppendLine("RESULT: PASS");
            WriteEvidence("E_PlayerAsKiller_PlayerNeverOwnWitness", log.ToString());
        }

        private static void WriteEvidence(string test, string body)
        {
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", EvidenceDir));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{test}.txt"), body + Environment.NewLine);
            Debug.Log($"[ClueGenerationEditModeTests] {test} PASS — evidence: {EvidenceDir}/");
        }
    }
}
