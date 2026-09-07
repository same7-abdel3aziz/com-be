namespace CompetitionManagementSystem.Security;

/// <summary>
/// Hard-coded password policy enforced before any Identity call.
/// Required because the Identity runtime options were observed to differ from the
/// configured values (e.g. accepting "Password1" with no special character).
/// Rules:
///   - minimum 8 characters
///   - at least one letter (any case, Unicode included)
///   - at least one digit
///   - at least one non-alphanumeric character (special character)
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 8;

    public static IReadOnlyList<string> Validate(string? password)
    {
        var errors = new List<string>();

        if (string.IsNullOrEmpty(password))
        {
            errors.Add($"Password is required and must be at least {MinimumLength} characters.");
            return errors;
        }

        if (password.Length < MinimumLength)
            errors.Add($"Password must be at least {MinimumLength} characters.");

        if (!password.Any(char.IsLetter))
            errors.Add("Password must contain at least one letter.");

        if (!password.Any(char.IsDigit))
            errors.Add("Password must contain at least one digit.");

        if (!password.Any(c => !char.IsLetterOrDigit(c)))
            errors.Add("Password must contain at least one special character (non-alphanumeric).");

        return errors;
    }
}