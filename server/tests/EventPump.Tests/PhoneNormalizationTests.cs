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
    private static readonly TrackingPlan ListedCodePlan = TrackingPlan.Parse(
        """{ "attributes": { "phone": { "type": "e164", "max_length": 16, "country_codes": ["44"] } }, "events": {} }""");

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
    [InlineData("447911123456", "+447911123456")]     // UK mobile without +
    [InlineData(" 447911123456 ", "+447911123456")]
    [InlineData("442071234567", "+442071234567")]     // London landline without +
    [InlineData("+447911123456", "+447911123456")]
    [InlineData("+491711234567", "+491711234567")]    // any country with +
    [InlineData("+14155550123", "+14155550123")]
    [InlineData("+77011234567", "+77011234567")]
    [InlineData("+390612345678", "+390612345678")]    // Italy keeps its leading 0
    // The national 0 kept after the code is dropped, so the stored value (and
    // every destination hash of it) is the one the destination computes.
    [InlineData("4407911123456", "+447911123456")]
    [InlineData("+4407911123456", "+447911123456")]
    [InlineData("+4901711234567", "+491711234567")]
    public void Accepted_phones_are_stored_in_e164(string sent, string stored)
    {
        Assert.Equal((stored, (string?)null), Normalize(ListedCodePlan, sent));
    }

    [Theory]
    [InlineData("+07911123456")]      // a national number with a + stuck on
    [InlineData("+99912345678")]      // 999 is not a calling code
    public void Phones_with_plus_but_no_real_calling_code_are_dropped_not_rejected(string sent)
    {
        // These passed before country_codes existed, so they must not fail an
        // installed client's whole call; no destination could match them, so
        // they aren't stored either.
        var (attributes, error) = ParsePhone(ListedCodePlan, sent);
        Assert.Null(error);
        Assert.Equal("{}", attributes!.AttributesJson);
        Assert.True(attributes.IsEmpty);
        Assert.Equal(["phone"], attributes.Dropped);
    }

    [Theory]
    [InlineData("44123456")]          // Norwegian mobile: starts with 44 but too short for the UK
    [InlineData("44791112345678")]    // too long for the UK
    [InlineData("4479111234")]        // UK mobile with digits missing
    [InlineData("7911123456")]        // UK national number without its 0
    [InlineData("07911123456")]       // UK national number
    [InlineData("00447911123456")]    // 00 prefix
    [InlineData("491711234567")]      // valid German number, but 49 isn't listed
    [InlineData("44 7911 123456")]    // separators
    [InlineData("+44 7911 123456")]
    [InlineData("44-7911-123-456")]
    [InlineData("447911123456789")]
    [InlineData("")]
    public void Rejected_phones_fail_with_invalid_attribute(string sent)
    {
        Assert.Equal((null, "invalid_attribute:phone"), Normalize(ListedCodePlan, sent));
    }

    [Fact]
    public void Without_country_codes_a_phone_needs_its_plus()
    {
        Assert.Equal((null, "invalid_attribute:phone"), Normalize(NoCodesPlan, "447911123456"));
        Assert.Equal(("+447911123456", (string?)null), Normalize(NoCodesPlan, "+447911123456"));
    }
}
