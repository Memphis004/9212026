using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Marooned.Shared;
using Marooned.Systems;
using NUnit.Framework;
using UnityEngine;

namespace Marooned.Tests.Editor
{
    /// <summary>
    /// Clue System v2 — Part (a): Data Model + Registry (EditMode evidence)
    ///
    /// A: ActionClueTriggerDefs โหลดจาก Luban ได้ครบ 7 triggers (source/clueDefId/weight ถูกต้อง)
    /// B: ClueDef ทุกตัวมี ClueCategory (อาจว่าง) + มี clue_wet_clothes (category "wet") เพิ่มจากเดิม 4 เป็น 5
    /// C: GameStateProvider.AllClueInstances เริ่มต้นว่าง (central registry)
    /// D: GetClueBoardHandler compile ผ่าน + อ่าน CollectedClueInstanceIds (field ใหม่)
    ///    แต่ยัง assign เข้า GetClueBoardResponse.CollectedClueCardIds เดิม (response shape คงเดิมจนกว่า commit (d))
    /// </summary>
    public class ClueSystemV2EditModeTests
    {
        public const string EvidenceDir = "TestEvidence/clue-system-v2-a";

        LubanDataService _data;
        GameStateProvider _state;

        [SetUp]
        public void SetUp()
        {
            _data = new LubanDataService(); // ตารางจริงจาก Resources/DataTables (gen.sh แล้ว)
            _state = new GameStateProvider();
        }

        [Test]
        public void A_ActionClueTriggerDefs_LoadsSevenTriggers()
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (a) Test A — {DateTime.Now:HH:mm:ss} ===");

            var triggers = _data.ActionClueTriggerDefs;
            log.AppendLine($"ActionClueTriggerDefs.Count = {triggers.Count}");

            Assert.That(triggers, Is.Not.Null, "ActionClueTriggerDefs ต้องถูกโหลดจาก Luban");
            Assert.That(triggers.Count, Is.EqualTo(7),
                $"ต้องมี 7 triggers ตาม ActionClueTriggerDef.csv (ได้มา {triggers.Count})");

            // spot-check ทุก triggerSource ที่ลง CSV
            Assert.That(triggers["trig_kill_blood"].TriggerSource, Is.EqualTo(ClueTriggerSource.KillSabotage));
            Assert.That(triggers["trig_kill_blood"].ClueDefId, Is.EqualTo("clue_blood_stain"));
            Assert.That(triggers["trig_kill_blood"].Weight, Is.EqualTo(100));

            Assert.That(triggers["trig_kill_scratch"].TriggerSource, Is.EqualTo(ClueTriggerSource.KillSabotage));
            Assert.That(triggers["trig_kill_scratch"].Weight, Is.EqualTo(30));

            Assert.That(triggers["trig_hunt_blood"].TriggerSource, Is.EqualTo(ClueTriggerSource.Hunting));
            Assert.That(triggers["trig_hunt_blood"].Weight, Is.EqualTo(25));

            Assert.That(triggers["trig_hunt_scratch"].TriggerSource, Is.EqualTo(ClueTriggerSource.Hunting));
            Assert.That(triggers["trig_hunt_scratch"].ClueDefId, Is.EqualTo("clue_scratch_mark"));
            Assert.That(triggers["trig_hunt_scratch"].Weight, Is.EqualTo(15));

            Assert.That(triggers["trig_water_wet"].TriggerSource, Is.EqualTo(ClueTriggerSource.IncidentalAction));
            Assert.That(triggers["trig_water_wet"].ClueDefId, Is.EqualTo("clue_wet_clothes"));
            Assert.That(triggers["trig_water_wet"].Weight, Is.EqualTo(20));

            Assert.That(triggers["trig_water_mud"].TriggerSource, Is.EqualTo(ClueTriggerSource.IncidentalAction));
            Assert.That(triggers["trig_water_mud"].ClueDefId, Is.EqualTo("clue_footprint_mud"));
            Assert.That(triggers["trig_water_mud"].Weight, Is.EqualTo(15));

            Assert.That(triggers["trig_task_sabotage"].TriggerSource, Is.EqualTo(ClueTriggerSource.TaskSabotage));
            Assert.That(triggers["trig_task_sabotage"].ClueDefId, Is.EqualTo("clue_torn_cloth"));
            Assert.That(triggers["trig_task_sabotage"].Weight, Is.EqualTo(40));

            // referential integrity: clueDefId ทุกแถวต้องชี้ ClueDef ที่มีจริง
            foreach (var t in triggers.Values)
                Assert.That(_data.ClueDefs.ContainsKey(t.ClueDefId),
                    $"{t.Id} อ้าง clueDefId '{t.ClueDefId}' ซึ่งไม่มีใน ClueDefs");

            foreach (var t in triggers.Values)
                log.AppendLine($"{t.Id}: source={t.TriggerSource}, clue={t.ClueDefId}, weight={t.Weight}");
            log.AppendLine("RESULT: PASS");
            WriteEvidence("A_ActionClueTriggerDefs_LoadsSevenTriggers", log.ToString());
        }

        [Test]
        public void B_AllClueDefs_HaveClueCategory_WetClothesAdded()
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (a) Test B — {DateTime.Now:HH:mm:ss} ===");

            var clues = _data.ClueDefs;
            log.AppendLine($"ClueDefs.Count = {clues.Count}");

            Assert.That(clues.Count, Is.EqualTo(5),
                $"เดิมมี 4 ชนิด + clue_wet_clothes (เพิ่มใหม่) = 5 (ได้มา {clues.Count})");
            Assert.That(clues.ContainsKey("clue_wet_clothes"), "clue_wet_clothes ต้องถูกเพิ่มใน ClueDef.csv");

            // ทุก def ต้องมี field ClueCategory (copied โดย MapClues — อาจว่างได้ แต่ห้าม null)
            foreach (var c in clues.Values)
            {
                Assert.That(c.ClueCategory, Is.Not.Null,
                    $"{c.Id}.ClueCategory ห้าม null — MapClues ต้อง copy clueCategory จาก generated def");
                log.AppendLine($"{c.Id}: category='{c.ClueCategory}', reliability={c.Reliability}");
            }

            Assert.That(clues["clue_wet_clothes"].ClueCategory, Is.EqualTo("wet"));
            Assert.That(clues["clue_blood_stain"].ClueCategory, Is.EqualTo("blood"));
            Assert.That(clues["clue_footprint_mud"].ClueCategory, Is.EqualTo("footprint"));

            log.AppendLine("RESULT: PASS");
            WriteEvidence("B_AllClueDefs_HaveClueCategory_WetClothesAdded", log.ToString());
        }

        [Test]
        public void C_AllClueInstances_RegistryStartsEmpty()
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (a) Test C — {DateTime.Now:HH:mm:ss} ===");

            Assert.That(_state.AllClueInstances, Is.Not.Null, "AllClueInstances ต้องถูก initialize ทันที");
            Assert.That(_state.AllClueInstances.Count, Is.EqualTo(0),
                "central registry เริ่มต้นต้องว่าง — clue instance จะถูกใส่ตอน commit (b) trigger system");
            log.AppendLine("AllClueInstances.Count = 0 (fresh GameStateProvider)");
            log.AppendLine("RESULT: PASS");
            WriteEvidence("C_AllClueInstances_RegistryStartsEmpty", log.ToString());
        }

        [Test]
        public void D_GetClueBoardHandler_Compiles_AndReturnsCollectedInstanceIds()
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (a) Test D — {DateTime.Now:HH:mm:ss} (updated for (d) shape) ===");

            var handler = new GetClueBoardHandler(_state, null, _data);

            // ยังไม่มี clue — ต้องได้ list ว่าง
            var empty = handler.InvokeAsync(new GetClueBoardRequest()).GetAwaiter().GetResult();
            Assert.That(empty.Entries, Is.Not.Null);
            Assert.That(empty.Entries, Is.Empty,
                "ยังไม่มี clue ถูก trigger → board ต้องว่าง");
            log.AppendLine($"empty board → {empty.Entries.Count} entr(ies) (shape ใหม่ (d): List<ClueBoardEntry>)");

            // ผู้เล่นเก็บ clue instance → handler อ่านจาก CollectedClueInstanceIds
            // แต่ instance ยังไม่อยู่ใน registry → ถูกข้าม (ไม่ crash)
            _state.GetPlayer().CollectedClueInstanceIds.Add("clue_inst_001");
            var res = handler.InvokeAsync(new GetClueBoardRequest()).GetAwaiter().GetResult();
            Assert.That(res.Entries.Count, Is.EqualTo(0),
                "instance id ที่ไม่มีใน registry ต้องถูกข้าม (ไม่ throw)");
            log.AppendLine("dangling instance id → skipped gracefully");

            // ใส่ instance จริงใน registry → entry เต็มรูป (def resolve + witness กรอง)
            _state.AllClueInstances["clue_inst_001"] = new ClueInstance
            {
                InstanceId = "clue_inst_001",
                DefId = "clue_blood_stain",
                LocationId = "beach",
                GameTimestamp = 12.3f,
                Source = ClueTriggerSource.KillSabotage,
                SourceActorId = "npc_killer",
                WitnessNpcIds = new List<string> { "player_local" },
            };
            var res2 = handler.InvokeAsync(new GetClueBoardRequest()).GetAwaiter().GetResult();
            Assert.That(res2.Entries.Count, Is.EqualTo(1), "instance ใน registry → 1 entry");
            var entry = res2.Entries[0];
            Assert.That(entry.InstanceId, Is.EqualTo("clue_inst_001"));
            Assert.That(entry.DisplayName, Is.EqualTo("คราบเลือด"), "DisplayName resolve จาก ClueDef");
            Assert.That(entry.Reliability, Is.EqualTo("Strong"), "Reliability resolve จาก ClueDef");
            Assert.That(entry.LocationId, Is.EqualTo("beach"));
            Assert.That(entry.WitnessNpcIds, Does.Contain("player_local"), "player_local คงอยู่เสมอ");
            log.AppendLine($"entry: {entry.InstanceId} | {entry.DisplayName} | {entry.Reliability} | {entry.LocationId} | witnesses=[{string.Join(", ", entry.WitnessNpcIds)}]");
            log.AppendLine("RESULT: PASS");
            WriteEvidence("D_GetClueBoardHandler_Compiles_AndReturnsCollectedInstanceIds", log.ToString());
        }

        private static void WriteEvidence(string test, string body)
        {
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", EvidenceDir));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{test}.txt"), body + Environment.NewLine);
            Debug.Log($"[ClueSystemV2EditModeTests] {test} PASS — evidence: {EvidenceDir}/");
        }
    }
}
