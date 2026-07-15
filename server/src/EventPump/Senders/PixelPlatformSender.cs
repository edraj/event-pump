using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EventPump.Worker;

namespace EventPump.Senders;

/// <summary>
/// Assembled user_data for pixel-platform (CAPI-style) destinations (SPEC §12).
/// PII only ever appears as SHA-256 hashes.
/// </summary>
public sealed record PixelUserData(
    string? EmailSha256,
    string? PhoneSha256,
    string? ExternalId,
    string? Fbp,
    string? Fbc,
    string? ClientIp,
    string? UserAgent,
    string? ClickIds);

/// <summary>
/// Abstract base for Meta/Snap/TikTok CAPI senders (SPEC §12): SHA-256
/// normalization of email/phone, consent gating (default OFF), and user_data
/// assembly from the identity registry. Subclasses implement the wire format.
/// </summary>
public abstract class PixelPlatformSender(string destination, bool consentGating) : IDestinationSender
{
    public string Destination => destination;

    public async Task<SendResult> SendAsync(DeliveryItem item, CancellationToken ct)
    {
        if (consentGating && !HasMarketingConsent(item))
            return SendResult.Skip("consent_absent");
        return await SendCoreAsync(item, BuildUserData(item), ct);
    }

    protected abstract Task<SendResult> SendCoreAsync(
        DeliveryItem item, PixelUserData userData, CancellationToken ct);

    // ------------------------------------------------------- normalization

    public static string Sha256Lower(string input)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    /// <summary>Trim + lowercase, then SHA-256 (Meta/TikTok/Snap shared rule).</summary>
    public static string? NormalizeEmail(string? email)
    {
        var normalized = email?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(normalized) ? null : Sha256Lower(normalized);
    }

    /// <summary>Digits only (country code kept, +/punctuation dropped), then SHA-256.</summary>
    public static string? NormalizePhone(string? phone)
    {
        if (phone is null) return null;
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 0 ? null : Sha256Lower(digits);
    }

    // ------------------------------------------------------------ assembly

    private static PixelUserData BuildUserData(DeliveryItem item)
    {
        string? email = null, phone = null;
        using (var properties = JsonDocument.Parse(item.PropertiesJson))
        {
            if (properties.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (properties.RootElement.TryGetProperty("email", out var e)
                    && e.ValueKind == JsonValueKind.String) email = e.GetString();
                if (properties.RootElement.TryGetProperty("phone", out var p)
                    && p.ValueKind == JsonValueKind.String) phone = p.GetString();
            }
        }

        var identity = item.Identity;
        email ??= identity?.Email;
        phone ??= identity?.Msisdn;
        string? userAgent = null;
        if (identity is not null)
        {
            using var context = JsonDocument.Parse(identity.ContextJson);
            if (context.RootElement.ValueKind == JsonValueKind.Object
                && context.RootElement.TryGetProperty("user_agent", out var ua)
                && ua.ValueKind == JsonValueKind.String)
                userAgent = ua.GetString();
        }

        return new PixelUserData(
            EmailSha256: NormalizeEmail(email),
            PhoneSha256: NormalizePhone(phone),
            ExternalId: item.UserId ?? identity?.UserId,
            Fbp: identity?.Fbp,
            Fbc: identity?.Fbc,
            ClientIp: identity?.ClientIp,
            UserAgent: userAgent,
            ClickIds: identity?.ClickIdsJson);
    }

    private static bool HasMarketingConsent(DeliveryItem item)
    {
        if (item.Identity is null) return false;
        using var context = JsonDocument.Parse(item.Identity.ContextJson);
        return context.RootElement.ValueKind == JsonValueKind.Object
               && context.RootElement.TryGetProperty("consent", out var consent)
               && consent.ValueKind == JsonValueKind.Object
               && consent.TryGetProperty("marketing", out var marketing)
               && marketing.ValueKind == JsonValueKind.True;
    }
}
