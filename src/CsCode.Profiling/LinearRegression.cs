namespace CsCode.Profiling;

/// <summary>Ordinary least-squares fit y = Intercept + Slope·x, with the slope's standard error, a 95 % interval and R².</summary>
public readonly record struct LinearFit(double Slope, double Intercept, double RSquared, double SlopeStandardError, int Count)
{
    /// <summary>Half-width of the slope's ~95 % confidence interval (Student t for small samples).</summary>
    public double SlopeConfidence95 => Count < 3 || double.IsNaN(SlopeStandardError) ? double.PositiveInfinity : SlopeStandardError * StudentT95(Count - 2);

    public double SlopeLow => Slope - SlopeConfidence95;
    public double SlopeHigh => Slope + SlopeConfidence95;

    /// <summary>Two-sided 97.5 % quantile of Student's t for <paramref name="degrees"/> degrees of freedom (table + normal limit).</summary>
    internal static double StudentT95(int degrees) => degrees switch
    {
        <= 0 => double.PositiveInfinity,
        1 => 12.706, 2 => 4.303, 3 => 3.182, 4 => 2.776, 5 => 2.571, 6 => 2.447, 7 => 2.365, 8 => 2.306, 9 => 2.262, 10 => 2.228,
        <= 15 => 2.131, <= 20 => 2.086, <= 30 => 2.042, <= 60 => 2.000, <= 120 => 1.980, _ => 1.960,
    };
}

public static class LinearRegression
{
    /// <summary>Fits <paramref name="y"/> against <paramref name="x"/> (same length, at least two points with distinct x).</summary>
    public static LinearFit Fit(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
    {
        if (x.Length != y.Length) throw new ArgumentException("x and y differ in length");
        var n = x.Length;
        if (n < 2) return new LinearFit(0, n == 1 ? y[0] : 0, 0, double.NaN, n);
        double mx = 0, my = 0;
        for (var i = 0; i < n; i++) { mx += x[i]; my += y[i]; }
        mx /= n;
        my /= n;
        double sxx = 0, sxy = 0, syy = 0;
        for (var i = 0; i < n; i++)
        {
            var dx = x[i] - mx;
            var dy = y[i] - my;
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }
        if (sxx == 0) return new LinearFit(0, my, 0, double.NaN, n);
        var slope = sxy / sxx;
        var intercept = my - slope * mx;
        var ssRes = 0.0;
        for (var i = 0; i < n; i++)
        {
            var r = y[i] - (intercept + slope * x[i]);
            ssRes += r * r;
        }
        var r2 = syy == 0 ? 1 : Math.Max(0, 1 - ssRes / syy);
        var se = n > 2 ? Math.Sqrt(ssRes / (n - 2) / sxx) : double.NaN;
        return new LinearFit(slope, intercept, r2, se, n);
    }

    /// <summary>Fits <paramref name="y"/> against its index (0, 1, 2, …).</summary>
    public static LinearFit FitIndexed(ReadOnlySpan<double> y)
    {
        Span<double> x = y.Length <= 1024 ? stackalloc double[y.Length] : new double[y.Length];
        for (var i = 0; i < x.Length; i++) x[i] = i;
        return Fit(x, y);
    }
}
