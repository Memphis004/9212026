using System;
using System.Collections.Generic;
using System.Linq;
using Marooned.Shared;
using Marooned.Systems.AI;
using MessagePipe;

namespace Marooned.Systems
{
    /// <summary>
    /// Owns every NpcState (ground truth, including hidden Role). Moves NPCs between
    /// locations, lets the Killer NPC attempt eliminations when unwitnessed, and
    /// spawns Clue cards on the resulting body / nearby NPCs.
    ///
    /// Phase 4 (Player-as-Killer): TryEliminate/CanEliminate เป็น method กลางที่ทั้ง
    /// AI killer (Tick) และ player (weapon card ผ่าน UseCardHandler) ใช้ร่วมกัน
    ///
    /// Lab C Phase 2 (NPC Embodiment):
    ///  - MoveNpc() = จุดเดียวที่เปลี่ยน CurrentLocationId + ต้อง seed PositionX/Y
    ///    ด้วย LocationDef.WorldX/Y ของ destination เสมอ (Position Seeding Rule —
    ///    กัน NPC warp ไป (0,0) ตอนข้ามโซน)
    ///  - SetupRound() แจกอาวุธ (การ์ด Category==Weapon ตัวแรกที่ Luban โหลด)
    ///    ให้ NPC ที่ได้ Role=Killer — ต้องมีอยู่จริงใน DataTables/CardDef.csv
    ///
    /// Step 4 (Basic AI Hook): TickBehavior hook สมองแยกตาม role —
    ///   Role==Killer → KillerPlanner.Tick, อื่นๆ → InnocentUtilityAI.Tick
    ///   (ทั้งคู่ inject ผ่าน constructor จาก VContainer — ห้าม new เอง)
    ///   TryAttemptElimination (สุ่ม 15%/tick) ถูกลบแล้ว — การฆ่า AI ทั้งหมด
    ///   ผ่าน KillerPlanner → TryEliminate (เส้นทางเดียวกับ player)
    ///
    /// IMPORTANT: never hand out NpcState directly to MCP query handlers — always go
    /// through DeductionSystem.BuildObservableView (see DeductionVisibilityRules).
    /// </summary>
    public class NpcDirectorSystem
    {
        private readonly Dictionary<string, NpcState> _npcs = new();
        private readonly Dictionary<string, LocationDef> _locations;
        private readonly LubanDataService _data;
        private readonly IPublisher<NpcEliminatedMessage> _eliminatedPublisher;
        private readonly IPublisher<NpcLocationChangedMessage> _npcLocationPublisher;
        private readonly Random _rng = new();

        // Step 4: สมองแยกตาม role (inject จาก DI — ห้าม new เองใน constructor นี้)
        private readonly InnocentUtilityAI _innocentAI;
        private readonly KillerPlanner _killerPlanner;

        // Clue System v2 (b): generation pipeline (inject จาก DI) — SpawnClues เดิม
        // hardcode card ids แล้วถูกแทนด้วย ClueGenerationSystem.TryGenerate ทั้งหมด
        private readonly ClueGenerationSystem _clueGeneration;

        public IReadOnlyDictionary<string, NpcState> Npcs => _npcs;

        public NpcDirectorSystem(LubanDataService dataService, IPublisher<NpcEliminatedMessage> eliminatedPublisher,
            IPublisher<NpcLocationChangedMessage> npcLocationPublisher,
            InnocentUtilityAI innocentAI, KillerPlanner killerPlanner, UtilityContext aiContext,
            ClueGenerationSystem clueGeneration = null)
        {
            _data = dataService;
            _locations = dataService.LocationDefs;
            _eliminatedPublisher = eliminatedPublisher;
            _npcLocationPublisher = npcLocationPublisher;
            _innocentAI = innocentAI;
            _killerPlanner = killerPlanner;
            _clueGeneration = clueGeneration;

            // กัน DI cycle (UtilityContext ไม่ resolve ระบบนี้ตอน build): ผูกตัวเองเข้า
            // context ที่นี่ — จุดเดียวของเกม AI อ่าน ctx.NpcDirector ตอน Tick เท่านั้น
            // (หลัง constructor จบ) จึงไม่มีจังหวะอ่านค่า null
            aiContext.Bind(this);
        }

        /// <summary>
        /// ย้าย NPC ไป location ใหม่ + Publish NpcLocationChangedMessage (Lab B)
        /// ให้ Visual layer (เช่น ChibiSpawnerView) subscribe แทนการ polling
        /// เป็นจุดเดียวที่อนุญาตให้ mutate CurrentLocationId — ระบบอื่นต้องเรียก method นี้
        ///
        /// ⚠️ Position Seeding Rule (Lab C Phase 2): set PositionX/Y = WorldX/Y ของ
        /// location ปลายทางทุกครั้ง — ไม่งั้น NPC เดิมจะ warp ไป (0,0) ขณะข้ามโซน
        /// (caller ที่ต้องการระบุตำแหน่งเองให้ override หลังเรียก method นี้)
        /// TargetX/Y ถูก seed ตรงกับ Position ด้วย (ถือว่า "ถึงเป้าแล้ว" ทันที) —
        /// กันเป้าเก่าจากโซนเดิมลาก NPC เดินข้ามแผนที่หลังย้ายโซน (warp แอบแฝง)
        /// </summary>
        public void MoveNpc(string npcId, string newLocationId)
        {
            if (!_npcs.TryGetValue(npcId, out var npc)) return;

            var oldLocationId = npc.CurrentLocationId;
            if (oldLocationId == newLocationId) return;

            npc.CurrentLocationId = newLocationId;

            // Position Seeding Rule — วาง NPC ที่จุดกึ่งกลางของโซนปลายทางทันที
            // + ให้ Target ตรงกับ Position (มาถึงแล้ว) จนกว่า wander/AI จะตั้งเป้าใหม่
            if (_locations.TryGetValue(newLocationId, out var destinationDef))
            {
                npc.PositionX = destinationDef.WorldX;
                npc.PositionY = destinationDef.WorldY;
                npc.TargetX = destinationDef.WorldX;
                npc.TargetY = destinationDef.WorldY;
            }

            _npcLocationPublisher.Publish(new NpcLocationChangedMessage
            {
                NpcId = npcId,
                OldLocationId = oldLocationId,
                NewLocationId = newLocationId
            });
        }

        /// <summary>
        /// Sets up a round: picks killerCount out of npcIds to be Killer, rest Innocent.
        /// Ratio guidance (confirmed): ~1 killer per 4-8 innocents, Among Us style
        /// (e.g. 5 NPC -> 1 killer, 10 NPC -> 2 killers).
        ///
        /// Lab C Phase 2: แจกอาวุธให้ Killer — ใช้การ์ด Category==Weapon ตัวแรกที่
        /// Luban โหลดจริง (CardDef.csv) ไม่ใช่ id hardcode — ถ้าไม่มีเลย log warning
        /// แล้วข้าม (Killer ยัง setup ได้ แค่ไม่มีอาวุธ — Step 4 KillerPlanner จะเดินหาอาวุธแทน)
        /// </summary>
        public void SetupRound(IEnumerable<string> npcIds, int killerCount)
        {
            _npcs.Clear();
            var ids = npcIds.ToList();
            var killerIds = ids.OrderBy(_ => _rng.Next()).Take(killerCount).ToHashSet();

            // ตรวจการ์ด Weapon จากข้อมูลจริง (Luban) — ห้าม hardcode id ที่ไม่มีอยู่จริง
            var weaponCardId = _data.CardDefs
                .Where(kv => kv.Value.Category == Marooned.Shared.CardCategory.Weapon)
                .Select(kv => kv.Key)
                .FirstOrDefault();
            if (weaponCardId == null)
                UnityEngine.Debug.LogWarning("[NpcDirectorSystem] ไม่มีการ์ด Category==Weapon ใน CardDef.csv (Luban) — Killer เริ่มรอบโดยไม่มีอาวุธ");

            foreach (var id in ids)
            {
                var npc = new NpcState
                {
                    Id = id,
                    Role = killerIds.Contains(id) ? NpcRole.Killer : NpcRole.Innocent,
                    IsAlive = true,
                    Activity = NpcActivityState.Idle,
                    // Position Seeding Rule: ตำแหน่งจริงถูก seed โดย MoveNpc() ซึ่ง
                    // RoundInitializer.AssignStartingLocations() เรียกตามหลังเสมอ
                };
                if (killerIds.Contains(id) && weaponCardId != null)
                    npc.Inventory.AddItem(weaponCardId);
                _npcs[id] = npc;
            }
        }

        public void Tick(float deltaSeconds)
        {
            foreach (var npc in _npcs.Values.Where(n => n.IsAlive))
            {
                TickBehavior(npc, deltaSeconds);
                // Step 4: TryAttemptElimination call site ถูกลบ — killer ลงมือผ่าน
                // KillerPlanner เท่านั้น (กัน elimination รันซ้อน 2 เส้นทาง)
            }
        }

        /// <summary>
        /// Step 4 hook: สมองแยกตาม role — ตั้ง target/ตัดสินใจ แล้ว NpcMovementSystem
        /// (ถูก tick ทีหลังในเฟรมเดียวกัน) จะเดินตาม target นั้นทันที
        /// </summary>
        private void TickBehavior(NpcState npc, float deltaSeconds)
        {
            if (npc.Role == NpcRole.Killer)
                _killerPlanner.Tick(npc, deltaSeconds);
            else
                _innocentAI.Tick(npc, deltaSeconds);
        }

        /// <summary>
        /// Phase 4 (Player-as-Killer): เช็คเงื่อนไขการ eliminate โดยไม่ mutate state ใดๆ (dry-run)
        /// — UseCardHandler เรียกก่อนหักการ์ด เพื่อกันการ์ดหายฟรีเมื่อลงมือไม่สำเร็จ (Safe UX)
        /// </summary>
        public (bool success, string reason) CanEliminate(string killerEntityId, string victimNpcId, string killerLocationId)
        {
            if (!_npcs.TryGetValue(victimNpcId, out var victim))
                return (false, "unknown_target");
            if (!victim.IsAlive)
                return (false, "target_already_dead");
            if (victim.CurrentLocationId != killerLocationId)
                return (false, "target_not_same_location");

            // กฎ "no witness" — นับ NPC อื่นที่มีชีวิตใน location เดียวกัน (ยกเว้น killer + victim)
            var witnessCount = _npcs.Values.Count(n =>
                n.IsAlive && n.Id != victimNpcId && n.Id != killerEntityId
                && n.CurrentLocationId == killerLocationId);
            if (witnessCount > 0)
                return (false, "witnessed");

            return (true, null);
        }

        /// <summary>
        /// Method กลางสำหรับ eliminate (ทั้ง AI killer และ player ผ่าน weapon card):
        /// ตรวจเงื่อนไขผ่าน CanEliminate ก่อน แล้วค่อย mutate state + spawn clues + publish
        /// NpcEliminatedMessage (event เดียวกันสำหรับทุก killer — visual layer ไม่ต้องรู้ต้นตอ)
        /// </summary>
        public (bool success, string reason) TryEliminate(string killerEntityId, string victimNpcId, string killerLocationId)
        {
            var (can, reason) = CanEliminate(killerEntityId, victimNpcId, killerLocationId);
            if (!can) return (false, reason);

            var victim = _npcs[victimNpcId]; // CanEliminate รับประกัน key มีอยู่แล้ว
            victim.IsAlive = false;

            var clueIds = SpawnClues(killerEntityId, victim);
            _eliminatedPublisher.Publish(new NpcEliminatedMessage
            {
                VictimNpcId = victim.Id,
                LocationId = victim.CurrentLocationId,
                SpawnedClueCardIds = clueIds
            });
            return (true, null);
        }

        /// <summary>
        /// Clue System v2 (b): spawn clues ผ่าน generation pipeline — kill หนึ่งครั้ง
        /// roll อิสระต่อ trigger (KillSabotage: blood 100% + scratch 30%) จึงได้ 0/1/2
        /// instances รายครั้ง ทุก instance ถูกเก็บใน GameStateProvider.AllClueInstances
        /// + publish ClueGeneratedMessage โดย ClueGenerationSystem เอง
        ///
        /// Backward compat (DeductionSystem เดิม): instance ids ถูกผนวกเข้า
        /// victim.AllConditionCardIds ต่อไป — FilterVisible จะไม่กรอง id ที่ไม่ใช่
        /// illness ออก จึงโผล่ใน observables เหมือน clue-card เดิม
        /// </summary>
        private List<string> SpawnClues(string killerEntityId, NpcState victim)
        {
            if (_clueGeneration == null)
            {
                UnityEngine.Debug.LogWarning("[NpcDirectorSystem] ClueGenerationSystem ไม่ถูก inject — ข้ามการ spawn clue (test shim?)");
                return new List<string>();
            }

            var instances = _clueGeneration.TryGenerate(
                ClueTriggerSource.KillSabotage,
                victim.CurrentLocationId,
                killerEntityId);

            // เพิ่ม clue instance ids เข้า victim.AllConditionCardIds (สำหรับ DeductionSystem เดิม)
            var instanceIds = instances.Select(c => c.InstanceId).ToList();
            victim.AllConditionCardIds.AddRange(instanceIds);

            return instanceIds;
        }
    }
}
