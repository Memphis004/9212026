using System.Collections.Generic;
using Marooned.Shared;

namespace Marooned.UI.Views
{
    /// <summary>
    /// View-layer กลุ่มเบาะแส (Clue System v2 (f) — drag-drop workspace):
    /// 1 กลุ่ม = clue instance ทุกตัวที่ DefId เดียวกัน (proxy = DisplayName เพราะ
    /// ClueBoardEntry ไม่พก DefId — resolve จาก GameStateProvider.AllClueInstances
    /// แล้ว fallback เป็น "name:{DisplayName}" เมื่อ instance หายจาก registry)
    /// คลังการ์ดโชว์ 1 การ์ดต่อกลุ่ม พร้อม "×N", การ์ด 1 ใบแทน instance ทั้งกลุ่ม
    /// (GroupNodeIds = instance ids ทั้งหมด — Presenter ใช้ pin/unpin ทีเดียวทั้งกลุ่ม)
    ///
    /// ไม่มี [MessagePackObject] — view data contract เหมือน data class อื่นของ view
    /// (Data Separation)
    /// </summary>
    public class ClueGroup
    {
        public string DefId;
        public string DisplayName;
        public string Reliability;
        public List<ClueBoardEntry> Instances = new();
    }

    /// <summary>กลุ่ม clue instance ตาม DefId — เดียวกับที่คลังการ์ด/กราฟใช้ (Locked decision 2)</summary>
    public static class ClueGroupUtil
    {
        /// <summary>
        /// จับกลุ่ม board entries ตาม DefId (resolve จาก registry กลางของ instance) —
        /// เรียงตามลำดับเจอแรก (first-occurrence, อย่าพึ่ง enumeration order ของ
        /// Dictionary); proxy ของ DefId ที่ใช้จริงคือ DisplayName (entry แรกของกลุ่ม —
        /// เบาะแสชนิดเดียวกันชื่อเดียวกันเสมอ)
        /// </summary>
        public static List<ClueGroup> GroupByDefId(IEnumerable<ClueBoardEntry> entries,
            IReadOnlyDictionary<string, ClueInstance> allClueInstances)
        {
            var groups = new List<ClueGroup>();
            var byKey = new Dictionary<string, ClueGroup>();
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrEmpty(e.InstanceId)) continue;

                // DefId จริงถ้า resolve ได้จาก registry กลาง; ไม่ได้ → DisplayName เป็น proxy
                // (clue ชนิดเดียวกัน DisplayName เดียวกันเสมอ — กันตกกลุ่มถ้า def หาย)
                string defId = null;
                if (allClueInstances != null && allClueInstances.TryGetValue(e.InstanceId, out var inst))
                    defId = inst.DefId;
                if (string.IsNullOrEmpty(defId))
                    defId = "name:" + e.DisplayName;

                if (!byKey.TryGetValue(defId, out var g))
                {
                    g = new ClueGroup
                    {
                        DefId = defId,
                        DisplayName = e.DisplayName,
                        Reliability = e.Reliability,
                        Instances = new List<ClueBoardEntry>()
                    };
                    byKey[defId] = g;
                    groups.Add(g);
                }
                g.Instances.Add(e);
            }
            return groups;
        }
    }
}
