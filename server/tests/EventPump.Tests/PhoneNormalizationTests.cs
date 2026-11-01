using System.Text.Json;
using EventPump.Api;
using EventPump.Config;
using Xunit;

namespace EventPump.Tests;

/// <summary>
/// SPEC §6.1 `e164` normalization, in memory: IdentityValidation.Parse only,
/// no database. UserAttributesTests covers the stored value end to end.
/// </summary>
public class PhoneNormalizationTests
{
    private static readonly TrackingPlan IraqPlan = TrackingPlan.Parse(
        """{ "attributes": { "phone": { "type": "e164", "max_length": 16, "country_codes": ["964"] } }, "events": {} }""");

    private static readonly TrackingPlan NoCodesPlan = TrackingPlan.Parse(
        """{ "attributes": { "phone": { "type": "e164", "max_length": 16 } }, "events": {} }""");

    private static (string? Phone, string? Error) Normalize(TrackingPlan plan, string phone)
    {
        var (attributes, error) = ParsePhone(plan, phone);
        if (attributes is null) return (null, error);
        using var stored = JsonDocument.Parse(attributes.AttributesJson);
        return (stored.RootElement.GetProperty("phone").GetString(), error);
    }

    private static (IdentityValidation.ParsedAttributes? Attributes, string? Error) ParsePhone(
        TrackingPlan plan, string phone)
    {
        using var doc = JsonDocument.Parse(
            $$"""
            { "session_key": "{{Guid.NewGuid()}}", "anonymous_id": "{{Guid.NewGuid()}}",
              "user_id": "u-1", "attributes": { "phone": "{{JsonEncodedText.Encode(phone)}}" } }
            """);
        var (_, attributes, error) = IdentityValidation.Parse(doc.RootElement, plan);
        return (attributes, error);
    }

    [Theory]
    [InlineData("9647701234567", "+9647701234567")]   // Iraqi mobile without +
    [InlineData(" 9647701234567 ", "+9647701234567")]
    [InlineData("96412345678", "+96412345678")]       // Baghdad landline without +
    [InlineData("+9647701234567", "+9647701234567")]
    [InlineData("+447911123456", "+447911123456")]    // any country with +
    [InlineData("+14155550123", "+14155550123")]
    [InlineData("+77011234567", "+77011234567")]
    [InlineData("+390612345678", "+390612345678")]    // Italy keeps its leading 0
    // The national 0 kept after the code is dropped, so the stored value (and
    // every destination hash of it) is the one the destination computes.
    [InlineData("96407701234567", "+9647701234567")]
    [InlineData("+96407701234567", "+9647701234567")]
    [InlineData("+4407911123456", "+447911123456")]
    public void Accepted_phones_are_stored_in_e164(string sent, string stored)
    {
        Assert.Equal((stored, (string?)null), Normalize(IraqPlan, sent));
    }

    [Theory]
    [InlineData("+07701234567")]      // a national number with a + stuck on
    [InlineData("+99912345678")]      // 999 is not a calling code
    public void Phones_with_plus_but_no_real_calling_code_are_dropped_not_rejected(string sent)
    {
        // These passed before country_codes existed, so they must not fail an
        // installed client's whole call; no destination could match them, so
        // they aren't stored either.
        var (attributes, error) = ParsePhone(IraqPlan, sent);
        Assert.Null(error);
        Assert.Equal("{}", attributes!.AttributesJson);
        Assert.True(attributes.IsEmpty);
        Assert.Equal(["phone"], attributes.Dropped);
    }

    [Theory]
    [InlineData("9641234567")]        // Indian mobile: starts with 964 but too short for Iraq
    [InlineData("9641234567890")]     // too long for Iraq
    [InlineData("96477012345")]       // Iraqi mobile with digits missing
    [InlineData("7701234567")]        // Iraqi national number without its 0
    [InlineData("07701234567")]       // Iraqi national number
    [InlineData("009647701234567")]   // 00 prefix
    [InlineData("447911123456")]      // valid UK number, but 44 isn't listed
    [InlineData("964 770 123 4567")]  // separators
    [InlineData("+964 770 123 4567")]
    [InlineData("964-770-123-4567")]
    [InlineData("9647701234567890")]
    [InlineData("")]
    public void Rejected_phones_fail_with_invalid_attribute(string sent)
    {
        Assert.Equal((null, "invalid_attribute:phone"), Normalize(IraqPlan, sent));
    }

    [Fact]
    public void Without_country_codes_a_phone_needs_its_plus()
    {
        Assert.Equal((null, "invalid_attribute:phone"), Normalize(NoCodesPlan, "9647701234567"));
        Assert.Equal(("+9647701234567", (string?)null), Normalize(NoCodesPlan, "+9647701234567"));
    }
}
