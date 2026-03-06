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
/// CRUD for Objetivo (student goals).
/// </summary>
public class ObjetivoFunctions
{
    private readonly IRepository<Objetivo> _repository;
    private readonly TenantContext _tenantContext;
    private readonly ILogger<ObjetivoFunctions> _logger;

    public ObjetivoFunctions(
        IRepository<Objetivo> repository,
        TenantContext tenantContext,
        ILogger<ObjetivoFunctions> logger)
    {
        _repository = repository;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    [Function("GetObjetivos")]
    public async Task<HttpResponseData> GetObjetivos(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "objetivos")] HttpRequestData req)
    {
        var queryParams = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var alunoId = queryParams["alunoId"];

        IReadOnlyList<Objetivo> objetivos;

        if (!string.IsNullOrEmpty(alunoId))
        {
            objetivos = await _repository.QueryAsync(
                _tenantContext.TenantId, o => o.AlunoId == alunoId);
        }
        else if (_tenantContext.IsAluno)
        {
            objetivos = await _repository.QueryAsync(
                _tenantContext.TenantId, o => o.AlunoId == _tenantContext.UserId);
        }
        else
        {
            objetivos = await _repository.GetAllAsync(_tenantContext.TenantId);
        }

        return await ValidationHelper.OkAsync(req, objetivos);
    }

    [Function("GetObjetivoById")]
    public async Task<HttpResponseData> GetById(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "objetivos/{id}")] HttpRequestData req,
        string id)
    {
        var objetivo = await _repository.GetByIdAsync(id, _tenantContext.TenantId);
        if (objetivo == null)
            throw new NotFoundException("Objetivo", id);

        if (_tenantContext.IsAluno && objetivo.AlunoId != _tenantContext.UserId)
            throw new ForbiddenException("Você só pode acessar seus próprios objetivos.");

        return await ValidationHelper.OkAsync(req, objetivo);
    }

    [Function("CreateObjetivo")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "objetivos")] HttpRequestData req)
    {
        var body = await req.ReadAsStringAsync();
        var objetivo = System.Text.Json.JsonSerializer.Deserialize<Objetivo>(body ?? "",
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (objetivo == null)
            throw new BusinessValidationException("Corpo da requisição inválido.");

        if (string.IsNullOrEmpty(objetivo.AlunoId))
            throw new BusinessValidationException("AlunoId é obrigatório.");

        if (_tenantContext.IsAluno)
            objetivo.AlunoId = _tenantContext.UserId;

        objetivo.TenantId = _tenantContext.TenantId;
        objetivo.CreatedBy = _tenantContext.UserId;
        objetivo.UpdatedBy = _tenantContext.UserId;

        var created = await _repository.CreateAsync(objetivo);
        return await ValidationHelper.CreatedAsync(req, created);
    }

    [Function("UpdateObjetivo")]
    public async Task<HttpResponseData> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "objetivos/{id}")] HttpRequestData req,
        string id)
    {
        var existing = await _repository.GetByIdAsync(id, _tenantContext.TenantId);
        if (existing == null)
            throw new NotFoundException("Objetivo", id);

        if (_tenantContext.IsAluno && existing.AlunoId != _tenantContext.UserId)
            throw new ForbiddenException("Você só pode editar seus próprios objetivos.");

        var body = await req.ReadAsStringAsync();
        var objetivo = System.Text.Json.JsonSerializer.Deserialize<Objetivo>(body ?? "",
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (objetivo == null)
            throw new BusinessValidationException("Corpo da requisição inválido.");

        objetivo.Id = id;
        objetivo.TenantId = _tenantContext.TenantId;
        objetivo.CreatedAt = existing.CreatedAt;
        objetivo.CreatedBy = existing.CreatedBy;
        objetivo.UpdatedBy = _tenantContext.UserId;

        var updated = await _repository.UpdateAsync(objetivo);
        return await ValidationHelper.OkAsync(req, updated);
    }

    [Function("DeleteObjetivo")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "objetivos/{id}")] HttpRequestData req,
        string id)
    {
        var existing = await _repository.GetByIdAsync(id, _tenantContext.TenantId);
        if (existing == null)
            throw new NotFoundException("Objetivo", id);

        if (_tenantContext.IsAluno && existing.AlunoId != _tenantContext.UserId)
            throw new ForbiddenException("Você só pode excluir seus próprios objetivos.");

        await _repository.DeleteAsync(id, _tenantContext.TenantId);
        return ValidationHelper.NoContent(req);
    }
}
