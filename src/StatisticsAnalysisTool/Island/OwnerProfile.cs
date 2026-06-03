using System;
using System.Collections.Generic;

namespace StatisticsAnalysisTool.Island;

public class OwnerProfile
{
    public string WebhookUrl { get; set; } = string.Empty;
    public decimal DefaultPayPerIsland { get; set; }
    public decimal OpeningBalance { get; set; }
    public List<OwnerWithdrawalEntry> Withdrawals { get; set; } = new();
    public List<OwnerCycleRecord> CycleHistory { get; set; } = new();
    public DayOfWeek PayoutDayOfWeek { get; set; } = DayOfWeek.Sunday;
    public OwnerEngagementType EngagementType { get; set; } = OwnerEngagementType.Unpaid;
    public ManagerResponsibility ManagerResponsibilities { get; set; } = ManagerResponsibility.None;
    public string Notes { get; set; } = string.Empty;
}
