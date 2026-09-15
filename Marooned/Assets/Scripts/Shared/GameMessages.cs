using System.Collections.Generic;
using MessagePack;

namespace Marooned.Shared
{
    // ---- Cross-process (Bridge <-> Unity) request/response messages ----
    // Pattern: Request-Response over MessagePipe.Interprocess, same convention as
    // the reference project's AwaitWorldEventRequest/Response (avoids the double
    // TCP-listener bug documented in Lab 6 of the reference project).

    [MessagePackObject]
    public class ExploreLocationRequest
    {
        [Key(0)] public string LocationId = string.Empty;
    }

    [MessagePackObject]
    public class ExploreLocationResponse
    {
        [Key(0)] public bool Success;
        [Key(1)] public List<string> FoundCardIds = new();
        [Key(2)] public string TriggeredEventId = string.Empty; // may be empty
    }

    [MessagePackObject]
    public class CraftCardRequest
    {
        [Key(0)] public string RecipeId = string.Empty;
    }

    [MessagePackObject]
    public class CraftCardResponse
    {
        [Key(0)] public bool Success;
        [Key(1)] public string FailureReason = string.Empty;
        [Key(2)] public string OutputCardId = string.Empty;
    }

    [MessagePackObject]
    public class AwaitNextEventRequest
    {
        [Key(0)] public int TimeoutSeconds = 30;
    }

    [MessagePackObject]
    public class AwaitNextEventResponse
    {
        [Key(0)] public bool TimedOut;
        [Key(1)] public string EventId = string.Empty;
        [Key(2)] public string Group = string.Empty; // "Survival" | "Social"
        [Key(3)] public string DisplayText = string.Empty;
    }

    [MessagePackObject]
    public class AccuseNpcRequest
    {
        [Key(0)] public string TargetNpcId = string.Empty;
    }

    [MessagePackObject]
    public class AccuseNpcResponse
    {
        [Key(0)] public bool WasCorrect;
        [Key(1)] public bool GameOverWin;
        [Key(2)] public bool GameOverLoss;
        [Key(3)] public string ResultText = string.Empty;
    }

    // ---- Added for the full MCP tool table (design doc §6): GetGameState,
    // GetVisibleNpcs, GetClueBoard, MoveToLocation, UseCard, CallMeeting ----

    [MessagePackObject]
    public class GetGameStateRequest
    {
        // no parameters; empty request kept for symmetry with the request/response pattern
    }

    [MessagePackObject]
    public class GetGameStateResponse
    {
        [Key(0)] public PlayerSurvivalState Player = new();
    }

    [MessagePackObject]
    public class GetVisibleNpcsRequest
    {
        // uses the player's current location server-side; no parameters needed
    }

    [MessagePackObject]
    public class GetVisibleNpcsResponse
    {
        [Key(0)] public List<NpcObservableView> Npcs = new();
    }

    [MessagePackObject]
    public class GetClueBoardRequest
    {
    }

    /// <summary>
    /// Clue System v2 (d): 1 แถวบน clue board — สร้างฝั่ง server จาก ClueInstance + ClueDef
    /// ⚠️ Information Hiding: ไม่มี SourceActorId ที่นี่ (ground truth อยู่ใน ClueInstance เท่านั้น)
    /// WitnessNpcIds = กรองแล้ว (exclude ผู้ก่อเหตุ/npc ตาย — กรองตอน build response)
    /// </summary>
    [MessagePackObject]
    public class ClueBoardEntry
    {
        [Key(0)] public string InstanceId = string.Empty;
        [Key(1)] public string DisplayName = string.Empty;
        [Key(2)] public string Reliability = string.Empty;
        [Key(3)] public string LocationId = string.Empty;
        [Key(4)] public List<string> WitnessNpcIds = new();
    }

    /// <summary>
    /// Clue System v2 (d): ⚠️ BREAKING — เปลี่ยนจาก List<string> CollectedClueCardIds
    /// เป็น List<ClueBoardEntry> Entries (AI VTuber ต้อง adapt shape ใหม่)
    /// </summary>
    [MessagePackObject]
    public class GetClueBoardResponse
    {
        [Key(0)] public List<ClueBoardEntry> Entries = new();
    }

    // ---- Clue System v2 (d): get_clue_graph (graph view ของ board เดียวกัน) ----
    [MessagePackObject]
    public class GetClueGraphRequest
    {
    }

    [MessagePackObject]
    public class GraphNode
    {
        [Key(0)] public string Id = string.Empty;    // clue instance id หรือ witness id
        [Key(1)] public string Type = string.Empty;  // "clue" | "npc"
        [Key(2)] public string Label = string.Empty; // DisplayName ของ clue / id ของ witness
    }

    [MessagePackObject]
    public class GraphEdge
    {
        [Key(0)] public string From = string.Empty;     // clue instance id
        [Key(1)] public string To = string.Empty;       // witness id
        [Key(2)] public string Relation = string.Empty; // "witnessed"
    }

    [MessagePackObject]
    public class GetClueGraphResponse
    {
        [Key(0)] public List<GraphNode> Nodes = new();
        [Key(1)] public List<GraphEdge> Edges = new();
    }

    [MessagePackObject]
    public class MoveToLocationRequest
    {
        [Key(0)] public string LocationId = string.Empty;
    }

    // ---- Clue System v2 (c): investigate_clue ----
    // ⚠️ Security: empty request โดยตั้งใจ — ห้ามรับ LocationId จาก caller (AI อาจ
    // สำรวจข้ามโซน บั๊กเดิมที่เคยแก้ใน ExplorationSystem) — handler บังคับใช้
    // _stateProvider.GetPlayer().CurrentLocationId ฝั่ง server เสมอ
    [MessagePackObject]
    public class InvestigateClueRequest
    {
    }

    [MessagePackObject]
    public class InvestigateClueResponse
    {
        [Key(0)] public bool Success;

        /// <summary>instance id ที่เจอ (ค่าว่างเมื่อไม่เจอ — โปรเจกต์ไม่ใช้ nullable)</summary>
        [Key(1)] public string FoundInstanceId = string.Empty;

        /// <summary>"no_clue_at_location" | "investigation_failed" (ค่าว่างเมื่อสำเร็จ)</summary>
        [Key(2)] public string FailureReason = string.Empty;
    }

    [MessagePackObject]
    public class MoveToLocationResponse
    {
        [Key(0)] public bool Success;
        [Key(1)] public string FailureReason = string.Empty;
    }

    [MessagePackObject]
    public class CancelMoveRequest
    {
    }

    [MessagePackObject]
    public class CancelMoveResponse
    {
        /// <summary>true = ยกเลิกการเดินที่กำลังรันอยู่; false = ไม่ได้เดินอยู่แล้ว (not_moving)</summary>
        [Key(0)] public bool Success;

        /// <summary>"" | "not_moving"</summary>
        [Key(1)] public string FailureReason = string.Empty;
    }

    [MessagePackObject]
    public class UseCardRequest
    {
        [Key(0)] public string CardId = string.Empty;

        // Phase 4 (Player-as-Killer): null/empty = Self; npc id = SingleTarget (เช่น weapon)
        [Key(1)] public string TargetId = string.Empty;
    }

    [MessagePackObject]
    public class UseCardResponse
    {
        [Key(0)] public bool Success;
        [Key(1)] public string FailureReason = string.Empty; // "missing_target", "witnessed", etc.
        [Key(2)] public string ResultText = string.Empty;    // เติมเฉพาะตอน Eliminate สำเร็จ
    }

    [MessagePackObject]
    public class CallMeetingRequest
    {
    }

    [MessagePackObject]
    public class CallMeetingResponse
    {
        [Key(0)] public bool Success;
        [Key(1)] public List<NpcObservableView> AttendingNpcs = new();
    }

    // ---- In-process broadcast messages (published locally inside Unity via MessagePipe) ----

    [MessagePackObject]
    public class SurvivalStatChangedMessage
    {
        [Key(0)] public string StatKey = string.Empty; // Hunger/Thirst/Mood/Fatigue
        [Key(1)] public float NewValue;
        [Key(2)] public float Delta;
    }

    [MessagePackObject]
    public class ConditionCardAppliedMessage
    {
        [Key(0)] public string TargetEntityId = string.Empty; // "player" or npcId
        [Key(1)] public string ConditionCardId = string.Empty;
    }

    [MessagePackObject]
    public class NpcEliminatedMessage
    {
        [Key(0)] public string VictimNpcId = string.Empty;
        [Key(1)] public string LocationId = string.Empty;
        [Key(2)] public List<string> SpawnedClueCardIds = new();
    }

    // ---- Clue System v2 (b): broadcast ต่อ clue instance ที่เกิดใหม่ (1 event / 1 clue) ----
    // In-process only (MessagePipe) — ClueGenerationSystem publish หลังเก็บ instance
    // เข้า registry กลางแล้ว; subscriber (UI/deduction/collect) อ่านรายละเอียดเพิ่ม
    // จาก GameStateProvider.AllClueInstances ด้วย InstanceId
    // (Information Hiding: message ไม่มี SourceActorId/WitnessNpcIds — ground truth
    // ยังอยู่ฝั่งระบบ ไม่กระจายออกทาง event bus)
    [MessagePackObject]
    public class ClueGeneratedMessage
    {
        [Key(0)] public string InstanceId = string.Empty;
        [Key(1)] public string DefId = string.Empty;
        [Key(2)] public string LocationId = string.Empty;
    }

    // ---- Added in Lab B (Chibi Sprite Integration): location-change broadcasts ----
    // In-process only (MessagePipe) — ใช้โดย ChibiSpawnerView เพื่อ spawn/despawn
    // chibi ตาม location ของผู้เล่น/NPC โดยไม่ต้อง polling ใน Update()

    [MessagePackObject]
    public class PlayerLocationChangedMessage
    {
        [Key(0)] public string OldLocationId = string.Empty;
        [Key(1)] public string NewLocationId = string.Empty;
    }

    [MessagePackObject]
    public class NpcLocationChangedMessage
    {
        [Key(0)] public string NpcId = string.Empty;
        [Key(1)] public string OldLocationId = string.Empty; // อาจเป็น null ตอนวาง NPC ครั้งแรกของรอบ
        [Key(2)] public string NewLocationId = string.Empty;
    }

    // ---- Added in Lab B Phase 3 (Player System): item pickup broadcast ----

    [MessagePackObject]
    public class ItemPickedUpMessage
    {
        [Key(0)] public string ItemId = string.Empty;
        [Key(1)] public string LocationId = string.Empty;
    }

    // ---- Added in Lab B Phase 5 (Card Hand UI): inventory change broadcast ----
    // CardInventorySystem publish ทุกครั้งที่ inventory เปลี่ยนจริง → CardHandPresenter
    // subscribe เพื่อ render มือการ์ดแบบ event-driven (แทน polling)

    [MessagePackObject]
    public class CardInventoryChangedMessage
    {
        [Key(0)] public string CardId = string.Empty;
        [Key(1)] public int NewCount; // จำนวนหลังเปลี่ยน (0 = หมด/ถูกลบออกจากมือ)
        [Key(2)] public int Delta;    // +เพิ่ม / -ลด
    }

    // ---- Added in Lab C Phase 1 (Hybrid BiomeScatter): harvestable node broadcast ----
    // In-process only (MessagePipe) — NodeHarvestSystem publish หลัง harvest สำเร็จ
    // 1 ครั้ง (ได้ไอเท็มเข้า inventory แล้ว) — Depleted=true เมื่อ durability หมด
    // (RegrowSeconds > 0 = จะกลับมาให้เก็บใหม่, 0 = หายถาวร)

    [MessagePackObject]
    public class NodeHarvestedMessage
    {
        [Key(0)] public string NodeId = string.Empty;
        [Key(1)] public string ItemId = string.Empty;
        [Key(2)] public int Count;
        [Key(3)] public bool Depleted;
        [Key(4)] public int RegrowSeconds;
    }

    // ---- Added in Lab C Phase 1.5 (MCP harvest_node): request/response ----
    // Bridge → Unity: NodeHarvestSystem ทำงานจริง — toolItemId ว่าง = ให้ระบบเลือก
    // tool ที่เหมาะสมจาก inventory เอง (ต่างจากกด E ที่หมายถึงมือเปล่า)

    [MessagePackObject]
    public class HarvestNodeRequest
    {
        // optional: specific tool card id (e.g. "tool_axe"); null/empty = auto-pick
        // the best matching tool from inventory (or bare hands if none needed)
        [Key(0)] public string ToolItemId = string.Empty;
    }

    [MessagePackObject]
    public class HarvestNodeResponse
    {
        [Key(0)] public bool Success;
        [Key(1)] public string FailureReason = string.Empty; // "no_node_in_range", "wrong_tool", "regrowing"
        [Key(2)] public string NodeId = string.Empty;        // node ที่พยายามเก็บ (เติมเมื่อเจอ node)
        [Key(3)] public string ItemId = string.Empty;        // yield card id (เติมเมื่อ Success)
        [Key(4)] public int Count;            // จำนวน yield (เติมเมื่อ Success)
        [Key(5)] public bool Depleted;        // durability หมดพอดี (เติมเมื่อ Success)
        [Key(6)] public int RegrowSeconds;    // > 0 เมื่อ Depleted และจะงอกใหม่ (เติมเมื่อ Success)
    }

    // ---- Clue System v2 (e) pin workspace: get_pinned_clues (bridge query tool) ----
    // อ่าน ordered pin list เดียวกับที่กระดานในเกมแสดง (CluePinState singleton — ค่าเดียว
    // กับ UI) เพื่อให้ AI VTuber เห็นว่า player กำลังเทียบ node อะไรอยู่บ้าง

    [MessagePackObject]
    public class GetPinnedCluesRequest
    {
        // ว่าง = รายการ pin ทั้งหมดตามลำดับ pin (MCP tool ใช้แบบนี้ — state ฝั่ง server
        // เหมือน get_clue_board); ใส่ id = resolve node เดียว (presenter ใช้ตอนคลิก node
        // — reuse chain เดียวกันทั้ง popup/การ์ด pin/MCP)
        [Key(0)] public string NodeId = string.Empty;
    }

    /// <summary>รายละเอียด node ที่ pin — Type=="clue" ใช้ field กลุ่ม clue, Type=="npc" ใช้กลุ่ม npc
    /// (เนื้อหาเดียวกับที่ popup/การ์ด pin บนกระดานโชว์ — ไม่มี ground truth เช่น SourceActorId/Role)</summary>
    [MessagePackObject]
    public class PinnedNodeEntry
    {
        [Key(0)] public string NodeId = string.Empty;
        [Key(1)] public string Type = string.Empty;             // "clue" | "npc"
        // ---- clue ----
        [Key(2)] public string DisplayName = string.Empty;
        [Key(3)] public string Reliability = string.Empty;      // Strong/Weak/RedHerring
        [Key(4)] public string LocationId = string.Empty;
        [Key(5)] public List<string> WitnessNpcIds = new();     // ผ่าน filter ของ GetClueBoardHandler แล้ว
        // ---- npc witness ----
        [Key(6)] public string Zone = string.Empty;             // CurrentLocationId (player เห็นจริง)
        [Key(7)] public bool IsAlive;
        [Key(8)] public List<string> WitnessedClues = new();    // "<label> @ <location>" — อาลิไบคร่าว ๆ
    }

    [MessagePackObject]
    public class GetPinnedCluesResponse
    {
        [Key(0)] public List<PinnedNodeEntry> Pinned = new();   // เรียงตามลำดับ pin (เก่าสุดก่อน)
    }

    // ---- Clue System v2 (e): set_pinned_clue (bridge mutating tool — AI pin/unpin เองได้) ----
    // Explicit set semantics (Pinned = true/false) ไม่ใช่ toggle — ปลอดภัยกว่าเวลา AI retry;
    // response แนบ pin list ล่าสุดเสมอ เพื่อให้ AI แก้ model ของตัวเองได้ใน step เดียว

    [MessagePackObject]
    public class SetPinnedClueRequest
    {
        [Key(0)] public string NodeId = string.Empty;  // node id จาก get_clue_graph (clue instance id หรือ witness npc id)
        [Key(1)] public bool Pinned;                   // true = pin, false = unpin
    }

    [MessagePackObject]
    public class SetPinnedClueResponse
    {
        [Key(0)] public bool Success;
        [Key(1)] public string FailureReason = string.Empty; // "missing_node_id", "unknown_node", "already_pinned", "not_pinned"
        [Key(2)] public List<string> PinnedNodeIds = new();  // state หลังคำสั่ง (เรียงตามลำดับ pin, เก่าสุดก่อน)
    }
}
