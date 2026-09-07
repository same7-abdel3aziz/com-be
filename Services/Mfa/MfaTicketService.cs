using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CompetitionManagementSystem.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CompetitionManagementSystem.Services.Mfa;

public static class MfaTicketPurposes
{
    public const string Setup = "mfa-setup";
    public const string Verify = "mfa-verify";
}

public sealed record MfaTicketContents(string UserId, string Purpose);

public interface IMfaTicketService
{
    string Issue(string userId, string purpose);
    MfaTicketContents? ValidateAndRead(string ticket, string expectedPurpose);
}

/// <summary>
/// Short-lived signed JWT carrying { sub = userId, purpose = mfa-setup|mfa-verify }.
/// Signed with the same JWT secret so verification is local. Audience is forced to
/// "mfa-ticket" so a ticket cannot be accepted by the main JwtBearer handler (which
/// validates against the API audience).
/// </summary>
public sealed class MfaTicketService : IMfaTicketService
{
    private const string TicketAudience = "mfa-ticket";
    private readonly JwtSettings _jwt;
    private readonly AdminSecurityOptions _admin;

    public MfaTicketService(IOptions<JwtSettings> jwt, IOptions<AdminSecurityOptions> admin)
    {
        _jwt = jwt.Value;
        _admin = admin.Value;
    }

    public string Issue(string userId, string purpose)
    {
        if (purpose != MfaTicketPurposes.Setup && purpose != MfaTicketPurposes.Verify)
            throw new ArgumentException("Unsupported MFA ticket purpose.", nameof(purpose));

        var ttl = TimeSpan.FromSeconds(_admin.MfaTicketTtlSeconds > 0 ? _admin.MfaTicketTtlSeconds : 300);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new("purpose", purpose),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SecretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: TicketAudience,
            claims: claims,
            expires: DateTime.UtcNow.Add(ttl),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public MfaTicketContents? ValidateAndRead(string ticket, string expectedPurpose)
    {
        if (string.IsNullOrWhiteSpace(ticket)) return null;

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SecretKey));
        var handler = new JwtSecurityTokenHandler();
        try
        {
            handler.ValidateToken(ticket, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ValidIssuer = _jwt.Issuer,
                ValidAudience = TicketAudience,
                IssuerSigningKey = key,
                ClockSkew = TimeSpan.Zero
            }, out var validated);

            var jwt = (JwtSecurityToken)validated;
            var sub = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value;
            var purpose = jwt.Claims.FirstOrDefault(c => c.Type == "purpose")?.Value;
            if (string.IsNullOrEmpty(sub) || purpose != expectedPurpose) return null;
            return new MfaTicketContents(sub, purpose);
        }
        catch
        {
            return null;
        }
    }
}
