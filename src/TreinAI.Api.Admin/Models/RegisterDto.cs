namespace TreinAI.Api.Admin.Models;

/// <summary>
/// DTO for self-registration (POST /auth/register).
/// </summary>
public class RegisterDto
{
    public string Nome { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Senha { get; set; } = string.Empty;
    public string? B2CObjectId { get; set; }
    public string? TenantId { get; set; }
}
