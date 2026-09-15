using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Marooned.Shared
{
    /// <summary>
    /// Clue System v2 (e): human-readable text rendering ของ GetClueGraphResponse —
    /// ใช้ร่วมกันทั้ง McpBridge (get_clue_graph tool — ตาม MCP convention ต้องคืน
    /// text summary ไม่ใช่ JSON/debug string) และ Unity (ClueBoardView summary header)
    /// เพื่อให้ AI VTuber อ่านกราฟแบบเดียวกับที่ผู้เล่นเห็นในเกม
    ///
    /// ⚠️ Reuse-only: formatter อ่านอย่างเดียว — ห้ามแก้ GetClueGraphHandler /
    /// GraphNode / GraphEdge (field จริง: node.Label / edge.From / edge.To)
    /// </summary>
    public static class ClueGraphTextFormat
    {
        /// <summary>
        /// เรนเดอร์กราฟเป็นบรรทัดต่อ clue:
        ///   รอยเลือด — seen near: npc_01, npc_02
        ///   รอยเท้า — seen near: no one observed
        /// </summary>
        public static string Render(GetClueGraphResponse graph)
        {
            if (graph == null)
                return "No clue graph data.";

            var clueNodes = graph.Nodes.Where(n => n.Type == "clue").ToList();
            if (clueNodes.Count == 0)
                return "No clues collected yet (empty graph).";

            // npc node label ตาม id (กราฟสร้างจาก board เดียวกัน — witness filter
            // เกิดใน GetClueBoardHandler แล้ว ตรงนี้แค่ map id → label)
            var npcLabels = graph.Nodes.Where(n => n.Type == "npc")
                .ToDictionary(n => n.Id, n => n.Label);

            var sb = new StringBuilder();
            sb.Append("Clue Graph — ").Append(clueNodes.Count).Append(" clue(s), ")
              .Append(graph.Edges.Count).AppendLine(" witness link(s):");

            foreach (var clue in clueNodes)
            {
                var witnesses = graph.Edges
                    .Where(e => e.From == clue.Id && npcLabels.ContainsKey(e.To))
                    .Select(e => npcLabels[e.To])
                    .ToList();

                sb.Append("  ").Append(string.IsNullOrEmpty(clue.Label) ? clue.Id : clue.Label)
                  .Append(" — seen near: ")
                  .AppendLine(witnesses.Count > 0 ? string.Join(", ", witnesses) : "no one observed");
            }

            return sb.ToString().TrimEnd('\r', '\n');
        }

        /// <summary>
        /// เรนเดอร์รายการ pin ของกระดาน (get_pinned_clues — state เดียวกับ UI):
        ///   Pinned Clues — 2 node(s) pinned (oldest first):
        ///     [clue] รอยขีดข่วน (Weak) @ beach — seen near: npc_02, npc_03
        ///     [npc] npc_02 | zone=beach • alive | witnessed: คราบเลือด @ beach ×3, รอยขีดข่วน @ cave
        /// </summary>
        public static string RenderPinned(GetPinnedCluesResponse pinned)
        {
            if (pinned == null || pinned.Pinned.Count == 0)
                return "No nodes pinned on the clue board (click a node then [pin] in-game to compare).";

            var sb = new StringBuilder();
            sb.Append("Pinned Clues — ").Append(pinned.Pinned.Count)
              .AppendLine(" node(s) pinned (oldest first):");

            foreach (var p in pinned.Pinned)
            {
                if (p == null) continue;
                if (p.Type == "npc")
                {
                    sb.Append("  [npc] ").Append(string.IsNullOrEmpty(p.NodeId) ? "?" : p.NodeId)
                      .Append(" | zone=").Append(string.IsNullOrEmpty(p.Zone) ? "unknown" : p.Zone)
                      .Append(" • ").Append(p.IsAlive ? "alive" : "dead")
                      .Append(" | witnessed: ")
                      .AppendLine(p.WitnessedClues.Count > 0
                          ? string.Join("; ", DedupeCounted(p.WitnessedClues))
                          : "nothing collected");
                }
                else
                {
                    sb.Append("  [clue] ").Append(string.IsNullOrEmpty(p.DisplayName) ? p.NodeId : p.DisplayName)
                      .Append(" (").Append(string.IsNullOrEmpty(p.Reliability) ? "?" : p.Reliability).Append(')')
                      .Append(" @ ").Append(string.IsNullOrEmpty(p.LocationId) ? "unknown" : p.LocationId)
                      .Append(" — seen near: ")
                      .AppendLine(p.WitnessNpcIds.Count > 0 ? string.Join(", ", p.WitnessNpcIds) : "no one observed");
                }
            }

            return sb.ToString().TrimEnd('\r', '\n');
        }

        /// <summary>
        /// รวมบรรทัดซ้ำเป็นรูป "<text> ×N" (N>1) — **display-only**: ตัวเลขนับจำนวน
        /// clue instance จริงที่เก็บได้ ข้อมูลดิบ (list เต็ม) ไม่เปลี่ยน — ทั้ง MCP
        /// (get_pinned_clues) และ view (popup/pin cards) ใช้รูปเดียวกัน
        /// </summary>
        public static List<string> DedupeCounted(List<string> lines)
        {
            if (lines == null || lines.Count == 0) return new List<string>();
            var outLines = new List<string>();
            var counts = new Dictionary<string, int>();
            var order = new List<string>(); // first-occurrence order — อย่าพึ่ง enumeration order ของ Dictionary
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                counts.TryGetValue(line, out var c);
                if (c == 0) order.Add(line);
                counts[line] = c + 1;
            }
            foreach (var line in order)
                outLines.Add(counts[line] > 1 ? $"{line} ×{counts[line]}" : line);
            return outLines;
        }
    }
}
