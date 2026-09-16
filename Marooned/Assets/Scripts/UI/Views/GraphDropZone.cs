using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Marooned.UI.Views
{
    /// <summary>
    /// ป้ายพื้นที่ graph workspace ด้านบนของกระดาน (Clue System v2 (f)):
    /// รับ ghost card ตอน OnEndDrag ของ DraggableCardHandler (RaycastAll เจอ dropzone
    /// ตัวนี้) — event NodesDropped(GroupNodeIds) = pin ทุก instance ในกลุ่ม;
    /// มี IDropHandler fallback เผื่อ ghost ถูกตั้ง blocksRaycasts=true กรณีอื่น
    /// (path หลักคือ RaycastAll จาก DraggableCardHandler)
    /// </summary>
    public class GraphDropZone : MonoBehaviour, IDropHandler
    {
        /// <summary>MVP Lite: การ์ดจากคลังถูกปล่อยในกราฟ → ส่งต่อรายชื่อ instance ให้ Presenter pin</summary>
        public event Action<List<string>> NodesDropped;

        /// <summary>node ถูกลากออกนอกกราฟ (จาก GraphNodeDragProxy) → Presenter unpin</summary>
        public event Action<string> NodeDraggedOut;

        public void HandleDrop(List<string> groupNodeIds)
        {
            if (groupNodeIds == null || groupNodeIds.Count == 0) return;
            NodesDropped?.Invoke(groupNodeIds);
        }

        public void HandleNodeDraggedOut(string nodeKey) => NodeDraggedOut?.Invoke(nodeKey);

        public void OnDrop(PointerEventData eventData)
        {
            // fallback — path หลักคือ DraggableCardHandler.OnEndDrag → RaycastAll
            // (ghost ตั้ง blocksRaycasts=false จึงไม่มีวันตกมาที่ handler นี้เอง)
        }
    }
}
