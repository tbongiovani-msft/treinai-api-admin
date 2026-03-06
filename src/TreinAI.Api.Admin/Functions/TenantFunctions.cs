using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using TreinAI.Shared.Exceptions;
using TreinAI.Shared.Middleware;
using TreinAI.Shared.Models;
using TreinAI.Shared.Repositories;
using TreinAI.Shared.Validation;

namespace TreinAI.Api.Admin.Functions;

/// <summary>
/// Admin-only CRUD for Tenant management.
/// </summary>
public class TenantFunctions
{
    private readonly IRepository<Tenant> _repository;
    private readonly TenantContext _tenantContext;
    private readonly ILogger<TenantFunctions> _logger;

    public TenantFunctions(
        IRepository<Tenant> repository,
        TenantContext tenantContext,
        ILogger<TenantFunctions> logger)
    {
        _repository = repository;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    [Function("GetTenants")]
    public async Task<HttpResponseData> GetTenants(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin/tenants")] HttpRequestData req)
    {
        if (!_tenantContext.IsAdmin)
            throw new ForbiddenException("Apenas administradores podem gerenciar tenants.");

        var tenants = await _repository.GetAllAsync(_tenantContext.TenantId);
        return await ValidationHelper.OkAsync(req, tenants);
    }

    [Function("GetTenantById")]
    public async Task<HttpResponseData> GetTenantById(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin/tenants/{id}")] HttpRequestData req,
        string id)
    {
        if (!_tenantContext.IsAdmin)
            throw new ForbiddenException("Apenas administradores podem gerenciar tenants.");

        var tenant = await _repository.GetByIdAsync(id, _tenantContext.TenantId);
        if (tenant == null)
            throw new NotFoundException("Tenant", id);

        return await ValidationHelper.OkAsync(req, tenant);
    }
}
