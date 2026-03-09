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
    /// POST /api/auth/register — Self-registration with email and password.
    /// If the email already exists but has no password (migrated from B2C/mock),
    /// sets the password and returns the existing user.
    /// Otherwise creates a new user with default role "aluno".
    /// </summary>
    [Function("AuthRegister")]
    public async Task<HttpResponseData> Register(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/register")] HttpRequestData req)
    {
        var validator = new RegisterValidator();
        var dto = await ValidationHelper.ValidateRequestAsync(req, validator);

        var tenantId = dto.TenantId ?? _tenantContext.TenantId;
        var email = dto.Email.Trim().ToLowerInvariant();

        // Check if user already exists by email
        var existing = await _usuarioRepository.QueryAsync(
            tenantId,
            u => u.Email == email && !u.IsDeleted);

        if (existing.Count > 0)
        {
            var existingUser = existing[0];

            // Migration path: user existed (from B2C/mock) but has no password → set it
            if (string.IsNullOrEmpty(existingUser.PasswordHash))
            {
                _logger.LogInformation(
                    "Setting password for existing user {Email} (migration from B2C/mock)",
                    email);
                existingUser.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Senha);
                existingUser.Nome = dto.Nome; // allow name update
                existingUser.UpdatedBy = existingUser.Id;
                var updated = await _usuarioRepository.UpdateAsync(existingUser);
                updated.PasswordHash = null; // Don't expose hash in response
                return await ValidationHelper.OkAsync(req, updated);
            }

            throw new BusinessValidationException("Já existe um usuário cadastrado com este e-mail.");
        }

        // Create user with default role "aluno"
        var usuario = new Usuario
        {
            TenantId = tenantId,
            Nome = dto.Nome,
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Senha),
            Role = "aluno",
            Ativo = true,
            DataCadastro = DateTime.UtcNow,
            CreatedBy = "self-register",
            UpdatedBy = "self-register"
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

        created.PasswordHash = null; // Don't expose hash in response
        return await ValidationHelper.CreatedAsync(req, created);
    }

    /// <summary>
    /// POST /api/auth/login — Email/password login.
    /// Validates credentials and returns the full Usuario object.
    /// </summary>
    [Function("AuthLogin")]
    public async Task<HttpResponseData> Login(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/login")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<LoginDto>();

        if (body == null || string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Senha))
            throw new BusinessValidationException("Email e senha são obrigatórios.");

        var tenantId = body.TenantId ?? _tenantContext.TenantId;
        if (string.IsNullOrEmpty(tenantId))
            tenantId = "t-treinai-001";

        var email = body.Email.Trim().ToLowerInvariant();

        var users = await _usuarioRepository.QueryAsync(
            tenantId,
            u => u.Email == email && u.Ativo && !u.IsDeleted);

        if (users.Count == 0)
            throw new BusinessValidationException("Email ou senha incorretos.");

        var user = users[0];

        // Validate password
        if (string.IsNullOrEmpty(user.PasswordHash))
        {
            _logger.LogWarning("Login attempt for user {Email} without password hash (needs registration)", email);
            throw new BusinessValidationException("Conta sem senha. Acesse 'Criar conta' para definir sua senha.");
        }

        if (!BCrypt.Net.BCrypt.Verify(body.Senha, user.PasswordHash))
            throw new BusinessValidationException("Email ou senha incorretos.");

        _logger.LogInformation("Successful login for user {Email} ({UserId})", email, user.Id);

        user.PasswordHash = null; // Don't expose hash in response
        return await ValidationHelper.OkAsync(req, user);
    }

    /// <summary>
    /// POST /api/auth/login-b2c — B2C login / auto-registration.
    /// Called by the frontend after Azure AD B2C authentication.
    /// 1. Tries to find user by B2C Object ID
    /// 2. If not found, tries to find by email (migration from mock → B2C)
    /// 3. If found by email, links the B2C Object ID
    /// 4. If not found at all, creates a new user as "aluno" (auto-registration)
    /// Returns the full Usuario object for the authenticated user.
    /// </summary>
    [Function("AuthLoginB2C")]
    public async Task<HttpResponseData> LoginB2C(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/login-b2c")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<LoginB2CDto>();

        if (body == null || string.IsNullOrWhiteSpace(body.B2CObjectId))
            throw new BusinessValidationException("B2CObjectId é obrigatório.");

        if (string.IsNullOrWhiteSpace(body.Email))
            throw new BusinessValidationException("Email é obrigatório.");

        var tenantId = body.TenantId ?? _tenantContext.TenantId;
        if (string.IsNullOrEmpty(tenantId))
            tenantId = "t-treinai-001"; // default seed tenant

        // 1. Try to find by B2C Object ID
        var users = await _usuarioRepository.QueryAsync(
            tenantId,
            u => u.B2CObjectId == body.B2CObjectId && u.Ativo && !u.IsDeleted);

        if (users.Count > 0)
        {
            _logger.LogInformation("B2C login: found user by OID {B2CObjectId} in tenant {TenantId}",
                body.B2CObjectId, tenantId);
            return await ValidationHelper.OkAsync(req, users[0]);
        }

        // 2. Try to find by email (migration from mock → B2C)
        var email = body.Email.Trim().ToLowerInvariant();
        users = await _usuarioRepository.QueryAsync(
            tenantId,
            u => u.Email == email && u.Ativo && !u.IsDeleted);

        if (users.Count > 0)
        {
            var existingUser = users[0];
            _logger.LogInformation(
                "B2C login: found user by email {Email}, linking B2C OID {B2CObjectId} (was: {OldOid})",
                email, body.B2CObjectId, existingUser.B2CObjectId);

            // Link the B2C Object ID to the existing account
            existingUser.B2CObjectId = body.B2CObjectId;
            existingUser.UpdatedBy = body.B2CObjectId;
            var updated = await _usuarioRepository.UpdateAsync(existingUser);
            return await ValidationHelper.OkAsync(req, updated);
        }

        // 3. Not found → auto-register as "aluno"
        var nome = body.Nome ?? email.Split('@')[0];
        _logger.LogInformation(
            "B2C login: no user found for OID {B2CObjectId} or email {Email}. Auto-registering as aluno.",
            body.B2CObjectId, email);

        var newUser = new Usuario
        {
            TenantId = tenantId,
            Nome = nome,
            Email = email,
            B2CObjectId = body.B2CObjectId,
            Role = "aluno",
            Ativo = true,
            DataCadastro = DateTime.UtcNow,
            CreatedBy = body.B2CObjectId,
            UpdatedBy = body.B2CObjectId
        };

        var created = await _usuarioRepository.CreateAsync(newUser);

        // Send email notification to admin (fire-and-forget)
        _ = Task.Run(async () =>
        {
            try { await SendAdminNotificationEmailAsync(created); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to send admin email for user {UserId}", created.Id); }
        });

        // Create in-app notification for admin users
        await CreateAdminNotificationAsync(created, tenantId);

        return await ValidationHelper.CreatedAsync(req, created);
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
