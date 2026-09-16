using System;
using System.Collections.Generic;
using Marooned.Shared;
using UnityEngine;

namespace Marooned.UI.Views
{
    /// <summary>
    /// ผู้แก้ไขภาพ NPC (Clue System v2 (f) — คลัง NPC): หา portrait จาก
    /// Resources/Portraits/{npcId} → ไม่เจอ = fallback วงกลมสีจาก hash ของ id
    /// (สีเดิมก่อนเสมอ — npc เดียวกันต้องได้สีเดียวกันทุก render)
    /// </summary>
    public static class NpcPortraitResolver
    {
        /// <summary>โหลด portrait ของ npc (null ได้ — caller ใช้ fallback ต่อ)</summary>
        public static Sprite LoadPortrait(string npcId)
        {
            if (string.IsNullOrEmpty(npcId)) return null;
            return Resources.Load<Sprite>($"Portraits/{npcId}");
        }

        /// <summary>fallback: วงกลมสีจาก hash ของ id — deterministic ต่อ npc</summary>
        public static Color FallbackColor(string npcId)
        {
            unchecked
            {
                var h = 2166136261;
                foreach (var c in npcId ?? "?")
                {
                    h ^= c;
                    h *= 16777619;
                }
                var r = ((h & 0xFF0000) >> 16) / 255f;
                var g = ((h & 0x00FF00) >> 8) / 255f;
                var b = (h & 0x0000FF) / 255f;
                // ยกพื้นความสว่าง — กันสีดำ/เข้มจนกลืนพื้นกระดาน
                return new Color(0.45f + 0.5f * r, 0.45f + 0.5f * g, 0.45f + 0.5f * b, 1f);
            }
        }
    }

    /// <summary>
    /// View-layer ข้อมูลการ์ดคลัง (Clue System v2 (f)): clue การ์ดเดียวต่อกลุ่ม DefId
    /// (Presenter สร้างจาก ClueGroup — GroupNodeIds = instance ids ทั้งกลุ่ม เพื่อ
    /// ลากวาง = pin ทุก instance) / npc การ์ดต่อคน (ids เท่านั้น — ห้าม IsAlive
    /// ตาม Locked decision 6) — ไม่มี [MessagePackObject] (Data Separation)
    /// </summary>
    public class ClueLibraryCardData
    {
        public string Key;                      // "group:{DisplayName}" หรือ npc id
        public string DisplayName;              // ชื่อบนการ์ด (clue = DisplayName, npc = id)
        public string Subtitle;                 // "×3 • Strong" หรือ "" (npc ไม่โชว์สถานะ)
        public List<string> GroupNodeIds = new(); // ids ที่ลากวางจะ pin/unpin พร้อมกัน
    }
}
