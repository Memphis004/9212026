using System;
using System.Collections.Generic;
using System.Linq;
using Marooned.Shared;
using Marooned.Systems;
using Marooned.Systems.AI;
using MessagePipe;
using NUnit.Framework;
using UnityEngine;
using VContainer;

namespace Marooned.Tests.Editor
{
    /// <summary>
    /// Hybrid Transition Points (Part 2) — EditMode tests หลักฐาน Task 8 (A-F)
    /// FSM เป็น plain C# (ข้อจำกัด #2) จึง drive แบบ deterministic ได้โดยไม่ต้อง Play:
    /// สร้าง NpcDirectorSystem จริง (Luban จริง + fake publisher) แล้ว tick
    /// NpcMovementSystem → NpcZoneTransitionSystem ตามลำดับจริงของ GameTickDriver
    /// </summary>
    public class NpcZoneTransitionEditModeTests
    {
        private class FakePublisher<T> : IPublisher<T>
        {
            public List<T> Published { get; } = new();
            public void Publish(T message) => Published.Add(message);
        }

        private LubanDataService _data;
        private UtilityContext _ctx;
        private NpcDirectorSystem _director;
        private FakePublisher<NpcLocationChangedMessage> _directorMessages;
        private NpcMovementSystem _movement;
        private NpcZoneTransitionSystem _transition;

        [SetUp]
        public void SetUp()
        {
            _data = new LubanDataService(); // โหลดตารางจริงจาก Resources (รวม TbZoneConnectionDef)
            _ctx = new UtilityContext(_data, new GameStateProvider());
            _directorMessages = new FakePublisher<NpcLocationChangedMessage>();

            var innocent = new InnocentUtilityAI(_ctx);
            var killer = new KillerPlanner(_ctx);
            // Clue System v2 (b): generator ตัวจริง — TryEliminate ผ่าน pipeline ใหม่เสมอ
            var clueGeneration = new ClueGenerationSystem(_data, new GameStateProvider(), _ctx,
                new FakePublisher<ClueGeneratedMessage>());
            _director = new NpcDirectorSystem(_data, new FakePublisher<NpcEliminatedMessage>(),
                _directorMessages, innocent, killer, _ctx, clueGeneration);
            _ctx.Bind(_director);

            _director.SetupRound(new[] { "npc_01", "npc_02" }, killerCount: 0); // innocent ทั้งคู่ — คุม role เองในเทสที่ต้องใช้
            _director.MoveNpc("npc_01", "beach");
            _director.MoveNpc("npc_02", "beach");

            _movement = new NpcMovementSystem(_director);
            _transition = new NpcZoneTransitionSystem(_director, _data);
        }

        private NpcState Npc(string id) => _director.Npcs[id];

        private void TickFrames(float dt, int maxFrames, Func<bool> until)
        {
            for (int i = 0; i < maxFrames && !until(); i++)
            {
                _movement.Tick(dt);
                _transition.Tick(dt); // tick order จริง: Director → Movement → Transition (Director ไม่ tick ในเทส FSM เพื่อไม่ให้ AI แทรก)
            }
        }

        // ---------- ข้อมูล Part 1 ยังอยู่ครบ ----------

        [Test]
        public void ZoneConnectionData_AllAdjacentPairsPresent_Bidirectional()
        {
            var pairs = new[]
            {
                (from: "beach", to: "jungle_edge"),
                (from: "jungle_edge", to: "beach"),
                (from: "jungle_edge", to: "deep_jungle"),
                (from: "deep_jungle", to: "jungle_edge"),
                (from: "beach", to: "cave_entrance"),
                (from: "cave_entrance", to: "beach"),
            };
            foreach (var p in pairs)
            {
                var def = _data.GetTransition(p.from, p.to);
                Assert.IsNotNull(def, $"missing row {p.from}->{p.to}");
                Assert.AreEqual(p.to, def.ToLocationId);
            }
        }

        // ---------- Test A+B: เดินเข้าจุดเชื่อม → exit → ข้ามโซนที่ arrival point → enter → ครบ cycle ----------

        [Test]
        public void FullCycle_WalkToPoint_Exit_CrossToArrivalPoint_Enter()
        {
            var npc = Npc("npc_01");
            Assert.AreEqual(NpcTransitionPhase.None, npc.TransitionPhase);

            var start = (npc.PositionX, npc.PositionY);
            Assert.IsTrue(Wander.MoveToZone(npc, _ctx, "jungle_edge", new System.Random()));
            Assert.AreEqual(NpcTransitionPhase.WalkingToPoint, npc.TransitionPhase);
            Assert.AreEqual("jungle_edge", npc.PendingTransitionTargetZoneId);
            Assert.AreNotEqual(start, (npc.TargetX, npc.TargetY), "target ต้องถูกตั้งเป็นจุดเชื่อม (8,4)");

            // เดินจริง: ระหว่าง WalkingToPoint position ต้องเปลี่ยน
            var posMid = -1f;
            TickFrames(0.2f, 40, () =>
            {
                if (npc.TransitionPhase == NpcTransitionPhase.WalkingToPoint && posMid < 0f && (npc.PositionX != start.Item1 || npc.PositionY != start.Item2))
                    posMid = npc.PositionX;
                return npc.TransitionPhase != NpcTransitionPhase.WalkingToPoint;
            });
            Assert.AreEqual(NpcTransitionPhase.Exiting, npc.TransitionPhase, "ถึงจุดเชื่อมแล้วต้องเข้า Exiting");
            Assert.GreaterOrEqual(posMid, 0f, "ต้องมีการเดินจริง (position เปลี่ยน) ก่อนถึงจุดเชื่อม");

            // exit anim 0.5s — ยังอยู่โซนเดิม, ยังไม่ย้าย
            _transition.Tick(0.25f);
            Assert.AreEqual("beach", npc.CurrentLocationId, "ระหว่าง exit anim ยังไม่ข้ามโซน");
            _transition.Tick(0.35f);

            // ข้ามโซนแล้ว: zone = jungle_edge + ยืนที่ arrival point (reverse row: jungle_edge→beach = 2,1)
            Assert.AreEqual("jungle_edge", npc.CurrentLocationId);
            Assert.AreEqual(NpcTransitionPhase.Entering, npc.TransitionPhase);
            Assert.IsNull(npc.PendingTransitionTargetZoneId);
            Assert.AreEqual(2f, npc.PositionX, 0.01f, "ต้องวางที่ arrival point X ของโซนปลายทาง (ไม่ใช่กลางโซน)");
            Assert.AreEqual(1f, npc.PositionY, 0.01f, "ต้องวางที่ arrival point Y ของโซนปลายทาง (ไม่ใช่กลางโซน)");
            Assert.AreEqual(1, _directorMessages.Published.Count(m => m.NpcId == "npc_01" && m.NewLocationId == "jungle_edge"),
                "MoveNpc ต้อง publish NpcLocationChangedMessage จุดเดียว");

            // enter anim 0.3s → กลับ None + เดินต่อได้
            _transition.Tick(0.4f);
            Assert.AreEqual(NpcTransitionPhase.None, npc.TransitionPhase);
            Assert.IsTrue(Wander.SetRandomTargetInZone(npc, _ctx, new System.Random()), "หลังจบ cycle AI ตั้ง target ใหม่ได้");
        }

        // ---------- Test C: AI ไม่ตั้ง target ซ้อนระหว่าง transition ----------

        [Test]
        public void AiGuard_InnocentAndKiller_SkipWhileTransitioning()
        {
            var innocentNpc = Npc("npc_01");
            Wander.MoveToZone(innocentNpc, _ctx, "jungle_edge", new System.Random());
            var (tx, ty, act, phase) = (innocentNpc.TargetX, innocentNpc.TargetY, innocentNpc.Activity, innocentNpc.TransitionPhase);

            var innocentAi = new InnocentUtilityAI(_ctx);
            for (int i = 0; i < 5; i++) innocentAi.Tick(innocentNpc, 1.0f);
            Assert.AreEqual(tx, innocentNpc.TargetX, "InnocentUtilityAI ต้องไม่แตะ target ระหว่าง transition");
            Assert.AreEqual(ty, innocentNpc.TargetY, "InnocentUtilityAI ต้องไม่แตะ target ระหว่าง transition");
            Assert.AreEqual(act, innocentNpc.Activity);
            Assert.AreEqual(phase, innocentNpc.TransitionPhase);
            Assert.IsNull(innocentAi.GetCurrentActionId("npc_01"), "ไม่ควรเลือก action ระหว่าง transition");

            // killer: role ตั้งตรงนี้ (SetupRound killerCount=0) — cooldown ต้องไม่ถูกแตะระหว่าง transition
            var killerNpc = Npc("npc_02");
            killerNpc.Role = NpcRole.Killer;
            _director.MoveNpc("npc_02", "beach"); // seed ใหม่หลังย้าย role (จริงๆ อยู่ beach แล้ว — ยืนยัน seed)
            Assert.IsTrue(Wander.MoveToZone(killerNpc, _ctx, "jungle_edge", new System.Random()));
            var (ktx, kty, cd) = (killerNpc.TargetX, killerNpc.TargetY, killerNpc.KillCooldownRemaining);
            var killerAi = new KillerPlanner(_ctx);
            for (int i = 0; i < 5; i++) killerAi.Tick(killerNpc, 1.0f);
            Assert.AreEqual(ktx, killerNpc.TargetX, "KillerPlanner ต้องไม่ re-target ระหว่าง transition");
            Assert.AreEqual(kty, killerNpc.TargetY);
            Assert.AreEqual(cd, killerNpc.KillCooldownRemaining, "cooldown ต้องไม่ถูกลดระหว่าง transition (Tick return ก่อน)");
        }

        // ---------- Test D: คู่ที่ไม่มีใน CSV → fallback teleport ----------

        [Test]
        public void FallbackTeleport_PairMissingInCsv_NoError()
        {
            var npc = Npc("npc_01");
            // beach→deep_jungle ไม่มี row (non-adjacent) — requireConnection:false เพื่อยังเรียกผ่าน MoveToZone
            Assert.IsNull(_data.GetTransition("beach", "deep_jungle"));
            Assert.DoesNotThrow(() => Wander.MoveToZone(npc, _ctx, "deep_jungle", new System.Random(), requireConnection: false));

            Assert.AreEqual("deep_jungle", npc.CurrentLocationId, "ต้อง teleport แบบเดิมผ่าน MoveNpc");
            Assert.AreEqual(NpcTransitionPhase.None, npc.TransitionPhase, "teleport ไม่เข้า FSM");
            Assert.IsNull(npc.PendingTransitionTargetZoneId);
            Assert.AreEqual(20f, npc.PositionX, 0.01f); // Position Seeding Rule: กลางโซน deep_jungle
            Assert.AreEqual(10f, npc.PositionY, 0.01f);
        }

        // ---------- Test E: ตายกลาง transition → cleanup ----------

        [Test]
        public void DeathMidTransition_ClearsPhaseAndTimer()
        {
            var npc = Npc("npc_01");
            Wander.MoveToZone(npc, _ctx, "jungle_edge", new System.Random());
            TickFrames(0.2f, 40, () => npc.TransitionPhase != NpcTransitionPhase.WalkingToPoint);
            Assert.AreEqual(NpcTransitionPhase.Exiting, npc.TransitionPhase); // timer กำลังนับ

            npc.IsAlive = false; // ตายกลาง exit anim
            _transition.Tick(0.016f);

            Assert.AreEqual(NpcTransitionPhase.None, npc.TransitionPhase, "phase ต้องถูกเคลียร์");
            Assert.IsNull(npc.PendingTransitionTargetZoneId, "pending ต้องถูกเคลียร์");
            Assert.AreEqual("beach", npc.CurrentLocationId, "ตายกลาง transition ต้องไม่ย้ายโซน");

            // ไม่ crash เมื่อ tick ต่อ (NPC ตายถูก skip)
            Assert.DoesNotThrow(() => _transition.Tick(0.016f));
        }

        // ---------- Test F: ground truth ไม่หลุด NpcObservableView + MCP shape คงเดิม ----------

        [Test]
        public void ObservableView_NoTransitionGroundTruthLeak()
        {
            var viewType = typeof(NpcObservableView);
            var members = viewType.GetFields().Select(f => f.Name)
                .Concat(viewType.GetProperties().Select(p => p.Name)).ToList();

            Assert.IsFalse(members.Any(n => n.Contains("Transition") || n.Contains("Pending")),
                $"NpcObservableView ห้ามมี ground truth transition fields (มี: {string.Join(",", members)})");
            Assert.AreEqual(6, members.Count, "รูปทรง observable view ต้องคงเดิม (6 members)");

            // projection จริง: ระหว่าง transition — NPC ยังถูกมองเห็นแบบเดิม (ไม่ expose phase)
            var npc = Npc("npc_01");
            Wander.MoveToZone(npc, _ctx, "jungle_edge", new System.Random());
            var deduction = new DeductionSystem(_director, new GameStateProvider(), _data);
            var views = deduction.GetObservableNpcsAt("beach");
            var view = views.FirstOrDefault(v => v.Id == "npc_01");
            Assert.IsNotNull(view, "NPC ระหว่าง transition ยังอยู่ในโซนเดิม — ต้องยังเห็นได้");
            Assert.AreEqual("beach", view.CurrentLocationId);
        }
    }
}
