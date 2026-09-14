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
    /// Clue System v2 — Part (c): Incidental Hooks (EditMode evidence)
    ///
    /// A: explore hook — ExplorationSystem.Explore() ที่ได้ loot "สายน้ำ" (water_bottle/
    ///    raw_fish) หรือสุ่ม 10% ต้องเรียก TryGenerate(IncidentalAction) — พิสูจน์แบบ
    ///    statistical (สร้าง ExplorationSystem ใหม่ทุก iteration เพื่อคืบ loot pool)
    ///    และ clue ที่เกิดต้องเป็นกลุ่ม wet/mud เท่านั้น ณ location ที่สำรวจ
    /// +: HarvestableNodeDef mapping — node_deer (isHuntingTarget=true) โหลดจาก Luban
    ///    จริง, node ทั่วไปยังเป็น false (รองรับ Test B ฝั่ง PlayMode)
    /// </summary>
    public class ClueHooksEditModeTests
    {
        public const string EvidenceDir = "TestEvidence/clue-system-v2-c";

        private class FakePublisher<T> : IPublisher<T>
        {
            public List<T> Published { get; } = new();
            public void Publish(T message) => Published.Add(message);
        }

        private LubanDataService _data;
        private GameStateProvider _state;
        private UtilityContext _ctx;
        private ClueGenerationSystem _gen;
        private FakePublisher<ClueGeneratedMessage> _clueMessages;
        private NpcDirectorSystem _director;

        [SetUp]
        public void SetUp()
        {
            _data = new LubanDataService();
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

        [Test]
        public void A_ExploreWaterLoot_HooksIncidentalAction()
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (c) Test A — {DateTime.Now:HH:mm:ss} ===");

            // beach loot: food_coconut(5) water_bottle(2) raw_fish(3) — สายน้ำ = 5/10
            var inventory = new CardInventorySystem(_state, _data, new FakePublisher<CardInventoryChangedMessage>());
            var playerLocationPub = new FakePublisher<PlayerLocationChangedMessage>();

            const int iterations = 100;
            var clueCountPerSource = new Dictionary<ClueTriggerSource, int>();
            var waterExplores = 0;

            for (var i = 0; i < iterations; i++)
            {
                // ระบบใหม่ทุก iteration — loot pool กลับมาเต็ม (เลี่ยง depletion)
                var explore = new ExplorationSystem(_data, inventory, _state, playerLocationPub, _gen);
                _state.GetPlayer().CurrentLocationId = "beach";
                var publishedBefore = _clueMessages.Published.Count;
                var (success, found) = explore.Explore("beach");
                Assert.IsTrue(success);

                var waterRelated = found.Any(id => id.Contains("water") || id == "raw_fish");
                if (waterRelated) waterExplores++;

                // message ใหม่ของ iteration นี้ = clue ที่ hook ยิง (publish 1:1 กับ instance)
                foreach (var msg in _clueMessages.Published.Skip(publishedBefore))
                {
                    var inst = _state.AllClueInstances[msg.InstanceId];
                    clueCountPerSource[inst.Source] = clueCountPerSource.GetValueOrDefault(inst.Source) + 1;
                    Assert.That(inst.LocationId, Is.EqualTo("beach"),
                        "clue ต้องเกิดที่ location ที่สำรวจเท่านั้น");
                }
            }

            var wet = clueCountPerSource.GetValueOrDefault(ClueTriggerSource.IncidentalAction);
            log.AppendLine($"explore('beach') x{iterations}: water-related loot {waterExplores} ครั้ง");
            log.AppendLine($"clue เกิดจาก IncidentalAction: {wet} ({100.0 * wet / iterations:F1}% — expect ≈19% = P(water)·35% + P(อื่น)·10%·35%)");
            foreach (var kv in clueCountPerSource)
                log.AppendLine($"  source {kv.Key}: {kv.Value}");

            Assert.That(wet, Is.GreaterThanOrEqualTo(1),
                "100 explores (water-loot 50% + สุ่ม 10%) → ต้องเกิด IncidentalAction clue อย่างน้อย 1 (P(0)≈10⁻⁹)");
            Assert.That(clueCountPerSource.Keys.All(s => s == ClueTriggerSource.IncidentalAction),
                "explore ต้องยิงเฉพาะกลุ่ม IncidentalAction (ไม่ใช่ Hunting/KillSabotage)");

            // def ที่เป็นไปได้ = wet/mud เท่านั้น (ตาม ActionClueTriggerDef.csv)
            var defs = _state.AllClueInstances.Values
                .Where(i => i.Source == ClueTriggerSource.IncidentalAction)
                .Select(i => i.DefId).Distinct().ToList();
            log.AppendLine($"defs ที่เกิดจริง: [{string.Join(", ", defs)}]");
            Assert.That(defs.All(d => d == "clue_wet_clothes" || d == "clue_footprint_mud"),
                "IncidentalAction ต้องปล่อยเฉพาะ clue_wet_clothes / clue_footprint_mud");

            log.AppendLine("RESULT: PASS");
            WriteEvidence("A_ExploreWaterLoot_HooksIncidentalAction", log.ToString());
        }

        [Test]
        public void HuntingDef_NodeDeerMapped_FromLuban()
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (c) Hunting-def mapping — {DateTime.Now:HH:mm:ss} ===");

            var defs = _data.HarvestableNodeDefs;
            Assert.That(defs.ContainsKey("node_deer"), "node_deer ต้องถูกเพิ่มใน HarvestableNodeDef.csv");
            Assert.That(defs["node_deer"].IsHuntingTarget, Is.True, "node_deer isHuntingTarget=true");
            Assert.That(defs["tree_wood"].IsHuntingTarget, Is.False, "tree_wood ยังเป็น false");
            Assert.That(defs["rock_stone"].IsHuntingTarget, Is.False, "rock_stone ยังเป็น false");
            Assert.That(defs["bush_berry"].IsHuntingTarget, Is.False, "bush_berry ยังเป็น false");
            log.AppendLine("node_deer.IsHuntingTarget=true; tree/rock/bush=false ( Luban → generated → Shared mapping ครบ)");
            log.AppendLine("RESULT: PASS");
            WriteEvidence("HuntingDef_NodeDeerMapped_FromLuban", log.ToString());
        }

        private static void WriteEvidence(string test, string body)
        {
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", EvidenceDir));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{test}.txt"), body + Environment.NewLine);
            Debug.Log($"[ClueHooksEditModeTests] {test} PASS — evidence: {EvidenceDir}/");
        }
    }
}
