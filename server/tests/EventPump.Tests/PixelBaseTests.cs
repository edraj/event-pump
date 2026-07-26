using EventPump.Senders;
using EventPump.Worker;
using Xunit;

namespace EventPump.Tests;

public class PixelBaseTests
{
    // sha256("user@example.com")
    private const string UserExampleComSha =
        "b4c9a289323b21a01c3e940f150eb9b8c542587f1abfd8f0e1cc1ffc5e475514";

    [Fact]
    public void Email_is_trimmed_lowercased_then_hashed()
    {
        Assert.Equal(UserExampleComSha, PixelPlatformSender.NormalizeEmail("  User@Example.COM "));
        Assert.Equal(UserExampleComSha, PixelPlatformSender.NormalizeEmail("user@example.com"));
        Assert.Null(PixelPlatformSender.NormalizeEmail(null));
        Assert.Null(PixelPlatformSender.NormalizeEmail("   "));
    }

    [Fact]
    public void Phone_keeps_digits_only_dropping_plus_and_punctuation()
    {
        var expected = PixelPlatformSender.Sha256Lower("9647701234567");
        Assert.Equal(expected, PixelPlatformSender.NormalizePhone("+964 (770) 123-4567"));
        Assert.Equal(expected, PixelPlatformSender.NormalizePhone("9647701234567"));
        Assert.Null(PixelPlatformSender.NormalizePhone("no digits"));
    }

    [Fact]
    public async Task Consent_gating_skips_without_consent_marker()
    {
        var sender = new RecordingPixelSender(consentGating: true);
        var gated = await sender.SendAsync(Item(identityContextJson: "{}"), CancellationToken.None);
        Assert.Equal(SendOutcome.Skip, gated.Outcome);
        Assert.Equal("consent_absent", gated.Detail);
        Assert.False(sender.CoreCalled);

        var granted = await sender.SendAsync(
            Item(identityContextJson: """{"consent":{"marketing":true}}"""), CancellationToken.None);
        Assert.Equal(SendOutcome.Delivered, granted.Outcome);
        Assert.True(sender.CoreCalled);
    }

    [Fact]
    public async Task Consent_gating_off_sends_without_marker()
    {
        var sender = new RecordingPixelSender(consentGating: false);
        var result = await sender.SendAsync(Item(identityContextJson: "{}"), CancellationToken.None);
        Assert.Equal(SendOutcome.Delivered, result.Outcome);
    }

    [Fact]
    public async Task User_data_carries_hashes_and_handles_never_raw_pii()
    {
        var sender = new RecordingPixelSender(consentGating: false);
        await sender.SendAsync(Item(
            propertiesJson: """{"email":"User@Example.com","phone":"+964 770 123 4567","sku":"A1"}""",
            identityContextJson: """{"user_agent":"Mozilla/5.0 Test"}"""), CancellationToken.None);

        var userData = sender.LastUserData!;
        Assert.Equal(UserExampleComSha, userData.EmailSha256);
        Assert.Equal(PixelPlatformSender.Sha256Lower("9647701234567"), userData.PhoneSha256);
        Assert.Equal("fb.1.1700000000.abc", userData.Fbc);
        Assert.Equal("fb.1.1700000000.def", userData.Fbp);
        Assert.Equal("203.0.113.9", userData.ClientIp);
        Assert.Equal("Mozilla/5.0 Test", userData.UserAgent);
        Assert.Equal("u-42", userData.ExternalId);
        // raw PII must never reach the payload assembly
        Assert.DoesNotContain("User@Example.com", userData.ToString() ?? "");
        Assert.NotNull(userData.ClickIds);
        Assert.Contains("gclid", userData.ClickIds!);
    }

    private static DeliveryItem Item(
        string propertiesJson = "{}", string identityContextJson = "{}")
        => new(
            EventRef: 1,
            ReceivedAt: DateTime.UtcNow,
            Destination: "meta",
            Attempts: 0,
            EventId: Guid.NewGuid(),
            EventName: "order_placed",
            Origin: "server",
            OccurredAt: DateTime.UtcNow,
            UserId: "u-42",
            AnonymousId: Guid.NewGuid(),
            SessionKey: Guid.NewGuid(),
            PropertiesJson: propertiesJson,
            ContextJson: "{}",
            Identity: new IdentitySnapshot(
                AnonymousId: Guid.NewGuid(),
                UserId: "u-42",
                SessionNumber: 2,
                Ga4ClientId: null,
                Ga4SessionId: null,
                FirebaseAppInstanceId: null,
                AmplitudeDeviceId: null,
                AdjustAdid: null,
                AdjustPlatformAdId: null,
                Fbp: "fb.1.1700000000.def",
                Fbc: "fb.1.1700000000.abc",
                ClickIdsJson: """{"gclid":{"value":"g1","captured_at":"2026-07-13T00:00:00Z"}}""",
                ContextJson: identityContextJson,
                ClientIp: "203.0.113.9"));

    private sealed class RecordingPixelSender(bool consentGating)
        : PixelPlatformSender("meta", consentGating)
    {
        public bool CoreCalled { get; private set; }
        public PixelUserData? LastUserData { get; private set; }

        protected override Task<SendResult> SendCoreAsync(
            DeliveryItem item, PixelUserData userData, CancellationToken ct)
        {
            CoreCalled = true;
            LastUserData = userData;
            return Task.FromResult(SendResult.Delivered());
        }
    }
}
