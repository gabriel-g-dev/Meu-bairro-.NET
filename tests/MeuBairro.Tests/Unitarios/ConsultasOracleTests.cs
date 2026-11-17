using System.Data.Common;
using MeuBairro.Api.Configuracao;
using MeuBairro.Api.Contratos;
using MeuBairro.Api.Dados;
using MeuBairro.Api.Dominio;
using MeuBairro.Api.Seguranca;
using MeuBairro.Api.Servicos;
using MeuBairro.Tests.Infra;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MeuBairro.Tests.Unitarios;

// Gera o SQL que o provedor Oracle enviaria, a partir do código real dos serviços, sem abrir conexão:
// a abertura é suprimida e o primeiro comando é capturado antes de ser executado.
public sealed class ConsultasOracleTests
{
    private sealed class ComandoCapturado(string sql, IReadOnlyList<object?> parametros) : Exception("comando capturado")
    {
        public string Sql { get; } = sql;
        public IReadOnlyList<object?> Parametros { get; } = parametros;
    }

    private sealed class SemConexao : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result) => InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class Captura : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            throw Capturar(command);

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default) =>
            throw Capturar(command);

        private static ComandoCapturado Capturar(DbCommand command) =>
            new(command.CommandText, command.Parameters.Cast<DbParameter>().Select(p => p.Value).ToList());
    }

    private static AppDbContext ContextoOracle(bool capturar)
    {
        var banco = new BancoOptions { Provedor = ProvedorBanco.Oracle };
        var options = new DbContextOptionsBuilder<AppDbContext>();
        BancoExtensions.ConfigurarOracle(options, "User Id=teste;Password=teste;Data Source=127.0.0.1:1/NAOEXISTE", banco)
            .ReplaceService<IModelCacheKeyFactory, ChaveModeloFactory>()
            .AddInterceptors(new BloqueioEscritaSaveChangesInterceptor(), new BloqueioEscritaComandoInterceptor());
        if (capturar)
            options.AddInterceptors(new SemConexao(), new Captura());
        return new AppDbContext(options.Options, Options.Create(banco));
    }

    private static (ContaService Contas, OcorrenciaService Ocorrencias) Servicos(AppDbContext db)
    {
        var relogio = new Relogio(TimeProvider.System, Options.Create(new AplicacaoOptions()));
        var unidade = new UnidadeDeTrabalho(db, new TravasPorChave());
        var enderecos = new EnderecoService(db, Options.Create(new EnderecoPadraoOptions()));
        var ambiente = new AmbienteFalso(Path.GetTempPath());
        var fotos = new ArmazenamentoFotos(Options.Create(new FotosOptions()), ambiente, NullLogger<ArmazenamentoFotos>.Instance);
        var tokens = new TokenService(Options.Create(new JwtOptions { Segredo = ApiFactory.SegredoJwt }), ambiente,
            NullLogger<TokenService>.Instance);
        var senhas = new SenhaService(Options.Create(new SenhaOptions { FatorTrabalho = 4 }));
        var categorias = new CategoriaService(db, Options.Create(new CategoriasOptions()), unidade);
        return (new ContaService(db, senhas, tokens, enderecos, relogio, unidade),
            new OcorrenciaService(db, categorias, enderecos, fotos, relogio, unidade, new ArquivosDaRequisicao(fotos),
                Options.Create(new FotosOptions())));
    }

    [Fact]
    public async Task Login_gera_consulta_oracle_com_lower_no_email()
    {
        await using var db = ContextoOracle(capturar: true);

        var capturado = await Assert.ThrowsAsync<ComandoCapturado>(() =>
            Servicos(db).Contas.EntrarAsync(new LoginRequest { Email = " Maria@Teste.COM ", Senha = "x" }, default));

        Assert.Contains("FROM \"USUARIOS\"", capturado.Sql);
        Assert.Contains("LOWER(\"u\".\"USUARIO_EMAIL\")", capturado.Sql);
        Assert.Contains("\"USUARIO_SENHA\"", capturado.Sql);
        Assert.Contains("maria@teste.com", capturado.Parametros);
        Assert.Contains("FETCH FIRST", capturado.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Listagem_paginada_gera_filtros_com_codigos_char_1_e_bairro_sem_caixa()
    {
        await using var db = ContextoOracle(capturar: true);
        var filtro = new FiltroOcorrencias { Status = "EM_ANALISE", Prioridade = "ALTA", Bairro = " Centro " };

        var capturado = await Assert.ThrowsAsync<ComandoCapturado>(() =>
            Servicos(db).Ocorrencias.ListarPaginadoAsync(filtro, new UsuarioAtual(1, Perfil.CIDADAO), "http://x", default));

        Assert.Contains("FROM \"SOLICITACOES\"", capturado.Sql);
        Assert.Contains("\"SOLICITACAO_STATUS\"", capturado.Sql);
        Assert.Contains("\"SOLICITACAO_PRIORIDADE\"", capturado.Sql);
        Assert.Contains("LOWER(\"e\".\"ENDERECO_BAIRRO\")", capturado.Sql);
        Assert.Contains("E", capturado.Parametros);
        Assert.Contains("A", capturado.Parametros);
        Assert.Contains("centro", capturado.Parametros);
    }

    [Fact]
    public void Consultas_centrais_dos_servicos_sao_traduzidas_para_oracle()
    {
        using var db = ContextoOracle(capturar: false);
        long[] ids = [1, 2, 3];

        var pagina = db.Solicitacoes.AsNoTracking().Include(s => s.Categoria).Include(s => s.Endereco)
            .Where(s => s.Status != StatusSolicitacao.RESOLVIDA)
            .OrderByDescending(s => s.DataAbertura).ThenByDescending(s => s.Id)
            .Skip(20).Take(10).ToQueryString();
        var fotos = db.Anexos.Where(a => ids.Contains(a.SolicitacaoId) && a.Tipo.StartsWith("image/"))
            .Select(a => new { a.SolicitacaoId, a.Id, a.Url }).ToQueryString();
        var avaliada = db.Solicitacoes.Where(s => s.Id == 1)
            .Select(s => new { s.UsuarioId, s.Status, Avaliada = s.Avaliacao != null, Anexos = s.Anexos.Count }).ToQueryString();
        var resumo = db.Solicitacoes.GroupBy(s => s.Status).Select(g => new { g.Key, Total = g.LongCount() }).ToQueryString();
        var historico = db.Andamentos.Where(a => a.SolicitacaoId == 1).OrderBy(a => a.Data).ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.Descricao, a.Status, a.Data, a.Funcionario.Setor }).ToQueryString();
        var duplicada = db.Solicitacoes.Where(s => s.UsuarioId == 1 && s.Titulo.ToLower() == "buraco"
                                                    && s.Endereco.Logradouro.ToLower() == "rua a")
            .Select(s => (long?)s.Id).ToQueryString();

        Assert.Contains("OFFSET", pagina, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INNER JOIN \"CATEGORIAS\"", pagina);
        Assert.Contains("INNER JOIN \"ENDERECOS\"", pagina);
        Assert.Contains("\"ANEXO_TIPO\" LIKE", fotos);
        Assert.Contains("\"AVALIACOES\"", avaliada);
        Assert.Contains("GROUP BY \"s\".\"SOLICITACAO_STATUS\"", resumo);
        Assert.Contains("\"FUNCIONARIOS\"", historico);
        Assert.Contains("LOWER(\"s\".\"SOLICITACAO_TITULO\")", duplicada);
    }

    [Fact]
    public void Ordenacao_por_prioridade_e_status_e_traduzida_para_oracle_sem_ordenar_pelo_codigo_gravado()
    {
        using var db = ContextoOracle(capturar: false);

        var sql = Ordenacao.Aplicar(db.Solicitacoes.AsNoTracking(), Ordenacao.Ler(["prioridade,desc", "status,asc"]))
            .ToQueryString();
        var ordem = sql[sql.IndexOf("ORDER BY", StringComparison.Ordinal)..];

        // Prioridade e status entram como CASE (posição no negócio); só o desempate usa uma coluna direto.
        Assert.StartsWith("ORDER BY CASE", ordem);
        Assert.Equal(2, ordem.Split("CASE").Length - 1);
        Assert.EndsWith("\"s\".\"SOLICITACAO_ID\"", ordem.Trim());
    }
}
