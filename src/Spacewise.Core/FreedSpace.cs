namespace Spacewise.Core;

// FR-CLN-001..003: the estimate (logical bytes) and the measurement (volume free space before and after) are
// different numbers and are never mixed. When they differ by more than 10 %, the report says why.
public static class FreedSpace
{
    // Null when there is nothing to explain: no measurement, nothing estimated, or the numbers agree within tolerance.
    public static string? WhyDifferent(long estimatedLogicalBytes, long? measuredDelta)
    {
        if (measuredDelta is not long measured || estimatedLogicalBytes <= 0) return null;
        // Integer arithmetic: 1100 against 1000 must count as exactly 10 %, not 10.000000000000009 %.
        if (Math.Abs(measured - estimatedLogicalBytes) * 10 <= estimatedLogicalBytes) return null;
        double ratio = (double)measured / estimatedLogicalBytes;
        if (measured < 0)
            return "Свободного места стало меньше: пока шла очистка, другие программы записали на том больше, чем освободилось. Измерение показывает итог для тома, а не только эту очистку.";
        if (ratio < 1)
            return "Освободилось меньше оценки. Возможные причины: у файлов с жёсткими ссылками место освобождается только после удаления всех имён; сжатые и разреженные файлы занимают на диске меньше своего логического размера; другие программы записывали на том во время очистки.";
        return "Освободилось больше оценки. Возможные причины: файлы занимали на диске больше своего логического размера из-за кластеров и служебных данных; параллельно освободили место другие программы.";
    }
}
