using System;
using System.Collections.Generic;

namespace StatisticsAnalysisTool.Island;

public class OwnerCycleRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Date { get; set; } = DateTime.UtcNow.Date;
    public CycleRecordType RecordType { get; set; } = CycleRecordType.Islands;
    public int IslandCount { get; set; }
    public decimal EarnedAmount { get; set; }
    public string Notes { get; set; } = string.Empty;
    public List<IslandOutcomeEntry> Outcomes { get; set; } = new();
}
