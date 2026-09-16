using System;
using System.Collections.Generic;
using System.Linq;
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
    /// MVP Lite presenter ของ clue board drag-drop workspace (Clue System v2 (f)):
    /// บน: GraphArea = เฉพาะ node ที่ pin — **นิยาม M(G) = {instance ใน G ที่ถูก pin} ∪
    /// {instance ใน G ที่พยานโดย npc ที่ถูก pin}**; กลุ่ม G โผล่เมื่อ M(G) ไม่ว่าง
    /// (×k, k=|M(G)|), npc N โผล่เมื่อ N ถูก pin หรือเป็นพยานของ instance ที่ pin,
    /// edge (G,N) เมื่อมี entry ใน G ที่ N เป็นพยาน และ (e pin หรือ N pin)
    /// ล่าง: คลัง 2 แถว (clue group ×N / npc portrait) — ลากขึ้นกราฟ = pin ทั้งกลุ่ม,
    /// ลาก node ออกนอกกรอบ = unpin, hover = tooltip (ไม่มี click เหลืออยู่)
    ///
    /// pin มี 2 รูปแบบ: instance id (ลากการ์ดจากคลัง) และ group key "group:{DisplayName}"
    /// (AI set_pinned_clue — graph node id = group key) — M(G) ขยาย group key = ทุก instance
    /// ในกลุ่ม; prune/tooltip/unpin รองรับทั้งสองแบบ
    ///
    /// Reuse: GetClueGraphHandler/GetClueBoardHandler เดิม (witness filter ที่ handler),
    /// ClueGraphTextFormat.DedupeCounted สำหรับรายการนับซ้ำ, CluePinState.Changed
    /// เดิมเป็น single refresh path (player drag / AI set_pinned_clue / prune)
    /// </summary>
    public class ClueBoardPresenter : IInitializable, IDisposable
    {
        private readonly IAsyncRequestHandler<GetClueGraphRequest, GetClueGraphResponse> _getClueGraphHandler;
        private readonly IAsyncRequestHandler<GetClueBoardRequest, GetClueBoardResponse> _getClueBoardHandler;
        private readonly NpcDirectorSystem _npcDirector;
        private readonly GameStateProvider _stateProvider; // AllClueInstances — resolve DefId ตอนจับกลุ่ม
        private readonly CluePinState _pins;
        private readonly ClueBoardView _view;
        private readonly ISubscriber<ClueGeneratedMessage> _subscriber;
        private IDisposable _subscription;
        private CancellationTokenSource _toggleLoopCts;

        /// <summary>groupKey → M(G) ล่าสุด (ใช้ resolve ตอน unpin กลุ่ม + tooltip clue)</summary>
        private Dictionary<string, List<ClueBoardEntry>> _lastGroups = new();
        /// <summary>board entries ล่าสุด (tooltip npc + ประกอบ M(G))</summary>
        private List<ClueBoardEntry> _lastEntries = new();

        private bool _rendering; // กัน render ซ้อน (PruneDead ยิง Changed กลาง render)
        private bool _dirty;     // Changed ยิงระหว่าง render → รอบถัดไปต้อง render อีกครั้ง

        public ClueBoardPresenter(
            IAsyncRequestHandler<GetClueGraphRequest, GetClueGraphResponse> getClueGraphHandler,
            IAsyncRequestHandler<GetClueBoardRequest, GetClueBoardResponse> getClueBoardHandler,
            NpcDirectorSystem npcDirector,
            GameStateProvider stateProvider,
            CluePinState pins,
            ClueBoardView view,
            ISubscriber<ClueGeneratedMessage> subscriber)
        {
            _getClueGraphHandler = getClueGraphHandler;
            _getClueBoardHandler = getClueBoardHandler;
            _npcDirector = npcDirector;
            _stateProvider = stateProvider;
            _pins = pins;
            _view = view;
            _subscriber = subscriber;
        }

        public void Initialize()
        {
            _subscription = _subscriber.Subscribe(_ => RenderAsync().Forget());
            _view.LibraryCardDropped += OnLibraryCardDropped;
            _view.GraphNodeUnpinRequested += OnGraphNodeUnpinRequested;
            _view.NodeHoverEnter += OnNodeHoverEnter;
            _view.NodeHoverExit += OnNodeHoverExit;
            _view.ToggleButtonPressed += TogglePanel;
            _pins.Changed += OnPinsChanged; // AI pin ผ่าน MCP → UI refresh ด้วย (path เดียวกับ player)
            _view.EnsureToggleButton();
            _toggleLoopCts = new CancellationTokenSource();
            ToggleLoopAsync(_toggleLoopCts.Token).Forget();
            RenderAsync().Forget();
        }

        private async UniTaskVoid ToggleLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (Input.GetKeyDown(KeyCode.Tab))
                    TogglePanel();
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }
        }

        /// <summary>เปิด/ปิดกระดาน — pin ค้างใน CluePinState (open ใหม่ render จาก state เดิม)</summary>
        public void TogglePanel()
        {
            var opening = !_view.gameObject.activeSelf;
            _view.gameObject.SetActive(opening);
            if (opening) RenderAsync().Forget();
            else _view.HideTooltip(); // ปิด tooltip ค้าง — เปิดใหม่ต้องไม่เห็น
        }

        /// <summary>
        /// render หลัก — บรรทัดแรกทุกครั้ง: **prune stale pin** (pin id ที่ไม่มีจริงแล้ว
        /// — ไม่อยู่ใน board entries และไม่อยู่ใน roster npc — กวาดทิ้งก่อนคำนวณ M(G))
        /// </summary>
        private async UniTask RenderAsync()
        {
            if (_rendering) { _dirty = true; return; }
            _rendering = true;
            try
            {
                var board = await _getClueBoardHandler.InvokeAsync(new GetClueBoardRequest());
                _lastEntries = board.Entries ?? new List<ClueBoardEntry>();

                // ---- prune stale pin (ก่อน M(G) เสมอ — Test K) ----
                // node ที่ยังมีจริง = instance บน board ∪ npc ใน roster ∪ group key ของกลุ่มที่ยังมี entry
                // (group key = pin ระดับกลุ่มจาก set_pinned_clue — ถือว่า live ตราบใดที่กลุ่มยังมี entry)
                var rosterIds = new HashSet<string>(_npcDirector.Npcs.Keys);
                var liveGroupKeys = new HashSet<string>(
                    ClueGroupUtil.GroupByDefId(_lastEntries, _stateProvider.AllClueInstances)
                        .Select(GroupKey));
                _pins.PruneDead(id =>
                    _lastEntries.Any(e => e != null && e.InstanceId == id)
                    || rosterIds.Contains(id)
                    || liveGroupKeys.Contains(id));

                ComputeGraphModel(out var viewNodes, out var viewEdges);
                _view.SetEdgeCache(viewEdges);
                _view.RenderGraph(viewNodes, viewEdges);

                // ---- คลัง 2 แถว: group ตาม DefId (×N) + npc roster (ids เท่านั้น) ----
                var groups = ClueGroupUtil.GroupByDefId(_lastEntries, _stateProvider.AllClueInstances);
                RenderClueLibrary(groups);
                RenderNpcLibrary();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ClueBoardPresenter] RenderAsync failed: {ex.Message}");
            }
            finally
            {
                _rendering = false;
            }
            if (_dirty)
            {
                _dirty = false;
                await RenderAsync(); // state เปลี่ยนระหว่าง render — วาดใหม่ด้วยข้อมูลล่าสุด
            }
        }

        /// <summary>
        /// คำนวณ M(G) แล้วแปลงเป็น view nodes/edges:
        /// G = กลุ่ม DefId; M(G) = {e ∈ G : e pin} ∪ {e ∈ G : N witness ของ e โดย N pin}
        /// </summary>
        private void ComputeGraphModel(out List<ClueGraphNodeData> viewNodes, out List<ClueGraphEdgeData> viewEdges)
        {
            viewNodes = new List<ClueGraphNodeData>();
            viewEdges = new List<ClueGraphEdgeData>();
            _lastGroups = new Dictionary<string, List<ClueBoardEntry>>();

            var groups = ClueGroupUtil.GroupByDefId(_lastEntries, _stateProvider.AllClueInstances);
            var pinnedSet = new HashSet<string>(_pins.PinnedNodeIds);

            // ตรวจ npc pin ที่มีจริง (pin ที่เป็น npc ต้องอยู่ใน roster — prune ทำแล้ว)
            var pinnedNpcIds = pinnedSet.Where(_npcDirector.Npcs.ContainsKey).ToHashSet();

            foreach (var g in groups)
            {
                var groupKey = GroupKey(g);
                // pin ระดับกลุ่ม (AI set_pinned_clue ใช้ graph node id = group key) = ทุก instance ใน G ถูก pin
                var groupPinned = pinnedSet.Contains(groupKey);
                var mG = g.Instances.Where(e =>
                        groupPinned
                        || pinnedSet.Contains(e.InstanceId)
                        || (e.WitnessNpcIds != null && e.WitnessNpcIds.Any(pinnedNpcIds.Contains)))
                    .ToList();
                _lastGroups[groupKey] = mG;
                if (mG.Count == 0) continue;

                var k = mG.Count;
                var label = k > 1 ? $"{g.DisplayName} ×{k}" : g.DisplayName;
                viewNodes.Add(new ClueGraphNodeData { Id = groupKey, Type = "clue", Label = label });

                // edge (G,N) เมื่อมี e ∈ G ที่ N เป็นพยาน และ (e ถูก pin หรือ N ถูก pin):
                //   (1) e ถูก pin → เส้นถึงพยานทุกตัวของ e (พยานเหล่านี้โผล่เพราะ e pin)
                //   (2) N ถูก pin → เส้น (G,N) ถ้ามี e ใน G ที่ N เป็นพยาน
                foreach (var e in mG)
                {
                    if (!(groupPinned || pinnedSet.Contains(e.InstanceId))) continue;
                    foreach (var w in e.WitnessNpcIds ?? new List<string>())
                        viewEdges.Add(new ClueGraphEdgeData { FromClueKey = groupKey, ToNpcId = w });
                }
                foreach (var npcId in pinnedNpcIds)
                    if (g.Instances.Any(e => e.WitnessNpcIds != null && e.WitnessNpcIds.Contains(npcId)))
                        viewEdges.Add(new ClueGraphEdgeData { FromClueKey = groupKey, ToNpcId = npcId });
            }

            // npc nodes: pin เอง หรือเป็นปลายทางของ edge (พยานของ instance ที่ pin)
            var edgeNpcIds = viewEdges.Select(e => e.ToNpcId).ToHashSet();
            foreach (var npcId in pinnedNpcIds.Concat(edgeNpcIds).Distinct())
                viewNodes.Add(new ClueGraphNodeData { Id = npcId, Type = "npc", Label = npcId });

            // dedupe edge คู่ (G,N)
            var seen = new HashSet<(string, string)>();
            viewEdges = viewEdges.Where(e => seen.Add((e.FromClueKey, e.ToNpcId))).ToList();
        }

        private static string GroupKey(ClueGroup g) => $"group:{g.DisplayName}";

        // ---- คลัง 2 แถว ----

        private void RenderClueLibrary(List<ClueGroup> groups)
        {
            var cards = new List<ClueLibraryCardData>();
            foreach (var g in groups)
            {
                if (g.Instances.Count == 0) continue;
                cards.Add(new ClueLibraryCardData
                {
                    Key = GroupKey(g),
                    DisplayName = g.DisplayName,
                    Subtitle = g.Instances.Count > 1 ? $"×{g.Instances.Count} • {g.Reliability}" : g.Reliability,
                    GroupNodeIds = g.Instances.Select(i => i.InstanceId).ToList(),
                });
            }
            _view.RenderClueLibrary(cards);
        }

        private void RenderNpcLibrary()
        {
            // roster: ids เท่านั้น — ห้าม IsAlive (Locked decision 6: ไม่มี public death channel)
            var cards = new List<ClueLibraryCardData>();
            foreach (var npc in _npcDirector.Npcs.Values)
            {
                cards.Add(new ClueLibraryCardData
                {
                    Key = npc.Id,
                    DisplayName = npc.Id,
                    Subtitle = "", // จงใจว่าง — ไม่โชว์สถานะชีวิต
                    GroupNodeIds = new List<string> { npc.Id },
                });
            }
            _view.RenderNpcLibrary(cards);
        }

        // ---- drag events (player) ----

        /// <summary>การ์ดคลังถูกปล่อยในกราฟ → pin ทุก instance ในกลุ่มที่ยังไม่ pin</summary>
        private void OnLibraryCardDropped(List<string> ids)
        {
            if (ids == null || ids.Count == 0) return;
            _pins.PinMany(ids.Where(id => !_pins.IsPinned(id)));
            // render เกิดผ่าน Changed subscription เสมอ
        }

        /// <summary>
        /// node ถูกลากออกนอกกรอบ → unpin เฉพาะสมาชิกที่ pinned อยู่ใน M(G) ของกลุ่มนั้น
        /// (clue group node) หรือตัวมันเอง (npc node); ไม่มีสมาชิก pinned = no-op + log
        /// </summary>
        private void OnGraphNodeUnpinRequested(string nodeKey)
        {
            if (nodeKey != null && nodeKey.StartsWith("group:"))
            {
                var members = _lastGroups.TryGetValue(nodeKey, out var mG)
                    ? mG.Where(e => _pins.IsPinned(e.InstanceId)).Select(e => e.InstanceId).ToList()
                    : new List<string>();
                if (_pins.IsPinned(nodeKey)) members.Add(nodeKey); // pin ระดับกลุ่ม (AI) → ถอนตัว key เองด้วย
                if (members.Count == 0)
                {
                    Debug.Log($"[ClueBoardPresenter] unpin {nodeKey}: no pinned members (no-op)");
                    return;
                }
                _pins.UnpinMany(members);
                return;
            }

            // npc node
            if (!_pins.IsPinned(nodeKey))
            {
                Debug.Log($"[ClueBoardPresenter] unpin {nodeKey}: not pinned (no-op)");
                return;
            }
            _pins.Unpin(nodeKey);
        }

        // ---- hover tooltip (รายละเอียด — decision 3) ----

        private void OnNodeHoverEnter(string key, string kind)
        {
            string text = null;
            if (kind == "npc") text = BuildNpcTooltip(key);
            else if (kind == "clue") text = BuildGroupTooltip(key);          // graph node — M(G) เท่านั้น
            else if (kind == "clue_library") text = BuildClueTooltip(key);   // การ์ดคลัง — ทั้งกลุ่ม
            if (text != null) _view.EnsureTooltipView().Show(text);
        }

        private void OnNodeHoverExit(string key, string kind) => _view.HideTooltip();

        /// <summary>tooltip npc: รายการ "{DisplayName} @ {LocationId}" ที่คนนี้เป็นพยาน (×N dedupe)</summary>
        private string BuildNpcTooltip(string npcId)
        {
            var witnessed = _lastEntries
                .Where(e => e != null && e.WitnessNpcIds != null && e.WitnessNpcIds.Contains(npcId))
                .Select(e => $"{e.DisplayName} @ {e.LocationId}")
                .ToList();
            var lines = ClueGraphTextFormat.DedupeCounted(witnessed);
            var zone = _npcDirector.Npcs.TryGetValue(npcId, out var npc) ? npc.CurrentLocationId : "?";
            var body = lines.Count > 0 ? string.Join("\n", lines) : "ไม่มีเบาะแสที่เป็นพยาน";
            return $"{npcId}\nโซน: {zone}\nเป็นพยาน:\n{body}";
        }

        /// <summary>tooltip clue group node: reliability + locations + พยาน — **จาก M(G) เท่านั้น**
        /// (ชุดเดียวกับ ×k บนป้าย node — กันตัวเลขเถียงกันเองบนจอ)</summary>
        private string BuildGroupTooltip(string groupKey)
        {
            if (!_lastGroups.TryGetValue(groupKey, out var mG) || mG.Count == 0)
                return groupKey;

            var g = ClueGroupUtil.GroupByDefId(_lastEntries, _stateProvider.AllClueInstances)
                .FirstOrDefault(x => GroupKey(x) == groupKey);
            if (g == null) return groupKey;

            var locations = mG.Select(e => e.LocationId).Distinct().ToList();
            // รายชื่อพยาน (raw ซ้ำได้ต่อ instance ใน M(G)) → DedupeCounted รวมเป็น "npc_02 ×3"
            // — ตัวเลข = จำนวน instance ใน M(G) ที่คนนั้นเป็นพยาน (ชุดเดียวกับ ×k บนป้าย)
            var witnesses = ClueGraphTextFormat.DedupeCounted(
                mG.SelectMany(e => e.WitnessNpcIds ?? new List<string>()).ToList());
            var rel = g.Reliability;
            var k = mG.Count;
            var body = $"ความน่าเชื่อถือ: {rel}\nพบที่: {string.Join(", ", locations)}\nพยาน (จากที่ pin):";
            return $"{g.DisplayName} ×{k}\n{body}\n{(witnesses.Count > 0 ? string.Join("\n", witnesses) : "ไม่มีผู้เห็น")}";
        }

        /// <summary>tooltip การ์ด clue ในคลัง (key = group key) — ข้อมูลทั้งกลุ่ม (ทุก instance —
        /// คลังเป็น catalog; ต่างจาก tooltip บน node ที่ใช้ M(G) เท่านั้น)</summary>
        private string BuildClueTooltip(string key)
        {
            var g = ClueGroupUtil.GroupByDefId(_lastEntries, _stateProvider.AllClueInstances)
                .FirstOrDefault(x => GroupKey(x) == key);
            if (g == null) return key;
            var instances = g.Instances;
            var locations = instances.Select(e => e.LocationId).Distinct().ToList();
            var witnesses = ClueGraphTextFormat.DedupeCounted(
                instances.SelectMany(e => e.WitnessNpcIds ?? new List<string>()).ToList());
            var body = $"ความน่าเชื่อถือ: {g.Reliability}\nพบที่: {string.Join(", ", locations)}\nพยาน:";
            return $"{g.DisplayName} ×{instances.Count}\n{body}\n{(witnesses.Count > 0 ? string.Join("\n", witnesses) : "ไม่มีผู้เห็น")}";
        }

        /// <summary>CluePinState.Changed → re-render ทั้งกราฟ (player drag / AI set_pinned_clue / prune)
        /// — render อยู่แล้ว = mark dirty (รอบนั้นอ่าน state ล่าสุดไม่ครบ — วาดใหม่หลังจบ)</summary>
        private void OnPinsChanged()
        {
            if (_rendering) { _dirty = true; return; }
            RenderAsync().Forget();
        }

        public void Dispose()
        {
            _toggleLoopCts?.Cancel();
            _toggleLoopCts?.Dispose();
            _toggleLoopCts = null;
            _view.LibraryCardDropped -= OnLibraryCardDropped;
            _view.GraphNodeUnpinRequested -= OnGraphNodeUnpinRequested;
            _view.NodeHoverEnter -= OnNodeHoverEnter;
            _view.NodeHoverExit -= OnNodeHoverExit;
            _view.ToggleButtonPressed -= TogglePanel;
            _pins.Changed -= OnPinsChanged;
            _subscription?.Dispose();
        }
    }
}
