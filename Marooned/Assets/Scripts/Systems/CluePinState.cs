using System;
using System.Collections.Generic;

namespace Marooned.Systems
{
    /// <summary>
    /// Clue System v2 (f) pin workspace: single source of truth ของ ordered pin list
    /// — **ไม่มีเพดานจำนวนแล้ว** (เดิม MaxPinned = 3 ตัดตัวเก่าสุดอัตโนมัติ — ลบทิ้ง
    /// ตาม redesign: ลากการ์ดกลุ่มขึ้นกราฟ = pin ทุก instance ในกลุ่มพร้อมกัน จำนวน
    /// จึงโตตามเนื้อหาจริง) — presenter เขียนผ่าน Pin/Unpin, อ่านโดย
    /// GetPinnedCluesHandler (get_pinned_clues MCP tool)
    ///
    /// ⚠️ ไม่ใช่ MessagePack class — state ฝั่ง Unity เท่านั้น (single-process);
    /// ฝั่ง bridge อ่านผ่าน GetPinnedCluesRequest/Response ทาง TCP
    /// </summary>
    public class CluePinState
    {
        /// <summary>node ids เรียงตามลำดับ pin (เก่าสุดก่อน)</summary>
        private readonly List<string> _pinnedNodeIds = new();

        /// <summary>เนื้อหา UI/MCP ใช้ presenter เป็นคนเขียนเสมอ — อ่านกลับได้ (tests)</summary>
        public IReadOnlyList<string> PinnedNodeIds => _pinnedNodeIds;

        public event Action Changed;

        /// <summary>pin ทีเดียวหลาย id (กลุ่มจากการ์ดคลัง / set_pinned_clue) — ข้ามที่ pin อยู่แล้ว;
        /// คืนจำนวนที่เพิ่มจริง; ยิง Changed ครั้งเดียว</summary>
        public int PinMany(IEnumerable<string> nodeIds)
        {
            var added = 0;
            foreach (var id in nodeIds)
            {
                if (string.IsNullOrEmpty(id) || _pinnedNodeIds.Contains(id)) continue;
                _pinnedNodeIds.Add(id);
                added++;
            }
            if (added > 0) Changed?.Invoke();
            return added;
        }

        /// <summary>pin ตัวเดียว (set_pinned_clue ใช้ Toggle เดิมอยู่ — คงไว้ให้ครบมือ)</summary>
        public void Pin(string nodeId) => PinMany(new[] { nodeId });

        /// <summary>ถอนทีเดียวหลาย id (ลาก node กลุ่มออกนอกกราฟ) — คืนจำนวนที่ถอนจริง;
        /// ยิง Changed ครั้งเดียว</summary>
        public int UnpinMany(IEnumerable<string> nodeIds)
        {
            var removed = 0;
            foreach (var id in nodeIds)
                removed += _pinnedNodeIds.Remove(id) ? 1 : 0;
            if (removed > 0) Changed?.Invoke();
            return removed;
        }

        /// <summary>ถอนตัวเดียว</summary>
        public void Unpin(string nodeId) => UnpinMany(new[] { nodeId });

        /// <summary>เข้า-ออก toggle (ปุ่มในเกม / AI tool) — คืน true = ตอนจบคือ pinned</summary>
        public bool Toggle(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return false;
            if (_pinnedNodeIds.Remove(nodeId)) { Changed?.Invoke(); return false; }
            _pinnedNodeIds.Add(nodeId);
            Changed?.Invoke();
            return true;
        }

        /// <summary>ล้าง pin ทั้งหมด (round reset / test isolation)</summary>
        public void Clear()
        {
            if (_pinnedNodeIds.Count == 0) return;
            _pinnedNodeIds.Clear();
            Changed?.Invoke();
        }

        public bool IsPinned(string nodeId) => _pinnedNodeIds.Contains(nodeId);

        /// <summary>ถอน pin ของ node ที่หายจากเกมแล้ว — presenter เรียกตอน render (Test K)</summary>
        public void PruneDead(Func<string, bool> nodeExistsInGraph)
        {
            var before = _pinnedNodeIds.Count;
            _pinnedNodeIds.RemoveAll(id => !nodeExistsInGraph(id));
            if (_pinnedNodeIds.Count != before) Changed?.Invoke();
        }
    }
}
