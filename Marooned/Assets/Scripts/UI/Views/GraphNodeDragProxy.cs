using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Marooned.UI.Views
{
    /// <summary>
    /// ติดบน node ทุกตัวใน graph workspace (Clue System v2 (f) — เดิมชื่อ
    /// GraphNodeClickProxy): ลากจัดตำแหน่ง + ลากออกนอกกรอบ = unpin
    /// ✅ ลบ click ทิ้งทั้งหมด (Locked decision 3 — ไม่มี IPointerClickHandler /
    /// Clicked event / _wasDragged เหลืออยู่) รายละเอียด = hover tooltip
    /// (HoverTooltip component แยกติดบน node เดียวกัน)
    /// </summary>
    public class GraphNodeDragProxy : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public string NodeKey { get; set; } // instance id / npc id / "group:{DisplayName}"

        private RectTransform _rect;
        private RectTransform _dropZoneRect; // กรอบ GraphArea — อยู่นอกนี้ = ลากออก
        private RectTransform _parentRect;   // พ่อของ node (= พื้นที่กราฟ) — แปลง screen→local

        /// <summary>MVP Lite: node ถูกลาก (จัดตำแหน่ง) → View เก็บ custom position + วาดเส้นใหม่</summary>
        public event Action<string, Vector2> Dragged;

        /// <summary>MVP Lite: node ถูกปล่อยนอกกรอบกราฟ → Presenter unpin (ทั้งกลุ่มถ้าเป็น clue group)</summary>
        public event Action<string> DraggedOutOfGraph;

        private bool _dragging;

        /// <summary>
        /// ✏️ v4: ต้องเรียก Init ตอน spawn node จาก View ทุกครั้ง — ส่ง RectTransform
        /// ของ GraphArea เข้ามา (ห้ามปล่อย null ไม่งั้นตรวจ "นอกกรอบ" ไม่ได้)
        /// </summary>
        public void Init(RectTransform dropZoneRect)
        {
            _dropZoneRect = dropZoneRect;
        }

        private void Awake()
        {
            _rect = (RectTransform)transform;
            _parentRect = transform.parent as RectTransform;
        }

        public void OnBeginDrag(PointerEventData eventData) => _dragging = true;

        public void OnDrag(PointerEventData eventData)
        {
            if (!_dragging || _parentRect == null) return;

            // screen → local ของพ่อ (พื้นที่กราฟ) แล้วขยับ node
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _parentRect, eventData.position, eventData.pressEventCamera, out var local))
            {
                _rect.anchoredPosition = local;
                Dragged?.Invoke(NodeKey, local);
            }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            _dragging = false;
            if (_dropZoneRect == null) return; // Init ไม่ถูกเรียก (bug ฝั่ง spawn) — ไม่ตัดสิน

            // ปล่อยนอกกรอบ GraphArea = ลากออก (unpin)
            var cam = eventData.pressEventCamera;
            var outside = !RectangleContainsScreenPoint(_dropZoneRect, eventData.position, cam);
            if (outside) DraggedOutOfGraph?.Invoke(NodeKey);
        }

        /// <summary>เช็คจุดบนจออยู่ในกรอบ rect ไหม (world corner 4 มุม → กรอบ AABB)</summary>
        private static bool RectangleContainsScreenPoint(RectTransform rt, Vector2 screenPos, Camera cam)
        {
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            var min = new Vector2(
                Mathf.Min(Mathf.Min(corners[0].x, corners[1].x), Mathf.Min(corners[2].x, corners[3].x)),
                Mathf.Min(Mathf.Min(corners[0].y, corners[1].y), Mathf.Min(corners[2].y, corners[3].y)));
            var max = new Vector2(
                Mathf.Max(Mathf.Max(corners[0].x, corners[1].x), Mathf.Max(corners[2].x, corners[3].x)),
                Mathf.Max(Mathf.Max(corners[0].x, corners[1].x), Mathf.Max(corners[2].x, corners[3].x)));
            // screen → world เมื่อ canvas ไม่ใช่ overlay
            Vector3 p = screenPos;
            if (cam != null)
            {
                p = cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, 10f));
            }
            return p.x >= min.x && p.x <= max.x && p.y >= min.y && p.y <= max.y;
        }
    }
}
