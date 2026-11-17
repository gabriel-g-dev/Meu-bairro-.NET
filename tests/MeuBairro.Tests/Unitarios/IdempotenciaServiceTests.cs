using MeuBairro.Api.Configuracao;
using MeuBairro.Api.Dados;
using MeuBairro.Api.Dominio;
using MeuBairro.Api.Infra.Idempotencia;
using MeuBairro.Api.Servicos;
using MeuBairro.Tests.Infra;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MeuBairro.Tests.Unitarios;

// Banco principal e tabela de controle no mesmo arquivo SQLite, como na configuração padrão da API.
public sealed class IdempotenciaServiceTests : IAsyncLifetime
{
    private readonly string pasta = Path.Combine(Path.GetTempPath(), $"meubairro-idem-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider tempo = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
    private string conexao = "";
    private IdempotenciaService servico = null!;
    private FabricaControle fabrica = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(pasta);
        var configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MeuBairro"] = $"Data Source={Path.Combine(pasta, "meubairro.db")}"
            })
            .Build();
        var conexoes = ConexoesBanco.Criar(configuracao, new AmbienteFalso(pasta), new BancoOptions());
        conexao = conexoes.Principal!;

        await using (var app = NovoAppDbContext())
            await app.Database.EnsureCreatedAsync();
        fabrica = new FabricaControle(new DbContextOptionsBuilder<ControleDbContext>().UseSqlite(conexoes.Controle).Options);
        await using (var controle = fabrica.CreateDbContext())
            await InicializadorBanco.CriarTabelaControleAsync(controle, default);

        var opcoes = Options.Create(new IdempotenciaOptions { RetencaoHoras = 24, BloqueioSegundos = 30 });
        servico = new IdempotenciaService(fabrica, conexoes, [], new Relogio(tempo, Options.Create(new AplicacaoOptions())), opcoes);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(pasta, recursive: true);
        return Task.CompletedTask;
    }

    private AppDbContext NovoAppDbContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conexao)
            .ReplaceService<IModelCacheKeyFactory, ChaveModeloFactory>().Options,
        Options.Create(new BancoOptions()));

    private async Task<RegistroIdempotencia> RegistroAsync(long id)
    {
        await using var db = fabrica.CreateDbContext();
        return await db.Idempotencia.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    [Fact]
    public void Configuracao_padrao_coloca_a_tabela_de_controle_no_banco_principal() =>
        Assert.True(servico.ConclusaoAtomica);

    [Fact]
    public async Task Ciclo_nova_concluida_e_repetida()
    {
        var inicio = await servico.IniciarAsync("CIDADAO:1|POST|/ocorrencias", "k1", "hash", default);
        await servico.ConcluirAsync(inicio.Id, inicio.Token!, 201, "application/json", "/ocorrencias/9", "{\"id\":9}"u8.ToArray());

        var repetida = await servico.IniciarAsync("CIDADAO:1|POST|/ocorrencias", "k1", "hash", default);

        Assert.Equal(ResultadoInicio.Nova, inicio.Resultado);
        Assert.Equal(ResultadoInicio.Repetida, repetida.Resultado);
        Assert.Equal(201, repetida.Registro!.StatusResposta);
        Assert.Equal("/ocorrencias/9", repetida.Registro.Location);
    }

    [Fact]
    public async Task Em_andamento_e_payload_divergente()
    {
        await servico.IniciarAsync("escopo", "k2", "hash-a", default);

        var concorrente = await servico.IniciarAsync("escopo", "k2", "hash-a", default);
        var divergente = await servico.IniciarAsync("escopo", "k2", "hash-b", default);
        var outroEscopo = await servico.IniciarAsync("outro-escopo", "k2", "hash-b", default);

        Assert.Equal(ResultadoInicio.EmAndamento, concorrente.Resultado);
        Assert.Equal(30, concorrente.EsperaSegundos);
        Assert.Equal(ResultadoInicio.PayloadDivergente, divergente.Resultado);
        Assert.Equal(ResultadoInicio.Nova, outroEscopo.Resultado);
    }

    [Fact]
    public async Task Processamento_interrompido_pode_ser_assumido_depois_do_bloqueio()
    {
        var original = await servico.IniciarAsync("escopo", "k3", "hash", default);
        tempo.Advance(TimeSpan.FromSeconds(31));

        var assumida = await servico.IniciarAsync("escopo", "k3", "hash", default);
        // A requisição antiga perdeu a posse: concluir com o token velho não tem efeito.
        var antigaConcluiu = await servico.ConcluirAsync(original.Id, original.Token!, 201, null, null, []);
        var aindaEmAndamento = await servico.IniciarAsync("escopo", "k3", "hash", default);

        Assert.Equal(ResultadoInicio.Nova, assumida.Resultado);
        Assert.Equal(original.Id, assumida.Id);
        Assert.NotEqual(original.Token, assumida.Token);
        Assert.False(antigaConcluiu);
        Assert.Equal(ResultadoInicio.EmAndamento, aindaEmAndamento.Resultado);
    }

    [Fact]
    public async Task Queda_antes_do_commit_nao_grava_o_negocio_e_a_chave_pode_ser_reprocessada()
    {
        var inicio = await servico.IniciarAsync("GESTOR:1|POST|/categorias", "queda", "hash", default);

        // Requisição em andamento: negócio e resultado da chave na mesma transação, mas o processo "cai" antes do commit.
        await using (var db = NovoAppDbContext())
        {
            await using var transacao = await db.Database.BeginTransactionAsync();
            db.Categorias.Add(new Categoria { Nome = "Categoria da queda", PrazoDias = 3 });
            await db.SaveChangesAsync();
            Assert.True(await servico.ConcluirNaTransacaoAsync(transacao.GetDbTransaction(), inicio.Id, inicio.Token!, 201, null, null, []));
        }

        await using (var db = NovoAppDbContext())
            Assert.False(await db.Categorias.AnyAsync(c => c.Nome == "Categoria da queda"));
        Assert.Equal(SituacaoIdempotencia.Processando, (await RegistroAsync(inicio.Id)).Situacao);

        // Depois do bloqueio, a nova tentativa assume a chave e executa (nada foi gravado antes).
        tempo.Advance(TimeSpan.FromSeconds(31));
        var novaTentativa = await servico.IniciarAsync("GESTOR:1|POST|/categorias", "queda", "hash", default);
        Assert.Equal(ResultadoInicio.Nova, novaTentativa.Resultado);
    }

    [Fact]
    public async Task Commit_grava_negocio_e_resultado_juntos()
    {
        var inicio = await servico.IniciarAsync("GESTOR:1|POST|/categorias", "commit", "hash", default);

        await using (var db = NovoAppDbContext())
        {
            await using var transacao = await db.Database.BeginTransactionAsync();
            db.Categorias.Add(new Categoria { Nome = "Categoria confirmada", PrazoDias = 3 });
            await db.SaveChangesAsync();
            Assert.True(await servico.ConcluirNaTransacaoAsync(transacao.GetDbTransaction(), inicio.Id, inicio.Token!, 201,
                "application/json", "/categorias/1", "{}"u8.ToArray()));
            await transacao.CommitAsync();
        }

        await using (var db = NovoAppDbContext())
            Assert.Equal(1, await db.Categorias.CountAsync(c => c.Nome == "Categoria confirmada"));
        var repetida = await servico.IniciarAsync("GESTOR:1|POST|/categorias", "commit", "hash", default);
        Assert.Equal(ResultadoInicio.Repetida, repetida.Resultado);
        Assert.Equal("/categorias/1", repetida.Registro!.Location);
    }

    [Fact]
    public async Task Conclusao_na_transacao_falha_se_a_chave_foi_assumida_por_outra_tentativa()
    {
        var original = await servico.IniciarAsync("escopo", "posse", "hash", default);
        tempo.Advance(TimeSpan.FromSeconds(31));
        await servico.IniciarAsync("escopo", "posse", "hash", default);

        await using var db = NovoAppDbContext();
        await using var transacao = await db.Database.BeginTransactionAsync();

        Assert.False(await servico.ConcluirNaTransacaoAsync(transacao.GetDbTransaction(), original.Id, original.Token!, 201, null, null, []));
    }

    [Fact]
    public async Task Descartar_libera_a_chave_e_expirados_sao_removidos()
    {
        var inicio = await servico.IniciarAsync("escopo", "k4", "hash", default);
        await servico.DescartarAsync(inicio.Id, inicio.Token!);
        var depois = await servico.IniciarAsync("escopo", "k4", "outro-hash", default);
        await servico.ConcluirAsync(depois.Id, depois.Token!, 200, null, null, []);
        // Uma chave já concluída não é apagada por descarte.
        await servico.DescartarAsync(depois.Id, depois.Token!);

        tempo.Advance(TimeSpan.FromHours(24));
        var removidos = await servico.RemoverExpiradosAsync(default);

        Assert.Equal(ResultadoInicio.Nova, depois.Resultado);
        Assert.Equal(1, removidos);
    }

    [Fact]
    public async Task Inicios_concorrentes_com_a_mesma_chave_tem_um_unico_vencedor()
    {
        var resultados = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => servico.IniciarAsync("escopo", "k5", "hash", default))));

        Assert.Single(resultados, r => r.Resultado == ResultadoInicio.Nova);
        Assert.All(resultados.Where(r => r.Resultado != ResultadoInicio.Nova),
            r => Assert.Equal(ResultadoInicio.EmAndamento, r.Resultado));
    }

    private sealed class FabricaControle(DbContextOptions<ControleDbContext> options) : IDbContextFactory<ControleDbContext>
    {
        public ControleDbContext CreateDbContext() => new(options);
    }
}
