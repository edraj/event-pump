using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace EventPump.Observability;

/// <summary>
/// Hand-rolled Prometheus text-exposition metrics (PLAN §2.1: prometheus-net and
/// the OTel Prometheus exporter carry no Native AOT guarantee, and five metric
/// families do not justify gambling the zero-warning requirement).
/// Thread-safe; exposition format v0.0.4.
/// </summary>
public sealed class MetricsRegistry
{
    private readonly List<MetricFamily> _families = [];

    public Counter Counter(string name, string help, params string[] labelNames)
        => Add(new Counter(name, help, labelNames));

    public Gauge Gauge(string name, string help, params string[] labelNames)
        => Add(new Gauge(name, help, labelNames));

    public Histogram Histogram(string name, string help, double[] buckets, params string[] labelNames)
        => Add(new Histogram(name, help, buckets, labelNames));

    public string Render()
    {
        var sb = new StringBuilder();
        lock (_families)
        {
            foreach (var family in _families) family.RenderInto(sb);
        }
        return sb.ToString();
    }

    private T Add<T>(T family) where T : MetricFamily
    {
        lock (_families) _families.Add(family);
        return family;
    }
}

public abstract class MetricFamily(string name, string help, string type, string[] labelNames)
{
    private protected readonly string Name = name;
    private protected readonly string[] LabelNames = labelNames;

    internal void RenderInto(StringBuilder sb)
    {
        sb.Append("# HELP ").Append(Name).Append(' ')
          .Append(help.Replace("\\", "\\\\").Replace("\n", "\\n")).Append('\n');
        sb.Append("# TYPE ").Append(Name).Append(' ').Append(type).Append('\n');
        RenderSamples(sb);
    }

    private protected abstract void RenderSamples(StringBuilder sb);

    private protected string ChildKey(string[] labelValues)
    {
        if (labelValues.Length != LabelNames.Length)
            throw new ArgumentException(
                $"{Name}: expected {LabelNames.Length} label value(s), got {labelValues.Length}");
        return string.Join('\x1f', labelValues);
    }

    private protected void AppendSample(
        StringBuilder sb, string[] labelValues, double value,
        string nameSuffix = "", (string Name, string Value)? extraLabel = null)
    {
        sb.Append(Name).Append(nameSuffix);
        if (labelValues.Length > 0 || extraLabel is not null)
        {
            sb.Append('{');
            var first = true;
            for (var i = 0; i < labelValues.Length; i++)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(LabelNames[i]).Append("=\"").Append(Escape(labelValues[i])).Append('"');
            }
            if (extraLabel is var (labelName, labelValue) && labelName is not null)
            {
                if (!first) sb.Append(',');
                sb.Append(labelName).Append("=\"").Append(Escape(labelValue)).Append('"');
            }
            sb.Append('}');
        }
        sb.Append(' ').Append(Num(value)).Append('\n');
    }

    private protected static string Num(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}

public sealed class Counter(string name, string help, string[] labelNames)
    : MetricFamily(name, help, "counter", labelNames)
{
    private readonly ConcurrentDictionary<string, CounterChild> _children = new();

    public CounterChild WithLabels(params string[] labelValues)
        => _children.GetOrAdd(ChildKey(labelValues), _ => new CounterChild(labelValues));

    private protected override void RenderSamples(StringBuilder sb)
    {
        foreach (var child in _children.Values)
            AppendSample(sb, child.LabelValues, child.Value);
    }
}

public sealed class CounterChild(string[] labelValues)
{
    internal readonly string[] LabelValues = labelValues;
    private double _value;

    internal double Value => Volatile.Read(ref _value);

    public void Inc(double amount = 1)
    {
        double initial, updated;
        do
        {
            initial = Volatile.Read(ref _value);
            updated = initial + amount;
        } while (Interlocked.CompareExchange(ref _value, updated, initial) != initial);
    }
}

public sealed class Gauge(string name, string help, string[] labelNames)
    : MetricFamily(name, help, "gauge", labelNames)
{
    private readonly ConcurrentDictionary<string, GaugeChild> _children = new();

    public GaugeChild WithLabels(params string[] labelValues)
        => _children.GetOrAdd(ChildKey(labelValues), _ => new GaugeChild(labelValues));

    private protected override void RenderSamples(StringBuilder sb)
    {
        foreach (var child in _children.Values)
            AppendSample(sb, child.LabelValues, child.Value);
    }
}

public sealed class GaugeChild(string[] labelValues)
{
    internal readonly string[] LabelValues = labelValues;
    private double _value;

    internal double Value => Volatile.Read(ref _value);

    public void Set(double value) => Volatile.Write(ref _value, value);
}

public sealed class Histogram : MetricFamily
{
    private readonly double[] _buckets;
    private readonly ConcurrentDictionary<string, HistogramChild> _children = new();

    internal Histogram(string name, string help, double[] buckets, string[] labelNames)
        : base(name, help, "histogram", labelNames)
    {
        _buckets = [.. buckets.OrderBy(b => b)];
    }

    public HistogramChild WithLabels(params string[] labelValues)
        => _children.GetOrAdd(ChildKey(labelValues), _ => new HistogramChild(labelValues, _buckets));

    private protected override void RenderSamples(StringBuilder sb)
    {
        foreach (var child in _children.Values)
        {
            long cumulative = 0;
            for (var i = 0; i < _buckets.Length; i++)
            {
                cumulative += child.BucketCount(i);
                AppendSample(sb, child.LabelValues, cumulative, "_bucket", ("le", Num(_buckets[i])));
            }
            AppendSample(sb, child.LabelValues, child.Count, "_bucket", ("le", "+Inf"));
            AppendSample(sb, child.LabelValues, child.Sum, "_sum");
            AppendSample(sb, child.LabelValues, child.Count, "_count");
        }
    }
}

public sealed class HistogramChild(string[] labelValues, double[] buckets)
{
    internal readonly string[] LabelValues = labelValues;
    private readonly long[] _bucketCounts = new long[buckets.Length];
    private long _count;
    private double _sum;

    internal long BucketCount(int index) => Interlocked.Read(ref _bucketCounts[index]);
    internal long Count => Interlocked.Read(ref _count);
    internal double Sum => Volatile.Read(ref _sum);

    public void Observe(double value)
    {
        for (var i = 0; i < buckets.Length; i++)
        {
            if (value <= buckets[i])
            {
                Interlocked.Increment(ref _bucketCounts[i]);
                break;
            }
        }
        Interlocked.Increment(ref _count);
        double initial, updated;
        do
        {
            initial = Volatile.Read(ref _sum);
            updated = initial + value;
        } while (Interlocked.CompareExchange(ref _sum, updated, initial) != initial);
    }
}
