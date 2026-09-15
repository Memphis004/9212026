using System;
using System.IO;
using System.Linq;
using System.Text;
using Cysharp.Threading.Tasks;
using Marooned.Core;
using Marooned.Shared;
using Marooned.Systems;
using Marooned.UI.Presenters;
using Marooned.UI.Views;
using MessagePipe;
using UnityEditor;
using UnityEngine;
using VContainer;

namespace Marooned.EditorTools
{
    /// <summary>
    /// Clue System v2 (e) — visual evidence runner (Editor menu, ต้องกด Play ก่อน):
    /// seed clue ผ่าน TryGenerate (kill hook เดียวกับเกม) + investigate เก็บเข้า board
    /// → render ผ่าน ClueBoardPresenter จริง → เขียนหลักฐานลง TestEvidence พร้อมให้
    /// จับ screenshot Game view ตาม (runner แค่เตรียมสถานะ + render ให้เฟรมจริง)
    /// </summary>
    public static class ClueBoardVisualRunner
    {
        private const string EvidenceDir = "TestEvidence/clue-system-v2-e";

        [MenuItem("Marooned/Clue Board/Seed Clues + Render (must be in Play mode)", priority = 50)]
        public static async void SeedAndRender()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("[ClueBoardVisualRunner] ต้องกด Play ก่อน (presenter/view ผูกกับ play mode)");
                return;
            }

            var log = new StringBuilder();
            log.AppendLine($"=== Clue Board visual seed — {System.DateTime.Now:HH:mm:ss} ===");

            var scope = UnityEngine.Object.FindFirstObjectByType<GameLifetimeScope>();
            if (scope == null)
            {
                Debug.LogError("[ClueBoardVisualRunner] หา GameLifetimeScope ไม่เจอ");
                return;
            }

            var container = scope.Container;
            var state = container.Resolve<GameStateProvider>();
            var director = container.Resolve<NpcDirectorSystem>();
            var player = state.GetPlayer();

            // ---- stage: player + NPC มีชีวิต 3 ตัว อยู่ beach ----
            player.CurrentLocationId = "beach";
            var witnesses = director.Npcs.Values.Where(n => n.IsAlive).Take(3).ToList();
            foreach (var w in witnesses) director.MoveNpc(w.Id, "beach");
            log.AppendLine($"[stage] player @ beach, witnesses: [{string.Join(", ", witnesses.Select(w => w.Id))}]");

            // ---- seed 2 clue ผ่าน pipeline จริง (blood=100% ทั้งคู่) + investigate เก็บ ----
            var gen = container.Resolve<ClueGenerationSystem>();
            var investigate = container.Resolve<IAsyncRequestHandler<InvestigateClueRequest, InvestigateClueResponse>>();
            player.CurrentLocationId = "beach";
            for (var i = 0; i < 2; i++)
            {
                gen.TryGenerate(ClueTriggerSource.KillSabotage, "beach", "npc_visual_killer_" + i);
                var r = await investigate.InvokeAsync(new InvestigateClueRequest());
                log.AppendLine($"[seed #{i + 1}] investigate: Success={r.Success} id={r.FoundInstanceId}");
            }

            await UniTask.Yield();

            // ---- render ผ่าน presenter จริง (path เดียวกับ ClueGeneratedMessage) ----
            // panel เริ่มซ่อน (toggle ด้วย Tab) — runner เปิดเองเพื่อจับ screenshot
            var presenter = container.Resolve<ClueBoardPresenter>();
            var view = container.Resolve<ClueBoardView>();
            view.gameObject.SetActive(true);
            var method = typeof(ClueBoardPresenter).GetMethod("RenderAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (UniTask)method!.Invoke(presenter, null)!;

            log.AppendLine($"[render] nodes={view.RenderedNodeCount} edges={view.RenderedEdgeCount}");

            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", EvidenceDir));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "visual-seed-render.txt"), log.ToString() + Environment.NewLine);
            Debug.Log($"[ClueBoardVisualRunner] DONE — nodes={view.RenderedNodeCount} edges={view.RenderedEdgeCount} — evidence: {EvidenceDir}/visual-seed-render.txt (จับ screenshot Game view ได้เลย)");
        }

        [MenuItem("Marooned/Clue Board/Clear Collected Clues (Play mode)", priority = 51)]
        public static void ClearCollected()
        {
            if (!Application.isPlaying) { Debug.LogError("[ClueBoardVisualRunner] ต้องกด Play ก่อน"); return; }
            var scope = UnityEngine.Object.FindFirstObjectByType<GameLifetimeScope>();
            var player = scope.Container.Resolve<GameStateProvider>().GetPlayer();
            player.CollectedClueInstanceIds.Clear();
            Debug.Log("[ClueBoardVisualRunner] CollectedClueInstanceIds cleared");
        }
    }
}
