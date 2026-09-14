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
    /// Lab C Phase 2.5A — Living NPCs (Survival Tick + Motive Events + Stub Actions)
    /// EditMode tests A-E: drive ระบบจริงตาม tick order ของ GameTickDriver
    /// (NpcSurvivalSystem → NpcDirectorSystem → NpcMovementSystem → NpcZoneTransitionSystem)
    /// ใช้ Luban จริง + VContainer container จริง (MessagePipe จริง) — stub แค่ publisher
    /// </summary>
    public class NpcSurvivalEditModeTests
    {
        private class FakePublisher<T> : IPublisher<T>
        {
            public List<T> Published { get; } = new();
            public void Publish(T message) => Published.Add(message);
        }

        private LubanDataService _data;
        private UtilityContext _ctx;
        private NpcDirectorSystem _director;
        private FakePublisher<NpcEliminatedMessage> _eliminatedMessages;
        private FakePublisher<NpcLocationChangedMessage> _directorMessages;
        private FakePublisher<ClueGeneratedMessage> _clueMessages;
        private NpcSurvivalSystem _survival;
        private NpcMovementSystem _movement;
        private NpcZoneTransitionSystem _transition;
        private InnocentUtilityAI _innocentAi;
        private IPublisher<NpcEliminatedMessage> _realBus;

        [SetUp]
        public void SetUp()
        {
            _data = new LubanDataService(); // ตารางจริงจาก Resources
            var stateProvider = new GameStateProvider();
            _ctx = new UtilityContext(_data, stateProvider);
            _eliminatedMessages = new FakePublisher<NpcEliminatedMessage>();
            _directorMessages = new FakePublisher<NpcLocationChangedMessage>();
            _clueMessages = new FakePublisher<ClueGeneratedMessage>();
            _innocentAi = new InnocentUtilityAI(_ctx);
            var killer = new KillerPlanner(_ctx);

            // Clue System v2 (b): generator ตัวจริง (ก่อน director ได้ — อ่าน
            // ctx.NpcDirector ตอน TryGenerate เท่านั้น จึงไม่มีปัญหาลำดับ construct)
            var clueGeneration = new ClueGenerationSystem(_data, stateProvider, _ctx, _clueMessages);

            _director = new NpcDirectorSystem(_data, _eliminatedMessages, _directorMessages,
                _innocentAi, killer, _ctx, clueGeneration);
            _ctx.Bind(_director);
            _director.SetupRound(new[] { "npc_01", "npc_02", "npc_03" }, killerCount: 0);
            _director.MoveNpc("npc_01", "beach");
            _director.MoveNpc("npc_02", "beach");
            _director.MoveNpc("npc_03", "jungle_edge");

            // MessagePipe จริง (ให้ hook subscribe ผ่าน container เดียวกับเกมจริง)
            // — test เผยแพร่เหตุการณ์ผ่าน bus จริงเอง เพราะ director ถือ fake publisher
            // (ลำดับเดียวกับเกมจริง: director publish → survival hook ยิง)
            var builder = new ContainerBuilder();
            builder.RegisterMessagePipe();
            var container = builder.Build();
            _survival = new NpcSurvivalSystem(_director, _data, _ctx,
                container.Resolve<ISubscriber<NpcEliminatedMessage>>());
            _realBus = container.Resolve<IPublisher<NpcEliminatedMessage>>();

            _movement = new NpcMovementSystem(_director);
            _transition = new NpcZoneTransitionSystem(_director, _data);
        }

        private NpcState Npc(string id) => _director.Npcs[id];

        /// <summary>tick ครบตามลำดับจริงของ GameTickDriver</summary>
        private void TickFrame(float dt)
        {
            _survival.Tick(dt);
            _director.Tick(dt);
            _movement.Tick(dt);
            _transition.Tick(dt);
        }

        private void TickSeconds(float seconds, float dt = 0.1f)
        {
            var elapsed = 0f;
            while (elapsed < seconds)
            {
                var step = Math.Min(dt, seconds - elapsed);
                TickFrame(step);
                elapsed += step;
            }
        }

        // ---------- Test A: decay ตามเวลา ----------

        [Test]
        public void Decay_HungerRises_FearAndCuriosityFall()
        {
            var npc = Npc("npc_01");
            npc.Survival.Hunger = 40f;
            npc.Survival.Fear = 80f;
            npc.Survival.Curiosity = 60f;

            TickSeconds(5f); // 5 วิเกม

            Assert.AreEqual(50f, npc.Survival.Hunger, 0.01f, "Hunger +2/วิ จาก 40 → 50");
            Assert.AreEqual(50f, npc.Survival.Fear, 0.01f, "Fear -6/วิ จาก 80 → 50");
            Assert.AreEqual(45f, npc.Survival.Curiosity, 0.01f, "Curiosity -3/วิ จาก 60 → 45");

            // clamp 0..100: Fear ลงถึง 0 แล้วหยุด, Hunger ขึ้นถึง 100 แล้วหยุด
            TickSeconds(30f);
            Assert.AreEqual(100f, npc.Survival.Hunger, "Hunger clamp ที่ 100");
            Assert.AreEqual(0f, npc.Survival.Fear, "Fear decay หยุดที่ 0");
            Assert.AreEqual(0f, npc.Survival.Curiosity, "Curiosity decay หยุดที่ 0");
        }

        [Test]
        public void Decay_DeadNpcSkipped()
        {
            var npc = Npc("npc_01");
            npc.IsAlive = false;
            npc.Survival.Hunger = 10f;
            npc.Survival.Fear = 90f;

            TickSeconds(10f);

            Assert.AreEqual(10f, npc.Survival.Hunger, "NPC ตายต้องไม่ถูก tick");
            Assert.AreEqual(90f, npc.Survival.Fear);
        }

        // ---------- Test B: เห็นศพ → Fear spike → Flee ชนะ re-evaluate ----------

        [Test]
        public void WitnessKill_FearSpikes_FleeActionWins()
        {
            var witness = Npc("npc_01"); // beach เดียวกับเหยื่อ
            Assert.AreEqual("beach", witness.CurrentLocationId);
            Assert.AreEqual(0f, witness.Survival.Fear);

            // kill จริงผ่าน director (สถานะฆ่าจริง) + publish ผ่าน bus จริง
            // (เกมจริง director เป็นคน publish — ที่นี่ fake จึง publish เองตามลำดับเดียวกัน)
            var victim = Npc("npc_02");
            var (success, _) = _director.TryEliminate("npc_01", "npc_02", "beach");
            Assert.IsTrue(success, "ต้องฆ่าสำเร็จ (killerCount=0 แต่ TryEliminate ไม่ได้กั้น role)");
            _realBus.Publish(new NpcEliminatedMessage
            {
                VictimNpcId = "npc_02",
                LocationId = "beach",
                SpawnedClueCardIds = new List<string>(),
            });

            Assert.AreEqual(50f, witness.Survival.Fear, 0.01f,
                "ผู้รอดชีวิตโซนเดียวกันต้องได้ Fear +50 จาก hook");

            // Fear=50 → FleeToSafeZone score 0.5 ชนะ IdleWander 0.1 หลัง re-evaluate
            TickFrame(0.016f);
            Assert.AreEqual("FleeToSafeZone", _innocentAi.GetCurrentActionId("npc_01"),
                "Fear พุ่ง → FleeToSafeZone ต้องชนะ re-evaluate ทันที");

            // หนีออกจาก beach: ยืนยันจาก event history (deterministic — ไม่ flak เพราะ
            // IdleWander อาจเดินกลับมา beach ในภายหลังหลัง Fear decay หมด)
            TickSeconds(20f);
            Assert.IsTrue(_directorMessages.Published.Any(m =>
                    m.NpcId == "npc_01" && m.OldLocationId == "beach"),
                "FleeToSafeZone ต้องพาออกจากโซนที่เห็นศพ (NpcLocationChangedMessage อย่างน้อย 1 ครั้ง)");
            Assert.Less(Npc("npc_01").Survival.Fear, 10f, "Fear ต้อง decay ใกล้ 0 แล้ว");
        }

        // ---------- Test C: ได้ยินเสียง → Curiosity spike → Investigate ----------

        [Test]
        public void HearNoise_CuriositySpikes_InvestigateActionWins()
        {
            var hearer = Npc("npc_03"); // jungle_edge — connected กับ beach (จุดเกิดเหตุ)
            Assert.AreEqual(0f, hearer.Survival.Curiosity);

            var victim = Npc("npc_02");
            var (success, _) = _director.TryEliminate("npc_01", "npc_02", "beach");
            Assert.IsTrue(success);
            _realBus.Publish(new NpcEliminatedMessage
            {
                VictimNpcId = "npc_02",
                LocationId = "beach",
                SpawnedClueCardIds = new List<string>(),
            });

            Assert.AreEqual(30f, hearer.Survival.Curiosity, 0.01f,
                "ผู้รอดชีวิต connected location ต้องได้ Curiosity +30");
            Assert.AreEqual("beach", hearer.Survival.LastNoiseLocationId,
                "ต้องจดจุดเสียงไว้ให้ InvestigateNoiseAction ใช้");

            // witness (npc_01) ต้องไม่โดนจดจุดเสียง (เห็นเอง — if/else if กันนับซ้ำ)
            Assert.IsNull(Npc("npc_01").Survival.LastNoiseLocationId);

            // Curiosity=30, Fear=50 (เห็นศพในโซนเดียวกับผู้ถูกฆ่า? ไม่ — npc_03 อยู่ jungle_edge
            // Fear=0) → score = 0.3 × 1.0 = 0.3 ชนะ IdleWander 0.1
            TickFrame(0.016f);
            Assert.AreEqual("InvestigateNoise", _innocentAi.GetCurrentActionId("npc_03"),
                "ได้ยินเสียง → InvestigateNoise ชนะ re-evaluate");

            // สำรวจ: เดินไป beach ผ่านจุดเชื่อม (FSM เดิม) — loop จนถึง (bounded 20 วิ)
            var reached = false;
            var elapsed = 0f;
            while (elapsed < 20f && !reached)
            {
                TickFrame(0.1f);
                elapsed += 0.1f;
                reached = Npc("npc_03").CurrentLocationId == "beach";
            }
            Assert.IsTrue(reached,
                $"ต้องเดินสำรวจถึงโซนจุดเสียงภายใน 20 วิ (ตอนจบ: {Npc("npc_03").CurrentLocationId})");
        }

        // ---------- Test D: clamp target ในรัศมีโซน + transition-aware ----------

        [Test]
        public void ClampTarget_KeepsNpcInZone_AndRespectsTransition()
        {
            var npc = Npc("npc_01"); // beach กลาง (0,0), radius 2.5/1.5

            // target หลุดโซน → ถูก clamp เข้าที่ (ผ่าน survival tick)
            npc.TargetX = 50f;
            npc.TargetY = -50f;
            _survival.Tick(0.016f);
            Assert.AreEqual(2.5f, npc.TargetX, 0.001f, "TargetX clamp ที่ worldX + 2.5");
            Assert.AreEqual(-1.5f, npc.TargetY, 0.001f, "TargetY clamp ที่ worldY - 1.5");

            // ⚠️ ระหว่าง transition — target = จุดเชื่อม (นอกรัศมี) ต้องไม่ถูก clamp
            Assert.IsTrue(Wander.MoveToZone(npc, _ctx, "jungle_edge", new System.Random()));
            Assert.AreEqual(NpcTransitionPhase.WalkingToPoint, npc.TransitionPhase);
            var (tx, ty) = (npc.TargetX, npc.TargetY);
            _survival.Tick(0.016f);
            Assert.AreEqual(tx, npc.TargetX, "ระหว่าง transition ห้าม clamp target (จุดเชื่อม)");
            Assert.AreEqual(ty, npc.TargetY);
        }

        [Test]
        public void ClampTarget_NoClampWhenTargetInsideZone()
        {
            var npc = Npc("npc_01");
            npc.TargetX = 1f; // ในรัศมี beach อยู่แล้ว
            npc.TargetY = 0.5f;
            _survival.Tick(0.016f); // clamp ผ่าน survival tick — ต้องไม่แตะ target ที่อยู่ในโซน
            Assert.AreEqual(1f, npc.TargetX, 0.001f, "target ในโซนต้องไม่ถูกแตะ");
            Assert.AreEqual(0.5f, npc.TargetY, 0.001f);
        }

        // ---------- Test E: info hiding — stats ไม่หลุด observables/MCP ----------

        [Test]
        public void InfoHiding_ObservableViewNeverExposesStats()
        {
            // หลัง motive spikes ค่า ground truth เปลี่ยนแล้ว — view ต้องยังเหมือนเดิม
            var victim = Npc("npc_02");
            _director.TryEliminate("npc_01", "npc_02", "beach");
            _realBus.Publish(new NpcEliminatedMessage
            {
                VictimNpcId = "npc_02",
                LocationId = "beach",
                SpawnedClueCardIds = new List<string>(),
            });
            Assert.Greater(Npc("npc_01").Survival.Fear, 0f, "precondition: Fear ถูกเพิ่มแล้ว");

            var deduction = new DeductionSystem(_director, new GameStateProvider(), _data);
            var viewType = typeof(NpcObservableView);
            var members = viewType.GetFields().Select(f => f.Name)
                .Concat(viewType.GetProperties().Select(p => p.Name)).ToList();

            Assert.IsFalse(members.Any(n =>
                    n.IndexOf("Hunger", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Fear", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Curiosity", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Survival", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("LastNoise", StringComparison.OrdinalIgnoreCase) >= 0),
                $"NpcObservableView ห้ามมี field stats (มี: {string.Join(",", members)})");
            Assert.AreEqual(6, members.Count, "รูปทรง observable 6 members ต้องคงเดิม");

            var views = deduction.GetObservableNpcsAt("beach");
            var view = views.FirstOrDefault(v => v.Id == "npc_01");
            Assert.IsNotNull(view);
            // view ที่ได้ = projection field-by-field — ไม่มีทางรั่วเว้นแต่เพิ่ม field ใหม่
        }

        // ---------- เสริม: LastNoiseLocationId lifecycle ----------

        [Test]
        public void NoiseHint_ClearedWhenCuriosityDecays()
        {
            var hearer = Npc("npc_03");
            var victim = Npc("npc_02");
            _director.TryEliminate("npc_01", "npc_02", "beach");
            _realBus.Publish(new NpcEliminatedMessage
            {
                VictimNpcId = "npc_02",
                LocationId = "beach",
                SpawnedClueCardIds = new List<string>(),
            });
            Assert.AreEqual("beach", hearer.Survival.LastNoiseLocationId);

            // Curiosity 30 → decay 3/วิ → < 5 threshold ใน ~9 วิ
            TickSeconds(12f);
            Assert.IsNull(hearer.Survival.LastNoiseLocationId,
                "Curiosity ต่ำกว่า threshold → hint ต้องถูกล้าง");
        }
    }
}
