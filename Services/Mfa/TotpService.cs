using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CompetitionManagementSystem.Models;
using CompetitionManagementSystem.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using OtpNet;
using QRCoder;

namespace CompetitionManagementSystem.Services.Mfa;

public sealed record TotpEnrollmentArtifacts(string OtpAuthUri, string ManualEntryKey, string QrCodeDataUri);

public interface ITotpService
{
    TotpEnrollmentArtifacts CreateEnrollment(ApplicationUser user, string issuer);
    bool VerifyCode(string encryptedSecret, string code);
    string EncryptSecret(string base32Secret);
    string DecryptSecret(string encrypted);

    (List<string> plaintextCodes, string encryptedBlob) GenerateRecoveryCodes(int count = 10);
    bool TryConsumeRecoveryCode(string encryptedBlob, string suppliedCode, out string updatedEncryptedBlob);
}

public sealed class TotpService : ITotpService
{
    private const string ProtectorPurpose = "AdminTotp.v1";
    private readonly IDataProtector _protector;
    private readonly AdminSecurityOptions _options;

    public TotpService(IDataProtectionProvider dataProtection, IOptions<AdminSecurityOptions> options)
    {
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _options = options.Value;
    }

    public TotpEnrollmentArtifacts CreateEnrollment(ApplicationUser user, string issuer)
    {
        var secretBytes = KeyGeneration.GenerateRandomKey(20);
        var base32 = Base32Encoding.ToString(secretBytes);

        var label = Uri.EscapeDataString($"{issuer}:{user.Email}");
        var encodedIssuer = Uri.EscapeDataString(issuer);
        var otpAuthUri = $"otpauth://totp/{label}?secret={base32}&issuer={encodedIssuer}&algorithm=SHA1&digits=6&period=30";

        using var qrGenerator = new QRCodeGenerator();
        using var qrData = qrGenerator.CreateQrCode(otpAuthUri, QRCodeGenerator.ECCLevel.Q);
        using var qr = new PngByteQRCode(qrData);
        var pngBytes = qr.GetGraphic(6);
        var dataUri = "data:image/png;base64," + Convert.ToBase64String(pngBytes);

        // Persisting the secret is the caller's responsibility (so we hand back encrypted via EncryptSecret).
        user.TotpSecretEncrypted = EncryptSecret(base32);

        return new TotpEnrollmentArtifacts(otpAuthUri, base32, dataUri);
    }

    public bool VerifyCode(string encryptedSecret, string code)
    {
        if (string.IsNullOrWhiteSpace(encryptedSecret) || string.IsNullOrWhiteSpace(code)) return false;
        var base32 = DecryptSecret(encryptedSecret);
        var secretBytes = Base32Encoding.ToBytes(base32);
        var totp = new Totp(secretBytes, step: 30, mode: OtpHashMode.Sha1, totpSize: 6);
        var window = new VerificationWindow(
            previous: Math.Max(0, _options.TotpVerificationWindowSteps),
            future: Math.Max(0, _options.TotpVerificationWindowSteps));
        return totp.VerifyTotp(code.Trim(), out _, window);
    }

    public string EncryptSecret(string base32Secret) => _protector.Protect(base32Secret);

    public string DecryptSecret(string encrypted) => _protector.Unprotect(encrypted);

    public (List<string> plaintextCodes, string encryptedBlob) GenerateRecoveryCodes(int count = 10)
    {
        var codes = new List<string>(count);
        var hashes = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var code = GenerateRecoveryCodeString();
            codes.Add(code);
            hashes.Add(HashRecoveryCode(code));
        }
        var json = JsonSerializer.Serialize(hashes);
        return (codes, _protector.Protect(json));
    }

    public bool TryConsumeRecoveryCode(string encryptedBlob, string suppliedCode, out string updatedEncryptedBlob)
    {
        updatedEncryptedBlob = encryptedBlob;
        if (string.IsNullOrWhiteSpace(encryptedBlob) || string.IsNullOrWhiteSpace(suppliedCode)) return false;

        var json = _protector.Unprotect(encryptedBlob);
        var hashes = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        var suppliedHash = HashRecoveryCode(suppliedCode);
        var index = hashes.FindIndex(h => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(h),
            Encoding.UTF8.GetBytes(suppliedHash)));
        if (index < 0) return false;

        hashes.RemoveAt(index);
        updatedEncryptedBlob = _protector.Protect(JsonSerializer.Serialize(hashes));
        return true;
    }

    private static string GenerateRecoveryCodeString()
    {
        // 10 hex chars (40 bits of entropy) formatted as XXXXX-XXXXX, e.g. "A1B2C-3D4E5".
        Span<byte> buf = stackalloc byte[5];
        RandomNumberGenerator.Fill(buf);
        var hex = Convert.ToHexString(buf);
        return hex.Substring(0, 5) + "-" + hex.Substring(5, 5);
    }

    private static string HashRecoveryCode(string code)
    {
        var normalized = code.Trim().Replace("-", "").ToUpperInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes);
    }
}
