using System;
using System.Collections.Generic;
using System.Linq;

namespace Marooned.Systems
{
    /// <summary>
    /// Lab C Phase 1 — mapper จาก Luban generated defs (cfg.game.*, readonly) →
    /// Shared defs (Marooned.Shared.*, ที่ gameplay systems ทุกตัวใช้อยู่)
    ///
    /// ทำไมต้องมีขั้นตอนนี้: generated code เก็บ enum เป็น string และ columns
    /// แบบ list/dictionary ใน CSV ถูกแตกเป็น column ซ้ำ (LootCard1/2/3,
    /// InputCard1/2) — mapping ตรง ๆ ไม่ได้ จึงแปลเป็น shape เดิมของ Shared def
    /// ที่ systems คุ้นเคย (enum แท้, Dictionary แท้) ณ จุดโหลดข้อมูลจุดเดียว
    ///
    /// ค่า default ตอน parse ไม่ได้/ว่าง ยึด default ของ Shared def เสมอ
    /// (TargetType = Self, EffectType = StatDelta, Category = Resource) เพื่อ
    /// backward compatibility กับข้อมูล mock เดิม
    /// </summary>
    internal static class LubanDefMappers
    {
        public static Dictionary<string, Marooned.Shared.CardDef> MapCards(
            IReadOnlyDictionary<string, cfg.game.CardDef> source)
        {
            var result = new Dictionary<string, Marooned.Shared.CardDef>(source.Count);
            foreach (var kv in source)
            {
                var d = kv.Value;
                result[kv.Key] = new Marooned.Shared.CardDef
                {
                    Id = d.Id,
                    Category = ParseEnum(d.Category, Marooned.Shared.CardCategory.Resource),
                    DisplayName = d.DisplayName,
                    SpritePath = d.SpritePath,
                    StackLimit = d.StackLimit,
                    TargetType = ParseEnum(d.TargetType, Marooned.Shared.CardTargetType.Self),
                    EffectType = ParseEnum(d.EffectType, Marooned.Shared.CardEffectType.StatDelta),
                    StatEffect = new Dictionary<string, float>
                    {
                        ["Hunger"] = d.HungerDelta,
                        ["Thirst"] = d.ThirstDelta,
                        ["Mood"] = d.MoodDelta,
                        ["Fatigue"] = d.FatigueDelta,
                    },
                };
            }
            return result;
        }

        public static Dictionary<string, Marooned.Shared.LocationDef> MapLocations(
            IReadOnlyDictionary<string, cfg.game.LocationDef> source)
        {
            var result = new Dictionary<string, Marooned.Shared.LocationDef>(source.Count);
            foreach (var kv in source)
            {
                var d = kv.Value;

                // LootTable: lootCard1..3 + lootWeight1..3 → cardId -> weight (ตัดช่องว่าง/น้ำหนัก 0)
                var loot = new Dictionary<string, int>();
                AddLootEntry(loot, d.LootCard1, d.LootWeight1);
                AddLootEntry(loot, d.LootCard2, d.LootWeight2);
                AddLootEntry(loot, d.LootCard3, d.LootWeight3);

                result[kv.Key] = new Marooned.Shared.LocationDef
                {
                    Id = d.Id,
                    DisplayName = d.DisplayName,
                    WorldX = d.WorldX,
                    WorldY = d.WorldY,
                    ConnectedLocationIds = d.ConnectedLocationIds != null
                        ? new List<string>(d.ConnectedLocationIds)
                        : new List<string>(),
                    LootTable = loot,
                    Capacity = d.Capacity,
                };
            }
            return result;
        }

        public static Dictionary<string, Marooned.Shared.RecipeDef> MapRecipes(
            IReadOnlyDictionary<string, cfg.game.RecipeDef> source)
        {
            var result = new Dictionary<string, Marooned.Shared.RecipeDef>(source.Count);
            foreach (var kv in source)
            {
                var d = kv.Value;

                // Inputs: InputCard1/2 + InputCount1/2 → cardId -> count (ตัดช่องว่าง)
                var inputs = new Dictionary<string, int>();
                AddLootEntry(inputs, d.InputCard1, d.InputCount1);
                AddLootEntry(inputs, d.InputCard2, d.InputCount2);

                result[kv.Key] = new Marooned.Shared.RecipeDef
                {
                    Id = d.Id,
                    OutputCardId = d.OutputCardId,
                    OutputCount = d.OutputCount,
                    Inputs = inputs,
                    RequiredLocationIds = string.IsNullOrEmpty(d.RequiredLocationId)
                        ? new List<string>()
                        : new List<string> { d.RequiredLocationId },
                    RequiredToolCardId = d.RequiredToolCardId,
                };
            }
            return result;
        }

        public static Dictionary<string, Marooned.Shared.ClueDef> MapClues(
            IReadOnlyDictionary<string, cfg.game.ClueDef> source)
        {
            var result = new Dictionary<string, Marooned.Shared.ClueDef>(source.Count);
            foreach (var kv in source)
            {
                var d = kv.Value;
                result[kv.Key] = new Marooned.Shared.ClueDef
                {
                    Id = d.Id,
                    DisplayName = d.DisplayName,
                    SpritePath = d.SpritePath,
                    Reliability = ParseEnum(d.Reliability, Marooned.Shared.ClueReliability.Strong),
                    VisibleToBystanders = d.VisibleToBystanders,
                    // Clue System v2 (a): copy field ใหม่จาก generated def — ไม่งั้น
                    // หายเงียบ ๆ ตอน map (Shared def จะคง default "" ตลอด)
                    ClueCategory = d.ClueCategory,
                };
            }
            return result;
        }

        /// <summary>
        /// Clue System v2 (a) — ตาราง trigger การกระทำ → clue (ActionClueTriggerDef.csv)
        /// pattern เดียวกับ MapCards: cfg.game.* (triggerSource เป็น string) →
        /// Marooned.Shared.ActionClueTriggerDef (enum แท้ ClueTriggerSource)
        /// </summary>
        public static Dictionary<string, Marooned.Shared.ActionClueTriggerDef> MapActionClueTriggers(
            IReadOnlyDictionary<string, cfg.game.ActionClueTriggerDef> source)
        {
            var result = new Dictionary<string, Marooned.Shared.ActionClueTriggerDef>(source.Count);
            foreach (var kv in source)
            {
                var d = kv.Value;
                result[kv.Key] = new Marooned.Shared.ActionClueTriggerDef
                {
                    Id = d.Id,
                    TriggerSource = ParseEnum(d.TriggerSource, Marooned.Shared.ClueTriggerSource.IncidentalAction),
                    ClueDefId = d.ClueDefId,
                    Weight = d.Weight,
                };
            }
            return result;
        }

        public static Dictionary<string, Marooned.Shared.IllnessDef> MapIllnesses(
            IReadOnlyDictionary<string, cfg.game.IllnessDef> source)
        {
            var result = new Dictionary<string, Marooned.Shared.IllnessDef>(source.Count);
            foreach (var kv in source)
            {
                var d = kv.Value;
                result[kv.Key] = new Marooned.Shared.IllnessDef
                {
                    Id = d.Id,
                    DisplayName = d.DisplayName,
                    Visible = d.Visible,
                    CureCardId = d.CureCardId,
                    SeverityGrowthPerHour = d.SeverityGrowthPerHour,
                };
            }
            return result;
        }

        public static Dictionary<string, Marooned.Shared.WorldEventDef> MapWorldEvents(
            IReadOnlyDictionary<string, cfg.game.WorldEventDef> source)
        {
            var result = new Dictionary<string, Marooned.Shared.WorldEventDef>(source.Count);
            foreach (var kv in source)
            {
                var d = kv.Value;
                result[kv.Key] = new Marooned.Shared.WorldEventDef
                {
                    Id = d.Id,
                    Group = d.Group,
                    Weight = d.Weight,
                    DisplayText = d.DisplayText,
                    RequiredLocationTags = string.IsNullOrEmpty(d.RequiredLocationTag)
                        ? new List<string>()
                        : new List<string> { d.RequiredLocationTag },
                };
            }
            return result;
        }

        // ---- helpers ----

        private static void AddLootEntry(Dictionary<string, int> target, string cardId, int weight)
        {
            if (string.IsNullOrEmpty(cardId) || weight <= 0) return;
            target[cardId] = weight;
        }

        private static T ParseEnum<T>(string raw, T fallback) where T : struct, Enum
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            return Enum.TryParse<T>(raw, ignoreCase: true, out var parsed) ? parsed : fallback;
        }
    }
}
