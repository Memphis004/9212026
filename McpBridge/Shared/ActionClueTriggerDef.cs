namespace Marooned.Shared
{
    /// <summary>
    /// Clue System v2 (a): 1 แถวใน DataTables/Data/ActionClueTriggerDef.csv —
    /// action ประเภท TriggerSource ปล่อย clue ClueDefId ด้วยน้ำหนัก Weight
    /// (config เท่านั้น — ของที่เกิดจริงในเกมคือ ClueInstance)
    /// </summary>
    public class ActionClueTriggerDef
    {
        public string Id = string.Empty;
        public ClueTriggerSource TriggerSource;
        public string ClueDefId = string.Empty; // ref ClueDef.Id
        public int Weight;
    }
}
