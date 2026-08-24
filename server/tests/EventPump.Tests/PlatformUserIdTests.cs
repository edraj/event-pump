using System.Net;
using System.Text;
using System.Text.Json;
using EventPump.Config;
using EventPump.Senders;
using EventPump.Worker;
using Xunit;

namespace EventPump.Tests;

/// <summary>
/// Migration 0010 + SPEC follow-up 2026-07-28: identify() carries per-
/// destination user identifiers. Each sender must prefer its own handle
/// when set, and fall back to the generic user_id when null.
/// </summary>
public class PlatformUserIdTests
{
    private static readonly Guid EventId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid SessionKey = Guid.Parse("018f4d5e-7b20-7abc-8def-0123456789ab");
    private static readonly Guid AnonymousId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Bodies.Add(r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private static TenantConfig Tenant() => TenantConfig.FromLegacyEnvironment(new EpConfig
    {
        DbConnString = "unused",
        Ga4Enabled = true, Ga4ApiSecret = "s", Ga4MeasurementId = "G-T",
        AmplitudeEnabled = true, AmplitudeApiKey = "amp",
        MoEngageEnabled = true, MoEngageAppId = "MOE", MoEngageApiKey = "k",
        MetaEnabled = true, MetaPixelId = "px", MetaAccessToken = "tok",
    }, TrackingPlan.Parse(
        """
        {
          "events": {
            "order_placed": {
              "origin": "server",
              "destinations": ["ga4", "amplitude", "moengage", "meta"]
            }
          }
        }
        """));

    private static IdentitySnapshot Identity(
        string? moengage = null, string? ga4 = null,
        string? amplitude = null, string? meta = null) => new(
            AnonymousId: AnonymousId,
            UserId: "app-42",
            SessionNumber: 1,
            Ga4ClientId: "c1", Ga4SessionId: "s1",
            FirebaseAppInstanceId: null,
            AmplitudeDeviceId: "d1",
            AdjustAdid: null, AdjustPlatformAdId: null,
            Fbp: null, Fbc: null, ClickIdsJson: "{}",
            ContextJson: "{}", ClientIp: null,
            MoEngageCustomerId: moengage,
            Ga4UserId: ga4,
            AmplitudeUserId: amplitude,
            MetaExternalId: meta);

    private static DeliveryItem Item(
        string destination, IdentitySnapshot identity, string? userId = "app-42") => new(
        AppId: "zainmart", EventRef: 1, ReceivedAt: DateTime.UtcNow, Destination: destination, Attempts: 0,
        EventId: EventId, EventName: "order_placed", Origin: "server", OccurredAt: DateTime.UtcNow,
        UserId: userId, AnonymousId: AnonymousId, SessionKey: SessionKey,
        PropertiesJson: "{}", ContextJson: "{}", Identity: identity);

    [Fact]
    public async Task Ga4_uses_ga4_user_id_when_set()
    {
        var stub = new StubHandler();
        await new Ga4Sender(Tenant(), 10_000, handler: stub)
            .SendAsync(Item("ga4", Identity(ga4: "G-42")), default);
        using var doc = JsonDocument.Parse(stub.Bodies.Single());
        Assert.Equal("G-42", doc.RootElement.GetProperty("user_id").GetString());
    }

    [Fact]
    public async Task Ga4_falls_back_to_generic_user_id_when_ga4_user_id_unset()
    {
        var stub = new StubHandler();
        await new Ga4Sender(Tenant(), 10_000, handler: stub)
            .SendAsync(Item("ga4", Identity()), default);
        using var doc = JsonDocument.Parse(stub.Bodies.Single());
        Assert.Equal("app-42", doc.RootElement.GetProperty("user_id").GetString());
    }

    [Fact]
    public async Task Amplitude_uses_amplitude_user_id_when_set()
    {
        var stub = new StubHandler();
        await new AmplitudeSender(Tenant(), 10_000, handler: stub)
            .SendAsync(Item("amplitude", Identity(amplitude: "A-42")), default);
        using var doc = JsonDocument.Parse(stub.Bodies.Single());
        Assert.Equal("A-42",
            doc.RootElement.GetProperty("events")[0].GetProperty("user_id").GetString());
    }

    [Fact]
    public async Task MoEngage_uses_moengage_customer_id_when_set()
    {
        var stub = new StubHandler();
        await new MoEngageSender(Tenant(), 10_000, handler: stub)
            .SendAsync(Item("moengage", Identity(moengage: "M-42")), default);
        using var doc = JsonDocument.Parse(stub.Bodies.Single());
        Assert.Equal("M-42", doc.RootElement.GetProperty("customer_id").GetString());
    }

    [Fact]
    public async Task Meta_uses_meta_external_id_when_set()
    {
        var stub = new StubHandler();
        await new MetaCapiSender(Tenant(), 10_000, handler: stub)
            .SendAsync(Item("meta", Identity(meta: "META-42")), default);
        using var doc = JsonDocument.Parse(stub.Bodies.Single());
        var externalId = doc.RootElement.GetProperty("data")[0]
            .GetProperty("user_data").GetProperty("external_id").GetString();
        // Meta hashes external_id before sending (SPEC §12), so we check that
        // the hash is of META-42 rather than app-42.
        Assert.Equal(PixelPlatformSender.Sha256Lower("META-42"), externalId);
    }

    // ---------------------------------------------------------------- stale handles
    //
    // The senders join an event to the CURRENT identity row. A handle recorded
    // for user A therefore reaches an event belonging to user B whenever the
    // two disagree — the account was switched mid-session, or the event was
    // queued before the switch and delivered after it. Shipping A's analytics
    // id on B's event merges two people into one profile, so the event's own
    // user_id must win. (EventStore.UpsertIdentityAsync clears the handles on
    // a switch; these guard the read side, which also covers the queued-event
    // race the upsert cannot see.)

    [Fact]
    public async Task Ga4_ignores_a_handle_recorded_for_a_different_user()
    {
        var stub = new StubHandler();
        await new Ga4Sender(Tenant(), 10_000, handler: stub)
            .SendAsync(Item("ga4", Identity(ga4: "G-42"), userId: "app-99"), default);
        using var doc = JsonDocument.Parse(stub.Bodies.Single());
        Assert.Equal("app-99", doc.RootElement.GetProperty("user_id").GetString());
    }

    [Fact]
    public async Task Amplitude_ignores_a_handle_recorded_for_a_different_user()
    {
        var stub = new StubHandler();
        await new AmplitudeSender(Tenant(), 10_000, handler: stub)
            .SendAsync(Item("amplitude", Identity(amplitude: "A-42"), userId: "app-99"), default);
        using var doc = JsonDocument.Parse(stub.Bodies.Single());
        Assert.Equal("app-99",
            doc.RootElement.GetProperty("events")[0].GetProperty("user_id").GetString());
    }

    [Fact]
    public async Task MoEngage_ignores_a_handle_recorded_for_a_different_user()
    {
        var stub = new StubHandler();
        await new MoEngageSender(Tenant(), 10_000, handler: stub)
            .SendAsync(Item("moengage", Identity(moengage: "M-42"), userId: "app-99"), default);
        using var doc = JsonDocument.Parse(stub.Bodies.Single());
        Assert.Equal("app-99", doc.RootElement.GetProperty("customer_id").GetString());
    }

    [Fact]
    public async Task Meta_ignores_a_handle_recorded_for_a_different_user()
    {
        var stub = new StubHandler();
        await new MetaCapiSender(Tenant(), 10_000, handler: stub)
            .SendAsync(Item("meta", Identity(meta: "META-42"), userId: "app-99"), default);
        using var doc = JsonDocument.Parse(stub.Bodies.Single());
        var externalId = doc.RootElement.GetProperty("data")[0]
            .GetProperty("user_data").GetProperty("external_id").GetString();
        Assert.Equal(PixelPlatformSender.Sha256Lower("app-99"), externalId);
    }

    [Fact]
    public async Task Handle_still_wins_when_the_event_names_nobody()
    {
        // A client event with no user_id of its own inherits the session's
        // person, handle included — this is the ordinary anonymous-page-view
        // shape and must not be caught by the guard above.
        var stub = new StubHandler();
        await new Ga4Sender(Tenant(), 10_000, handler: stub)
            .SendAsync(Item("ga4", Identity(ga4: "G-42"), userId: null), default);
        using var doc = JsonDocument.Parse(stub.Bodies.Single());
        Assert.Equal("G-42", doc.RootElement.GetProperty("user_id").GetString());
    }
}
