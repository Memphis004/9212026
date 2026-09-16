using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Marooned.UI.Views
{
    /// <summary>
    /// ติดบนการ์ดคลัง (Clue System v2 (f) — drag/scroll arbitration, Locked decision 5):
    /// ปัดแนวนอนเด่น (เกิน slop ~10px) = เลื่อน ScrollRect แถวคลัง (forward ให้ parent
    /// ScrollRect ผ่าน scrollRect.OnBeginDrag/OnDrag/OnEndDrag เพราะ bubble หยุดที่การ์ด),
    /// ลากแนวตั้งเด่น = หยิบการ์ด (สร้าง ghost) — state: Pending → Scrolling | Dragging
    /// ตัดสินครั้งเดียวแล้วไม่เปลี่ยนใจใน gesture เดียว
    ///
    /// OnEndDrag ตอน Dragging → EventSystem.RaycastAll หา GraphDropZone →
    /// HandleDrop(GroupNodeIds) (ลากวางกราฟ = pin ทุก instance ในกลุ่ม — decision 1)
    /// OnBeginDrag → ซ่อน tooltip ที่เปิดค้างอยู่
    /// </summary>
    public class DraggableCardHandler : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        private const float AxisSlop = 10f; // px — เกินนี้ถึงตัดสินแนว (slop ตาม spec)

        public string CardKey { get; set; }              // "group:{DisplayName}" หรือ npc id
        public List<string> GroupNodeIds { get; set; } = new(); // instance ids ทั้งกลุ่ม (pin พร้อมกัน)
        public HoverTooltipView TooltipView { get; set; }       // ปิด tooltip ค้างตอนเริ่มลาก

        /// <summary>MVP Lite: การ์ดถูกปล่อยใน dropzone → ส่งต่อ ids ให้ Presenter pin (ผูกจาก View)</summary>
        public event Action<List<string>> DroppedOnGraph;

        private enum DragState { Pending, Scrolling, Dragging }
        private DragState _state = DragState.Pending;

        private RectTransform _rect;
        private ScrollRect _parentScroll;   // ScrollRect แถวคลัง (forward horizontal scroll)
        private GameObject _ghost;          // สำเนาการ์ดลอยตามเมาส์ (สร้างเฉพาะตอน Dragging)
        private Canvas _canvas;

        private void Awake()
        {
            _rect = (RectTransform)transform;
            _canvas = GetComponentInParent<Canvas>();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            _state = DragState.Pending;
            TooltipView?.Hide(); // ซ่อน tooltip ค้างทันทีที่เริ่มลาก (ไม่ว่าจะลากหรือปัด)
        }

        public void OnDrag(PointerEventData eventData)
        {
            switch (_state)
            {
                case DragState.Pending:
                    if (Mathf.Abs(eventData.delta.x) > Mathf.Abs(eventData.delta.y))
                    {
                        if (Mathf.Abs(eventData.delta.x) > AxisSlop)
                        {
                            _state = DragState.Scrolling;
                            ForwardBegin(eventData);
                            Forward(eventData);
                        }
                        // แนวนอนยังไม่เกิน slop = รอตัดสินต่อ
                    }
                    else
                    {
                        if (Mathf.Abs(eventData.delta.y) > AxisSlop)
                        {
                            _state = DragState.Dragging;
                            CreateGhost(eventData);
                        }
                        // แนวตั้งยังไม่เกิน slop = รอตัดสินต่อ
                    }
                    break;

                case DragState.Scrolling:
                    Forward(eventData);
                    break;

                case DragState.Dragging:
                    MoveGhost(eventData);
                    break;
            }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (_state == DragState.Scrolling)
            {
                ForwardEnd(eventData);
            }
            else if (_state == DragState.Dragging)
            {
                var zone = FindDropZone(eventData);
                if (zone != null) DroppedOnGraph?.Invoke(GroupNodeIds);
                DestroyGhost();
            }
            _state = DragState.Pending;
        }

        // ---- forward ให้ ScrollRect แถวคลัง (ปัดแนวนอน — bubble หยุดที่การ์ด ต้องส่งเอง) ----

        private void ForwardBegin(PointerEventData eventData)
        {
            _parentScroll = _parentScroll != null ? _parentScroll : GetComponentInParent<ScrollRect>();
            if (_parentScroll != null)
            {
                eventData.pointerDrag = _parentScroll.gameObject; // ScrollRect.OnDrag ตรวจ pointerDrag == gameObject
                _parentScroll.OnBeginDrag(eventData);
            }
        }

        private void Forward(PointerEventData eventData)
        {
            if (_parentScroll != null)
            {
                eventData.pointerDrag = _parentScroll.gameObject;
                _parentScroll.OnDrag(eventData);
            }
        }

        private void ForwardEnd(PointerEventData eventData)
        {
            if (_parentScroll != null)
            {
                eventData.pointerDrag = _parentScroll.gameObject;
                _parentScroll.OnEndDrag(eventData);
            }
        }

        // ---- ghost (สำเนาการ์ดลอยตามเมาส์) — สร้างเฉพาะตอนตัดสินแล้วว่า Dragging ----

        private void CreateGhost(PointerEventData eventData)
        {
            var host = _canvas != null ? (RectTransform)_canvas.transform : (RectTransform)transform.root;
            _ghost = new GameObject($"DragGhost_{CardKey}", typeof(RectTransform), typeof(CanvasGroup));
            _ghost.transform.SetParent(host, false);
            var grt = (RectTransform)_ghost.transform;
            grt.sizeDelta = _rect.sizeDelta;
            grt.localScale = Vector3.one;

            var group = _ghost.GetComponent<CanvasGroup>();
            group.alpha = 0.75f;          // ตาม spec
            group.blocksRaycasts = false; // ไม่บัง raycast — RaycastAll เจอ dropzone ใต้เมาส์

            // ghost เป็น solid card สีตามต้นฉบับ (พอสำหรับ feedback — ไม่ clone ลูกทั้งหมด)
            var img = _ghost.AddComponent<Image>();
            img.sprite = null;
            img.color = new Color(0.16f, 0.14f, 0.12f, 0.9f);

            MoveGhost(eventData);
        }

        private void MoveGhost(PointerEventData eventData)
        {
            if (_ghost == null) return;
            var host = (RectTransform)_ghost.transform.parent;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    host, eventData.position, eventData.pressEventCamera, out var local))
                ((RectTransform)_ghost.transform).anchoredPosition = local;
        }

        private void DestroyGhost()
        {
            if (_ghost != null) Destroy(_ghost);
            _ghost = null;
        }

        /// <summary>RaycastAll ใต้เมาส์ → GraphDropZone ตัวแรก (ghost blocksRaycasts=false จึงไม่บัง)</summary>
        private GraphDropZone FindDropZone(PointerEventData eventData)
        {
            var results = new List<RaycastResult>();
            EventSystem.current.RaycastAll(eventData, results);
            foreach (var r in results)
            {
                var zone = r.gameObject.GetComponentInParent<GraphDropZone>();
                if (zone != null) return zone;
            }
            return null;
        }

        private void OnDestroy()
        {
            if (_ghost != null) Destroy(_ghost); // gesture ยังไม่จบแต่การ์ดโดน clear (re-render)
            _ghost = null;
        }
    }
}
