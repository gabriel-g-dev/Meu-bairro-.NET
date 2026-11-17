using MeuBairro.Api.Configuracao;
using MeuBairro.Api.Dados;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace MeuBairro.Api.Servicos;

// DATE do Oracle não guarda fuso nem frações de segundo: as datas ficam no horário local configurado, em segundos.
public sealed class Relogio
{
    private readonly TimeProvider tempo;
    private readonly TimeZoneInfo fuso;

    public Relogio(TimeProvider tempo, IOptions<AplicacaoOptions> opcoes)
    {
        this.tempo = tempo;
        fuso = BuscarFuso(opcoes.Value.FusoHorario);
    }

    public DateTime Agora()
    {
        var local = TimeZoneInfo.ConvertTime(tempo.GetUtcNow(), fuso).DateTime;
        return new DateTime(local.Ticks - local.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Unspecified);
    }

    public long AgoraUnixMs() => tempo.GetUtcNow().ToUnixTimeMilliseconds();

    private static TimeZoneInfo BuscarFuso(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}

// Serializa operações sobre a mesma chave (ex.: o mesmo cidadão) dentro do processo,
// evitando que duas requisições simultâneas passem juntas pelas verificações de duplicidade.
public sealed class TravasPorChave
{
    private readonly SemaphoreSlim[] travas = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<IDisposable> AdquirirAsync(string chave, CancellationToken ct)
    {
        var trava = travas[(StringComparer.Ordinal.GetHashCode(chave) & int.MaxValue) % travas.Length];
        await trava.WaitAsync(ct);
        return new Liberacao(trava);
    }

    private sealed class Liberacao(SemaphoreSlim trava) : IDisposable
    {
        private int liberada;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref liberada, 1) == 0)
                trava.Release();
        }
    }
}

// Abre a transação (ou participa da que já existe, como a da idempotência) e só depois adquire a trava em memória.
// A ordem fixa transação → trava evita espera circular com o bloqueio de escrita do SQLite.
public sealed class UnidadeDeTrabalho(AppDbContext db, TravasPorChave travas)
{
    public async Task<EscopoTransacao> IniciarAsync(string? trava, CancellationToken ct)
    {
        var transacao = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            return new EscopoTransacao(transacao, trava is null ? null : await travas.AdquirirAsync(trava, ct));
        }
        catch
        {
            if (transacao is not null)
                await transacao.DisposeAsync();
            throw;
        }
    }
}

public sealed class EscopoTransacao(IDbContextTransaction? transacao, IDisposable? trava) : IAsyncDisposable
{
    // Se a transação foi aberta por quem chamou, a confirmação fica a cargo dele.
    public Task ConfirmarAsync(CancellationToken ct) => transacao?.CommitAsync(ct) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (transacao is not null)
            await transacao.DisposeAsync();
        trava?.Dispose();
    }
}

// Fotos gravadas em disco durante a requisição: são apagadas se a transação dela for desfeita depois.
public sealed class ArquivosDaRequisicao(ArmazenamentoFotos fotos)
{
    private readonly List<string> criados = [];

    public void Registrar(string url) => criados.Add(url);

    public void Descartar()
    {
        foreach (var url in criados)
            fotos.Excluir(url);
        criados.Clear();
    }
}
