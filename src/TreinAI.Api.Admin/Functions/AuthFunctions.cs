using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using TreinAI.Api.Admin.Models;
using TreinAI.Api.Admin.Validators;
using TreinAI.Shared.Exceptions;
using TreinAI.Shared.Middleware;
using TreinAI.Shared.Models;
using TreinAI.Shared.Repositories;
using TreinAI.Shared.Services;
using TreinAI.Shared.Validation;

namespace TreinAI.Api.Admin.Functions;

/// <summary>
/// Authentication and self-registration endpoints.
/// </summary>
public class AuthFunctions
{
    private readonly IRepository<Usuario> _usuarioRepository;
    private readonly IRepository<Notificacao> _notificacaoRepository;
    private readonly IEmailService _emailService;
    private readonly TenantContext _tenantContext;
    private readonly ILogger<AuthFunctions> _logger;

    private const string AdminEmail = "tbongiovani@outlook.com";

    public AuthFunctions(
        IRepository<Usuario> usuarioRepository,
        IRepository<Notificacao> notificacaoRepository,
        IEmailService emailService,
        TenantContext tenantContext,
        ILogger<AuthFunctions> logger)
    {
        _usuarioRepository = usuarioRepository;
        _notificacaoRepository = notificacaoRepository;
        _emailService = emailService;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/auth/register — Self-registration.
    /// Creates a new user with default role "aluno" and sends email notification to admin.
    /// Also creates an in-app notification for the admin.
    /// </summary>
    [Function("AuthRegister")]
    public async Task<HttpResponseData> Register(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/register")] HttpRequestData req)
    {
        var validator = new RegisterValidator();
        var dto = await ValidationHelper.ValidateRequestAsync(req, validator);

        var tenantId = dto.TenantId ?? _tenantContext.TenantId;

        // Check if user already exists by email
        var existing = await _usuarioRepository.QueryAsync(
            tenantId,
            u => u.Email == dto.Email && !u.IsDeleted);

        if (existing.Count > 0)
            throw new BusinessValidationException("Já existe um usuário cadastrado com este e-mail.");

        // Create user with default role "aluno"
        var usuario = new Usuario
        {
            TenantId = tenantId,
            Nome = dto.Nome,
            Email = dto.Email,
            B2CObjectId = dto.B2CObjectId ?? _tenantContext.UserId,
            Role = "aluno",
            Ativo = true,
            DataCadastro = DateTime.UtcNow,
            CreatedBy = dto.B2CObjectId ?? _tenantContext.UserId,
            UpdatedBy = dto.B2CObjectId ?? _tenantContext.UserId
        };

        _logger.LogInformation(
            "Registering new user: {Nome} ({Email}) in tenant {TenantId}",
            usuario.Nome, usuario.Email, tenantId);

        var created = await _usuarioRepository.CreateAsync(usuario);

        // Send email notification to admin (fire-and-forget, don't block registration)
        _ = Task.Run(async () =>
        {
            try
            {
                await SendAdminNotificationEmailAsync(created);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send admin notification email for user {UserId}", created.Id);
            }
        });

        // Create in-app notification for admin users
        await CreateAdminNotificationAsync(created, tenantId);

        return await ValidationHelper.CreatedAsync(req, created);
    }

    /// <summary>
    /// POST /api/auth/login — Mock login by email.
    /// Finds an existing user by email and returns their profile.
    /// In production this is replaced by Azure AD B2C authentication.
    /// </summary>
    [Function("AuthLogin")]
    public async Task<HttpResponseData> Login(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/login")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<LoginDto>();

        if (body == null || string.IsNullOrWhiteSpace(body.Email))
            throw new BusinessValidationException("Email é obrigatório.");

        var tenantId = body.TenantId ?? _tenantContext.TenantId;

        var users = await _usuarioRepository.QueryAsync(
            tenantId,
            u => u.Email == body.Email.Trim().ToLower() && u.Ativo && !u.IsDeleted);

        if (users.Count == 0)
        {
            var notFound = req.CreateResponse(System.Net.HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { title = "Usuário não encontrado", detail = "Nenhum usuário ativo encontrado com este e-mail." });
            return notFound;
        }

        _logger.LogInformation("Mock login for user {Email} in tenant {TenantId}", body.Email, tenantId);

        return await ValidationHelper.OkAsync(req, users[0]);
    }

    private async Task SendAdminNotificationEmailAsync(Usuario newUser)
    {
        var subject = $"[treinAI] Novo cadastro: {newUser.Nome}";
        var htmlBody = $"""
            <html>
            <body style="font-family: Arial, sans-serif; padding: 20px;">
                <h2>🏋️ Novo cadastro no treinAI</h2>
                <table style="border-collapse: collapse; width: 100%; max-width: 500px;">
                    <tr><td style="padding: 8px; font-weight: bold;">Nome:</td><td style="padding: 8px;">{newUser.Nome}</td></tr>
                    <tr><td style="padding: 8px; font-weight: bold;">Email:</td><td style="padding: 8px;">{newUser.Email}</td></tr>
                    <tr><td style="padding: 8px; font-weight: bold;">Data:</td><td style="padding: 8px;">{newUser.DataCadastro:dd/MM/yyyy HH:mm} UTC</td></tr>
                    <tr><td style="padding: 8px; font-weight: bold;">Role padrão:</td><td style="padding: 8px;">aluno</td></tr>
                    <tr><td style="padding: 8px; font-weight: bold;">ID:</td><td style="padding: 8px;">{newUser.Id}</td></tr>
                </table>
                <p style="margin-top: 20px; color: #666;">
                    Acesse o painel de administração para revisar e, se necessário, promover o usuário para professor ou admin.
                </p>
            </body>
            </html>
            """;

        await _emailService.SendEmailAsync(AdminEmail, subject, htmlBody);

        _logger.LogInformation("Admin notification email sent for new user {UserId}", newUser.Id);
    }

    private async Task CreateAdminNotificationAsync(Usuario newUser, string tenantId)
    {
        // Find admin users to notify
        var admins = await _usuarioRepository.QueryAsync(
            tenantId,
            u => u.Role == "admin" && u.Ativo && !u.IsDeleted);

        foreach (var admin in admins)
        {
            var notificacao = new Notificacao
            {
                TenantId = tenantId,
                UserId = admin.Id,
                Titulo = "Novo cadastro",
                Mensagem = $"{newUser.Nome} ({newUser.Email}) se cadastrou no sistema e está aguardando revisão.",
                Tipo = "novo_cadastro",
                LinkUrl = $"/usuarios/{newUser.Id}",
                Lida = false,
                CreatedBy = "system"
            };

            await _notificacaoRepository.CreateAsync(notificacao);
        }

        _logger.LogInformation(
            "Created in-app notifications for {AdminCount} admins about new user {UserId}",
            admins.Count, newUser.Id);
    }
}
