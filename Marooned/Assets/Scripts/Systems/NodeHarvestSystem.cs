using Marooned.Shared;
using MessagePipe;
using UnityEngine;
using Marooned.Core;

namespace Marooned.Systems
{
    /// <summary>
    /// ผลการเก็บเกี่ยว 1 ครั้ง — ใช้ทั้งโดย GameTickDriver (log) และ MCP
    /// HarvestNodeHandler (map เป็น HarvestNodeResponse ส่งกลับ bridge)
    /// </summary>
    public class HarvestResult
    {
        public bool Success;
        /// <summary>null เมื่อสำเร็จ — "no_node_in_range", "unknown_node",
        /// "missing_tool" (node ต้องใช้ tool แต่ไม่มีใน inventory), "wrong_tool",
        /// "regrowing" (มาจาก HarvestableNodeComponent)</summary>
        public string FailureReason;
        /// <summary>node ที่พยายามเก็บ (null เมื่อ no_node_in_range)</summary>
        public string NodeId;
        /// <summary>tool ที่ใช้จริง (null = มือเปล่า — เติมโดย auto-pick จาก inventory)</summary>
        public string UsedToolId;
        public string ItemId;
        public int Count;
        public bool Depleted;
        public int RegrowSeconds;
    }

    /// <summary>
    /// Lab C Phase 1 (Tool-Gathering Ready) — เก็บเกี่ยว harvestable node ด้วยปุ่ม
    /// interact (E/Space ผ่าน PlayerInputService เดิม): หา node ใกล้สุดในรัศมี →
    /// เลือก tool → ลด durability → ได้ไอเท็มเข้า inventory → publish
    /// NodeHarvestedMessage (discrete event ครั้งเดียวต่อการเก็บ 1 ครั้ง)
    ///
    /// Phase 1.5 (MCP harvest_node): tool เลือกจาก inventory อัตโนมัติ
    /// (auto-pick) ทั้งผู้เล่นกด E และ MCP tool — node ที่ต้องใช้ tool_axe จะ
    /// ใช้ tool_axe ที่มีในมือให้เอง ถ้าไม่มีการ์ด tool นั้นเลยจึง fail ด้วย
    /// "missing_tool" (เดิม E หมายถึงมือเปล่าเสมอ ทำให้เก็บ tree_wood ไม่ได้เลย)
    ///
    /// plain C# singleton (VContainer) — tick โดย GameTickDriver เหมือน
    /// ItemPickupSystem ระบบนี้จึงเป็น "จุดรวม" ของ harvest: component
    /// (HarvestableNodeComponent) เก็บ state ต่อต้น, system จัดการข้ามระบบ
    /// (inventory + message) ตาม convention "System ไม่แตะ GameObject"
    /// (อ่าน state ผ่าน property ของ component ล้วน ๆ)
    /// </summary>
    public class NodeHarvestSystem
    {
        private readonly PlayerInputService _input;
        private readonly GameStateProvider _stateProvider;
        private readonly CardInventorySystem _inventory;
        private readonly LubanDataService _data;
        private readonly IPublisher<NodeHarvestedMessage> _harvestPublisher;
        private readonly ClueGenerationSystem _clueGeneration;

        /// <summary>โหมดเก็บเหมือน ItemPickupSystem: InteractKey (E/Space)</summary>
        public PickupMode Mode = PickupMode.InteractKey;

        /// <summary>รัศมีเก็บเกี่ยว (world unit)</summary>
        public float HarvestRadius = 1.6f;

        public NodeHarvestSystem(PlayerInputService input, GameStateProvider stateProvider,
            CardInventorySystem inventory, LubanDataService data,
            IPublisher<NodeHarvestedMessage> harvestPublisher, ClueGenerationSystem clueGeneration)
        {
            _input = input;
            _stateProvider = stateProvider;
            _inventory = inventory;
            _data = data;
            _harvestPublisher = harvestPublisher;
            _clueGeneration = clueGeneration;
        }

        /// <summary>เรียกทุกเฟรมจาก GameTickDriver — อ่าน interact key แล้วพยายาม harvest</summary>
        public void Tick(float deltaSeconds)
        {
            if (Mode == PickupMode.InteractKey)
            {
                // edge-triggered เหมือน ItemPickupSystem: กด 1 ครั้ง = พยายาม 1 ครั้ง
                if (!_input.ConsumeInteractPressed()) return;
                var result = TryHarvestNearest();
                Debug.Log(result.Success
                    ? $"[NodeHarvestSystem] harvest (InteractKey) '{result.NodeId}' → +{result.Count} {result.ItemId} @ {_stateProvider.GetPlayer().CurrentLocationId}"
                    : $"[NodeHarvestSystem] harvest (InteractKey) ไม่สำเร็จ: {result.FailureReason}");
            }
            else // WalkOver — เก็บเกี่ยวไม่เหมาะกับการเดินทับ แต่เผื่ออนาคต (ป่าละลายทุกทิศ)
            {
                TryHarvestNearest();
            }
        }

        /// <summary>
        /// หา node ใกล้ผู้เล่นที่สุดในรัศมี HarvestRadius แล้วเก็บด้วย tool ที่มีใน
        /// inventory (auto-pick) — ใช้ทั้งโดยกด E และ MCP harvest_node
        /// </summary>
        public HarvestResult TryHarvestNearest()
        {
            var player = _stateProvider.GetPlayer();
            var position = new Vector2(player.PositionX, player.PositionY);
            return TryHarvestNearest(position, HarvestRadius, toolItemId: null);
        }

        /// <summary>
        /// พยายามเก็บเกี่ยว node ใกล้สุดภายในรัศมีจากตำแหน่งที่กำหนด —
        /// toolItemId null/ว่าง = auto-pick tool ที่เหมาะสมจาก inventory
        /// (หรือมือเปล่าถ้า node ไม่ต้องใช้ tool); ระบุ id เฉพาะ = ต้องตรงที่ node
        /// ต้องการ ไม่งั้น fail ด้วย "wrong_tool"
        /// </summary>
        public HarvestResult TryHarvestNearest(Vector2 position, float radius, string toolItemId)
        {
            var node = FindNearestNode(position, radius);
            if (node == null)
                return new HarvestResult { Success = false, FailureReason = "no_node_in_range" };

            return TryHarvest(node, toolItemId);
        }

        /// <summary>พยายามเก็บเกี่ยว node ที่ระบุ (เลือก tool → yield → publish)</summary>
        public HarvestResult TryHarvest(HarvestableNodeComponent node, string toolItemId)
        {
            if (node == null)
                return new HarvestResult { Success = false, FailureReason = "no_node_in_range" };

            var result = new HarvestResult { NodeId = node.NodeId };

            // def ต้องมีเสมอ (BiomeScatterView ตรวจก่อนติด component) — ยังกัน crash
            if (!_data.HarvestableNodeDefs.TryGetValue(node.NodeId, out var def))
            {
                Debug.LogWarning($"[NodeHarvestSystem] node '{node.NodeId}' ไม่มีใน HarvestableNodeDefs — ไม่ได้ไอเท็ม");
                result.FailureReason = "unknown_node";
                return result;
            }

            // ---- เลือก tool ----
            // ระบุ id เฉพาะมา → ผิดกับที่ node ต้องการ = wrong_tool (node ที่ไม่
            // ต้องใช้ tool ส่ง tool อะไรมาก็ได้ — component ไม่เช็คอยู่แล้ว)
            if (!string.IsNullOrEmpty(toolItemId))
            {
                if (!string.IsNullOrEmpty(def.RequiredToolCardId) && toolItemId != def.RequiredToolCardId)
                {
                    result.FailureReason = "wrong_tool";
                    return result;
                }
                result.UsedToolId = toolItemId;
            }
            else
            {
                // auto-pick: node ต้องใช้ tool → ต้องมีการ์ด tool นั้นใน inventory
                // (ไม่มี = "missing_tool" — ต่างจาก "wrong_tool" ที่แปลว่าใช้ผิดตัว)
                if (!string.IsNullOrEmpty(def.RequiredToolCardId))
                {
                    if (!_inventory.HasAtLeast(def.RequiredToolCardId, 1))
                    {
                        result.FailureReason = "missing_tool";
                        return result;
                    }
                    result.UsedToolId = def.RequiredToolCardId;
                }
                // node ไม่ต้องใช้ tool → มือเปล่า (UsedToolId คง null)
            }

            // เช็ค tool + ลด durability ผ่าน component (gate สุดท้าย — regrowing ฯลฯ)
            if (!node.TryHarvest(result.UsedToolId, out var failureReason))
            {
                Debug.Log($"[NodeHarvestSystem] harvest '{node.NodeId}' ไม่สำเร็จ: {failureReason}");
                result.FailureReason = failureReason;
                return result;
            }

            // yield เข้า inventory (TryAdd ตรวจ card id มีใน CardDefs ให้เอง)
            if (!_inventory.TryAdd(def.YieldItemId, def.YieldCount))
            {
                Debug.LogWarning($"[NodeHarvestSystem] yield card '{def.YieldItemId}' ไม่มีใน CardDefs — ได้ของไม่เข้า inventory (durability ยังถูกหัก)");
            }

            result.Success = true;
            result.ItemId = def.YieldItemId;
            result.Count = def.YieldCount;
            result.Depleted = node.RemainingDurability <= 0;
            result.RegrowSeconds = result.Depleted ? def.RegrowTime : 0;

            _harvestPublisher.Publish(new NodeHarvestedMessage
            {
                NodeId = node.NodeId,
                ItemId = def.YieldItemId,
                Count = def.YieldCount,
                Depleted = result.Depleted,
                RegrowSeconds = result.RegrowSeconds,
            });

            Debug.Log($"[NodeHarvestSystem] harvested '{node.NodeId}' → +{def.YieldCount} {def.YieldItemId} (durability {node.RemainingDurability}, depleted={result.Depleted})");

            // Clue System v2 (c): hunting hook — node ที่ mark isHuntingTarget=true ใน
            // HarvestableNodeDef.csv (เช่น node_deer) ถือว่า "ล่าสัตว์" → roll clue
            // กลุ่ม Hunting ตาม ActionClueTriggerDef.csv (trig_hunt_blood / trig_hunt_scratch)
            // sourceActorId = player เสมอ (ผู้เก็บคือผู้เล่น — ตามกติกา witness จึงไม่
            // เป็นพยาน clue ของตัวเอง)
            if (def.IsHuntingTarget)
            {
                _clueGeneration.TryGenerate(ClueTriggerSource.Hunting,
                    _stateProvider.GetPlayer().CurrentLocationId,
                    sourceActorId: GameStateProvider.LocalPlayerId);
            }

            return result;
        }

        /// <summary>หา node ที่ยังเก็บได้ (ไม่ regrow) ใกล้ผู้เล่นที่สุดในรัศมี</summary>
        private HarvestableNodeComponent FindNearestNode(Vector2 position, float radius)
        {
            HarvestableNodeComponent best = null;
            float bestSqr = radius * radius;
            foreach (var node in UnityEngine.Object.FindObjectsByType<HarvestableNodeComponent>(FindObjectsSortMode.None))
            {
                if (node == null || node.IsRegrowing) continue;
                var sqr = ((Vector2)node.transform.position - position).sqrMagnitude;
                if (sqr <= bestSqr) { bestSqr = sqr; best = node; }
            }
            return best;
        }
    }
}
