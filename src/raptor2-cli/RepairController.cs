using Raptor2.Wire;

namespace Raptor2.Cli;

/// <summary>
/// Chooses how many repair symbols to send with each block on its first pass.
///
/// Fixed mode sends ceil(K x overhead), at least 1, like the original raptor. Adaptive mode estimates the packet loss
/// rate p from what happened to earlier segments: for each segment it records how many blocks it sent, how many still
/// needed a repair round, and how many repair symbols they carried. Losses per block are modelled as Poisson(p K),
/// p is the maximum-likelihood fit over recent segments, and each block then gets the fewest repair symbols that keep
/// the chance of it needing a repair round under <see cref="Target"/>. On a clean link that is zero, so blocks need no
/// encoding at all; any loss raises it again within a segment or two.
/// </summary>
public sealed class RepairController
{
    /// <summary>
    /// Fraction of large blocks allowed to need a repair round. A repair round sends only what the MicroVM says a block
    /// still needs, so a miss mid-transfer costs a few symbols and a round trip; only an object's last segment holds up
    /// its completion, so it gets the stricter <see cref="TailTarget"/>.
    /// </summary>
    public const double Target = 0.05;
    public const double TailTarget = 0.005;
    /// <summary>Small blocks (K at or below this) are cheap to encode, so they always get at least one repair symbol and a stricter target.</summary>
    public const int SmallK = 64;
    public const double SmallTarget = 0.001;
    private const double MinLoss = 1e-6, MaxLoss = 0.3, Decay = 0.9;
    private const int MaxObservations = 64;

    private readonly double? _fixed;
    private readonly object _gate = new();
    private readonly List<(double Weight, int Blocks, int Missing, int Repairs, int K)> _seen = [];
    private double _loss;

    /// <param name="fixedOverhead">A fixed fraction of K, or null for adaptive.</param>
    public RepairController(double? fixedOverhead)
    {
        _fixed = fixedOverhead;
        // A weak prior, decaying like any observation: one of two blocks missing with no repair symbols (p near 0.1%).
        _seen.Add((0.5, 2, 1, 0, 512));
        _loss = Fit(_seen, MaxLoss);
    }

    public bool Adaptive => _fixed is null;
    public double LossEstimate { get { lock (_gate) return _loss; } }

    /// <param name="tail">True for the blocks of an object's last segment.</param>
    public int RepairsFor(int k, bool tail = false)
    {
        if (_fixed is { } overhead) return Layout.RepairCount(k, overhead);
        double lambda = LossEstimate * k;
        if (k <= SmallK) return Math.Min(k, Math.Max(1, Quantile(lambda, SmallTarget)));
        return Math.Min(k, Quantile(lambda, tail ? TailTarget : Target));
    }

    /// <summary>Records a segment's first pass: <paramref name="blocks"/> sent with <paramref name="repairs"/> repair symbols each, <paramref name="missing"/> of them incomplete.</summary>
    public void Report(int blocks, int missing, int repairs, int k)
    {
        if (_fixed is not null || blocks <= 0 || k <= 0) return;
        lock (_gate)
        {
            // A segment far worse than the estimate predicts means conditions changed (congestion, a resumed MicroVM):
            // forget the history so the estimate follows at once instead of averaging the change away.
            double expected = Math.Exp(LogTails(_loss * k, repairs).LogTail);
            if (missing / (double)blocks > Math.Max(0.25, 4 * expected)) _seen.Clear();
            for (int i = 0; i < _seen.Count; i++) _seen[i] = _seen[i] with { Weight = _seen[i].Weight * Decay };
            _seen.Add((1.0, blocks, Math.Clamp(missing, 0, blocks), repairs, k));
            if (_seen.Count > MaxObservations) _seen.RemoveAt(0);
            // One segment may raise the estimate at most fourfold (or to 1%): a segment where every block missed says
            // only that loss is high, not how high, and the maximum-likelihood answer would be the upper bound.
            _loss = Fit(_seen, Math.Max(0.01, _loss * 4));
        }
    }

    /// <summary>Maximum-likelihood loss rate over the weighted observations, at most <paramref name="upper"/>.</summary>
    private static double Fit(List<(double Weight, int Blocks, int Missing, int Repairs, int K)> seen, double upper)
    {
        double LogLikelihood(double p)
        {
            double sum = 0;
            foreach (var (w, n, m, r, k) in seen) sum += Term(w, n, m, r, k, p);
            return sum;
        }
        static double Term(double w, int n, int m, int r, int k, double p)
        {
            var (logTail, logCdf) = LogTails(p * k, r);
            return w * (m * logTail + (n - m) * logCdf);
        }
        // Golden-section search on log p; the likelihood is unimodal in p.
        double a = Math.Log(MinLoss), b = Math.Log(Math.Clamp(upper, MinLoss * 2, MaxLoss));
        const double g = 0.6180339887498949;
        double c = b - g * (b - a), d = a + g * (b - a);
        double fc = LogLikelihood(Math.Exp(c)), fd = LogLikelihood(Math.Exp(d));
        for (int i = 0; i < 60; i++)
        {
            if (fc > fd) { b = d; d = c; fd = fc; c = b - g * (b - a); fc = LogLikelihood(Math.Exp(c)); }
            else { a = c; c = d; fc = fd; d = a + g * (b - a); fd = LogLikelihood(Math.Exp(d)); }
        }
        return Math.Exp((a + b) / 2);
    }

    /// <summary>
    /// log P(X > r) and log P(X &lt;= r) for X ~ Poisson(lambda), accurate in the far tails (where 1 - cdf would cancel
    /// to zero). The smaller probability is summed as a series from its largest term; the other is log1p(-exp(.)).
    /// </summary>
    internal static (double LogTail, double LogCdf) LogTails(double lambda, int r)
    {
        if (lambda <= 0) return (double.NegativeInfinity, 0);
        double logLambda = Math.Log(lambda);
        double LogPmf(int x) { double s = -lambda + x * logLambda; for (int i = 2; i <= x; i++) s -= Math.Log(i); return s; }
        if (lambda <= r + 1)
        {
            // Tail: pmf(r+1) x (1 + lambda/(r+2) + lambda^2/((r+2)(r+3)) + ...), terms shrinking.
            double sum = 1, term = 1;
            for (int j = r + 2; j < r + 2 + 10_000; j++) { term *= lambda / j; sum += term; if (term < 1e-17 * sum) break; }
            double logTail = LogPmf(r + 1) + Math.Log(sum);
            return (logTail, Math.Log(-double.ExpM1(logTail)));
        }
        else
        {
            // Cdf: pmf(r) x (1 + r/lambda + r(r-1)/lambda^2 + ...), terms shrinking.
            double sum = 1, term = 1;
            for (int j = r; j >= 1; j--) { term *= j / lambda; sum += term; if (term < 1e-17 * sum) break; }
            double logCdf = LogPmf(r) + Math.Log(sum);
            return (Math.Log(-double.ExpM1(logCdf)), logCdf);
        }
    }

    /// <summary>P(X > r) for X ~ Poisson(lambda).</summary>
    internal static double TailAbove(double lambda, int r)
    {
        if (lambda <= 0) return 0;
        double term = Math.Exp(-lambda), cdf = term;
        for (int i = 1; i <= r; i++) { term *= lambda / i; cdf += term; }
        return Math.Max(0, 1 - cdf);
    }

    /// <summary>The smallest r with P(X > r) at or below <paramref name="target"/>.</summary>
    internal static int Quantile(double lambda, double target)
    {
        int r = 0;
        while (TailAbove(lambda, r) > target && r < 100_000) r++;
        return r;
    }
}
