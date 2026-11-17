using System.Data.Common;
using MeuBairro.Api.Configuracao;
using MeuBairro.Api.Dados;
using MeuBairro.Api.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace MeuBairro.Api.Infra.Idempotencia;

public enum ResultadoInicio { Nova, Repetida, EmAndamento, PayloadDivergente }

public sealed record InicioIdempotencia(
    ResultadoInicio Resultado,
    long Id = 0,
    string? Token = null,
    RegistroIdempotencia? Registro = null,
    int EsperaSegundos = 0);

// Persiste as chaves no SQLite. O índice único (escopo, chave) garante que só uma requisição concorrente
// "vence"; as demais recebem a resposta guardada ou 409 enquanto a primeira está em andamento.
public sealed class IdempotenciaService(
    IDbContextFactory<ControleDbContext> fabrica,
    ConexoesBanco conexoes,
    IEnumerable<IInterceptor> interceptadores,
    Relogio relogio,
    IOptions<IdempotenciaOptions> opcoes)
{
    // Com a tabela de controle no mesmo arquivo do banco principal, o resultado da chave é gravado
    // na mesma transação dos dados do negócio: ou os dois são confirmados, ou nenhum.
    public bool ConclusaoAtomica => conexoes.ControleCompartilhado;

    public async Task<InicioIdempotencia> IniciarAsync(string escopo, string chave, string hash, CancellationToken ct)
    {
        var cfg = opcoes.Value;
        var bloqueioMs = Math.Max(1, cfg.BloqueioSegundos) * 1000L;

        for (var tentativa = 0; tentativa < 5; tentativa++)
        {
            var agora = relogio.AgoraUnixMs();
            await using var db = await fabrica.CreateDbContextAsync(ct);
            var existente = await db.Idempotencia.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Escopo == escopo && r.Chave == chave, ct);

            if (existente is null)
            {
                var registro = new RegistroIdempotencia
                {
                    Escopo = escopo,
                    Chave = chave,
                    HashRequisicao = hash,
                    Situacao = SituacaoIdempotencia.Processando,
                    Token = NovoToken(),
                    CriadoEmMs = agora,
                    BloqueadoAteMs = agora + bloqueioMs,
                    ExpiraEmMs = agora + Math.Max(1, cfg.RetencaoHoras) * 3_600_000L
                };
                db.Idempotencia.Add(registro);
                try
                {
                    await db.SaveChangesAsync(ct);
                    return new InicioIdempotencia(ResultadoInicio.Nova, registro.Id, registro.Token);
                }
                catch (DbUpdateException ex) when (Erros.ViolacaoUnicidade(ex))
                {
                    continue; // outra requisição gravou a mesma chave primeiro
                }
            }

            if (existente.ExpiraEmMs <= agora)
            {
                await db.Idempotencia.Where(r => r.Id == existente.Id && r.ExpiraEmMs <= agora).ExecuteDeleteAsync(ct);
                continue;
            }

            if (!string.Equals(existente.HashRequisicao, hash, StringComparison.Ordinal))
                return new InicioIdempotencia(ResultadoInicio.PayloadDivergente);

            if (existente.Situacao == SituacaoIdempotencia.Concluida)
                return new InicioIdempotencia(ResultadoInicio.Repetida, Registro: existente);

            if (existente.BloqueadoAteMs > agora)
            {
                var espera = (int)Math.Ceiling((existente.BloqueadoAteMs - agora) / 1000.0);
                return new InicioIdempotencia(ResultadoInicio.EmAndamento, EsperaSegundos: Math.Max(1, espera));
            }

            // O processamento anterior foi interrompido sem concluir: assume a chave de forma atômica.
            var token = NovoToken();
            var assumiu = await db.Idempotencia
                .Where(r => r.Id == existente.Id && r.Token == existente.Token && r.Situacao == SituacaoIdempotencia.Processando)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Token, token)
                    .SetProperty(r => r.BloqueadoAteMs, agora + bloqueioMs), ct);
            if (assumiu == 1)
                return new InicioIdempotencia(ResultadoInicio.Nova, existente.Id, token);
        }

        return new InicioIdempotencia(ResultadoInicio.EmAndamento, EsperaSegundos: 1);
    }

    // Grava o resultado usando a conexão e a transação do negócio. Retorna false se a chave já não pertence
    // a esta requisição (outra assumiu depois do tempo de bloqueio): nesse caso quem chamou deve desfazer tudo.
    public async Task<bool> ConcluirNaTransacaoAsync(DbTransaction transacao, long id, string token, int status,
        string? tipoConteudo, string? location, byte[]? corpo)
    {
        var options = new DbContextOptionsBuilder<ControleDbContext>()
            .UseSqlite(transacao.Connection!)
            .AddInterceptors(interceptadores)
            .Options;
        await using var db = new ControleDbContext(options);
        await db.Database.UseTransactionAsync(transacao);
        return await ConcluirAsync(db, id, token, status, tipoConteudo, location, corpo);
    }

    // Usado quando a tabela de controle fica em outro arquivo (ex.: banco principal Oracle).
    public async Task<bool> ConcluirAsync(long id, string token, int status, string? tipoConteudo, string? location, byte[]? corpo)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        return await ConcluirAsync(db, id, token, status, tipoConteudo, location, corpo);
    }

    // Libera a chave quando o processamento falha, permitindo que o cliente repita a operação.
    public async Task DescartarAsync(long id, string token)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        await db.Idempotencia
            .Where(r => r.Id == id && r.Token == token && r.Situacao == SituacaoIdempotencia.Processando)
            .ExecuteDeleteAsync();
    }

    public async Task<int> RemoverExpiradosAsync(CancellationToken ct)
    {
        var agora = relogio.AgoraUnixMs();
        await using var db = await fabrica.CreateDbContextAsync(ct);
        return await db.Idempotencia.Where(r => r.ExpiraEmMs <= agora).ExecuteDeleteAsync(ct);
    }

    private static async Task<bool> ConcluirAsync(ControleDbContext db, long id, string token, int status,
        string? tipoConteudo, string? location, byte[]? corpo) =>
        await db.Idempotencia
            .Where(r => r.Id == id && r.Token == token && r.Situacao == SituacaoIdempotencia.Processando)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Situacao, SituacaoIdempotencia.Concluida)
                .SetProperty(r => r.StatusResposta, (int?)status)
                .SetProperty(r => r.TipoConteudo, tipoConteudo)
                .SetProperty(r => r.Location, location)
                .SetProperty(r => r.CorpoResposta, corpo)) == 1;

    private static string NovoToken() => Guid.NewGuid().ToString("N");
}

public sealed class LimpezaIdempotencia(
    IdempotenciaService servico,
    TimeProvider tempo,
    IOptions<IdempotenciaOptions> opcoes,
    ILogger<LimpezaIdempotencia> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalo = TimeSpan.FromMinutes(Math.Max(1, opcoes.Value.IntervaloLimpezaMinutos));
        using var timer = new PeriodicTimer(intervalo, tempo);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var removidos = await servico.RemoverExpiradosAsync(stoppingToken);
                    if (removidos > 0)
                        log.LogInformation("{Quantidade} chaves de idempotência expiradas removidas", removidos);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning("Falha ao limpar chaves de idempotência expiradas: {Tipo}", ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // encerramento da aplicação
        }
    }
}
