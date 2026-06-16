using System;

namespace StatisticsAnalysisTool.Island;

// Single source of truth for the island "accounting day".
//
// Albion's game day rolls at UTC midnight, not the machine's local midnight. A cycle finished after
// local midnight but before UTC midnight still belongs to the previous UTC day in-game. Bucketing
// island records and the "Done today" counters by the local calendar day therefore desynced them:
// a late-night session stamped the record on one day while the counter read another, showing 0/N done.
// Every island day boundary (records, "today" counters, payout periods, weeks) must go through here.
public static class IslandTime
{
    // The current Albion accounting day (UTC date, time component zeroed).
    public static DateTime Today => DateTime.UtcNow.Date;
}
