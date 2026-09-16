using System;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using TMPro;
using UnityEngine.UI;

namespace Marooned.UI.Views
{
    /// <summary>
    /// ติดบนการ์ดคลัง/การ์ดทุกตัว (Clue System v2 (f) — drag-drop workspace):
    /// IPointerEnter/IPointerExit ยิง event — เนื้อหา tooltip ประกอบฝั่ง Presenter
    /// (MVP Lite: view ส่งแค่ key/id), แสดงผ่าน HoverTooltipView กลางตัวเดียว
    /// (Locked decision 3: รายละเอียด = hover tooltip เท่านั้น — ไม่มี click)
    /// ใช้ร่วมทั้ง clue และ npc
    /// </summary>
    public class HoverTooltip : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler
    {
        public string NodeKey { get; set; }   // instance id แทน / npc id / "group:{DisplayName}"
        public string TooltipKind { get; set; } // "clue" | "npc" — Presenter ใช้แยกการประกอบเนื้อหา

        /// <summary>MVP Lite: hover เข้า/ออก → ส่งต่อ key ให้ Presenter (view passive)</summary>
        public event Action<string, string> HoverEnter; // (nodeKey, kind)
        public event Action<string, string> HoverExit;

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (string.IsNullOrEmpty(NodeKey)) return;
            HoverEnter?.Invoke(NodeKey, TooltipKind);
        }

        public void OnPointerExit(PointerEventData eventData) => HoverExit?.Invoke(NodeKey, TooltipKind);
    }

    /// <summary>
    /// Tooltip กลางตัวเดียวใต้ root canvas (✏️ v4 — ชื่อ HoverTooltipView ไม่ใช่
    /// NpcHoverTooltipView กันเข้าใจผิดว่าเฉพาะ npc; ใช้ร่วมทั้ง npc และ clue node)
    /// — raycastTarget=false ทั้ง bg/text จึงไม่บัง hover/drag ของการ์ดใต้มัน
    /// Presenter เรียก Show(text) / Hide() — passive แสดงผลอย่างเดียว
    /// </summary>
    public class HoverTooltipView : MonoBehaviour
    {
        private RectTransform _root;
        private Image _bg;
        private TMP_Text _text;
        private bool _ready;

        [SerializeField] private Vector2 offset = new Vector2(16f, -16f); // จากตำแหน่งเมาส์

        /// <summary>แสดง tooltip ที่ตำแหน่งเมาส์ปัจจุบัน</summary>
        public void Show(string text)
        {
            EnsureBuilt();
            if (_root == null || _text == null) return;
            _text.text = text;
            _root.gameObject.SetActive(true);
            FollowMouse();
        }

        /// <summary>ซ่อน tooltip (เรียกได้ปลอดภัยแม้ยังไม่เคย Show)</summary>
        public void Hide()
        {
            if (_root != null) _root.gameObject.SetActive(false);
        }

        /// <summary>ข้อความปัจจุบัน (runtime tests — build ก่อนถ้ายังไม่เคย Show)</summary>
        public string GetTextForTests()
        {
            EnsureBuilt();
            return _text != null ? _text.text : null;
        }

        private void Update()
        {
            if (_root == null || !_root.gameObject.activeSelf) return;
            FollowMouse();
        }

        private void FollowMouse()
        {
            // Screen → local ของ parent canvas (render mode overlay = scale 1:1 กับ
            // reference resolution ของ CanvasScaler — ใช้ pos จาก event camera ไม่ได้
            // เพราะไม่มี event เข้ามา; ใช้ Input.mousePosition แปลงผ่าน RectTransformUtility)
            var canvas = _root.GetComponentInParent<Canvas>();
            var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    (RectTransform)canvas.transform, Input.mousePosition, cam, out var local))
            {
                _root.anchoredPosition = local + offset;
            }
        }

        /// <summary>สร้าง hierarchy ครั้งเดียว (idempotent) — child ของ root canvas เพื่อวาดทับทุกอย่าง</summary>
        private void EnsureBuilt()
        {
            if (_ready) return;
            _ready = true;

            var canvas = GetComponentInParent<Canvas>();
            var host = canvas != null ? canvas.transform : transform;
            var go = new GameObject("HoverTooltipView", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(host, false);
            _root = (RectTransform)go.transform;
            _root.anchorMin = Vector2.zero;
            _root.anchorMax = Vector2.zero;
            _root.pivot = new Vector2(0f, 1f);
            _root.sizeDelta = new Vector2(420f, 160f);
            _root.localScale = Vector3.one;

            _bg = go.GetComponent<Image>();
            _bg.sprite = null;
            _bg.color = new Color(0.08f, 0.07f, 0.06f, 0.96f);
            _bg.raycastTarget = false; // ✅ tooltip ไม่บังคลิก/drag การ์ดใต้มัน

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(_root, false);
            var tRt = (RectTransform)textGo.transform;
            tRt.anchorMin = Vector2.zero;
            tRt.anchorMax = Vector2.one;
            tRt.offsetMin = new Vector2(14f, 10f);
            tRt.offsetMax = new Vector2(-14f, -10f);

            _text = textGo.AddComponent<TextMeshProUGUI>();
            var font = GetComponentInParent<ClueBoardView>()?.GetLabelFontPublic()
                       ?? Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f.name.Contains("Sarabun"));
            if (font != null) _text.font = font;
            _text.fontSize = 24;
            _text.alignment = TextAlignmentOptions.TopLeft;
            _text.color = new Color(0.95f, 0.93f, 0.88f);
            _text.raycastTarget = false; // ✅

            go.SetActive(false);
        }
    }
}
