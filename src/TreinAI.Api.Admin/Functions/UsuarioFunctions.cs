using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using TreinAI.Shared.Exceptions;
using TreinAI.Shared.Middleware;
using TreinAI.Shared.Models;
using TreinAI.Shared.Models.Enums;
using TreinAI.Shared.Repositories;
using TreinAI.Shared.Validation;

namespace TreinAI.Api.Admin.Functions;

/// <summary>
/// CRUD for Usuario (system users / role management).
/// </summary>
public class UsuarioFunctions
{
    private readonly IRepository<Usuario> _repository;
    private readonly TenantContext _tenantContext;
    private readonly ILogger<UsuarioFunctions> _logger;

    public UsuarioFunctions(
        IRepository<Usuario> repository,
        TenantContext tenantContext,
        ILogger<UsuarioFunctions> logger)
    {
        _repository = repository;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    [Function("GetUsuarios")]
    public async Task<HttpResponseData> GetUsuarios(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin/usuarios")] HttpRequestData req)
    {
        if (!_tenantContext.IsAdmin)
            throw new ForbiddenException("Apenas administradores podem gerenciar usuários.");

        var usuarios = await _repository.GetAllAsync(_tenantContext.TenantId);
        return await ValidationHelper.OkAsync(req, usuarios);
    }

    [Function("GetUsuarioById")]
    public async Task<HttpResponseData> GetUsuarioById(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin/usuarios/{id}")] HttpRequestData req,
        string id)
    {
        if (!_tenantContext.IsAdmin && _tenantContext.UserId != id)
            throw new ForbiddenException("Sem permissão.");

        var usuario = await _repository.GetByIdAsync(id, _tenantContext.TenantId);
        if (usuario == null)
            throw new NotFoundException("Usuario", id);

        return await ValidationHelper.OkAsync(req, usuario);
    }

    [Function("GetUsuarioMe")]
    public async Task<HttpResponseData> GetMe(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "usuarios/me")] HttpRequestData req)
    {
        if (string.IsNullOrEmpty(_tenantContext.UserId))
            throw new UnauthorizedAccessException();

        var usuarios = await _repository.QueryAsync(
            _tenantContext.TenantId,
            u => u.B2CObjectId == _tenantContext.UserId);

        var me = usuarios.FirstOrDefault();
        if (me == null)
            throw new NotFoundException("Usuário não encontrado para o ID atual.");

        return await ValidationHelper.OkAsync(req, me);
    }

    [Function("UpdateUsuarioRole")]
    public async Task<HttpResponseData> UpdateRole(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "admin/usuarios/{id}/role")] HttpRequestData req,
        string id)
    {
        if (!_tenantContext.IsAdmin)
            throw new ForbiddenException("Apenas administradores podem alterar papéis.");

        var existing = await _repository.GetByIdAsync(id, _tenantContext.TenantId);
        if (existing == null)
            throw new NotFoundException("Usuario", id);

        var body = await req.ReadAsStringAsync();
        var roleUpdate = System.Text.Json.JsonSerializer.Deserialize<RoleUpdateDto>(body ?? "",
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (roleUpdate == null || !Enum.TryParse<UserRole>(roleUpdate.Role, true, out var newRole))
            throw new BusinessValidationException("Role inválido. Use: Admin, Professor ou Aluno.");

        existing.Role = newRole.ToString().ToLowerInvariant();
        existing.UpdatedBy = _tenantContext.UserId;

        var updated = await _repository.UpdateAsync(existing);
        return await ValidationHelper.OkAsync(req, updated);
    }
}

internal class RoleUpdateDto
{
    public string Role { get; set; } = string.Empty;
}
