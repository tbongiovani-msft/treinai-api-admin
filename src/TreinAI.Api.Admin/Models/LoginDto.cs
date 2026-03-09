namespace TreinAI.Api.Admin.Models;

/// <summary>
/// DTO for email/password login (POST /auth/login).
/// </summary>
public class LoginDto
{
    public string Email { get; set; } = string.Empty;
    public string Senha { get; set; } = string.Empty;
    public string? TenantId { get; set; }
}
