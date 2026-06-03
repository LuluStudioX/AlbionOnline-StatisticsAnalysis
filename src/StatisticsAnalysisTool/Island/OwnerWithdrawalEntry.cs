using System;
using System.Text.Json.Serialization;

namespace StatisticsAnalysisTool.Island;

public class OwnerWithdrawalEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public decimal Amount { get; set; }
    public string Notes { get; set; } = string.Empty;
    // The Monday that starts the week this payment covers. Null = derive from Timestamp.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? PaidForWeekStart { get; set; }
}
