using EventPump.Observability;
using Xunit;

namespace EventPump.Tests;

public class MetricsTests
{
    [Fact]
    public void Counter_renders_prometheus_text()
    {
        var reg = new MetricsRegistry();
        var counter = reg.Counter("events_ingested_total", "Events accepted at ingestion.", "origin", "endpoint");
        counter.WithLabels("client", "/v1/events").Inc();
        counter.WithLabels("client", "/v1/events").Inc(2);
        counter.WithLabels("server", "/internal/v1/events").Inc();

        var text = reg.Render();

        Assert.Contains("# HELP events_ingested_total Events accepted at ingestion.", text);
        Assert.Contains("# TYPE events_ingested_total counter", text);
        Assert.Contains("""events_ingested_total{origin="client",endpoint="/v1/events"} 3""", text);
        Assert.Contains("""events_ingested_total{origin="server",endpoint="/internal/v1/events"} 1""", text);
    }

    [Fact]
    public void Gauge_sets_and_renders_with_and_without_labels()
    {
        var reg = new MetricsRegistry();
        var pending = reg.Gauge("outbox_pending", "Deliveries currently pending.", "destination");
        pending.WithLabels("ga4").Set(42);
        pending.WithLabels("ga4").Set(7);
        var plain = reg.Gauge("up_since_seconds", "Process uptime.");
        plain.WithLabels().Set(1.5);

        var text = reg.Render();

        Assert.Contains("# TYPE outbox_pending gauge", text);
        Assert.Contains("""outbox_pending{destination="ga4"} 7""", text);
        Assert.Contains("up_since_seconds 1.5", text);
    }

    [Fact]
    public void Histogram_renders_cumulative_buckets_sum_and_count()
    {
        var reg = new MetricsRegistry();
        var h = reg.Histogram("delivery_latency_seconds", "Send latency.", [0.1, 1, 5], "destination");
        h.WithLabels("ga4").Observe(0.05);
        h.WithLabels("ga4").Observe(0.5);
        h.WithLabels("ga4").Observe(10);

        var text = reg.Render();

        Assert.Contains("# TYPE delivery_latency_seconds histogram", text);
        Assert.Contains("""delivery_latency_seconds_bucket{destination="ga4",le="0.1"} 1""", text);
        Assert.Contains("""delivery_latency_seconds_bucket{destination="ga4",le="1"} 2""", text);
        Assert.Contains("""delivery_latency_seconds_bucket{destination="ga4",le="5"} 2""", text);
        Assert.Contains("""delivery_latency_seconds_bucket{destination="ga4",le="+Inf"} 3""", text);
        Assert.Contains("""delivery_latency_seconds_sum{destination="ga4"} 10.55""", text);
        Assert.Contains("""delivery_latency_seconds_count{destination="ga4"} 3""", text);
    }

    [Fact]
    public void Label_values_are_escaped()
    {
        var reg = new MetricsRegistry();
        var c = reg.Counter("weird_total", "Escaping.", "reason");
        c.WithLabels("a\"b\\c\nd").Inc();

        var text = reg.Render();

        Assert.Contains("""weird_total{reason="a\"b\\c\nd"} 1""", text);
    }
}
