using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Marooned.Shared;
using MessagePipe;

namespace Marooned.Systems
{
    // IMPORTANT (Unity-specific): MessagePipe's Unity build replaces every
    // ValueTask<T> in the async interfaces with UniTask<T> (requires the
    // UniTask package, already in the openupm add list in README). So
    // IAsyncRequestHandler<TReq,TRes> here means:
    //   UniTask<TRes> InvokeAsync(TReq request, CancellationToken ct = default)
    // NOT System.Threading.Tasks.ValueTask<TRes> like on the McpBridge (.NET) side.
    // Do not copy this file as-is into McpBridge/ — the .NET side keeps ValueTask.

    public class ExploreLocationHandler : IAsyncRequestHandler<ExploreLocationRequest, ExploreLocationResponse>
    {
        private readonly ExplorationSystem _exploration;
        private readonly McpMainThreadDispatcher _mainThread;

        public ExploreLocationHandler(ExplorationSystem exploration, McpMainThreadDispatcher mainThread)
        {
            _exploration = exploration;
            _mainThread = mainThread;
        }

        public async UniTask<ExploreLocationResponse> InvokeAsync(ExploreLocationRequest request, CancellationToken cancellationToken = default)
        {
            // Test C fix: handler ถูก invoke บน TCP background thread — body ต้อง
            // รันบน main thread เพราะ publish chain (inventory → UI re-render,
            // location → chibi spawn) แตะ Unity API
            return await _mainThread.EnqueueAsync(() =>
            {
                var (success, found) = _exploration.Explore(request.LocationId);
                return new ExploreLocationResponse
                {
                    Success = success,
                    FoundCardIds = found,
                    TriggeredEventId = "" // wire up WorldEventSystem roll here in Lab A
                };
            });
        }
    }

    public class CraftCardHandler : IAsyncRequestHandler<CraftCardRequest, CraftCardResponse>
    {
        private readonly CraftingSystem _crafting;
        private readonly McpMainThreadDispatcher _mainThread;

        public CraftCardHandler(CraftingSystem crafting, McpMainThreadDispatcher mainThread)
        {
            _crafting = crafting;
            _mainThread = mainThread;
        }

        public async UniTask<CraftCardResponse> InvokeAsync(CraftCardRequest request, CancellationToken cancellationToken = default)
        {
            return await _mainThread.EnqueueAsync(() =>
            {
                var (success, reason, output) = _crafting.TryCraft(request.RecipeId);
                return new CraftCardResponse { Success = success, FailureReason = reason, OutputCardId = output };
            });
        }
    }

    /// <summary>
    /// Lab C Phase 1.5 (MCP harvest_node): เก็บเกี่ยว harvestable node ที่ใกล้ผู้เล่น
    /// ที่สุดในรัศมีของ NodeHarvestSystem — delegate งานทั้งหมดให้
    /// NodeHarvestSystem.TryHarvestNearest (auto-pick tool จาก inventory เมื่อ
    /// ToolItemId ว่าง) แล้ว map HarvestResult → HarvestNodeResponse
    /// publish chain (inventory → UI re-render, NodeHarvestedMessage) แตะ Unity
    /// API จึงต้อง enqueue ไป main thread เหมือน handler อื่น
    /// </summary>
    public class HarvestNodeHandler : IAsyncRequestHandler<HarvestNodeRequest, HarvestNodeResponse>
    {
        private readonly NodeHarvestSystem _harvest;
        private readonly McpMainThreadDispatcher _mainThread;

        public HarvestNodeHandler(NodeHarvestSystem harvest, McpMainThreadDispatcher mainThread)
        {
            _harvest = harvest;
            _mainThread = mainThread;
        }

        public async UniTask<HarvestNodeResponse> InvokeAsync(HarvestNodeRequest request, CancellationToken cancellationToken = default)
        {
            return await _mainThread.EnqueueAsync(() =>
            {
                var result = _harvest.TryHarvestNearest();
                return new HarvestNodeResponse
                {
                    Success = result.Success,
                    FailureReason = result.FailureReason ?? "",
                    NodeId = result.NodeId ?? "",
                    ItemId = result.ItemId ?? "",
                    Count = result.Count,
                    Depleted = result.Depleted,
                    RegrowSeconds = result.RegrowSeconds,
                };
            });
        }
    }

    public class AwaitNextEventHandler : IAsyncRequestHandler<AwaitNextEventRequest, AwaitNextEventResponse>
    {
        private readonly WorldEventSystem _worldEvents;
        public AwaitNextEventHandler(WorldEventSystem worldEvents) => _worldEvents = worldEvents;

        public UniTask<AwaitNextEventResponse> InvokeAsync(AwaitNextEventRequest request, CancellationToken cancellationToken = default)
        {
            // Lab A: naive poll; Lab E should replace with a proper async wait
            // (signalled from WorldEventSystem.Tick) so this doesn't busy-loop.
            if (_worldEvents.TryDequeue(out var evt))
                return UniTask.FromResult(new AwaitNextEventResponse { TimedOut = false, EventId = evt.Id, Group = evt.Group, DisplayText = evt.DisplayText });

            return UniTask.FromResult(new AwaitNextEventResponse { TimedOut = true });
        }
    }

    public class AccuseNpcHandler : IAsyncRequestHandler<AccuseNpcRequest, AccuseNpcResponse>
    {
        private readonly DeductionSystem _deduction;
        private readonly McpMainThreadDispatcher _mainThread;

        public AccuseNpcHandler(DeductionSystem deduction, McpMainThreadDispatcher mainThread)
        {
            _deduction = deduction;
            _mainThread = mainThread;
        }

        public async UniTask<AccuseNpcResponse> InvokeAsync(AccuseNpcRequest request, CancellationToken cancellationToken = default)
        {
            return await _mainThread.EnqueueAsync(() =>
            {
                var (correct, win, loss, text) = _deduction.Accuse(request.TargetNpcId);
                return new AccuseNpcResponse { WasCorrect = correct, GameOverWin = win, GameOverLoss = loss, ResultText = text };
            });
        }
    }

    public class GetGameStateHandler : IAsyncRequestHandler<GetGameStateRequest, GetGameStateResponse>
    {
        private readonly GameStateProvider _stateProvider;
        public GetGameStateHandler(GameStateProvider stateProvider) => _stateProvider = stateProvider;

        public UniTask<GetGameStateResponse> InvokeAsync(GetGameStateRequest request, CancellationToken cancellationToken = default)
        {
            return UniTask.FromResult(new GetGameStateResponse { Player = _stateProvider.GetPlayer() });
        }
    }

    public class GetVisibleNpcsHandler : IAsyncRequestHandler<GetVisibleNpcsRequest, GetVisibleNpcsResponse>
    {
        private readonly DeductionSystem _deduction;
        private readonly GameStateProvider _stateProvider;

        public GetVisibleNpcsHandler(DeductionSystem deduction, GameStateProvider stateProvider)
        {
            _deduction = deduction;
            _stateProvider = stateProvider;
        }

        public UniTask<GetVisibleNpcsResponse> InvokeAsync(GetVisibleNpcsRequest request, CancellationToken cancellationToken = default)
        {
            var npcs = _deduction.GetObservableNpcsAt(_stateProvider.GetPlayer().CurrentLocationId);
            return UniTask.FromResult(new GetVisibleNpcsResponse { Npcs = npcs });
        }
    }

    /// <summary>
    /// Clue System v2 (d): ⚠️ BREAKING — response เป็น List<ClueBoardEntry> แล้ว
    /// (เดิม: List<string> CollectedClueCardIds — AI VTuber ต้อง adapt)
    /// Loop CollectedClueInstanceIds → ClueInstance (registry กลาง) → ClueBoardEntry
    /// พร้อม resolve DisplayName/Reliability จาก ClueDef + กรอง WitnessNpcIds
    /// </summary>
    public class GetClueBoardHandler : IAsyncRequestHandler<GetClueBoardRequest, GetClueBoardResponse>
    {
        private readonly GameStateProvider _stateProvider;
        private readonly NpcDirectorSystem _npcDirector;
        private readonly LubanDataService _data;

        public GetClueBoardHandler(GameStateProvider stateProvider, NpcDirectorSystem npcDirector,
            LubanDataService dataService)
        {
            _stateProvider = stateProvider;
            _npcDirector = npcDirector;
            _data = dataService;
        }

        public UniTask<GetClueBoardResponse> InvokeAsync(GetClueBoardRequest request, CancellationToken cancellationToken = default)
        {
            var player = _stateProvider.GetPlayer();
            var entries = new List<ClueBoardEntry>(player.CollectedClueInstanceIds.Count);

            foreach (var instanceId in player.CollectedClueInstanceIds)
            {
                if (!_stateProvider.AllClueInstances.TryGetValue(instanceId, out var clue))
                    continue; // instance หายจาก registry (round reset?) — ข้าม ไม่ crash

                entries.Add(new ClueBoardEntry
                {
                    InstanceId = clue.InstanceId,
                    DisplayName = ResolveDisplayName(clue.DefId),
                    Reliability = ResolveReliability(clue.DefId),
                    LocationId = clue.LocationId,
                    WitnessNpcIds = FilterWitnesses(clue),
                });
            }

            return UniTask.FromResult(new GetClueBoardResponse { Entries = entries });
        }

        /// <summary>
        /// ⚠️ Information Hiding ตาม spec: กรองแค่ exclude killer/source + IsAlive
        /// (player_local คงไว้เสมอ — player เป็นพยานของตัวเองได้หลัง investigate)
        /// ไม่มีระบบ "ไม่เคย observe" — ไม่มี sighting log จริงในเกม
        /// </summary>
        internal List<string> FilterWitnesses(ClueInstance clue)
        {
            return clue.WitnessNpcIds
                .Where(id => id == GameStateProvider.LocalPlayerId ||
                             (_npcDirector.Npcs.TryGetValue(id, out var npc) && npc.IsAlive))
                .ToList();
        }

        internal string ResolveDisplayName(string defId)
            => _data.ClueDefs.TryGetValue(defId, out var def) ? def.DisplayName : defId;

        internal string ResolveReliability(string defId)
            => _data.ClueDefs.TryGetValue(defId, out var def) ? def.Reliability.ToString() : string.Empty;
    }

    /// <summary>
    /// Clue System v2 (d): get_clue_graph — graph view ของ clue board เดียวกัน
    /// Nodes: clue ที่ player เก็บ (type="clue", label=DisplayName) + witness หลังกรอง (type="npc", label=id)
    /// Edges: clue → witness (relation="witnessed")
    /// ⚠️ Information Hiding: ไม่มี SourceActorId ใน nodes/edges เด็ดขาด
    /// </summary>
    public class GetClueGraphHandler : IAsyncRequestHandler<GetClueGraphRequest, GetClueGraphResponse>
    {
        // ⚠️ ผูกผ่าน interface MessagePipe (IAsyncRequestHandler<...>) ไม่ใช่ concrete class —
        // VContainer register เฉพาะ interface mapping ตอน RegisterAsyncRequestHandler
        private readonly IAsyncRequestHandler<GetClueBoardRequest, GetClueBoardResponse> _board;
        private readonly GameStateProvider _stateProvider;

        public GetClueGraphHandler(IAsyncRequestHandler<GetClueBoardRequest, GetClueBoardResponse> board,
            GameStateProvider stateProvider)
        {
            _board = board;
            _stateProvider = stateProvider;
        }

        public UniTask<GetClueGraphResponse> InvokeAsync(GetClueGraphRequest request, CancellationToken cancellationToken = default)
        {
            var board = _board.InvokeAsync(new GetClueBoardRequest()).GetAwaiter().GetResult();

            var nodes = new List<GraphNode>();
            var edges = new List<GraphEdge>();
            var seenWitnesses = new HashSet<string>();

            foreach (var entry in board.Entries)
            {
                nodes.Add(new GraphNode { Id = entry.InstanceId, Type = "clue", Label = entry.DisplayName });

                foreach (var witnessId in entry.WitnessNpcIds)
                {
                    if (seenWitnesses.Add(witnessId))
                        nodes.Add(new GraphNode { Id = witnessId, Type = "npc", Label = witnessId });

                    edges.Add(new GraphEdge { From = entry.InstanceId, To = witnessId, Relation = "witnessed" });
                }
            }

            return UniTask.FromResult(new GetClueGraphResponse { Nodes = nodes, Edges = edges });
        }
    }

    /// <summary>
    /// Clue System v2 (e) pin workspace: get_pinned_clues — รายการ pin เดียวกับที่กระดาน
    /// ในเกมแสดง (อ่านจาก CluePinState singleton เดียวกับที่ ClueBoardPresenter เขียน —
    /// state เดียวกัน ไม่มีสำเนา)
    /// clue pin → resolve ผ่าน GetClueBoardHandler เดิม (witness filter เดิม รวม player_local)
    /// npc pin → NpcDirectorSystem (โซนที่ player เห็นจริง + อาลิไบคร่าว ๆ = เบาะแสที่เป็นพยาน)
    /// pin ของ node ที่หายไปจากเกม (clue ถูกถอน/npc โดนลบ) ไม่ถูกส่งกลับ — ไม่มี stale หลุด
    /// ⚠️ Information Hiding: เนื้อหาเดียวกับ popup บนกระดาน — ไม่มี SourceActorId/killer Role
    /// </summary>
    public class GetPinnedCluesHandler : IAsyncRequestHandler<GetPinnedCluesRequest, GetPinnedCluesResponse>
    {
        private readonly CluePinState _pins;
        private readonly IAsyncRequestHandler<GetClueBoardRequest, GetClueBoardResponse> _board;
        private readonly NpcDirectorSystem _npcDirector;

        public GetPinnedCluesHandler(CluePinState pins,
            IAsyncRequestHandler<GetClueBoardRequest, GetClueBoardResponse> board,
            NpcDirectorSystem npcDirector)
        {
            _pins = pins;
            _board = board;
            _npcDirector = npcDirector;
        }

        public UniTask<GetPinnedCluesResponse> InvokeAsync(GetPinnedCluesRequest request, CancellationToken cancellationToken = default)
        {
            // NodeId ว่าง = ทั้งหมดตามลำดับ pin (MCP tool); ใส่ id = node เดียว (presenter/reuse)
            IEnumerable<string> ids = string.IsNullOrEmpty(request.NodeId)
                ? _pins.PinnedNodeIds
                : new[] { request.NodeId };

            // GetClueGraphHandler ใช้ pattern เดียวกัน — InvokeAsync ของ board sync-complete เสมอ
            var board = _board.InvokeAsync(new GetClueBoardRequest()).GetAwaiter().GetResult();
            var pinned = new List<PinnedNodeEntry>();

            foreach (var nodeId in ids)
            {
                ClueBoardEntry clueEntry = null;
                foreach (var e in board.Entries)
                    if (e != null && e.InstanceId == nodeId) { clueEntry = e; break; }

                if (clueEntry != null)
                {
                    pinned.Add(new PinnedNodeEntry
                    {
                        NodeId = clueEntry.InstanceId,
                        Type = "clue",
                        DisplayName = clueEntry.DisplayName,
                        Reliability = clueEntry.Reliability,
                        LocationId = clueEntry.LocationId,
                        WitnessNpcIds = clueEntry.WitnessNpcIds ?? new List<string>(),
                    });
                    continue;
                }

                // npc pin — ถ้า npc หายไปเอง (โดนลบ) ก็ข้าม (ไม่คืน stale)
                if (!_npcDirector.Npcs.TryGetValue(nodeId, out var npc)) continue;

                var witnessed = new List<string>();
                foreach (var e in board.Entries)
                    if (e != null && e.WitnessNpcIds != null && e.WitnessNpcIds.Contains(nodeId))
                        witnessed.Add($"{e.DisplayName} @ {e.LocationId}");

                pinned.Add(new PinnedNodeEntry
                {
                    NodeId = nodeId,
                    Type = "npc",
                    Zone = npc.CurrentLocationId,
                    IsAlive = npc.IsAlive,
                    WitnessedClues = witnessed,
                });
            }

            return UniTask.FromResult(new GetPinnedCluesResponse { Pinned = pinned });
        }
    }

    /// <summary>
    /// Clue System v2 (e): set_pinned_clue — AI pin/unpin node บนกระดานเองได้
    /// Explicit set semantics (Pinned = true/false) ไม่ใช่ toggle — idempotent ปลอดภัยกับ retry
    /// ⚠️ Validation: NodeId ต้องเป็น node ในกราฟจริง (get_clue_graph — universe เดียวกับ
    /// ที่ board วาด หลัง filter) กัน pin id ปลอม/หมดอายุ; idempotent: pin ซ้ำ/unpin ที่ไม่ได้ pin = OK
    /// ⚠️ Main-thread: CluePinState.Changed ถูก presenter subscribe (UI refresh) — mutate
    /// ผ่าน McpMainThreadDispatcher เหมือน mutating handlers อื่น
    /// ⚠️ Response แนบ PinnedNodeIds ล่าสุดทุกครั้ง — AI แก้ model ใน step เดียว
    /// </summary>
    public class SetPinnedClueHandler : IAsyncRequestHandler<SetPinnedClueRequest, SetPinnedClueResponse>
    {
        private readonly CluePinState _pins;
        private readonly IAsyncRequestHandler<GetClueGraphRequest, GetClueGraphResponse> _graph;
        private readonly McpMainThreadDispatcher _mainThread;

        public SetPinnedClueHandler(CluePinState pins,
            IAsyncRequestHandler<GetClueGraphRequest, GetClueGraphResponse> graph,
            McpMainThreadDispatcher mainThread)
        {
            _pins = pins;
            _graph = graph;
            _mainThread = mainThread;
        }

        public UniTask<SetPinnedClueResponse> InvokeAsync(SetPinnedClueRequest request, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(request.NodeId))
                return UniTask.FromResult(new SetPinnedClueResponse
                {
                    Success = false,
                    FailureReason = "missing_node_id",
                    PinnedNodeIds = _pins.PinnedNodeIds.ToList(),
                });

            // ตรวจว่า node มีจริงในกราฟ (board หลัง filter — เท่ากับสิ่งที่ AI เห็นผ่าน get_clue_graph)
            var graph = _graph.InvokeAsync(new GetClueGraphRequest()).GetAwaiter().GetResult();
            var exists = graph.Nodes.Any(n => n != null && n.Id == request.NodeId);
            if (!exists)
                return UniTask.FromResult(new SetPinnedClueResponse
                {
                    Success = false,
                    FailureReason = "unknown_node",
                    PinnedNodeIds = _pins.PinnedNodeIds.ToList(),
                });

            // mutate บน main thread (presenter subscribe Changed → UI refresh)
            return _mainThread.EnqueueAsync(() =>
            {
                var wasPinned = _pins.IsPinned(request.NodeId);
                if (request.Pinned == wasPinned)
                {
                    // idempotent: pin ซ้ำ / unpin ที่ไม่ได้ pin = สำเร็จ ไม่ต้องทำอะไร
                    // (FailureReason เป็น informational — response ยัง Success)
                    return new SetPinnedClueResponse
                    {
                        Success = true,
                        FailureReason = request.Pinned ? "already_pinned" : "not_pinned",
                        PinnedNodeIds = _pins.PinnedNodeIds.ToList(),
                    };
                }

                // Toggle = set/unset เป๊ะ (state ก่อนหน้าตรงข้ามแล้ว) — ไม่มีเพดานจำนวน
                // (redesign (f): workspace semantics — pin ได้เท่าไหร่ก็ได้)
                _pins.Toggle(request.NodeId);
                return new SetPinnedClueResponse
                {
                    Success = true,
                    PinnedNodeIds = _pins.PinnedNodeIds.ToList(),
                };
            });
        }
    }

    /// <summary>
    /// Clue System v2 (c): investigate_clue — ค้นหา clue instance ณ ตำแหน่งปัจจุบันของ
    /// player ฝั่ง server เท่านั้น
    /// ⚠️ Security: InvestigateClueRequest เป็น empty request โดยตั้งใจ — ห้ามรับ
    /// LocationId จาก caller ป้องกัน AI สำรวจข้ามโซน (บั๊กเดิมที่เคยแก้ใน
    /// ExplorationSystem)
    /// Information Hiding: คืนเฉพาะ instance id ของ clue ที่เจอ (เห็นได้) —
    /// ไม่มี SourceActorId/WitnessNpcIds ใน response เด็ดขาด
    /// </summary>
    public class InvestigateClueHandler : IAsyncRequestHandler<InvestigateClueRequest, InvestigateClueResponse>
    {
        private readonly GameStateProvider _stateProvider;
        private readonly LubanDataService _data;
        private readonly McpMainThreadDispatcher _mainThread;
        private readonly System.Random _rng = new();

        public InvestigateClueHandler(GameStateProvider stateProvider, LubanDataService dataService,
            McpMainThreadDispatcher mainThread)
        {
            _stateProvider = stateProvider;
            _data = dataService;
            _mainThread = mainThread;
        }

        public UniTask<InvestigateClueResponse> InvokeAsync(InvestigateClueRequest request, CancellationToken cancellationToken = default)
        {
            // Threading (Lab B Phase 6 pattern): handler ถูก invoke บน TCP background
            // thread — mutate state (CollectedClueInstanceIds) + อ่าน registry ต้อง
            // รันบน main thread เสมอ ไม่งั้น race กับ game tick
            return _mainThread.EnqueueAsync(() =>
            {
                // ⚠️ ใช้ player location ฝั่ง server เท่านั้น — request ไม่มีพารามิเตอร์ให้แก้
                var locationId = _stateProvider.GetPlayer().CurrentLocationId;

                // หา clue ทั้งหมดใน location นี้ที่ยังไม่ถูกเก็บ
                var cluesHere = _stateProvider.AllClueInstances.Values
                    .Where(c => c.LocationId == locationId
                             && !_stateProvider.GetPlayer().CollectedClueInstanceIds.Contains(c.InstanceId))
                    .ToList();

                if (cluesHere.Count == 0)
                    return new InvestigateClueResponse { Success = false, FailureReason = "no_clue_at_location" };

                // VisibleToBystanders=true → เห็นทันที; false → roll 50%
                // (ค้นหาเรียงตามลำดับ registry — ตัวแรกที่ "มองเห็นได้" ในรอบนี้)
                ClueInstance found = null;
                foreach (var c in cluesHere)
                {
                    if (!_data.ClueDefs.TryGetValue(c.DefId, out var def)) continue;
                    if (def.VisibleToBystanders || _rng.NextDouble() < 0.5)
                    {
                        found = c;
                        break;
                    }
                }

                if (found == null)
                    return new InvestigateClueResponse { Success = false, FailureReason = "investigation_failed" };

                // เก็บเข้า inventory ของ player (กันซ้ำ — instance เดียวเก็บครั้งเดียว)
                var player = _stateProvider.GetPlayer();
                if (!player.CollectedClueInstanceIds.Contains(found.InstanceId))
                    player.CollectedClueInstanceIds.Add(found.InstanceId);

                return new InvestigateClueResponse { Success = true, FoundInstanceId = found.InstanceId };
            });
        }
    }

    public class MoveToLocationHandler : IAsyncRequestHandler<MoveToLocationRequest, MoveToLocationResponse>
    {
        private readonly GameStateProvider _stateProvider;
        private readonly LubanDataService _data;
        private readonly IPublisher<PlayerLocationChangedMessage> _playerLocationPublisher;
        private readonly McpMainThreadDispatcher _mainThread;
        private readonly WorldBounds _bounds; // คลมจุดหมายการเลื่อนอยู่ในกรอบเล่นเดิม
        private readonly PlayerAutoMoveSystem _autoMove; // จุด Begin/Cancel เดินอัตโนมัติ

        /// <summary>เวลาสูงสุด (วินาที) ที่รอเดินถึงปลายทาง — เกินแล้วคืน move_timeout
        /// (public ให้ PlayMode test ตั้งค่าสั้นเพื่อทดสอบ failure path แบบ deterministic)</summary>
        public float MoveTimeoutSeconds = 30f;

        public MoveToLocationHandler(GameStateProvider stateProvider, LubanDataService data,
            IPublisher<PlayerLocationChangedMessage> playerLocationPublisher, McpMainThreadDispatcher mainThread,
            WorldBounds bounds, PlayerAutoMoveSystem autoMove)
        {
            _stateProvider = stateProvider;
            _data = data;
            _playerLocationPublisher = playerLocationPublisher;
            _mainThread = mainThread;
            _bounds = bounds;
            _autoMove = autoMove;
        }

        public async UniTask<MoveToLocationResponse> InvokeAsync(MoveToLocationRequest request, CancellationToken cancellationToken = default)
        {
            // ---- Phase 1 (main thread): validate + Begin auto-move — ยังไม่เปลี่ยนโซน ----
            // publish chain (ChibiSpawnerView spawn/despawn chibi) แตะ Unity API จึงต้อง enqueue
            // หมายเหตุ: Begin แทนที่การเดินที่กำลังรันอยู่เสมอ (redirect กลางทางได้) —
            // handler ของการเดินเก่าจะตรวจเจอผ่าน MoveGeneration แล้วคืน superseded
            var setup = await _mainThread.EnqueueAsync(() =>
            {
                if (!_data.LocationDefs.TryGetValue(request.LocationId, out var targetDef))
                    return (Success: false, FailureReason: "unknown_location", OldLocationId: "", Gen: 0);

                var current = _stateProvider.GetPlayer().CurrentLocationId;
                if (!string.IsNullOrEmpty(current)
                    && _data.LocationDefs.TryGetValue(current, out var currentDef)
                    && currentDef.ConnectedLocationIds != null
                    && !currentDef.ConnectedLocationIds.Contains(request.LocationId))
                {
                    return (Success: false, FailureReason: "not_connected", OldLocationId: current, Gen: 0);
                }

                // ตั้งเป้าให้ PlayerAutoMoveSystem (Single Source of Truth: ระบบนี้
                // เป็นคนเดียวที่เขียน PositionX/Y ระหว่างเดิน) — ยังไม่ set CurrentLocationId
                var player = _stateProvider.GetPlayer();
                var gen = _autoMove.Begin(
                    System.Math.Clamp(targetDef.WorldX, _bounds.MinX, _bounds.MaxX),
                    System.Math.Clamp(targetDef.WorldY, _bounds.MinY, _bounds.MaxY));
                player.FacingRight = player.TargetX > player.PositionX;
                return (Success: true, FailureReason: "", OldLocationId: current, Gen: gen);
            });

            if (!setup.Success)
                return new MoveToLocationResponse { Success = false, FailureReason = setup.FailureReason };

            // ---- Phase 2: รอเดินถึงจริง หรือ ถูกแทนที่/ยกเลิกกลางทาง ----
            // PlayerAutoMoveSystem.Tick ปิด IsAutoMoving เองเมื่อระยะ ≤ 0.1;
            // Begin/Cancel ของคำสั่งอื่น bump MoveGeneration → predicate เป็นจริงทันที
            var gen = setup.Gen;
            var finished = await _mainThread.WaitUntilAsync(
                () => !_stateProvider.GetPlayer().IsAutoMoving || _autoMove.MoveGeneration != gen,
                MoveTimeoutSeconds);

            // ---- Phase 3 (main thread): จบการเดินแบบไหน ปฏิบัติตามนั้น ----
            return await _mainThread.EnqueueAsync(() =>
            {
                if (!finished || _autoMove.MoveGeneration != gen)
                {
                    // ถูก supersede (redirect/cancel กลางทาง) หรือ timeout —
                    // ห้าม commit โซน: การเดินปัจจุบันไม่ใช่ของ request นี้แล้ว
                    // (timeout: ปลด lock ไม่งั้นผู้เล่นค้าง — movement งดรับ input ตลอดไป)
                    if (!finished)
                    {
                        var stuck = _stateProvider.GetPlayer();
                        stuck.IsAutoMoving = false;
                        stuck.Activity = NpcActivityState.Idle;
                    }
                    return new MoveToLocationResponse { Success = false, FailureReason = finished ? "superseded" : "move_timeout" };
                }

                var player = _stateProvider.GetPlayer();
                player.CurrentLocationId = request.LocationId;

                // Lab B: publish เพื่อให้ Visual layer (ChibiSpawnerView / WorldItemSystem)
                // เปลี่ยนฉากหลังเดินถึงจริงเท่านั้น — ไม่ใช่ตอนเริ่มเดิน
                _playerLocationPublisher.Publish(new PlayerLocationChangedMessage
                {
                    OldLocationId = setup.OldLocationId,
                    NewLocationId = request.LocationId
                });

                return new MoveToLocationResponse { Success = true };
            });
        }
    }

    /// <summary>
    /// cancel_move (MCP): ยกเลิกการเดินอัตโนมัติที่กำลังรันอยู่ทันที — หยุดตรงนั้น
    /// (ไม่เปลี่ยนตำแหน่ง/โซน) ปลดล็อกคีย์บอร์ดเฟรมถัดไป; ผู้เล่นยังอยู่โซนเดิม
    /// จนกว่าจะสั่ง move ใหม่ ผ่าน PlayerAutoMoveSystem.Cancel (generation bump →
    /// waiter ของ move_to_location ที่ยังค้างจะได้ superseded เอง)
    /// </summary>
    public class CancelMoveHandler : IAsyncRequestHandler<CancelMoveRequest, CancelMoveResponse>
    {
        private readonly PlayerAutoMoveSystem _autoMove;
        private readonly McpMainThreadDispatcher _mainThread;

        public CancelMoveHandler(PlayerAutoMoveSystem autoMove, McpMainThreadDispatcher mainThread)
        {
            _autoMove = autoMove;
            _mainThread = mainThread;
        }

        public async UniTask<CancelMoveResponse> InvokeAsync(CancelMoveRequest request, CancellationToken cancellationToken = default)
        {
            // Cancel แตะ Unity state (IsAutoMoving/Activity) — marshal ไป main thread
            // ตามธรรมเนียม handler อื่น (Test C fix: handler invoke บน TCP thread)
            return await _mainThread.EnqueueAsync(() =>
            {
                var cancelled = _autoMove.Cancel();
                return new CancelMoveResponse
                {
                    Success = cancelled,
                    FailureReason = cancelled ? "" : "not_moving",
                };
            });
        }
    }

    /// <summary>
    /// Phase 4 (Player-as-Killer): รองรับ weapon card ผ่าน NpcDirectorSystem.TryEliminate
    /// Order of Operations: ตรวจ card → ตรวจ targeting → เช็คเงื่อนไข (dry-run) → ค่อยหักการ์ด
    /// → ดำเนินการจริง — เช็คก่อนหักเสมอเพื่อกันการ์ดหายฟรี (Safe UX)
    /// </summary>
    public class UseCardHandler : IAsyncRequestHandler<UseCardRequest, UseCardResponse>
    {
        private readonly GameStateProvider _stateProvider;
        private readonly CardInventorySystem _inventory;
        private readonly LubanDataService _data;
        private readonly NpcDirectorSystem _npcDirector; // ใหม่ Phase 4 — inject ผ่าน constructor
        private readonly McpMainThreadDispatcher _mainThread;

        public UseCardHandler(GameStateProvider stateProvider, CardInventorySystem inventory,
            LubanDataService data, NpcDirectorSystem npcDirector, McpMainThreadDispatcher mainThread)
        {
            _stateProvider = stateProvider;
            _inventory = inventory;
            _data = data;
            _npcDirector = npcDirector;
            _mainThread = mainThread;
        }

        public async UniTask<UseCardResponse> InvokeAsync(UseCardRequest request, CancellationToken cancellationToken = default)
        {
            // publish chain (inventory → UI, eliminate → chibi despawn) แตะ Unity API
            return await _mainThread.EnqueueAsync(() =>
            {
                if (!_data.CardDefs.TryGetValue(request.CardId, out var def))
                    return new UseCardResponse { Success = false, FailureReason = "unknown_card" };

                // Phase 4: ตรวจ targeting requirement ก่อน
                if (def.TargetType == CardTargetType.SingleTarget && string.IsNullOrEmpty(request.TargetId))
                    return new UseCardResponse { Success = false, FailureReason = "missing_target" };
                if (def.TargetType == CardTargetType.Self && !string.IsNullOrEmpty(request.TargetId))
                    return new UseCardResponse { Success = false, FailureReason = "invalid_target_type" };

                // Phase 4 (Safe UX): เช็คเงื่อนไข elimination ก่อนหักการ์ด — CanEliminate เป็น
                // dry-run ไม่ mutate state ทำให้การ์ดไม่หายเมื่อลงมือไม่สำเร็จ (witnessed ฯลฯ)
                if (def.EffectType == CardEffectType.Eliminate)
                {
                    var player = _stateProvider.GetPlayer();
                    var (canEliminate, failReason) = _npcDirector.CanEliminate(
                        GameStateProvider.LocalPlayerId, request.TargetId, player.CurrentLocationId);
                    if (!canEliminate)
                        return new UseCardResponse { Success = false, FailureReason = failReason };
                }

                // ผ่านเงื่อนไขทั้งหมดแล้วค่อยหักการ์ด
                if (!_inventory.TryConsume(request.CardId, 1))
                    return new UseCardResponse { Success = false, FailureReason = "not_in_inventory" };

                // ดำเนินการจริง
                switch (def.EffectType)
                {
                    case CardEffectType.Eliminate:
                    {
                        var player = _stateProvider.GetPlayer();
                        var (success, reason) = _npcDirector.TryEliminate(
                            GameStateProvider.LocalPlayerId, request.TargetId, player.CurrentLocationId);
                        if (!success)
                            return new UseCardResponse { Success = false, FailureReason = reason };
                        return new UseCardResponse { Success = true, ResultText = $"eliminated_{request.TargetId}" };
                    }

                    case CardEffectType.StatDelta:
                    default:
                    {
                        if (def.StatEffect != null)
                        {
                            var player = _stateProvider.GetPlayer();
                            foreach (var kv in def.StatEffect)
                            {
                                switch (kv.Key)
                                {
                                    case "Hunger": player.Hunger = System.Math.Clamp(player.Hunger + kv.Value, 0f, 100f); break;
                                    case "Thirst": player.Thirst = System.Math.Clamp(player.Thirst + kv.Value, 0f, 100f); break;
                                    case "Mood": player.Mood = System.Math.Clamp(player.Mood + kv.Value, 0f, 100f); break;
                                    case "Fatigue": player.Fatigue = System.Math.Clamp(player.Fatigue + kv.Value, 0f, 100f); break;
                                }
                            }
                        }
                        return new UseCardResponse { Success = true };
                    }
                }
            });
        }
    }

    public class CallMeetingHandler : IAsyncRequestHandler<CallMeetingRequest, CallMeetingResponse>
    {
        private readonly DeductionSystem _deduction;
        private readonly GameStateProvider _stateProvider;
        private readonly McpMainThreadDispatcher _mainThread;

        public CallMeetingHandler(DeductionSystem deduction, GameStateProvider stateProvider,
            McpMainThreadDispatcher mainThread)
        {
            _deduction = deduction;
            _stateProvider = stateProvider;
            _mainThread = mainThread;
        }

        public async UniTask<CallMeetingResponse> InvokeAsync(CallMeetingRequest request, CancellationToken cancellationToken = default)
        {
            // Lab A: meeting just snapshots whoever is currently visible; a real
            // "gather everyone" pause/summon step is a later lab.
            // (อ่าน state เปล่าๆ — แต่กัน race กับ main thread ที่ mutate dict ด้วย
            // เพราะ handler ถูก invoke บน TCP thread เหมือนกัน)
            return await _mainThread.EnqueueAsync(() =>
            {
                var npcs = _deduction.GetObservableNpcsAt(_stateProvider.GetPlayer().CurrentLocationId);
                return new CallMeetingResponse { Success = true, AttendingNpcs = npcs };
            });
        }
    }
}
