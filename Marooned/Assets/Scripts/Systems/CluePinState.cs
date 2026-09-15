using System;
using System.Collections.Generic;

namespace Marooned.Systems
{
    /// <summary>
    /// Clue System v2 (e) pin workspace: single source of truth ของ ordered pin list
    /// (สูงสุด 3, เกินตัดตัวเก่าสุด) — presenter เขียนผ่าน TogglePin, อ่านโดย
    /// GetPinnedCluesHandler (get_pinned_clues MCP tool) เพื่อให้ AI VTuber เห็น
    /// state เดียวกับที่กระดานในเกมแสดง
    ///
    /// ⚠️ ไม่ใช่ MessagePack class — state ฝั่ง Unity เท่านั้น (single-process);
    /// ฝั่ง bridge อ่านผ่าน GetPinnedCluesRequest/Response ทาง TCP
    /// </summary>
    public class CluePinState
    {
        /// <summary>node ids เรียงตามลำดับ pin (เก่าสุดก่อน)</summary>
        private readonly List<string> _pinnedNodeIds = new();
        public const int MaxPinned = 3;

        /// <summary>เนื้อหา popup/pin-card ใช้ presenter เป็นคนเขียนเสมอ — อ่านกลับได้ (tests)</summary>
        public IReadOnlyList<string> PinnedNodeIds => _pinnedNodeIds;

        public event Action Changed;

        /// <summary>pin อยู่แล้ว = ถอน (คืน false = unpinned) — ไม่อยู่ = ปัก (เกิน 3 ตัดตัวเก่าสุด, คืน true)</summary>
        public bool Toggle(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return false;

            if (_pinnedNodeIds.Remove(nodeId))
            {
                Changed?.Invoke();
                return false; // unpinned
            }

            _pinnedNodeIds.Add(nodeId);
            if (_pinnedNodeIds.Count > MaxPinned)
                _pinnedNodeIds.RemoveAt(0); // ตัวเก่าสุดหลุดอัตโนมัติ

            Changed?.Invoke();
            return true; // pinned
        }

        /// <summary>ล้าง pin ทั้งหมด (round reset / test isolation)</summary>
        public void Clear()
        {
            if (_pinnedNodeIds.Count == 0) return;
            _pinnedNodeIds.Clear();
            Changed?.Invoke();
        }

        public bool IsPinned(string nodeId) => _pinnedNodeIds.Contains(nodeId);

        /// <summary>ถอน pin ของ node ที่หายจากกราฟแล้ว (clue ถูกถอน/npc โดนลบ) — presenter เรียกตอน render</summary>
        public void PruneDead(Func<string, bool> nodeExistsInGraph)
        {
            var before = _pinnedNodeIds.Count;
            _pinnedNodeIds.RemoveAll(id => !nodeExistsInGraph(id));
            if (_pinnedNodeIds.Count != before) Changed?.Invoke();
        }
    }
}
