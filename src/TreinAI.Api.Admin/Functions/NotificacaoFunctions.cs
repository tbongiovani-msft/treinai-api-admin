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
/// CRUD for Notificacao (in-app notifications).
/// </summary>
public class NotificacaoFunctions
{
    private readonly IRepository<Notificacao> _repository;
    private readonly TenantContext _tenantContext;
    private readonly ILogger<NotificacaoFunctions> _logger;

    public NotificacaoFunctions(
        IRepository<Notificacao> repository,
        TenantContext tenantContext,
        ILogger<NotificacaoFunctions> logger)
    {
        _repository = repository;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/notificacoes — Get notifications for current user.
    /// </summary>
    [Function("GetNotificacoes")]
    public async Task<HttpResponseData> GetNotificacoes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "notificacoes")] HttpRequestData req)
    {
        var queryParams = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var apenasNaoLidas = queryParams["naoLidas"] == "true";

        IReadOnlyList<Notificacao> notificacoes;

        if (apenasNaoLidas)
        {
            notificacoes = await _repository.QueryAsync(
                _tenantContext.TenantId,
                n => n.UserId == _tenantContext.UserId && !n.Lida);
        }
        else
        {
            notificacoes = await _repository.QueryAsync(
                _tenantContext.TenantId,
                n => n.UserId == _tenantContext.UserId);
        }

        return await ValidationHelper.OkAsync(req, notificacoes);
    }

    /// <summary>
    /// PUT|PATCH /api/notificacoes/{id}/lida — Mark notification as read.
    /// </summary>
    [Function("MarcarNotificacaoLida")]
    public async Task<HttpResponseData> MarcarLida(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", "put", Route = "notificacoes/{id}/lida")] HttpRequestData req,
        string id)
    {
        var notificacao = await _repository.GetByIdAsync(id, _tenantContext.TenantId);
        if (notificacao == null)
            throw new NotFoundException("Notificacao", id);

        if (notificacao.UserId != _tenantContext.UserId)
            throw new ForbiddenException("Você só pode marcar suas próprias notificações como lida.");

        notificacao.Lida = true;
        notificacao.LidaEm = DateTime.UtcNow;
        notificacao.UpdatedBy = _tenantContext.UserId;

        var updated = await _repository.UpdateAsync(notificacao);
        return await ValidationHelper.OkAsync(req, updated);
    }

    /// <summary>
    /// PUT /api/notificacoes/lidas — Mark all notifications as read for the current user.
    /// </summary>
    [Function("MarcarTodasNotificacoesLidas")]
    public async Task<HttpResponseData> MarcarTodasLidas(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "notificacoes/lidas")] HttpRequestData req)
    {
        var naoLidas = await _repository.QueryAsync(
            _tenantContext.TenantId,
            n => n.UserId == _tenantContext.UserId && !n.Lida);

        foreach (var notificacao in naoLidas)
        {
            notificacao.Lida = true;
            notificacao.LidaEm = DateTime.UtcNow;
            notificacao.UpdatedBy = _tenantContext.UserId;
            await _repository.UpdateAsync(notificacao);
        }

        _logger.LogInformation("Marked {Count} notifications as read for user {UserId}", naoLidas.Count, _tenantContext.UserId);
        return await ValidationHelper.OkAsync(req, new { marcadas = naoLidas.Count });
    }

    /// <summary>
    /// GET /api/notificacoes/count — Count unread notifications.
    /// </summary>
    [Function("CountNotificacoesNaoLidas")]
    public async Task<HttpResponseData> CountNaoLidas(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "notificacoes/count")] HttpRequestData req)
    {
        var count = await _repository.CountAsync(
            _tenantContext.TenantId,
            n => n.UserId == _tenantContext.UserId && !n.Lida);

        return await ValidationHelper.OkAsync(req, new { naoLidas = count });
    }
}
