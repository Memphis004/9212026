using System;
using System.Collections.Generic;
using System.Linq;
using Marooned.Shared;
using Marooned.Systems.AI;
using MessagePipe;
using UnityEngine;

namespace Marooned.Systems
{
    /// <summary>
    /// Clue System v2 — Part (b): Generation Pipeline
    /// จุดเดียวของเกมที่สร้าง ClueInstance: filter trigger ตาม ClueTriggerSource →
    /// roll อิสระต่อ trigger ด้วย weight → snapshot witnesses (exclude ผู้ก่อเหตุ) →
    /// เก็บ registry กลาง (GameStateProvider.AllClueInstances) → publish ClueGeneratedMessage
    ///
    /// DI (กัน cycle): ต้องอ่านรายชื่อ NPC จาก NpcDirectorSystem แต่ NpcDirectorSystem
    /// เองก็ถือ ClueGenerationSystem ผ่าน constructor — จึง inject UtilityContext
    /// (pattern เดียวกับสมอง AI) แล้วอ่าน ctx.NpcDirector ตอน TryGenerate เท่านั้น
    /// (NpcDirectorSystem constructor Bind(this) ก่อนใครจะเรียก TryGenerate ได้เสมอ)
    ///
    /// Information Hiding (commit a): SourceActorId + WitnessNpcIds เป็น ground truth
    /// เก็บใน instance เท่านั้น — message ที่ publish ไม่มีทั้งสอง field
    ///
    /// Multi-clue: การกระทำ 1 ครั้ง roll อิสระ "ต่อ trigger" (ไม่ใช่ single-pick) —
    /// weight=100 เกิดเสมอ, weight=30 = โอกาส 30% → kill หนึ่งครั้งได้ 0/1/2 clues
    /// (weight ทั้งคู่ไม่ติด = 0 ได้จริง)
    /// </summary>
    public class ClueGenerationSystem
    {
        private readonly LubanDataService _data;
        private readonly GameStateProvider _stateProvider;
        private readonly UtilityContext _aiContext;
        private readonly IPublisher<ClueGeneratedMessage> _publisher;
        private readonly System.Random _rng = new();

        public ClueGenerationSystem(LubanDataService dataService, GameStateProvider stateProvider,
            UtilityContext aiContext, IPublisher<ClueGeneratedMessage> publisher)
        {
            _data = dataService;
            _stateProvider = stateProvider;
            _aiContext = aiContext;
            _publisher = publisher;
        }

        /// <summary>
        /// สร้าง clue ตาม trigger ของ source ที่กำหนด — คืน instance ที่เกิดจริง
        /// (อาจว่างเปล่าเมื่อ roll ไม่ติดสักตัว) ทุก instance ที่เกิดถูกเก็บใน
        /// GameStateProvider.AllClueInstances และ publish ClueGeneratedMessage แล้ว
        /// </summary>
        /// <param name="source">ชนิดการกระทำที่ทำให้เกิด clue</param>
        /// <param name="locationId">โซนที่เหตุการณ์เกิด</param>
        /// <param name="sourceActorId">ผู้ก่อเหตุ ("player_local" หรือ npc id) — null/ว่าง = ไม่ระบุตัว</param>
        public List<ClueInstance> TryGenerate(ClueTriggerSource source, string locationId, string sourceActorId = null)
        {
            var result = new List<ClueInstance>();

            // 1. Filter triggers ที่ตรงกับ source
            var triggers = _data.ActionClueTriggerDefs.Values
                .Where(t => t.TriggerSource == source)
                .ToList();

            // NpcDirectorSystem ถูก Bind แล้วเสมอ ณ จุดนี้ (constructor ของมันรันก่อน
            // ใครจะเรียก TryGenerate — ดู comment DI ด้านบน)
            var npcDirector = _aiContext.NpcDirector;

            // 2. Roll อิสระต่อ trigger (ไม่ใช่ single-pick)
            //    weight=100 → เกิดเสมอ, weight=30 → โอกาส 30%
            foreach (var trigger in triggers)
            {
                if (_rng.Next(100) >= trigger.Weight) continue;

                // 3. Snapshot witnesses — NPC มีชีวิตในโซนเดียวกัน ตัดผู้ก่อเหตุออก
                var witnesses = npcDirector.Npcs.Values
                    .Where(n => n.IsAlive
                             && n.Id != sourceActorId // ⚠️ ผู้ก่อเหตุไม่เป็นพยานเหตุการณ์ตัวเอง
                             && n.CurrentLocationId == locationId)
                    .Select(n => n.Id)
                    .ToList();

                // เพิ่ม player ถ้าอยู่ location เดียวกัน และไม่ใช่ผู้ก่อเหตุเอง
                // ⚠️ player ไม่เป็นพยานตัวเอง (player-as-killer)
                if (sourceActorId != GameStateProvider.LocalPlayerId
                    && _stateProvider.GetPlayer().CurrentLocationId == locationId)
                {
                    witnesses.Add(GameStateProvider.LocalPlayerId);
                }

                // 4. สร้าง instance
                var instance = new ClueInstance
                {
                    InstanceId = Guid.NewGuid().ToString(),
                    DefId = trigger.ClueDefId,
                    LocationId = locationId,
                    GameTimestamp = Time.time,
                    Source = source,
                    SourceActorId = sourceActorId ?? string.Empty,
                    WitnessNpcIds = witnesses,
                };

                // 5. เก็บใน registry กลาง (multiplayer-ready — ไม่ผูกกับ player)
                _stateProvider.AllClueInstances[instance.InstanceId] = instance;

                // 6. Publish message (ต่อ clue ที่เกิด — ไม่มี ground truth fields)
                _publisher.Publish(new ClueGeneratedMessage
                {
                    InstanceId = instance.InstanceId,
                    DefId = instance.DefId,
                    LocationId = instance.LocationId,
                });

                result.Add(instance);
            }

            return result; // อาจเป็น empty list ถ้า roll ไม่ติดเลย
        }
    }
}
