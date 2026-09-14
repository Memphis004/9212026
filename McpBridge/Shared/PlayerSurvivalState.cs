using System.Collections.Generic;
using MessagePack;

namespace Marooned.Shared
{
    [MessagePackObject]
    public class PlayerSurvivalState
    {
        [Key(0)] public float Hunger = 100f;
        [Key(1)] public float Thirst = 100f;
        [Key(2)] public float Mood = 100f;
        [Key(3)] public float Fatigue = 0f;

        /// <summary>Illness/Injury card ids currently affecting the player.</summary>
        [Key(4)] public List<string> ActiveConditionCardIds = new();

        /// <summary>cardId -> count.</summary>
        [Key(5)] public Dictionary<string, int> Inventory = new();

        [Key(6)] public string CurrentLocationId = string.Empty;

        [Key(7)] public bool IsAlive = true;

        /// <summary>
        /// Clue System v2 (a): instance ids ที่ผู้เล่นเก็บ/สังเกตไว้ (เดิมชื่อ
        /// CollectedClueCardIds — rename เท่านั้น, MessagePack Key(8) คงเดิมเพื่อ
        /// wire compatibility กับ session เก่า)
        /// </summary>
        [Key(8)] public List<string> CollectedClueInstanceIds = new();

        /// <summary>How many times the player has accused someone wrongly. Hitting the cap = loss.</summary>
        [Key(9)] public int WrongAccusations = 0;

        [Key(10)] public ChibiAppearance Avatar = new();

        // ---- Added Lab B Phase 3 (Player System): world position for free movement ----
        /// <summary>ตำแหน่งจริงบนโลก (top-down lite) — อัปเดตโดย PlayerMovementSystem เท่านั้น</summary>
        [Key(11)] public float PositionX;
        [Key(12)] public float PositionY;
        [Key(13)] public bool FacingRight = true;

        /// <summary>activity ปัจจุบันของผู้เล่น (Idle/Walking...) — ใช้เลือก animation (TODO: ย้ายไป enum ของ player เอง)</summary>
        [Key(14)] public NpcActivityState Activity = NpcActivityState.Idle;

        // ---- Added Auto-Move (MoveToLocation): จุดหมาย + flag ของระบบเดินอัตโนมัติ ----
        /// <summary>จุดหมายการเดินอัตโนมัติ (world unit) — ตั้งโดย MoveToLocationHandler, อ่านโดย PlayerAutoMoveSystem</summary>
        [Key(15)] public float TargetX;
        [Key(16)] public float TargetY;

        /// <summary>true = กำลังเดินอัตโนมัติหา TargetX/Y — PlayerMovementSystem งดรับคีย์บอร์ดขณะนี้ (Test C)</summary>
        [Key(17)] public bool IsAutoMoving;
    }
}
