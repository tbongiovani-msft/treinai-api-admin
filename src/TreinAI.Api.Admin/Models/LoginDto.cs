namespace TreinAI.Api.Admin.Models;

/// <summary>
/// DTO for mock login (POST /auth/login).
/// In production, Azure AD B2C handles authentication.
/// </summary>
public class LoginDto
{
    public string Email { get; set; } = string.Empty;
    public string? TenantId { get; set; }
}
