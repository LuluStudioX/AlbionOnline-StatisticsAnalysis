namespace StatisticsAnalysisTool.Island;

public record SlotGridCell(int Col, int Row, string State, string Label, bool IsSmall, bool IsHighlighted = false);
