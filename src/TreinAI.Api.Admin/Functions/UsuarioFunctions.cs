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

    /// <summary>
    /// GET /api/admin/usuarios/pendentes — Lists newly registered users (last 48h) pending admin review.
    /// Returns users with role "aluno" created in the last 48 hours, ordered by most recent first.
    /// Supports ?horas= query param to override the 48h default window.
    /// </summary>
    [Function("GetUsuariosPendentes")]
    public async Task<HttpResponseData> GetUsuariosPendentes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin/usuarios/pendentes")] HttpRequestData req)
    {
        if (!_tenantContext.IsAdmin)
            throw new ForbiddenException("Apenas administradores podem visualizar cadastros pendentes.");

        var queryParams = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var horasStr = queryParams["horas"];
        var horas = 48;

        if (!string.IsNullOrEmpty(horasStr) && int.TryParse(horasStr, out var horasParam) && horasParam > 0)
            horas = horasParam;

        var limite = DateTime.UtcNow.AddHours(-horas);

        _logger.LogInformation(
            "Getting pending users for last {Horas}h in tenant {TenantId}",
            horas, _tenantContext.TenantId);

        var pendentes = await _repository.QueryAsync(
            _tenantContext.TenantId,
            u => u.DataCadastro >= limite && !u.IsDeleted);

        // Order by most recent first
        var resultado = pendentes
            .OrderByDescending(u => u.DataCadastro)
            .ToList();

        return await ValidationHelper.OkAsync(req, resultado);
    }
}

internal class RoleUpdateDto
{
    public string Role { get; set; } = string.Empty;
}
