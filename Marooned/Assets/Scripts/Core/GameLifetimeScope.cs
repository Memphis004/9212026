using MessagePipe;
using Marooned.Shared;
using Marooned.Systems;
using Marooned.Systems.AI;
using Marooned.UI.Core;
using Marooned.UI.Presenters;
using Marooned.UI.Views;
using VContainer;
using VContainer.Unity;

namespace Marooned.Core
{
    /// <summary>
    /// Composition root. Same shape as the reference project's GameLifetimeScope:
    /// wires MessagePipe (in-process bus) + MessagePipe.Interprocess (TCP, Unity as
    /// host) + gameplay subsystems + the MCP-facing state provider.
    ///
    /// Fixed against MessagePipe.Interprocess's real Unity/VContainer API (the
    /// previous draft used made-up method names). Per the official README's
    /// Unity section:
    ///   var options = builder.RegisterMessagePipe();
    ///   var messagePipeBuilder = builder.ToMessagePipeBuilder();
    ///   var interprocessOptions = messagePipeBuilder.AddTcpInterprocess(host, port, cfg);
    ///   builder.RegisterAsyncRequestHandler&lt;TReq,TRes,THandler&gt;(options); // exposes the handler; TCP dispatch happens because HostAsServer=true
    ///
    /// Unity has no open-generics/auto-registration (IL2CPP), so every
    /// request/handler pair must be registered manually here — unlike McpBridge's
    /// plain .NET side, which gets IRemoteRequestHandler&lt;TReq,TRes&gt; for free
    /// once AddTcpInterprocess is configured as a client (HostAsServer=false).
    ///
    /// STILL VERIFY: exact overload names/order can drift between package
    /// versions. If `dotnet`/Unity reports a different signature, trust the
    /// compiler/IntelliSense over this comment and adjust — this is written
    /// against MessagePipe.Interprocess 1.8.2's public README sample, not a
    /// tested build.
    /// </summary>
    public class GameLifetimeScope : LifetimeScope
    {
        [UnityEngine.SerializeField] private string interprocessHost = "127.0.0.1";
        [UnityEngine.SerializeField] private int interprocessPort = 3216; // different port than reference project's 3215

        [UnityEngine.Header("Player Movement — ขอบเขตแผนที่รวม (Lab B Phase 6)")]
        // PlayerMovementSystem เป็น plain C# (ไม่ใช่ MonoBehaviour) ใส่ SerializeField
        // ไม่ได้ — scope เป็นผู้เก็บค่าแทนแล้ว inject ผ่าน WorldBounds ตอน Configure
        [UnityEngine.SerializeField] private float worldMinX = -11f;
        [UnityEngine.SerializeField] private float worldMaxX = 11f;
        [UnityEngine.SerializeField] private float worldMinY = -3.5f;
        [UnityEngine.SerializeField] private float worldMaxY = 5f;

        // TcpWorker เปิด TCP listener ค้างไว้ — ถ้าไม่ Dispose ตอนออก play mode,
        // accept-loop thread จะรั้ง socket ข้าม play session (session ถัดไป bind ไม่ได้:
        // "Only one usage of each socket address") — container dispose ของ VContainer
        // ไม่การันตีว่าครอบคลุม worker ที่ register ผ่าน MessagePipe interprocess
        // จึงเก็บ reference มาปิดเองใน OnDestroy
        private MessagePipe.Interprocess.Workers.TcpWorker _tcpWorker;

        protected override void Configure(IContainerBuilder builder)
        {
            var options = builder.RegisterMessagePipe();

            // Enables the MessagePipe Diagnostics window + GlobalMessagePipe helpers.
            builder.RegisterBuildCallback(c => GlobalMessagePipe.SetProvider(c.AsServiceProvider()));

            var messagePipeBuilder = builder.ToMessagePipeBuilder();
            var interprocessOptions = messagePipeBuilder.AddTcpInterprocess(interprocessHost, interprocessPort, tcp =>
            {
                tcp.HostAsServer = true;
            });

            // บังคับให้ VContainer สร้าง TcpWorker จริง ไม่ใช่แค่ register ไว้เฉยๆ
            builder.RegisterBuildCallback(container =>
            {
                container.Resolve<MessagePipe.Interprocess.Workers.TcpWorker>();
            });

            // builder.RegisterBuildCallback(container =>
            // {
            //     try
            //     {
            //         _tcpWorker = container.Resolve<MessagePipe.Interprocess.Workers.TcpWorker>();
            //     }
            //     catch (System.Exception ex)
            //     {
            //         _tcpWorker = null;
            //         UnityEngine.Debug.LogError(
            //             $"[GameLifetimeScope] TcpWorker ล้มเหลว (port {interprocessPort} อาจถูกใช้ค้างจาก session ก่อนหน้า) " +
            //             $"— MCP bridge จะต่อไม่ได้รอบนี้ แต่ระบบอื่น (UI, gameplay) ยังทำงานต่อได้ปกติ: {ex}");
            //     }
            // });

            // --- Gameplay subsystems ---
            builder.Register<SurvivalStatSystem>(Lifetime.Singleton).AsSelf();
            builder.Register<CardInventorySystem>(Lifetime.Singleton).AsSelf();
            builder.Register<CraftingSystem>(Lifetime.Singleton).AsSelf();
            builder.Register<ExplorationSystem>(Lifetime.Singleton).AsSelf();
            builder.Register<NpcDirectorSystem>(Lifetime.Singleton).AsSelf();
            builder.Register<DeductionSystem>(Lifetime.Singleton).AsSelf();
            builder.Register<WorldEventSystem>(Lifetime.Singleton).AsSelf();
            builder.Register<LubanDataService>(Lifetime.Singleton).AsSelf();

            // --- Player system (Lab B Phase 3) ---
            // input service (plain C# ticked by GameTickDriver) + movement + pickup
            builder.Register<PlayerInputService>(Lifetime.Singleton).AsSelf();
            // Lab B Phase 6: ขอบเขตเดินของ "แผนที่รวม" — แก้ค่าใน Inspector ของ
            // GameLifetimeScope (worldMinX/worldMaxX/worldMinY/worldMaxY)
            builder.RegisterInstance(new WorldBounds(worldMinX, worldMaxX, worldMinY, worldMaxY));
            builder.Register<PlayerMovementSystem>(Lifetime.Singleton).AsSelf();
            // Auto-Move (MoveToLocation fix): เดินผู้เล่นเข้าหา TargetX/Y ที่
            // MoveToLocationHandler ตั้งไว้ — GameTickDriver เรียก Tick ระหว่าง
            // survival กับ PlayerMovementSystem (ก่อน movement เสมอ เฟรมที่
            // ปิด IsAutoMoving movement กลับมารับคีย์บอร์ดได้ทันทีในเฟรมเดียวกัน)
            builder.Register<PlayerAutoMoveSystem>(Lifetime.Singleton).AsSelf();
            builder.Register<ItemPickupSystem>(Lifetime.Singleton).AsSelf();
            // WorldItemSystem เป็น IInitializable → EntryPoint เพื่อให้ spawn ชุดแรก
            // หลัง container build เสร็จ (subscribe PlayerLocationChangedMessage ภายใน)
            // .AsSelf() เพิ่มการ register ตัวคลาสเอง (RegisterEntryPoint พื้นฐาน
            // register เฉพาะ implemented interfaces ทำให้ Resolve<WorldItemSystem> ไม่ได้)
            builder.RegisterEntryPoint<WorldItemSystem>(Lifetime.Singleton).AsSelf();
            // Lab B Phase 6 (Test C fix): mutating MCP handlers ถูก invoke บน TCP
            // background thread — ต้องมาร์ชั่นงานกลับมา main thread ก่อนแตะ Unity API
            // (publish chain เช่น UI re-render / chibi spawn จะ throw ไม่งั้น)
            builder.RegisterEntryPoint<McpMainThreadDispatcher>(Lifetime.Singleton).AsSelf();

            // --- NPC AI (Lab C Phase 2 Step 4) ---
            // UtilityContext สร้างครั้งเดียว (ถือ Data/StateProvider) — สมอง AI ทั้งสอง
            // ฝั่งได้ context นี้ผ่าน constructor (inject จาก container ห้าม new เอง)
            // แล้ว NpcDirectorSystem รับสมองเข้าไปใช้ใน TickBehavior
            // กัน DI cycle: context ที่นี่จงใจ *ไม่* resolve NpcDirectorSystem ตอน build
            // (NpcDirectorSystem → AI → context → NpcDirectorSystem จะเป็น cycle) —
            // NpcDirectorSystem จะ Bind(this) เข้า context เองใน constructor (จุดเดียว)
            // AI อ่าน ctx.NpcDirector ตอน Tick เท่านั้น ซึ่งเกิดหลัง construct จบแล้วเสมอ
            builder.Register(c => new UtilityContext(
                c.Resolve<LubanDataService>(),
                c.Resolve<GameStateProvider>()), Lifetime.Singleton);
            builder.Register<InnocentUtilityAI>(Lifetime.Singleton).AsSelf();
            builder.Register<KillerPlanner>(Lifetime.Singleton).AsSelf();

            // --- Clue System v2 (b): generation pipeline ---
            // จุดเดียวที่สร้าง ClueInstance (TryGenerate) — kill-hook ของ
            // NpcDirectorSystem.SpawnClues เดิม hardcode card ids ถูกแทนด้วย pipeline นี้
            // ถือ UtilityContext (ไม่ใช่ NpcDirectorSystem ตรง ๆ) เพื่อกัน DI cycle:
            // NpcDirectorSystem → ClueGenerationSystem → UtilityContext → NpcDirectorSystem
            // (TryGenerate อ่าน ctx.NpcDirector ตอนเรียกจริงเท่านั้น — constructor ของ
            // director Bind(this) เข้า context เสมอ จึงไม่มีจังหวะ null)
            builder.Register<ClueGenerationSystem>(Lifetime.Singleton).AsSelf();

            // --- NPC movement (Lab C Phase 2) ---
            // เดิน NPC เข้าหา TargetX/Y ที่ถูกตั้งไว้ — การเลือก target เป็นหน้าที่
            // AI (Step 4) ระบบนี้เหลือหน้าที่เดินอย่างเดียว; GameTickDriver เรียก Tick
            // หลัง NpcDirectorSystem.Tick() เสมอ (target ที่ตั้งในเฟรมนี้ต้องถูกเดินทันที)
            builder.Register<NpcMovementSystem>(Lifetime.Singleton).AsSelf();

            // --- NPC survival motives (Lab C Phase 2.5A) ---
            // decay/drain stats 0-100 + motive hooks จาก NpcEliminatedMessage —
            // implement IDisposable (pattern WorldItemSystem) เพื่อ dispose subscription
            // GameTickDriver เรียก Tick ก่อน NpcDirectorSystem เสมอ (motive ใหม่ต้อง
            // พร้อมก่อน AI ตัดสินใจในเฟรมเดียวกัน)
            builder.Register<NpcSurvivalSystem>(Lifetime.Singleton).AsSelf();

            // --- NPC zone transition (Hybrid Transition Points Part 2) ---
            // FSM ข้ามโซนแบบเดินผ่านจุดเชื่อม (ZoneConnectionDef) — GameTickDriver
            // เรียก Tick หลัง NpcMovementSystem เสมอ (Movement เดินถึงจุดเชื่อม →
            // ระบบนี้เปลี่ยน phase/ข้ามโซนทันทีในเฟรมเดียว)
            builder.Register<NpcZoneTransitionSystem>(Lifetime.Singleton).AsSelf();

            // --- Biome scatter + tool-gathering (Lab C Phase 1) ---
            // BiomeScatterSystem คำนวณตำแหน่ง spawn อย่างเดียว (ไม่แตะ GameObject) —
            // publish BiomeChangedMessage ให้ BiomeScatterView (MonoBehaviour บน scene)
            // เป็นคน Instantiate/Destroy; WorldBounds ใช้ instance เดียวกับ movement
            builder.Register<BiomeScatterSystem>(Lifetime.Singleton).AsSelf();
            // NodeHarvestSystem: เก็บเกี่ยว harvestable node (E) — เช็ค tool กับ
            // HarvestableNodeDef.csv, yield เข้า inventory, publish NodeHarvestedMessage
            builder.Register<NodeHarvestSystem>(Lifetime.Singleton).AsSelf();

            // --- State provider consumed by MCP query handlers ---
            builder.Register<GameStateProvider>(Lifetime.Singleton).AsSelf();

            // --- MCP-facing request handlers (Bridge -> Unity, request/response) ---
            // Unity is the SERVER (HostAsServer = true above): registering the
            // async handler here is what makes it network-callable, no separate
            // "expose over TCP" call needed on the server side per the README.
            builder.RegisterAsyncRequestHandler<ExploreLocationRequest, ExploreLocationResponse, ExploreLocationHandler>(options);
            builder.RegisterAsyncRequestHandler<CraftCardRequest, CraftCardResponse, CraftCardHandler>(options);
            builder.RegisterAsyncRequestHandler<AwaitNextEventRequest, AwaitNextEventResponse, AwaitNextEventHandler>(options);
            builder.RegisterAsyncRequestHandler<AccuseNpcRequest, AccuseNpcResponse, AccuseNpcHandler>(options);
            builder.RegisterAsyncRequestHandler<GetGameStateRequest, GetGameStateResponse, GetGameStateHandler>(options);
            builder.RegisterAsyncRequestHandler<GetVisibleNpcsRequest, GetVisibleNpcsResponse, GetVisibleNpcsHandler>(options);
            builder.RegisterAsyncRequestHandler<GetClueBoardRequest, GetClueBoardResponse, GetClueBoardHandler>(options);

            // Clue System v2 (d): get_clue_graph — reuse GetClueBoardHandler ให้ witness
            // filtering/DisplayName resolve เกิดที่เดียว (information hiding เดียวกัน)
            builder.RegisterAsyncRequestHandler<GetClueGraphRequest, GetClueGraphResponse, GetClueGraphHandler>(options);
            builder.RegisterAsyncRequestHandler<MoveToLocationRequest, MoveToLocationResponse, MoveToLocationHandler>(options);
            // cancel_move: ยกเลิก auto-move ที่กำลังรัน (VTuber เปลี่ยนใจ/หยุดเพื่อสำรวจ)
            builder.RegisterAsyncRequestHandler<CancelMoveRequest, CancelMoveResponse, CancelMoveHandler>(options);
            builder.RegisterAsyncRequestHandler<UseCardRequest, UseCardResponse, UseCardHandler>(options);
            builder.RegisterAsyncRequestHandler<CallMeetingRequest, CallMeetingResponse, CallMeetingHandler>(options);
            // Lab C Phase 1.5 (harvest_node): เก็บเกี่ยว node ใกล้ผู้เล่น (auto-pick tool)
            builder.RegisterAsyncRequestHandler<HarvestNodeRequest, HarvestNodeResponse, HarvestNodeHandler>(options);
            // Clue System v2 (c): investigate_clue — ค้น clue ที่ตำแหน่งปัจจุบันของ player
            // (empty request — ห้ามรับ LocationId จาก caller กันสำรวจข้ามโซน)
            builder.RegisterAsyncRequestHandler<InvestigateClueRequest, InvestigateClueResponse, InvestigateClueHandler>(options);

            // --- UI root ---
            builder.RegisterEntryPoint<UIRoot>();

            // --- UI: Card Hand (Lab B Phase 5) ---
            // CardHandView ต้องอยู่ใน hierarchy ของ scope นี้ (CardHandSystem — สร้างโดย
            // Assets/Editor/UiSetupAutomation.cs) ไม่งั้น RegisterComponentInHierarchy
            // จะ throw ตอน container build
            builder.RegisterComponentInHierarchy<CardHandView>();
            // Lab B Phase 5 (click-to-use): ChibiSpawnerView ให้ event NpcClicked/WorldClicked
            // + SetTargetSelectionMode — CardHandPresenter ใช้เป็น "เลือกเป้าหมาย" ของ weapon card
            builder.RegisterComponentInHierarchy<ChibiSpawnerView>();
            // Presenter เป็น plain C# (IInitializable) — subscribe CardInventoryChangedMessage
            // แล้ว push RenderHand ให้ view; CardHandView ถูก inject จาก hierarchy registration บน
            // UseCardHandler resolve ผ่าน IAsyncRequestHandler<UseCardRequest, UseCardResponse>
            // (register ไว้แล้วด้านบน) — ผลลัพธ์ Success/Failure แสดงผ่าน CardHandView.ShowFeedback
            builder.RegisterEntryPoint<CardHandPresenter>(Lifetime.Singleton).AsSelf();
        }

        /// <summary>
        /// Play mode exit / scene teardown / app quit → ปิด TCP listener ทิ้ง
        /// (TcpWorker.Dispose = cancel accept/receive loop + SocketTcpServer.Dispose
        /// ซึ่งปิด listening socket) — กัน port 3216 ค้างข้าม play session
        /// </summary>
        protected override void OnDestroy()
        {
            try
            {
                _tcpWorker?.Dispose();
                if (_tcpWorker != null)
                    UnityEngine.Debug.Log($"[GameLifetimeScope] TcpWorker disposed — port {interprocessPort} released");
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[GameLifetimeScope] TcpWorker.Dispose ล้มเหลว (port {interprocessPort} อาจยังค้าง): {ex.Message}");
            }
            _tcpWorker = null;

            base.OnDestroy(); // DisposeCore → Container.Dispose ตามปกติ
        }
    }
}
