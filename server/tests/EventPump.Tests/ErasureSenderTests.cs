using System.Net;
using System.Text.Json;
using EventPump.Config;
using EventPump.Senders;
using EventPump.Worker;
using Xunit;

namespace EventPump.Tests;

public class ErasureSenderTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            Requests.Add((r, body));
            return responder(r);
        }
    }

    private static Stub Ok() => new(_ =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

    private static Stub Status(HttpStatusCode code) => new(_ =>
        new HttpResponseMessage(code) { Content = new StringContent("{}") });

    private static readonly TrackingPlan Plan = TrackingPlan.Parse(
        """{ "attributes": { "email": { "type": "email" }, "phone": { "type": "e164" } } }""");

    private static TenantConfig Tenant() => new()
    {
        AppId = "zainmart",
        TenantApiKey = "k",
        InternalToken = "t",
        MoEngageEnabled = true,
        MoEngageAppId = "MOE-APP",
        MoEngageApiKey = "moe-key",
        AdjustEnabled = true,
        AdjustAppToken = "adj-token",
        AmplitudeEnabled = true,
        AmplitudeApiKey = "amp-key",
        AmplitudeSecretKey = "amp-secret",
        Plan = Plan,
    };

    private static DeliveryItem Item(
        string destination, string eventName, string? userId = "u-1", string context = "{}",
        DateTime? leaseExpiresAt = null) =>
        new("zainmart", 1, DateTime.UtcNow, destination, 0, Guid.NewGuid(), eventName,
            "server", DateTime.UtcNow, userId, null, null, "{}", context, null, leaseExpiresAt);

    private static DeliveryItem Person(
        string destination, string context = "{}", DateTime? leaseExpiresAt = null) =>
        Item(destination, TrackingPlan.ErasureRequestedEventName,
             context: context, leaseExpiresAt: leaseExpiresAt);

    [Fact]
    public async Task Moengage_deletes_under_the_handle_it_knows_the_person_by()
    {
        var stub = Ok();
        var sender = new MoEngageErasureSender(Tenant(), 5000, stub);

        var result = await sender.SendAsync(
            Person("moengage_erasure", """{"moengage_customer_id":"M-42"}"""), default);

        Assert.Equal(SendOutcome.Delivered, result.Outcome);
        var (request, body) = Assert.Single(stub.Requests);
        Assert.Equal(
            "https://api-01.moengage.com/v1/customer/delete?app_id=MOE-APP",
            request.RequestUri!.ToString());
        using var payload = JsonDocument.Parse(body);
        Assert.Equal("customer_id", payload.RootElement.GetProperty("identity_type").GetString());
        Assert.Equal("M-42", payload.RootElement.GetProperty("identity_value").GetString());
    }

    [Fact]
    public async Task Moengage_falls_back_to_our_user_id_when_no_handle_was_recorded()
    {
        var stub = Ok();
        var sender = new MoEngageErasureSender(Tenant(), 5000, stub);

        await sender.SendAsync(Person("moengage_erasure"), default);

        var (_, body) = Assert.Single(stub.Requests);
        Assert.Contains("u-1", body);
    }

    [Fact]
    public async Task Moengage_nulls_attributes_for_the_attributes_variant()
    {
        var stub = Ok();
        var sender = new MoEngageErasureSender(Tenant(), 5000, stub);

        var result = await sender.SendAsync(
            Item("moengage_erasure", TrackingPlan.AttributesErasureRequestedEventName,
                 context: """{"moengage_customer_id":"M-42"}"""), default);

        Assert.Equal(SendOutcome.Delivered, result.Outcome);
        var (request, body) = Assert.Single(stub.Requests);
        Assert.Equal("https://api-01.moengage.com/v1/customer/MOE-APP",
                     request.RequestUri!.ToString());
        using var payload = JsonDocument.Parse(body);
        var attributes = payload.RootElement.GetProperty("attributes");
        Assert.Equal(JsonValueKind.Null, attributes.GetProperty("email").ValueKind);
        Assert.Equal(JsonValueKind.Null, attributes.GetProperty("mobile").ValueKind);
    }

    [Fact]
    public async Task Moengage_skips_when_the_gate_is_off_and_sends_nothing()
    {
        var stub = Ok();
        var sender = new MoEngageErasureSender(
            Tenant() with { MoEngageErasureEnabled = false }, 5000, stub);

        var result = await sender.SendAsync(Person("moengage_erasure"), default);

        Assert.Equal(SendOutcome.Skip, result.Outcome);
        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Throttling_and_server_faults_retry(HttpStatusCode code)
    {
        var sender = new MoEngageErasureSender(Tenant(), 5000, Status(code));

        var result = await sender.SendAsync(Person("moengage_erasure"), default);

        Assert.Equal(SendOutcome.Retry, result.Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_rejected_erasure_goes_dead_rather_than_retrying_forever(HttpStatusCode code)
    {
        var sender = new MoEngageErasureSender(Tenant(), 5000, Status(code));

        var result = await sender.SendAsync(Person("moengage_erasure"), default);

        Assert.Equal(SendOutcome.Dead, result.Outcome);
    }

    [Fact]
    public async Task Adjust_forgets_the_device_it_recorded()
    {
        var stub = Ok();
        var sender = new AdjustErasureSender(Tenant(), 5000, stub);

        var result = await sender.SendAsync(
            Person("adjust_erasure", """{"adjust_adid":"ADID-9"}"""), default);

        Assert.Equal(SendOutcome.Delivered, result.Outcome);
        var (request, _) = Assert.Single(stub.Requests);
        Assert.StartsWith("https://gdpr.adjust.com/gdpr_forget_device", request.RequestUri!.ToString());
        Assert.Contains("adid=ADID-9", request.RequestUri!.Query);
        Assert.Contains("app_token=adj-token", request.RequestUri!.Query);
        // Unset stays unset: a tenant configured before this field existed
        // sends the URL it always sent.
        Assert.DoesNotContain("environment=", request.RequestUri!.Query);
    }

    /// <summary>
    /// The erasure is scoped the same way the event send is. Unscoped, a
    /// sandbox tenant's forget names a device the production scope has never
    /// seen — Adjust answers 200 for an unknown device, so the row settles
    /// `delivered` with nothing forgotten — and a UAT handset's real
    /// production install is what sits at that scope waiting to be erased by
    /// mistake.
    /// </summary>
    [Fact]
    public async Task Adjust_forgets_within_the_environment_the_tenant_configured()
    {
        var stub = Ok();
        var sender = new AdjustErasureSender(
            Tenant() with { AdjustEnvironment = "sandbox" }, 5000, stub);

        var result = await sender.SendAsync(
            Person("adjust_erasure", """{"adjust_adid":"ADID-9"}"""), default);

        Assert.Equal(SendOutcome.Delivered, result.Outcome);
        var (request, _) = Assert.Single(stub.Requests);
        Assert.Contains("environment=sandbox", request.RequestUri!.Query);
    }

    [Theory]
    [InlineData("android", "gps_adid")]
    [InlineData("Android", "gps_adid")]
    [InlineData("ios", "idfa")]
    [InlineData("iOS 17.2", "idfa")]
    public async Task Adjust_sends_a_raw_platform_ad_id_under_the_parameter_its_os_names(
        string os, string parameter)
    {
        var stub = Ok();
        var sender = new AdjustErasureSender(Tenant(), 5000, stub);

        // A GAID sent as `adid` matches no device and Adjust still answers
        // 200, so the row would read `delivered` with nothing forgotten.
        var result = await sender.SendAsync(
            Person("adjust_erasure",
                   $$"""{"adjust_platform_ad_id":"RAW-7","os":"{{os}}"}"""), default);

        Assert.Equal(SendOutcome.Delivered, result.Outcome);
        var (request, _) = Assert.Single(stub.Requests);
        Assert.Contains($"&{parameter}=RAW-7", request.RequestUri!.Query);
        // `&adid=`, not `adid=` — that is a suffix of `gps_adid=`.
        Assert.DoesNotContain("&adid=", request.RequestUri!.Query);
    }

    [Fact]
    public async Task Adjust_prefers_its_own_device_id_over_the_platform_ad_id()
    {
        var stub = Ok();
        var sender = new AdjustErasureSender(Tenant(), 5000, stub);

        await sender.SendAsync(
            Person("adjust_erasure",
                   """{"adjust_adid":"ADID-9","adjust_platform_ad_id":"RAW-7","os":"ios"}"""),
            default);

        var (request, _) = Assert.Single(stub.Requests);
        Assert.Contains("adid=ADID-9", request.RequestUri!.Query);
        Assert.DoesNotContain("idfa=", request.RequestUri!.Query);
    }

    [Theory]
    [InlineData("""{"adjust_platform_ad_id":"RAW-7"}""")]
    [InlineData("""{"adjust_platform_ad_id":"RAW-7","os":"tvos"}""")]
    public async Task Adjust_will_not_guess_which_parameter_an_ad_id_belongs_in(string context)
    {
        var stub = Ok();
        var sender = new AdjustErasureSender(Tenant(), 5000, stub);

        var result = await sender.SendAsync(Person("adjust_erasure", context), default);

        Assert.Equal(SendOutcome.Skip, result.Outcome);
        Assert.Equal("no_adjust_device", result.Detail);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Adjust_skips_rather_than_claiming_success_with_no_device()
    {
        var stub = Ok();
        var sender = new AdjustErasureSender(Tenant(), 5000, stub);

        var result = await sender.SendAsync(Person("adjust_erasure"), default);

        Assert.Equal(SendOutcome.Skip, result.Outcome);
        Assert.Equal("no_adjust_device", result.Detail);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Adjust_marks_the_call_server_to_server()
    {
        var stub = Ok();
        var sender = new AdjustErasureSender(Tenant(), 5000, stub);

        // The event endpoint sends s2s=1; without it here Adjust can reject
        // the call, and any non-429/non-5xx is recorded `dead` — one attempt
        // and the erasure is abandoned for good.
        await sender.SendAsync(Person("adjust_erasure", """{"adjust_adid":"ADID-9"}"""), default);

        var (request, _) = Assert.Single(stub.Requests);
        Assert.Contains("s2s=1", request.RequestUri!.Query);
    }

    [Fact]
    public async Task Adjust_forgets_every_device_the_person_was_seen_on()
    {
        var stub = Ok();
        var sender = new AdjustErasureSender(Tenant(), 5000, stub);

        // gdpr_forget_device erases one device. Sending only the newest leaves
        // the older phone tracked while this row reads `delivered`.
        var result = await sender.SendAsync(
            Person("adjust_erasure",
                   """
                   {"adjust_devices":[{"adid":"ADID-new"},
                                      {"platform_ad_id":"GAID-old","os":"android"}]}
                   """),
            default);

        Assert.Equal(SendOutcome.Delivered, result.Outcome);
        Assert.Equal(2, stub.Requests.Count);
        Assert.Contains(stub.Requests, r => r.Request.RequestUri!.Query.Contains("adid=ADID-new"));
        Assert.Contains(stub.Requests, r => r.Request.RequestUri!.Query.Contains("gps_adid=GAID-old"));
    }

    [Fact]
    public async Task Adjust_names_the_devices_it_could_not_forget()
    {
        var attempts = 0;
        var stub = new Stub(_ => new HttpResponseMessage(
            attempts++ == 0 ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
        { Content = new StringContent("{}") });
        var sender = new AdjustErasureSender(Tenant(), 5000, stub);

        var result = await sender.SendAsync(
            Person("adjust_erasure",
                   """{"adjust_devices":[{"adid":"ADID-1"},{"adid":"ADID-2"}]}"""),
            default);

        // A partly completed erasure must not read like one failed call.
        Assert.Equal(SendOutcome.Dead, result.Outcome);
        Assert.Equal("http_400 (1/2 forgotten)", result.Detail);
    }

    [Fact]
    public async Task Adjust_retries_the_whole_delivery_when_one_device_fails_transiently()
    {
        var attempts = 0;
        var stub = new Stub(_ => new HttpResponseMessage(
            attempts++ == 0 ? HttpStatusCode.BadRequest : HttpStatusCode.ServiceUnavailable)
        { Content = new StringContent("{}") });
        var sender = new AdjustErasureSender(Tenant(), 5000, stub);

        var result = await sender.SendAsync(
            Person("adjust_erasure",
                   """{"adjust_devices":[{"adid":"ADID-1"},{"adid":"ADID-2"}]}"""),
            default);

        // Re-driving is the only way the transient one gets another attempt,
        // and forget_device is idempotent, so the rest cost nothing again.
        Assert.Equal(SendOutcome.Retry, result.Outcome);
    }

    [Fact]
    public async Task Adjust_will_not_call_a_person_forgotten_over_a_device_it_cannot_address()
    {
        var stub = Ok();
        var sender = new AdjustErasureSender(Tenant(), 5000, stub);

        // One device we can send, one whose os we cannot classify. Reporting
        // `delivered` here is the false success this sender exists to avoid --
        // the second device is held and still tracked.
        var result = await sender.SendAsync(
            Person("adjust_erasure",
                   """
                   {"adjust_devices":[{"adid":"ADID-1"},
                                      {"platform_ad_id":"RAW-7","os":"tvos"}]}
                   """),
            default);

        Assert.Equal(SendOutcome.Dead, result.Outcome);
        Assert.Equal("no_adjust_device (1/2 forgotten)", result.Detail);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Adjust_stops_the_pass_at_the_first_transient_failure()
    {
        var stub = Status(HttpStatusCode.ServiceUnavailable);
        var sender = new AdjustErasureSender(Tenant(), 5000, stub);

        var result = await sender.SendAsync(
            Person("adjust_erasure",
                   """{"adjust_devices":[{"adid":"A-1"},{"adid":"A-2"},{"adid":"A-3"}]}"""),
            default);

        // Adjust being unreachable is not a fact about one device; walking the
        // rest spends a sender timeout apiece to learn the same thing.
        Assert.Equal(SendOutcome.Retry, result.Outcome);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Adjust_stops_the_fan_out_while_the_lease_still_holds()
    {
        var stub = Ok();
        var sender = new AdjustErasureSender(Tenant(), 5000, stub);

        // Running past the lease lets a second worker re-claim the row and
        // make the same calls alongside this pass.
        var result = await sender.SendAsync(
            Person("adjust_erasure",
                   """{"adjust_devices":[{"adid":"A-1"},{"adid":"A-2"}]}""",
                   leaseExpiresAt: DateTime.UtcNow.AddSeconds(1)),
            default);

        Assert.Equal(SendOutcome.Retry, result.Outcome);
        Assert.Equal("lease_expiring (0/2 forgotten)", result.Detail);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Amplitude_deletes_by_user_and_device()
    {
        var stub = Ok();
        var sender = new AmplitudeErasureSender(Tenant(), 5000, stub);

        var result = await sender.SendAsync(
            Person("amplitude_erasure",
                   """{"amplitude_user_id":"AU-1","amplitude_device_id":"AD-1"}"""), default);

        Assert.Equal(SendOutcome.Delivered, result.Outcome);
        var (request, body) = Assert.Single(stub.Requests);
        Assert.Equal("https://amplitude.com/api/2/deletions/users",
                     request.RequestUri!.ToString());
        Assert.Contains("AU-1", body);
        Assert.Contains("AD-1", body);
    }

    [Fact]
    public async Task Amplitude_deletes_every_device_in_one_request()
    {
        var stub = Ok();
        var sender = new AmplitudeErasureSender(Tenant(), 5000, stub);

        var result = await sender.SendAsync(
            Person("amplitude_erasure",
                   """{"amplitude_device_ids":["AD-1","AD-2"]}"""), default);

        Assert.Equal(SendOutcome.Delivered, result.Outcome);
        var (_, body) = Assert.Single(stub.Requests);
        using var payload = JsonDocument.Parse(body);
        Assert.Equal(
            ["AD-1", "AD-2"],
            payload.RootElement.GetProperty("device_ids").EnumerateArray().Select(d => d.GetString()));
    }

    [Fact]
    public async Task Amplitude_chunks_a_device_list_too_long_for_one_request()
    {
        var stub = Ok();
        var sender = new AmplitudeErasureSender(Tenant(), 5000, stub);
        var deviceIds = string.Join(",", Enumerable.Range(0, 150).Select(i => $"\"AD-{i}\""));

        // Sent as one array this is a payload Amplitude can answer 4xx to, and
        // a 4xx is recorded `dead` on the first attempt -- an erasure nobody
        // can retry.
        var result = await sender.SendAsync(
            Person("amplitude_erasure", $$"""{"amplitude_device_ids":[{{deviceIds}}]}"""),
            default);

        Assert.Equal(SendOutcome.Delivered, result.Outcome);
        Assert.Equal(2, stub.Requests.Count);
        using var first = JsonDocument.Parse(stub.Requests[0].Body);
        using var second = JsonDocument.Parse(stub.Requests[1].Body);
        Assert.Equal(100, first.RootElement.GetProperty("device_ids").GetArrayLength());
        Assert.Equal(50, second.RootElement.GetProperty("device_ids").GetArrayLength());
        // The person delete rides the first chunk only.
        Assert.True(first.RootElement.TryGetProperty("user_ids", out _));
        Assert.False(second.RootElement.TryGetProperty("user_ids", out _));
    }

    [Fact]
    public async Task Amplitude_lets_an_id_it_holds_nothing_under_come_back_as_a_failure()
    {
        var stub = Ok();
        var sender = new AmplitudeErasureSender(Tenant(), 5000, stub);

        await sender.SendAsync(Person("amplitude_erasure"), default);

        // ignore_invalid_id:true would answer 2xx for an id Amplitude never
        // held — a DSR recorded `delivered` against a profile never touched.
        var (_, body) = Assert.Single(stub.Requests);
        using var payload = JsonDocument.Parse(body);
        Assert.False(payload.RootElement.GetProperty("ignore_invalid_id").GetBoolean());
    }

    [Fact]
    public async Task Amplitude_skips_when_the_secret_key_is_missing()
    {
        var stub = Ok();
        var sender = new AmplitudeErasureSender(
            Tenant() with { AmplitudeSecretKey = "" }, 5000, stub);

        var result = await sender.SendAsync(Person("amplitude_erasure"), default);

        Assert.Equal(SendOutcome.Skip, result.Outcome);
        Assert.Equal("no_secret_key", result.Detail);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Ga4_records_the_gap_instead_of_stranding_the_row()
    {
        var sender = new Ga4ErasureSender(Tenant());

        var result = await sender.SendAsync(Person("ga4_erasure"), default);

        Assert.Equal(SendOutcome.Skip, result.Outcome);
        Assert.Equal("ga4_oauth_not_configured", result.Detail);
    }
}
