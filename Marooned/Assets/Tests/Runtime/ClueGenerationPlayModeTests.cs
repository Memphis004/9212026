using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cysharp.Threading.Tasks;
using Marooned.Core;
using Marooned.Shared;
using Marooned.Systems;
using MessagePipe;
using NUnit.Framework;
using VContainer;
using UnityEngine;
using UnityEngine.TestTools;

namespace Marooned.EditorTools
{
    /// <summary>
    /// Clue System v2 — Part (b): PlayMode evidence (kill-hook จริงใน scene จริง)
    ///
    /// A: force kill โดย player ใช้มีด — ผ่าน UseCardHandler จริง (container-resolved,
    ///    เส้นทางเดียวกับ MCP use_card) → AllClueInstances เพิ่ม 1-2 ตัว (blood=100%,
    ///    scratch=30%), ClueGeneratedMessage ยิงครบทุก instance, witness ไม่มี player_local
    /// F: regression — get_game_state / get_visible_npcs / get_clue_board resolve จาก
    ///    container และ invoke ได้ปกติหลังเพิ่ม ClueGenerationSystem เข้า DI graph
    ///
    /// Determinism: scene boot มี 5 NPC อยู่โซนเดียวกับ player (RoundInitializer) —
    /// ก่อน kill ย้าย player+เหยื่อไป cave_entrance และ NPC ที่เหลือไป deep_jungle
    /// (teleport ทาง state ผ่าน MoveNpc) แล้ว retry สูงสุด 10 รอบ หาก AI เดินมาแทรก
    /// ทำให้ CanEliminate ตอบ witnessed/target_not_same_location
    /// </summary>
    public class ClueGenerationPlayModeTests
    {
        public const string EvidenceDir = "TestEvidence/clue-system-v2-b";

        GameLifetimeScope _scope;
        GameStateProvider _stateProvider;
        NpcDirectorSystem _director;
        StringBuilder _log;

        [UnitySetUp]
        public IEnumerator SetUp() => UniTask.ToCoroutine(async () =>
        {
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "SampleScene")
            {
                var load = UnityEngine.SceneManagement.SceneManager.LoadSceneAsync("SampleScene");
                while (load != null && !load.isDone) await UniTask.Yield();
            }

            var deadline = Time.realtimeSinceStartup + 60f;
            while (Time.realtimeSinceStartup < deadline)
            {
                _scope = UnityEngine.Object.FindFirstObjectByType<GameLifetimeScope>();
                if (_scope != null && Application.isPlaying)
                {
                    _stateProvider = _scope.Container.Resolve<GameStateProvider>();
                    if (_stateProvider != null) break;
                }
                await UniTask.Yield();
            }
            Assert.That(_scope != null, "GameLifetimeScope ไม่เจอใน play mode");
            _director = _scope.Container.Resolve<NpcDirectorSystem>();
            _log = new StringBuilder();
            await UniTask.Yield();
        });

        [UnityTest]
        public IEnumerator A_PlayerKnifeKill_GeneratesClues_AndPublishesMessages() => UniTask.ToCoroutine(async () =>
        {
            _log.Clear();
            _log.AppendLine($"=== Clue System v2 (b) Test A — {DateTime.Now:HH:mm:ss} ===");

            var container = _scope.Container;
            var subscriber = container.Resolve<ISubscriber<ClueGeneratedMessage>>();
            var messageCount = 0;
            var subscription = subscriber.Subscribe(msg =>
            {
                messageCount++;
                _log.AppendLine($"  [message] {msg.DefId} @ {msg.LocationId} (id={msg.InstanceId.Substring(0, 8)}…)");
            });

            try
            {
                var player = _stateProvider.GetPlayer();
                var victimId = _director.Npcs.Keys.First();
                var before = _stateProvider.AllClueInstances.Count;

                // ---- stage: player + เหยื่อ อยู่ cave_entrance สองต่อ, NPC อื่นย้ายออกไกล ----
                player.CurrentLocationId = "cave_entrance";
                player.Inventory["knife_basic"] = 1; // force: ให้มีด (มีจริงใน CardDef.csv, Eliminate)
                foreach (var npc in _director.Npcs.Values)
                    _director.MoveNpc(npc.Id, npc.Id == victimId ? "cave_entrance" : "deep_jungle");
                await UniTask.Yield();

                // ---- force kill ผ่าน UseCardHandler จริง (retry กัน AI เดินมาแทรก) ----
                var useCard = container.Resolve<IAsyncRequestHandler<UseCardRequest, UseCardResponse>>();
                var (success, reason) = (false, "not_attempted");
                for (var attempt = 1; attempt <= 10 && !success; attempt++)
                {
                    // re-stage ทุก attempt — NPC AI อาจเดินรบกวนระหว่างรอบก่อน
                    player.CurrentLocationId = "cave_entrance";
                    if (!_director.Npcs.TryGetValue(victimId, out var v) || !v.IsAlive) break; // ตายไปแล้ว
                    _director.MoveNpc(victimId, "cave_entrance");
                    foreach (var npc in _director.Npcs.Values.Where(n => n.Id != victimId))
                        if (npc.CurrentLocationId == "cave_entrance") _director.MoveNpc(npc.Id, "deep_jungle");

                    var response = await useCard.InvokeAsync(
                        new UseCardRequest { CardId = "knife_basic", TargetId = victimId });
                    success = response.Success;
                    reason = response.FailureReason;
                    _log.AppendLine($"attempt {attempt}: use_card(knife_basic → {victimId}) = {success} {reason}");
                    await UniTask.Yield();
                }
                Assert.IsTrue(success, $"force kill ต้องสำเร็จ (attempt สุดท้าย: {reason})");
                Assert.IsFalse(_director.Npcs[victimId].IsAlive, "เหยื่อต้องตาย");

                // ---- ยืนยัน clue ที่เกิด ----
                var generated = _stateProvider.AllClueInstances.Values
                    .Where(i => i.SourceActorId == GameStateProvider.LocalPlayerId)
                    .ToList();
                _log.AppendLine($"AllClueInstances: {before} → {_stateProvider.AllClueInstances.Count} (+{generated.Count})");
                foreach (var inst in generated)
                    _log.AppendLine($"  {inst.DefId} @ {inst.LocationId} ts={inst.GameTimestamp:F2} " +
                                    $"witnesses=[{string.Join(",", inst.WitnessNpcIds)}] (player = killer → ห้ามมี player_local)");

                Assert.That(generated.Count, Is.InRange(1, 2),
                    "blood (weight=100) เกิดเสมอ → +1; scratch (30%) อาจทำให้ +2");
                Assert.That(_stateProvider.AllClueInstances.Count - before, Is.EqualTo(generated.Count),
                    "registry ต้องเพิ่มเท่ากับ instance ที่ kill นี้สร้าง");
                foreach (var inst in generated)
                {
                    Assert.That(inst.Source, Is.EqualTo(ClueTriggerSource.KillSabotage));
                    Assert.That(inst.LocationId, Is.EqualTo("cave_entrance"));
                    Assert.That(inst.WitnessNpcIds, Does.Not.Contain(GameStateProvider.LocalPlayerId),
                        "⚠️ player-killer ห้ามเป็นพยานตัวเอง");
                }
                Assert.That(messageCount, Is.EqualTo(generated.Count),
                    "ClueGeneratedMessage ต้องยิงครบ 1 ต่อ 1 instance (ผ่าน bus จริง)");

                _log.AppendLine($"victim.AllConditionCardIds=[{string.Join(",", _director.Npcs[victimId].AllConditionCardIds)}]");
                _log.AppendLine("RESULT: PASS");
                WriteEvidence("A_PlayerKnifeKill_GeneratesClues_AndPublishesMessages", _log.ToString());
            }
            finally
            {
                subscription.Dispose();
            }
            await UniTask.Yield();
        });

        [UnityTest]
        public IEnumerator F_McpQueryHandlers_StillWork_AfterDiChange() => UniTask.ToCoroutine(async () =>
        {
            var log = new StringBuilder();
            log.AppendLine($"=== Clue System v2 (b) Test F (regression) — {DateTime.Now:HH:mm:ss} ===");
            var container = _scope.Container;

            // ---- get_game_state ----
            var getGameState = container.Resolve<IAsyncRequestHandler<GetGameStateRequest, GetGameStateResponse>>();
            var gameState = await getGameState.InvokeAsync(new GetGameStateRequest());
            Assert.That(gameState.Player, Is.Not.Null);
            Assert.That(gameState.Player.Hunger, Is.InRange(0f, 100f));
            Assert.That(gameState.Player.Thirst, Is.InRange(0f, 100f));
            Assert.That(gameState.Player.CurrentLocationId, Is.Not.Empty);
            log.AppendLine($"get_game_state: location={gameState.Player.CurrentLocationId} " +
                           $"hunger={gameState.Player.Hunger:F1} thirst={gameState.Player.Thirst:F1} → OK");

            // ---- get_visible_npcs (DeductionSystem เดิม + instance ids ใน AllConditionCardIds) ----
            var getVisible = container.Resolve<IAsyncRequestHandler<GetVisibleNpcsRequest, GetVisibleNpcsResponse>>();
            var visible = await getVisible.InvokeAsync(new GetVisibleNpcsRequest());
            Assert.That(visible.Npcs, Is.Not.Null);
            foreach (var npc in visible.Npcs)
                Assert.That(npc.Id, Is.Not.Empty);
            log.AppendLine($"get_visible_npcs: {visible.Npcs.Count} npc(s) → OK " +
                           $"(AllConditionCardIds ผ่าน FilterVisible ไม่ throw)");

            // ---- get_clue_board (shape ใหม่จาก commit (d): List<ClueBoardEntry>) ----
            var getClueBoard = container.Resolve<IAsyncRequestHandler<GetClueBoardRequest, GetClueBoardResponse>>();
            var clueBoard = await getClueBoard.InvokeAsync(new GetClueBoardRequest());
            Assert.That(clueBoard.Entries, Is.Not.Null);
            log.AppendLine($"get_clue_board: {clueBoard.Entries.Count} entry(ies) → OK (shape (d))");

            // ---- get_clue_graph (ใหม่จาก commit (d)) resolve + ว่างได้ ----
            var getClueGraph = container.Resolve<IAsyncRequestHandler<GetClueGraphRequest, GetClueGraphResponse>>();
            var clueGraph = await getClueGraph.InvokeAsync(new GetClueGraphRequest());
            Assert.That(clueGraph.Nodes, Is.Not.Null);
            Assert.That(clueGraph.Edges, Is.Not.Null);
            log.AppendLine($"get_clue_graph: {clueGraph.Nodes.Count} node(s), {clueGraph.Edges.Count} edge(s) → OK");

            // ---- DI graph ครบ: handler ทุกตัว (รวม edge ใหม่ ClueGenerationSystem) resolve ได้ ----
            container.Resolve<ClueGenerationSystem>();
            container.Resolve<NpcDirectorSystem>();
            container.Resolve<DeductionSystem>();
            log.AppendLine("DI resolve: ClueGenerationSystem / NpcDirectorSystem / DeductionSystem → OK");
            log.AppendLine("RESULT: PASS");
            WriteEvidence("F_McpQueryHandlers_StillWork_AfterDiChange", log.ToString());
            await UniTask.Yield();
        });

        private void WriteEvidence(string test, string body)
        {
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", EvidenceDir));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{test}.txt"),
                $"{body}\nRESULT: PASS\n");
            Debug.Log($"[ClueGenerationPlayModeTests] {test} PASS — evidence: {EvidenceDir}/");
        }
    }
}
