using System;
using System.Collections.Generic;
using System.Linq;
using Marooned.Shared;
using MessagePipe;

namespace Marooned.Systems
{
    /// <summary>Tracks how depleted each LocationDef's loot table currently is.</summary>
    public class LocationRuntimeState
    {
        public Dictionary<string, int> RemainingWeight = new();
    }

    public class ExplorationSystem
    {
        private readonly Dictionary<string, LocationDef> _locations;
        private readonly Dictionary<string, LocationRuntimeState> _runtime = new();
        private readonly CardInventorySystem _inventory;
        private readonly PlayerSurvivalState _player;
        private readonly IPublisher<PlayerLocationChangedMessage> _playerLocationPublisher;
        private readonly ClueGenerationSystem _clueGeneration;
        private readonly Random _rng = new();

        public ExplorationSystem(LubanDataService dataService, CardInventorySystem inventory,
            GameStateProvider stateProvider, IPublisher<PlayerLocationChangedMessage> playerLocationPublisher,
            ClueGenerationSystem clueGeneration)
        {
            _locations = dataService.LocationDefs;
            _inventory = inventory;
            _player = stateProvider.GetPlayer();
            _playerLocationPublisher = playerLocationPublisher;
            _clueGeneration = clueGeneration;

            foreach (var loc in _locations.Values)
                _runtime[loc.Id] = new LocationRuntimeState { RemainingWeight = new Dictionary<string, int>(loc.LootTable) };
        }

        public (bool success, List<string> foundCardIds) Explore(string locationId)
        {
            if (!_locations.ContainsKey(locationId)) return (false, new List<string>());

            var runtime = _runtime[locationId];
            var pool = runtime.RemainingWeight.Where(kv => kv.Value > 0).ToList();
            if (pool.Count == 0) return (true, new List<string>()); // node depleted, exploring is still a valid (empty-handed) action

            var totalWeight = pool.Sum(kv => kv.Value);
            var roll = _rng.Next(0, totalWeight);
            string picked = pool[0].Key;
            var cumulative = 0;
            foreach (var kv in pool)
            {
                cumulative += kv.Value;
                if (roll < cumulative) { picked = kv.Key; break; }
            }

            runtime.RemainingWeight[picked] -= 1;
            _inventory.TryAdd(picked, 1);

            // Clue System v2 (c): incidental hook — สำรวจแล้วเจอของ "สายน้ำ" (water_bottle,
            // raw_fish ฯลฯ) หรือสุ่ม 10% ทั่วไป → อาจทิ้ง clue ตาม ActionClueTriggerDef.csv
            // (trig_water_wet → clue_wet_clothes, trig_water_mud → clue_footprint_mud)
            // หมายเหตุ: ไม่มี "water tag" ใน LocationDef จึงเดาจาก loot card id ที่ได้จริง
            bool isWaterRelated = picked.Contains("water") || picked == "raw_fish";
            if (isWaterRelated || _rng.Next(100) < 10)
                _clueGeneration.TryGenerate(ClueTriggerSource.IncidentalAction, locationId);

            // Lab B: publish PlayerLocationChangedMessage เมื่อ Explore ย้ายผู้เล่น
            // (Visual layer เช่น ChibiSpawnerView subscribe แทนการ polling)
            var oldLocationId = _player.CurrentLocationId;
            _player.CurrentLocationId = locationId;
            if (oldLocationId != locationId)
                _playerLocationPublisher.Publish(new PlayerLocationChangedMessage { OldLocationId = oldLocationId, NewLocationId = locationId });

            return (true, new List<string> { picked });
        }
    }
}
