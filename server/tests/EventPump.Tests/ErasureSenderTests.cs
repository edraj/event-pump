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
        string destination, string eventName, string? userId = "u-1", string context = "{}") =>
        new("zainmart", 1, DateTime.UtcNow, destination, 0, Guid.NewGuid(), eventName,
            "server", DateTime.UtcNow, userId, null, null, "{}", context, null);

    private static DeliveryItem Person(string destination, string context = "{}") =>
        Item(destination, TrackingPlan.ErasureRequestedEventName, context: context);

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
