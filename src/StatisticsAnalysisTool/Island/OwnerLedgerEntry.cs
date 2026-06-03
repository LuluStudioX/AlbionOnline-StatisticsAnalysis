using System;
using System.Collections.Generic;

namespace StatisticsAnalysisTool.Island;

public class OwnerLedgerEntry
{
    public Guid Id { get; init; }
    public DateTime Date { get; init; }
    public string Type { get; init; } = string.Empty;
    public int? IslandCount { get; init; }
    public decimal Amount { get; init; }
    public string Notes { get; init; } = string.Empty;
    public IReadOnlyList<IslandOutcomeEntry> Outcomes { get; init; } = Array.Empty<IslandOutcomeEntry>();
    public bool IsEarning => Amount >= 0;
}
