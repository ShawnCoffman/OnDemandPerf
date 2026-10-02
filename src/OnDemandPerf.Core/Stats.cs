namespace OnDemandPerf.Core;

/// <summary>Small statistics helpers shared by every benchmark so variation is computed one way.</summary>
public static class Stats
{
    public const string VariationMethod = "sample standard deviation (n-1) / mean, percent";

    public static double Mean(IReadOnlyList<double> values) => values.Count == 0 ? 0 : values.Sum() / values.Count;

    /// <summary>Sample standard deviation (n-1). With only a few passes the population formula understates spread.</summary>
    public static double SampleStdDev(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;
        double mean = Mean(values);
        double sumSquares = 0;
        foreach (double value in values) sumSquares += (value - mean) * (value - mean);
        return Math.Sqrt(sumSquares / (values.Count - 1));
    }

    public static double VariationPercent(IReadOnlyList<double> values)
    {
        double mean = Mean(values);
        return mean == 0 ? 0 : SampleStdDev(values) / mean * 100;
    }

    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
