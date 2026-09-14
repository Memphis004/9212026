using System.Collections.Generic;
using MessagePack;

namespace Marooned.Shared
{
    /// <summary>
    /// Clue System v2 (a): แหล่งที่มาของ clue instance — เกิดจาก action ประเภทไหน
    /// (ตรงกับ DataTables/Data/ActionClueTriggerDef.csv column triggerSource)
    /// </summary>
    public enum ClueTriggerSource
    {
        KillSabotage,
        TaskSabotage,
        IncidentalAction,
        Hunting
    }

    /// <summary>
    /// Clue System v2 (a): clue เปลี่ยนจาก "card string" เป็น "instance object" —
    /// 1 การกระทำ = 1 instance ที่มี metadata ของตัวเอง (เวลาที่เกิด, ใครทำ, ใครเห็น)
    ///
    /// ⚠️ Information Hiding: SourceActorId + WitnessNpcIds เป็น ground truth
    /// ฝั่งระบบ (deduction/witness propagation ใช้ภายใน) — ห้าม expose ตรง ๆ
    /// ผ่าน MCP response; จะเปิดเฉพาะผ่านการ investigate ใน commit ถัด ๆ ไป
    ///
    /// Multiplayer-ready: instance ทั้งหมดถูกเก็บกลางที่
    /// GameStateProvider.AllClueInstances (ไม่ผูกกับ player คนเดียว)
    ///
    /// ห้ามใช้ ? nullable suffix — โปรเจกต์ไม่ได้ enable nullable reference types
    /// </summary>
    [MessagePackObject]
    public class ClueInstance
    {
        [Key(0)] public string InstanceId = string.Empty;
        [Key(1)] public string DefId = string.Empty;              // ref ClueDef.Id
        [Key(2)] public string LocationId = string.Empty;

        /// <summary>เวลาในเกม (วินาทีนับจากเริ่มรอบ) ตอน clue เกิด</summary>
        [Key(3)] public float GameTimestamp;

        [Key(4)] public ClueTriggerSource Source;

        /// <summary>internal only — actor ที่ทำให้เกิด clue นี้ (ground truth)</summary>
        [Key(5)] public string SourceActorId = string.Empty;

        /// <summary>internal only — snapshot ของ NPC ที่เห็น ณ ตอนเกิด (ground truth)</summary>
        [Key(6)] public List<string> WitnessNpcIds = new();
    }
}
