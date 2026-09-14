using Marooned.Shared;
using MessagePipe;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

var builder = Host.CreateApplicationBuilder(args);

// Stdio MCP uses stdout for JSON-RPC framing, so ALL logs must go to stderr,
// or the MCP client will fail to parse the stream.
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// --- MessagePipe + TCP interprocess client ---
// Unity is the server (HostAsServer = true, see GameLifetimeScope.cs) and must
// already be running (Play mode) before this process starts, or the TCP
// connection will fail. Host/port must match GameLifetimeScope's
// interprocessHost/interprocessPort (127.0.0.1:3216 by default).
builder.Services.AddMessagePipe()
    .AddTcpInterprocess("127.0.0.1", 3216, tcp =>
    {
        tcp.HostAsServer = false;
    });

// --- MCP server over stdio, tools auto-discovered from this assembly ---
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

namespace Marooned.McpBridge
{
    /// <summary>
    /// Read-only-ish query tools. GetVisibleNpcs / GetClueBoard only ever return
    /// what DeductionSystem.GetObservableNpcsAt builds server-side -- the true
    /// NpcRole never crosses this boundary, by construction (see design doc §5).
    /// </summary>
    [McpServerToolType]
    public class SurvivalQueryTools
    {
        private readonly IRemoteRequestHandler<GetGameStateRequest, GetGameStateResponse> _getGameState;
        private readonly IRemoteRequestHandler<GetVisibleNpcsRequest, GetVisibleNpcsResponse> _getVisibleNpcs;
        private readonly IRemoteRequestHandler<GetClueBoardRequest, GetClueBoardResponse> _getClueBoard;
        private readonly IRemoteRequestHandler<GetClueGraphRequest, GetClueGraphResponse> _getClueGraph;

        public SurvivalQueryTools(
            IRemoteRequestHandler<GetGameStateRequest, GetGameStateResponse> getGameState,
            IRemoteRequestHandler<GetVisibleNpcsRequest, GetVisibleNpcsResponse> getVisibleNpcs,
            IRemoteRequestHandler<GetClueBoardRequest, GetClueBoardResponse> getClueBoard,
            IRemoteRequestHandler<GetClueGraphRequest, GetClueGraphResponse> getClueGraph)
        {
            _getGameState = getGameState;
            _getVisibleNpcs = getVisibleNpcs;
            _getClueBoard = getClueBoard;
            _getClueGraph = getClueGraph;
        }

        [McpServerTool, Description("Get the player's current survival stats, inventory, location and active conditions.")]
        public async Task<string> GetGameState()
        {
            var res = await _getGameState.InvokeAsync(new GetGameStateRequest());
            var p = res.Player;
            var inv = string.Join(", ", p.Inventory.Count == 0
                ? new[] { "(empty)" }
                : p.Inventory.Select(kv => $"{kv.Key} x{kv.Value}"));
            var conditions = p.ActiveConditionCardIds.Count == 0 ? "none" : string.Join(", ", p.ActiveConditionCardIds);
            return $"Location: {p.CurrentLocationId} | Alive: {p.IsAlive}\n" +
                   $"Hunger: {p.Hunger:0} | Thirst: {p.Thirst:0} | Mood: {p.Mood:0} | Fatigue: {p.Fatigue:0}\n" +
                   $"Inventory: {inv}\n" +
                   $"Conditions: {conditions}\n" +
                   $"Wrong accusations so far: {p.WrongAccusations}";
        }

        [McpServerTool, Description("Get NPCs visible at the player's current location. Only shows what a bystander could actually observe -- never the true killer/innocent role.")]
        public async Task<string> GetVisibleNpcs()
        {
            var res = await _getVisibleNpcs.InvokeAsync(new GetVisibleNpcsRequest());
            if (res.Npcs.Count == 0) return "No NPCs visible here.";
            return string.Join("\n", res.Npcs.Select(n =>
            {
                var conditions = n.VisibleConditionCardIds.Count == 0 ? "none visible" : string.Join(", ", n.VisibleConditionCardIds);
                return $"{n.Id} | alive={n.IsAlive} | activity={n.Activity} | visible conditions: {conditions}";
            }));
        }

        [McpServerTool, Description("Get the player's clue board for deduction. ⚠️ Shape changed: returns one entry per collected clue instance with fields instance_id, display_name, reliability (Strong/Weak/RedHerring), location_id, and witness_npc_ids (already filtered -- dead NPCs and the perpetrator never appear). Use get_clue_graph for a node/edge view of the same data.")]
        public async Task<string> GetClueBoard()
        {
            var res = await _getClueBoard.InvokeAsync(new GetClueBoardRequest());
            return res.Entries.Count == 0
                ? "No clues collected yet."
                : string.Join("\n", res.Entries.Select(e =>
                    $"{e.InstanceId} | {e.DisplayName} | reliability={e.Reliability} | location={e.LocationId} | witnesses=[{string.Join(", ", e.WitnessNpcIds)}]"));
        }

        [McpServerTool, Description("Get the player's clue board as a graph: nodes are collected clues (type=clue) and their surviving witnesses (type=npc), edges are clue->witness with relation=witnessed. Same filtered data as get_clue_board -- perpetrators and dead NPCs never appear. Useful for reasoning about who saw what.")]
        public async Task<string> GetClueGraph()
        {
            var res = await _getClueGraph.InvokeAsync(new GetClueGraphRequest());
            if (res.Nodes.Count == 0)
                return "No clues collected yet (empty graph).";
            var nodes = string.Join(", ", res.Nodes.Select(n => $"{n.Id}({n.Type}:{n.Label})"));
            var edges = string.Join(", ", res.Edges.Select(e => $"{e.From} -[{e.Relation}]-> {e.To}"));
            return $"nodes: [{nodes}]\nedges: [{edges}]";
        }
    }

    /// <summary>Mutating tools -- each round-trips through Unity's request/response handlers.</summary>
    [McpServerToolType]
    public class SurvivalActionTools
    {
        private readonly IRemoteRequestHandler<ExploreLocationRequest, ExploreLocationResponse> _explore;
        private readonly IRemoteRequestHandler<CraftCardRequest, CraftCardResponse> _craft;
        private readonly IRemoteRequestHandler<MoveToLocationRequest, MoveToLocationResponse> _move;
        private readonly IRemoteRequestHandler<CancelMoveRequest, CancelMoveResponse> _cancelMove;
        private readonly IRemoteRequestHandler<UseCardRequest, UseCardResponse> _useCard;
        private readonly IRemoteRequestHandler<AwaitNextEventRequest, AwaitNextEventResponse> _awaitNextEvent;
        private readonly IRemoteRequestHandler<HarvestNodeRequest, HarvestNodeResponse> _harvestNode;

        public SurvivalActionTools(
            IRemoteRequestHandler<ExploreLocationRequest, ExploreLocationResponse> explore,
            IRemoteRequestHandler<CraftCardRequest, CraftCardResponse> craft,
            IRemoteRequestHandler<MoveToLocationRequest, MoveToLocationResponse> move,
            IRemoteRequestHandler<CancelMoveRequest, CancelMoveResponse> cancelMove,
            IRemoteRequestHandler<UseCardRequest, UseCardResponse> useCard,
            IRemoteRequestHandler<AwaitNextEventRequest, AwaitNextEventResponse> awaitNextEvent,
            IRemoteRequestHandler<HarvestNodeRequest, HarvestNodeResponse> harvestNode)
        {
            _explore = explore;
            _craft = craft;
            _move = move;
            _cancelMove = cancelMove;
            _useCard = useCard;
            _awaitNextEvent = awaitNextEvent;
            _harvestNode = harvestNode;
        }

        [McpServerTool, Description("Explore the player's current location. Returns any cards found; empty if the node is temporarily depleted.")]
        public async Task<string> ExploreLocation([Description("Location id to explore, e.g. 'beach'")] string locationId)
        {
            var res = await _explore.InvokeAsync(new ExploreLocationRequest { LocationId = locationId });
            if (!res.Success) return $"Could not explore '{locationId}'.";
            return res.FoundCardIds.Count == 0
                ? $"Explored '{locationId}' but found nothing this time."
                : $"Found: {string.Join(", ", res.FoundCardIds)}";
        }

        [McpServerTool, Description("Craft a recipe by id using cards currently in inventory.")]
        public async Task<string> CraftCard([Description("Recipe id, e.g. 'recipe_cook_fish'")] string recipeId)
        {
            var res = await _craft.InvokeAsync(new CraftCardRequest { RecipeId = recipeId });
            return res.Success ? $"Crafted: {res.OutputCardId}" : $"Craft failed: {res.FailureReason}";
        }

        [McpServerTool, Description("Move the player to a connected location id.")]
        public async Task<string> MoveToLocation([Description("Destination location id")] string locationId)
        {
            var res = await _move.InvokeAsync(new MoveToLocationRequest { LocationId = locationId });
            return res.Success ? $"Moved to {locationId}." : $"Move failed: {res.FailureReason}";
        }

        [McpServerTool, Description("Cancel the player's current auto-move (from move_to_location) immediately. The player stops where they are -- position and location do not change. Returns 'not_moving' if no walk is in progress.")]
        public async Task<string> CancelMove()
        {
            var res = await _cancelMove.InvokeAsync(new CancelMoveRequest());
            return res.Success ? "Move cancelled -- player stopped where they were." : $"Cancel failed: {res.FailureReason}";
        }

        [McpServerTool, Description("Consume/use a card from inventory (food, water, medicine, etc). For weapon cards (e.g. knife_basic) target_id is required -- the target NPC must be in the same location and there must be no other NPC witnessing, or the attempt fails and the card is not consumed.")]
        public async Task<string> UseCard(
            [Description("Card id to use")] string cardId,
            [Description("ID of the target NPC (required for weapon cards, e.g. 'npc_03'); omit for self-use cards")] string? targetId = null)
        {
            var res = await _useCard.InvokeAsync(new UseCardRequest { CardId = cardId, TargetId = targetId ?? string.Empty });
            if (!res.Success) return $"Could not use {cardId}: {res.FailureReason}";
            return string.IsNullOrEmpty(res.ResultText) ? $"Used {cardId}." : $"Used {cardId}: {res.ResultText}";
        }

        [McpServerTool, Description("Harvest the nearest harvestable node (tree, rock, berry bush) within reach of the player. Tools are picked automatically from inventory (e.g. tool_axe for trees); bare-handed works for nodes that need no tool. Returns the item gained, or the failure reason (no_node_in_range, missing_tool, regrowing).")]
        public async Task<string> HarvestNode([Description("Optional tool card id to use explicitly (e.g. 'tool_axe'); omit to auto-pick from inventory")] string? toolItemId = null)
        {
            var res = await _harvestNode.InvokeAsync(new HarvestNodeRequest { ToolItemId = toolItemId ?? string.Empty });
            if (!res.Success)
            {
                return res.NodeId.Length == 0
                    ? $"Harvest failed: {res.FailureReason}"
                    : $"Harvest failed ({res.NodeId}): {res.FailureReason}";
            }
            var toolNote = res.Depleted
                ? (res.RegrowSeconds > 0 ? $" Node depleted -- regrows in {res.RegrowSeconds}s." : " Node depleted permanently.")
                : "";
            return $"Harvested {res.NodeId}: +{res.Count} {res.ItemId}.{toolNote}";
        }

        [McpServerTool, Description("Block until the next world event fires (Survival or Social group), or time out.")]
        public async Task<string> AwaitNextEvent([Description("Seconds to wait before timing out")] int timeoutSeconds = 30)
        {
            var res = await _awaitNextEvent.InvokeAsync(new AwaitNextEventRequest { TimeoutSeconds = timeoutSeconds });
            return res.TimedOut ? "No event occurred within the timeout." : $"[{res.Group}] {res.DisplayText} (id={res.EventId})";
        }
    }

    /// <summary>Social-deduction specific tools.</summary>
    [McpServerToolType]
    public class DeductionTools
    {
        private readonly IRemoteRequestHandler<CallMeetingRequest, CallMeetingResponse> _callMeeting;
        private readonly IRemoteRequestHandler<AccuseNpcRequest, AccuseNpcResponse> _accuse;
        private readonly IRemoteRequestHandler<InvestigateClueRequest, InvestigateClueResponse> _investigateClue;

        public DeductionTools(
            IRemoteRequestHandler<CallMeetingRequest, CallMeetingResponse> callMeeting,
            IRemoteRequestHandler<AccuseNpcRequest, AccuseNpcResponse> accuse,
            IRemoteRequestHandler<InvestigateClueRequest, InvestigateClueResponse> investigateClue)
        {
            _callMeeting = callMeeting;
            _accuse = accuse;
            _investigateClue = investigateClue;
        }

        [McpServerTool, Description("Report a body / call an emergency meeting when a murder is discovered.")]
        public async Task<string> CallMeeting()
        {
            var res = await _callMeeting.InvokeAsync(new CallMeetingRequest());
            if (!res.Success) return "Could not call a meeting right now.";
            return res.AttendingNpcs.Count == 0
                ? "Meeting called, but no NPCs are present."
                : $"Meeting called. Attending: {string.Join(", ", res.AttendingNpcs.Select(n => n.Id))}";
        }

        [McpServerTool, Description("Accuse an NPC of being the killer during a meeting. Wrong accusations cost mood and count toward a loss condition.")]
        public async Task<string> AccuseNpc([Description("NPC id to accuse")] string targetNpcId)
        {
            var res = await _accuse.InvokeAsync(new AccuseNpcRequest { TargetNpcId = targetNpcId });
            if (res.GameOverWin) return $"Correct! All killers caught. YOU WIN. ({res.ResultText})";
            if (res.GameOverLoss) return $"Too many wrong accusations. YOU LOSE. ({res.ResultText})";
            return res.WasCorrect
                ? $"Correct -- one killer down, but more may remain. ({res.ResultText})"
                : $"Wrong accusation. ({res.ResultText})";
        }

        [McpServerTool, Description("Investigate the player's current location for clues (blood stains, footprints, wet clothes, etc). Takes no parameters -- it always uses the player's current location server-side, so move first if you want to search elsewhere. Returns the found clue instance id (added to the clue board), or the failure reason (no_clue_at_location, investigation_failed).")]
        public async Task<string> InvestigateClue()
        {
            // Clue System v2 (c): no parameters by design -- location comes from
            // player state server-side (prevents cross-zone snooping)
            var res = await _investigateClue.InvokeAsync(new InvestigateClueRequest());
            if (!res.Success) return $"Investigation failed: {res.FailureReason}";
            return $"Found a clue! (instance {res.FoundInstanceId}) -- added to your clue board.";
        }
    }
}
