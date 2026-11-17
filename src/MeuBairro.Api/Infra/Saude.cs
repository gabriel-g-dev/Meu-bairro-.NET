using System.Diagnostics;
using MeuBairro.Api.Configuracao;
using MeuBairro.Api.Dados;
using MeuBairro.Api.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace MeuBairro.Api.Infra;

public static class Saude
{
    public const string TagVivo = "live";
    public const string TagPronto = "ready";

    public static IServiceCollection AddVerificacoesDeSaude(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck("aplicacao", () => HealthCheckResult.Healthy("API em execução"), [TagVivo])
            .AddCheck<BancoHealthCheck>("banco", HealthStatus.Unhealthy, [TagPronto])
            .AddCheck<IdempotenciaHealthCheck>("idempotencia", HealthStatus.Degraded, [TagPronto])
            .AddCheck<FotosHealthCheck>("armazenamento-fotos", HealthStatus.Degraded, [TagPronto]);
        return services;
    }

    public static void MapSaude(this IEndpointRouteBuilder app)
    {
        Mapear(app, "/health", _ => true, "Estado completo da API e das dependências");
        Mapear(app, "/health/live", c => c.Tags.Contains(TagVivo), "Liveness: o processo está no ar (não consulta o banco)");
        Mapear(app, "/health/ready", c => c.Tags.Contains(TagPronto), "Readiness: banco, idempotência e pasta de fotos");
    }

    // Endpoints Minimal API (em vez de MapHealthChecks) para aparecerem no OpenAPI com o formato da resposta.
    private static void Mapear(IEndpointRouteBuilder app, string rota, Func<HealthCheckRegistration, bool> filtro, string resumo) =>
        app.MapGet(rota, async (HealthCheckService saude, HttpResponse resposta, CancellationToken ct) =>
            {
                var relatorio = await saude.CheckHealthAsync(filtro, ct);
                resposta.Headers.CacheControl = "no-store, no-cache";
                resposta.Headers.Pragma = "no-cache";
                resposta.Headers.Expires = "Thu, 01 Jan 1970 00:00:00 GMT";
                // Healthy e Degraded atendem (200); Unhealthy não deve receber tráfego (503).
                var codigo = relatorio.Status == HealthStatus.Unhealthy
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status200OK;
                return TypedResults.Json(Converter(relatorio), statusCode: codigo);
            })
            .AllowAnonymous()
            .DisableRateLimiting()
            .WithTags("Health Checks")
            .WithSummary(resumo)
            .Produces<RelatorioSaudeResponse>(StatusCodes.Status200OK)
            .Produces<RelatorioSaudeResponse>(StatusCodes.Status503ServiceUnavailable);

    // Mostra o estado de cada dependência sem incluir exceções, conexões ou outros detalhes internos.
    private static RelatorioSaudeResponse Converter(HealthReport relatorio) => new(
        relatorio.Status.ToString(),
        Math.Round(relatorio.TotalDuration.TotalMilliseconds, 1),
        relatorio.Entries.Select(e => new VerificacaoSaudeResponse(
            e.Key,
            e.Value.Status.ToString(),
            e.Value.Description,
            Math.Round(e.Value.Duration.TotalMilliseconds, 1),
            e.Value.Data)).ToList());
}

public sealed record VerificacaoSaudeResponse(
    string Nome, string Status, string? Descricao, double DuracaoMs, IReadOnlyDictionary<string, object> Dados);

public sealed record RelatorioSaudeResponse(string Status, double DuracaoMs, IReadOnlyList<VerificacaoSaudeResponse> Verificacoes);

public sealed class BancoHealthCheck(
    IServiceScopeFactory scopes,
    IOptions<BancoOptions> opcoes,
    ILogger<BancoHealthCheck> log) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var cfg = opcoes.Value;
        var dados = new Dictionary<string, object> { ["provedor"] = cfg.Provedor.ToString() };
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, cfg.TimeoutVerificacaoSegundos)));
        var cronometro = Stopwatch.StartNew();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Consulta real (somente leitura) a uma tabela do modelo: valida conexão e estrutura.
            await db.Categorias.AsNoTracking().OrderBy(c => c.Id).Select(c => c.Id).FirstOrDefaultAsync(limite.Token);
            cronometro.Stop();
            dados["latenciaMs"] = cronometro.ElapsedMilliseconds;

            return cronometro.ElapsedMilliseconds > cfg.LimiteLatenciaMs
                ? HealthCheckResult.Degraded(
                    $"Banco respondeu em {cronometro.ElapsedMilliseconds} ms (limite de {cfg.LimiteLatenciaMs} ms)", data: dados)
                : HealthCheckResult.Healthy("Banco de dados acessível", dados);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Verificação do banco falhou: {Tipo}: {Mensagem}", ex.GetType().Name, ex.Message);
            return new HealthCheckResult(context.Registration.FailureStatus, "Banco de dados indisponível", data: dados);
        }
    }
}

public sealed class IdempotenciaHealthCheck(
    IDbContextFactory<ControleDbContext> fabrica,
    ILogger<IdempotenciaHealthCheck> log) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var db = await fabrica.CreateDbContextAsync(ct);
            await db.Idempotencia.AsNoTracking().OrderBy(r => r.Id).Select(r => r.Id).FirstOrDefaultAsync(ct);
            return HealthCheckResult.Healthy("Armazenamento de idempotência acessível");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Verificação do armazenamento de idempotência falhou: {Tipo}", ex.GetType().Name);
            return new HealthCheckResult(context.Registration.FailureStatus,
                "Armazenamento de idempotência indisponível: requisições com Idempotency-Key serão recusadas");
        }
    }
}

public sealed class FotosHealthCheck(ArmazenamentoFotos fotos) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(fotos.PodeGravar()
            ? HealthCheckResult.Healthy("Pasta de fotos disponível para gravação")
            : new HealthCheckResult(context.Registration.FailureStatus,
                "Pasta de fotos sem permissão de gravação: o envio de fotos ficará indisponível"));
}
