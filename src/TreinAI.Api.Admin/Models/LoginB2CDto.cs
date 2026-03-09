namespace TreinAI.Api.Admin.Models;

/// <summary>
/// DTO for B2C login/auto-registration (POST /auth/login-b2c).
/// Called by the frontend after Azure AD B2C authentication.
/// Finds the user by B2C Object ID or email, or creates a new one.
/// </summary>
public class LoginB2CDto
{
    /// <summary>Azure AD B2C Object ID (from /.auth/me clientPrincipal.userId)</summary>
    public string B2CObjectId { get; set; } = string.Empty;

    /// <summary>User email (from clientPrincipal.userDetails or claims)</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>User display name (from B2C claims)</summary>
    public string? Nome { get; set; }

    /// <summary>Tenant ID (defaults to seed tenant)</summary>
    public string? TenantId { get; set; }
}
