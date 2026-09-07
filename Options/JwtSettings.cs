namespace CompetitionManagementSystem.Options;

public sealed class JwtSettings
{
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;

    // Access-token lifetime in minutes. Configurable via JwtSettings:ExpiryMinutes.
    // 30 minutes is the security baseline; tokens older than this are rejected by JwtBearer.
    public int ExpiryMinutes { get; set; } = 30;
}