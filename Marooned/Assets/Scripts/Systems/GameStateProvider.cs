using System;
using System.Collections.Generic;
using System.Linq;
using Marooned.Shared;
using Luban.SimpleJSON;
using UnityEngine;

namespace Marooned.Systems
{
    /// <summary>Single live instance, same idea as the reference project's SectStateProvider (Lab 7) — no re-creating state every query.</summary>
    public class GameStateProvider
    {
        /// <summary>id ของผู้เล่นหลัก (single-player) — ทุก call site เดิมชี้ตัวนี้ผ่าน GetPlayer()</summary>
        public const string LocalPlayerId = "player_local";

        // Phase 4 (Multiplayer-ready): เก็บ player แบบ Dictionary แต่ API หน้าตาเดิม —
        // ตัวละครทั้งหมดในเกมปัจจุบันยังใช้ตัวเดียว (player_local) เหมือนเดิมทุกอย่าง
        private readonly Dictionary<string, PlayerSurvivalState> _players = new()
        {
            // Mock starting state so GetGameState has something to show immediately
            // in the first round-trip test. Move this into real save/new-game logic later.
            [LocalPlayerId] = new PlayerSurvivalState
            {
                CurrentLocationId = "beach",
                Inventory = new Dictionary<string, int> { ["food_coconut"] = 1 },
            },
        };

        /// <summary>API เดิม — เรียก GetPlayer() ไม่ใส่ param ได้ผลลัพธ์เดิม (ตัวละครผู้เล่นหลัก)</summary>
        public PlayerSurvivalState GetPlayer(string playerId = LocalPlayerId) => _players[playerId];

        // Clue System v2 (a): central registry — clue instance ทุกอันที่เกิดในรอบนี้
        // (เก็บกลางแบบ multiplayer-ready ไม่ผูกกับ player คนใด; ผู้เล่นแต่ละคนถือ
        // เฉพาะ instance ids ที่ตัวเองเก็บผ่าน PlayerSurvivalState.CollectedClueInstanceIds)
        public Dictionary<string, ClueInstance> AllClueInstances { get; } = new();

        /// <summary>Phase 4 (multiplayer-ready): ได้ player ตาม id โดยสร้างใหม่ให้ถ้ายังไม่มี</summary>
        public PlayerSurvivalState GetOrCreatePlayer(string playerId)
        {
            if (!_players.TryGetValue(playerId, out var state))
            {
                state = new PlayerSurvivalState();
                _players[playerId] = state;
            }
            return state;
        }

        /// <summary>Phase 4 (multiplayer-ready): player ทุกตัวในระบบ (อ่านอย่างเดียว)</summary>
        public IReadOnlyDictionary<string, PlayerSurvivalState> AllPlayers => _players;
    }

    /// <summary>
    /// Loads Luban-generated JSON (DataTables CSV → Luban gen → JSON + typed C#)
    /// into plain dictionaries at boot. Lab C Phase 1: โหลดของจริงจาก
    /// Resources/DataTables/game_tb*.json ผ่าน generated code (cfg.Tables — root
    /// class อยู่ใน namespace cfg, ส่วน bean/table wrapper อยู่ใน cfg.game)
    /// แล้ว map เป็น def dictionaries — ปิด TODO Lab A+1 เดิม (mock พิมพ์มือ) +
    /// เพิ่มตารางใหม่ BiomeDefs / HarvestableNodeDefs สำหรับ BiomeScatterSystem
    /// </summary>
    public class LubanDataService
    {
        public Dictionary<string, CardDef> CardDefs { get; private set; } = new();
        public Dictionary<string, LocationDef> LocationDefs { get; private set; } = new();
        public Dictionary<string, RecipeDef> RecipeDefs { get; private set; } = new();
        public Dictionary<string, ClueDef> ClueDefs { get; private set; } = new();
        public Dictionary<string, IllnessDef> IllnessDefs { get; private set; } = new();
        public Dictionary<string, WorldEventDef> WorldEventDefs { get; private set; } = new();

        // Clue System v2 (a): ตาราง trigger การกระทำ → clue (ActionClueTriggerDef.csv)
        public Dictionary<string, ActionClueTriggerDef> ActionClueTriggerDefs { get; private set; } = new();

        // ---- Lab C Phase 1 (Hybrid BiomeScatter) — typed defs จาก generated code ----
        public Dictionary<string, cfg.game.BiomeDef> BiomeDefs { get; private set; } = new();
        public Dictionary<string, cfg.game.HarvestableNodeDef> HarvestableNodeDefs { get; private set; } = new();

        // ---- Hybrid Transition Points (Part 1 — foundation) — consume generated
        //      cfg.game.ZoneConnectionDef ตรงๆ (ไม่มี Shared mirror) index ด้วย
        //      (from, to) pair เพื่อ lookup จุดเชื่อมแบบ O(1) ตอน Part 2 ----
        private Dictionary<(string From, string To), cfg.game.ZoneConnectionDef> _zoneConnectionsByPair = new();

        /// <summary>
        /// หา connection จากโซน from → to (null ถ้าไม่มีจุดเชื่อมตรง)
        /// Part 2 จะใช้ใน NpcZoneTransitionSystem (เดินเข้าจุดเชื่อม → ข้ามโซน)
        /// </summary>
        public cfg.game.ZoneConnectionDef GetTransition(string from, string to) =>
            _zoneConnectionsByPair.TryGetValue((from, to), out var def) ? def : null;

        // ChibiPartDefs / ChibiOutfitDefs ถูกถอดออก: ตารางไม่มีอยู่ใน Luban pipeline
        // จริง (event.xml ไม่นิยาม bean — ดูหมายเหตุในไฟล์นั้น) และ mock เดิมก็ว่างเปล่า

        public LubanDataService()
        {
            // Called from the constructor (not a separate lifecycle hook) so data
            // is guaranteed loaded the moment VContainer resolves this singleton —
            // no dependency on IInitializable/EntryPoint ordering.
            LoadAll();
        }

        public void LoadAll()
        {
            var tables = LoadLubanTables();

            // generated defs (cfg.game.*, readonly, enum เป็น string) → Shared defs
            // (enum แท้ + Dictionary แท้) ผ่าน mapper จุดเดียว — systems ทุกตัวคง
            // consume Marooned.Shared.* เหมือนเดิมทุกอย่าง
            CardDefs = LubanDefMappers.MapCards(tables.TbCardDef.DataMap);
            LocationDefs = LubanDefMappers.MapLocations(tables.TbLocationDef.DataMap);
            RecipeDefs = LubanDefMappers.MapRecipes(tables.TbRecipeDef.DataMap);
            ClueDefs = LubanDefMappers.MapClues(tables.TbClueDef.DataMap);
            IllnessDefs = LubanDefMappers.MapIllnesses(tables.TbIllnessDef.DataMap);
            WorldEventDefs = LubanDefMappers.MapWorldEvents(tables.TbWorldEventDef.DataMap);
            ActionClueTriggerDefs = LubanDefMappers.MapActionClueTriggers(tables.TbActionClueTriggerDef.DataMap);

            BiomeDefs = tables.TbBiomeDef.DataMap.ToDictionarySafe();
            HarvestableNodeDefs = tables.TbHarvestableNodeDef.DataMap.ToDictionarySafe();

            // Hybrid Transition Points (Part 1): DataList (ไม่ใช่ DataMap เพราะ key
            // ของเราคือ (from,to) ไม่ใช่ id) — duplicate pair จะ throw ตอน load
            // ซึ่งถือเป็น config error ที่ควรให้ crash ตั้งแต่ boot
            _zoneConnectionsByPair = tables.TbZoneConnectionDef.DataList
                .ToDictionary(c => (c.FromLocationId, c.ToLocationId));

            Debug.Log($"[LubanDataService] loaded {CardDefs.Count} cards, {LocationDefs.Count} locations, " +
                      $"{RecipeDefs.Count} recipes, {ClueDefs.Count} clues, {IllnessDefs.Count} illnesses, " +
                      $"{WorldEventDefs.Count} events, {BiomeDefs.Count} biomes, {HarvestableNodeDefs.Count} harvestable nodes, " +
                      $"{_zoneConnectionsByPair.Count} zone connections, {ActionClueTriggerDefs.Count} action clue triggers");
        }

        /// <summary>
        /// สร้าง cfg.Tables (generated) — อ่าน JSON แต่ละตารางจาก
        /// Resources/DataTables/ ตามชื่อไฟล์ที่ Tables.cs ร้องขอ (game_tbcarddef ฯลฯ)
        /// Luban runtime: SimpleJSON.JSON.Parse จาก package com.code-philosophy.luban
        /// </summary>
        private static readonly Dictionary<string, JSONNode> _jsonCache = new();

        private static cfg.Tables LoadLubanTables()
        {
            return new cfg.Tables(name =>
            {
                // เช็ก Cache ก่อน — ถ้ามีแล้วคืนเลย ไม่ต้องโหลดใหม่
                if (_jsonCache.TryGetValue(name, out var cached))
                    return cached;

                var textAsset = Resources.Load<TextAsset>($"DataTables/{name}");
                if (textAsset == null)
                    throw new InvalidOperationException(
                        $"[LubanDataService] หา DataTables/{name}.json ไม่เจอใน Resources — รัน DataTables/gen.bat แล้วหรือยัง?");

                var json = JSON.Parse(textAsset.text);

                // ✅ เก็บเข้า Cache แทนการทำลายทิ้ง
                _jsonCache[name] = json;

                // ❌ ลบบรรทัดนี้ออก: UnityEngine.Object.Destroy(textAsset);

                return json;
            });
        }
    }

    /// <summary>helper: IReadOnlyDictionary → mutable Dictionary (copy กัน caller แก้ตารางต้น)</summary>
    internal static class LubanDataServiceExtensions
    {
        public static Dictionary<string, TValue> ToDictionarySafe<TValue>(
            this IReadOnlyDictionary<string, TValue> source)
        {
            var result = new Dictionary<string, TValue>(source.Count);
            foreach (var kv in source) result[kv.Key] = kv.Value;
            return result;
        }
    }
}
