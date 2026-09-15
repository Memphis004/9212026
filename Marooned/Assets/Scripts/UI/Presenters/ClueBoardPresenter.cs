using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Marooned.Shared;
using Marooned.Systems;
using Marooned.UI.Views;
using MessagePipe;
using UnityEngine;
using VContainer.Unity;

namespace Marooned.UI.Presenters
{
    /// <summary>
    /// MVP Lite presenter ของ clue board graph (Clue System v2 (e) — presentation layer):
    /// subscribe ClueGeneratedMessage → re-render (Test C reactivity) + initial render
    /// ตอน Initialize + poll Tab เปิด/ปิดกระดาน (toggle อยู่ฝั่ง presenter โดยเจตนา —
    /// เมื่อ panel inactive View.Update() ไม่รัน poll ใน view จะเปิดคืนไม่ได้เลย)
    /// + คลิก node → popup รายละเอียด: clue = เบาะแส (reuse GetClueBoardHandler —
    /// witness filter เดียวกับ board), npc witness = โซนปัจจุบัน + อาลิไบคร่าว ๆ
    /// (โซนจาก NpcState ที่ player เห็นจริงผ่าน chibi, อาลิไบ = เบาะแสที่คนนี้เป็นพยาน
    /// จาก board entries ที่ผ่าน filter แล้ว — ไม่มี ground truth หลุดเข้า popup)
    /// + ปักหมุดหลาย node (สูงสุด 3) เทียบกันแบบ side-by-side — pin list อยู่ใน
    /// CluePinState (singleton — state เดียวกับที่ get_pinned_clues MCP tool อ่าน),
    /// เนื้อหา resolve สดจาก board handler/npc director ทุก render
    ///
    /// ⚠️ Reuse-only (Single Source of Truth): เรียก GetClueGraphHandler ที่มีอยู่แล้ว
    /// ผ่าน interface MessagePipe — handler จัดการ witness filter (รวม player_local) ผ่าน
    /// GetClueBoardHandler แล้ว ที่นี่แค่ map GraphNode/GraphEdge → view data แล้ว push ให้
    /// ClueBoardView (passive) render — ห้าม filter/duplicate ตรรกะใหม่ที่นี่
    /// </summary>
    public class ClueBoardPresenter : IInitializable, IDisposable
    {
        private readonly IAsyncRequestHandler<GetClueGraphRequest, GetClueGraphResponse> _getClueGraphHandler;
        private readonly IAsyncRequestHandler<GetClueBoardRequest, GetClueBoardResponse> _getClueBoardHandler;
        private readonly NpcDirectorSystem _npcDirector;
        private readonly CluePinState _pins;
        private readonly ClueBoardView _view;
        private readonly ISubscriber<ClueGeneratedMessage> _subscriber;
        private IDisposable _subscription;
        private CancellationTokenSource _toggleLoopCts;

        public ClueBoardPresenter(
            IAsyncRequestHandler<GetClueGraphRequest, GetClueGraphResponse> getClueGraphHandler,
            IAsyncRequestHandler<GetClueBoardRequest, GetClueBoardResponse> getClueBoardHandler,
            NpcDirectorSystem npcDirector,
            CluePinState pins,
            ClueBoardView view,
            ISubscriber<ClueGeneratedMessage> subscriber)
        {
            _getClueGraphHandler = getClueGraphHandler;
            _getClueBoardHandler = getClueBoardHandler;
            _npcDirector = npcDirector;
            _pins = pins;
            _view = view;
            _subscriber = subscriber;
        }

        public void Initialize()
        {
            _subscription = _subscriber.Subscribe(_ => RenderAsync().Forget());
            _view.NodeClicked += OnNodeClicked;
            _view.PinRequested += TogglePin;
            _view.ToggleButtonPressed += TogglePanel;
            _pins.Changed += OnPinsChanged; // AI pin ผ่าน MCP → UI refresh ด้วย (path เดียวกับ player)
            _view.EnsureToggleButton(); // ปุ่ม HUD บน Canvas — มองเห็น/กดได้แม้กระดานซ่อน
            _toggleLoopCts = new CancellationTokenSource();
            ToggleLoopAsync(_toggleLoopCts.Token).Forget();
            RenderAsync().Forget(); // Initial render (panel เริ่มซ่อน — ข้อมูลพร้อมไว้ก่อนเปิด)
        }

        /// <summary>
        /// Poll Tab ผ่าน UniTask PlayerLoop (plain C# ไม่มี Update — pattern เดียวกับ
        /// "UniTask ไม่ใช่ coroutine" ของโปรเจกต์); cancel ผ่าน CancellationTokenSource
        /// ตอน Dispose — Forget กลืน OperationCanceledException ให้เอง
        /// </summary>
        private async UniTaskVoid ToggleLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (Input.GetKeyDown(KeyCode.Tab))
                    TogglePanel();
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }
        }

        /// <summary>เปิด/ปิดกระดาน — เปิดทุกครั้ง render ใหม่ให้ข้อมูลสดจาก handler</summary>
        public void TogglePanel()
        {
            var opening = !_view.gameObject.activeSelf;
            _view.gameObject.SetActive(opening);
            if (opening) RenderAsync().Forget();
            else _view.HideNodeDetail(); // ปิด popup ค้างไว้ด้วย — เปิดใหม่ต้องไม่เห็นการ์ดเก่า
            // (pin ค้างไว้ได้ — RenderAsync จะวาดแถว pin ใหม่จาก _pinnedNodeIds เอง)
        }

        /// <summary>UniTask (ไม่ใช่ UniTaskVoid) เพื่อให้ runtime tests await ผ่าน reflection ได้</summary>
        private async UniTask RenderAsync()
        {
            try
            {
                var response = await _getClueGraphHandler.InvokeAsync(new GetClueGraphRequest());

                var viewNodes = new List<ClueGraphNodeData>();
                var viewEdges = new List<ClueGraphEdgeData>();

                // ⚠️ Field name จริงของ MessagePack classes: node.Label (ไม่ใช่ DisplayName),
                // edge.From / edge.To (ไม่ใช่ SourceId/TargetId)
                foreach (var node in response.Nodes)
                    viewNodes.Add(new ClueGraphNodeData { Id = node.Id, Type = node.Type, Label = node.Label });

                foreach (var edge in response.Edges)
                    viewEdges.Add(new ClueGraphEdgeData { FromClueId = edge.From, ToNpcId = edge.To });

                _view.RenderGraph(viewNodes, viewEdges);

                // การ์ดปักหมุด: resolve สดจาก board handler/npc director ทุก render —
                // pin ของ node ที่หายจากกราฟถูกถอนอัตโนมัติ (ข้อมูล stale ไม่ค้าง)
                await RefreshPinnedAsync();

                // popup เปิดค้างข้าม re-render → refresh ตาม type ของ node (ข้อมูลสดเสมอ)
                var openId = _view.DetailNodeIdForTests;
                if (openId != null) await ShowDetailAsync(openId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ClueBoardPresenter] RenderAsync failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _toggleLoopCts?.Cancel();
            _toggleLoopCts?.Dispose();
            _toggleLoopCts = null;
            _view.NodeClicked -= OnNodeClicked;
            _view.PinRequested -= TogglePin;
            _view.ToggleButtonPressed -= TogglePanel;
            _pins.Changed -= OnPinsChanged;
            _subscription?.Dispose();
        }

        // ---- คลิก node → popup รายละเอียด: clue จาก GetClueBoardHandler เดิม, npc = โซน + อาลิไบ ----

        /// <summary>คลิก node เดิมที่ popup เปิดอยู่ = ปิด (toggle) — คลิกตัวใหม่ = เปลี่ยนเนื้อหา</summary>
        private void OnNodeClicked(string nodeId)
        {
            if (_view.DetailNodeIdForTests == nodeId)
            {
                _view.HideNodeDetail();
                return;
            }
            ShowDetailAsync(nodeId).Forget();
        }

        /// <summary>
        /// แยก type ของ node: clue (มีใน board entries) → ClueNodeDetail,
        /// ไม่งั้นลอง npc witness → NpcNodeDetail (โซน + เบาะแสที่เป็นพยาน)
        /// </summary>
        private async UniTask ShowDetailAsync(string nodeId)
        {
            try
            {
                var response = await _getClueBoardHandler.InvokeAsync(new GetClueBoardRequest());

                ClueBoardEntry clueEntry = null;
                foreach (var e in response.Entries)
                    if (e != null && e.InstanceId == nodeId) { clueEntry = e; break; }

                if (clueEntry != null)
                {
                    _view.ShowClueDetail(new ClueNodeDetail
                    {
                        InstanceId = clueEntry.InstanceId,
                        Label = clueEntry.DisplayName,
                        Reliability = clueEntry.Reliability,
                        LocationId = clueEntry.LocationId,
                        Witnesses = clueEntry.WitnessNpcIds,
                    });
                    return;
                }

                // npc witness: NpcState.CurrentLocationId = โซนที่ player เห็นจริง (chibi เดินอยู่)
                if (!_npcDirector.Npcs.TryGetValue(nodeId, out var npc))
                {
                    _view.HideNodeDetail(); // node หายไประหว่างคลิก (clue ถูกถอน / npc ถูกลบ)
                    return;
                }

                // "อาลิไบ" = เบาะแสที่เก็บได้ที่คนนี้เป็นพยาน (จาก entries ผ่าน filter แล้ว —
                // การยืนยันว่าเห็น X @ Y หมายถึงตัวเองอยู่แถวนั้นตอนนั้นด้วย = alibi คร่าว ๆ
                // ตามแนวคิด GDD "ระบบ alibi กลาง ๆ ให้ query")
                var witnessed = new List<string>();
                foreach (var e in response.Entries)
                    if (e != null && e.WitnessNpcIds != null && e.WitnessNpcIds.Contains(nodeId))
                        witnessed.Add($"{e.DisplayName} @ {e.LocationId}");

                _view.ShowNpcDetail(new NpcNodeDetail
                {
                    NpcId = nodeId,
                    Zone = npc.CurrentLocationId,
                    IsAlive = npc.IsAlive,
                    WitnessedClues = witnessed,
                });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ClueBoardPresenter] ShowDetailAsync failed: {ex.Message}");
            }
        }

        // ---- Pin (ปักหมุดหลาย node เทียบกัน) — รายการอยู่ฝั่ง presenter, สูงสุด 3
        //      (เกินจะตัดตัวเก่าสุด) เนื้อหา resolve สดทุก render เพื่อไม่ค้าง stale ----

        /// <summary>ปัก/ถอนหมุด (จากปุ่มใน popup หรือ × บนการ์ด) — mutate state เดียว
        /// (render ซ้ำเกิดผ่าน Changed subscription เสมอ — ครอบคลุม AI pin ผ่าน MCP ด้วย)</summary>
        private void TogglePin(string nodeId) => _pins.Toggle(nodeId);

        /// <summary>CluePinState.Changed → render แถว pin ใหม่จาก state (source เดียวสำหรับ
        /// player คลิก / AI set_pinned_clue / PruneDead ตอน node หาย)</summary>
        private void OnPinsChanged() => RefreshPinnedAsync().Forget();

        /// <summary>
        /// Resolve การ์ดปักหมุดสดจาก board handler/npc director แล้วส่งให้ view วาด —
        /// pin ที่ node หายจากกราฟ (clue ถูกถอน/npc โดนลบ) ถูกถอนอัตโนมัติก่อนวาด
        /// (รูปแบบข้อความผ่าน formatter ของ view — popup กับการ์ดใช้ format เดียวกัน)
        /// </summary>
        private async UniTask RefreshPinnedAsync()
        {
            try
            {
                // ถอน pin ของ node ที่ไม่อยู่ในกราฟแล้ว (ต้องทำก่อนวาด — กันการ์ดค้าง stale)
                _pins.PruneDead(_view.HasNode);

                if (_pins.PinnedNodeIds.Count == 0)
                {
                    _view.RenderPinned(null);
                    return;
                }

                var response = await _getClueBoardHandler.InvokeAsync(new GetClueBoardRequest());
                var cards = new List<PinnedCardData>();
                foreach (var nodeId in _pins.PinnedNodeIds)
                {
                    ClueBoardEntry clueEntry = null;
                    foreach (var e in response.Entries)
                        if (e != null && e.InstanceId == nodeId) { clueEntry = e; break; }

                    if (clueEntry != null)
                    {
                        var d = new ClueNodeDetail
                        {
                            InstanceId = clueEntry.InstanceId,
                            Label = clueEntry.DisplayName,
                            Reliability = clueEntry.Reliability,
                            LocationId = clueEntry.LocationId,
                            Witnesses = clueEntry.WitnessNpcIds,
                        };
                        cards.Add(new PinnedCardData
                        {
                            NodeId = d.InstanceId,
                            Title = ClueBoardView.FormatClueTitle(d),
                            Body = ClueBoardView.FormatClueBody(d),
                        });
                        continue;
                    }

                    // npc pin — ถ้า npc หายไปเอง (โดนลบ) ก็ข้าม (RenderPinned วาดเฉพาะที่เหลือ)
                    if (!_npcDirector.Npcs.TryGetValue(nodeId, out var npc)) continue;

                    var witnessed = new List<string>();
                    foreach (var e in response.Entries)
                        if (e != null && e.WitnessNpcIds != null && e.WitnessNpcIds.Contains(nodeId))
                            witnessed.Add($"{e.DisplayName} @ {e.LocationId}");

                    var n = new NpcNodeDetail
                    {
                        NpcId = nodeId,
                        Zone = npc.CurrentLocationId,
                        IsAlive = npc.IsAlive,
                        WitnessedClues = witnessed,
                    };
                    cards.Add(new PinnedCardData
                    {
                        NodeId = n.NpcId,
                        Title = ClueBoardView.FormatNpcTitle(n),
                        Body = ClueBoardView.FormatNpcBody(n),
                    });
                }

                _view.RenderPinned(cards);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ClueBoardPresenter] RefreshPinnedAsync failed: {ex.Message}");
            }
        }
    }
}
