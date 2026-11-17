using System.Data.Common;
using MeuBairro.Api.Configuracao;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using Oracle.EntityFrameworkCore.Infrastructure;

namespace MeuBairro.Api.Dados;

public static class BancoExtensions
{
    public const string ConexaoPrincipal = "MeuBairro";
    public const string ConexaoControle = "Controle";
    public const string ConexaoOracle = "Oracle";

    public static IServiceCollection AddBancoDeDados(this IServiceCollection services)
    {
        services.AddSingleton<BloqueioEscritaComandoInterceptor>();
        services.AddSingleton<BloqueioEscritaSaveChangesInterceptor>();
        services.AddSingleton(sp => ConexoesBanco.Criar(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IHostEnvironment>(),
            sp.GetRequiredService<IOptions<BancoOptions>>().Value));

        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            var banco = sp.GetRequiredService<IOptions<BancoOptions>>().Value;

            if (banco.Provedor == ProvedorBanco.Oracle)
            {
                var conexao = sp.GetRequiredService<IConfiguration>().GetConnectionString(ConexaoOracle);
                if (string.IsNullOrWhiteSpace(conexao))
                    throw new InvalidOperationException(
                        "Banco:Provedor=Oracle exige ConnectionStrings:Oracle (defina por variável de ambiente ou user-secrets)");

                ConfigurarOracle(options, conexao, banco);
                if (!banco.Oracle.PermitirEscrita)
                    options.AddInterceptors(
                        sp.GetRequiredService<BloqueioEscritaSaveChangesInterceptor>(),
                        sp.GetRequiredService<BloqueioEscritaComandoInterceptor>());
            }
            else
            {
                options.UseSqlite(sp.GetRequiredService<ConexoesBanco>().Principal!)
                    .AddInterceptors(FuncoesSqliteInterceptor.Instancia);
            }

            // Ponto de extensão para interceptadores registrados no contêiner (usado pelos testes de falha).
            options.AddInterceptors(sp.GetServices<IInterceptor>());
            options.ReplaceService<IModelCacheKeyFactory, ChaveModeloFactory>();
        });

        // Requisições simultâneas com a mesma Idempotency-Key terminam numa violação do índice único, já tratada
        // pelo IdempotenciaService; sem o ajuste, o EF registraria cada uma como erro. Falhas reais continuam
        // registradas pelo ErroMiddleware e pelos avisos do serviço.
        services.AddDbContextFactory<ControleDbContext>((sp, options) =>
            options.UseSqlite(sp.GetRequiredService<ConexoesBanco>().Controle)
                .AddInterceptors(sp.GetServices<IInterceptor>())
                .ConfigureWarnings(w => w.Log(
                    (CoreEventId.SaveChangesFailed, LogLevel.Debug),
                    (RelationalEventId.CommandError, LogLevel.Debug))));

        return services;
    }

    public static DbContextOptionsBuilder ConfigurarOracle(DbContextOptionsBuilder options, string conexao, BancoOptions banco)
    {
        var versao = banco.Oracle.VersaoCompatibilidade switch
        {
            >= 23 => OracleSQLCompatibility.DatabaseVersion23,
            >= 21 => OracleSQLCompatibility.DatabaseVersion21,
            _ => OracleSQLCompatibility.DatabaseVersion19
        };
        return options.UseOracle(conexao, o => o.UseOracleSQLCompatibility(versao));
    }
}

// Conexões SQLite já resolvidas. Sem ConnectionStrings:Controle, a tabela de idempotência fica no mesmo
// arquivo do banco principal, o que permite confirmar o resultado da chave na mesma transação do negócio.
// Com o Oracle ela fica num SQLite à parte, para não acrescentar tabelas ao esquema compartilhado.
public sealed class ConexoesBanco
{
    private const string PadraoPrincipal = "Data Source=data/meubairro.db";
    private const string PadraoControleSeparado = "Data Source=data/meubairro-controle.db";

    private ConexoesBanco(string? principal, string controle, bool compartilhado)
    {
        Principal = principal;
        Controle = controle;
        ControleCompartilhado = compartilhado;
    }

    public string? Principal { get; }
    public string Controle { get; }
    public bool ControleCompartilhado { get; }

    public static ConexoesBanco Criar(IConfiguration configuracao, IHostEnvironment ambiente, BancoOptions banco)
    {
        var raiz = ambiente.ContentRootPath;
        var principal = banco.Provedor == ProvedorBanco.Sqlite
            ? CaminhoSqlite.Resolver(configuracao.GetConnectionString(BancoExtensions.ConexaoPrincipal) ?? PadraoPrincipal, raiz)
            : null;

        var controleConfigurado = configuracao.GetConnectionString(BancoExtensions.ConexaoControle);
        var controle = !string.IsNullOrWhiteSpace(controleConfigurado)
            ? CaminhoSqlite.Resolver(controleConfigurado, raiz)
            : principal ?? CaminhoSqlite.Resolver(PadraoControleSeparado, raiz);

        return new ConexoesBanco(principal, controle, principal is not null && MesmoArquivo(principal, controle));
    }

    private static bool MesmoArquivo(string a, string b)
    {
        var x = new SqliteConnectionStringBuilder(a);
        var y = new SqliteConnectionStringBuilder(b);
        var emMemoria = x.Mode == SqliteOpenMode.Memory || x.DataSource is ":memory:" or "";
        return !emMemoria && string.Equals(x.DataSource, y.DataSource, StringComparison.OrdinalIgnoreCase);
    }
}

// O lower() nativo do SQLite só converte letras ASCII: "Água" continuaria "Água" e não seria igual a "água".
// A versão registrada aqui segue as regras Unicode do .NET, como o LOWER do Oracle, para que filtros e
// verificações sem distinção de maiúsculas (bairro, título, e-mail) funcionem também com letras acentuadas.
public sealed class FuncoesSqliteInterceptor : DbConnectionInterceptor
{
    public static readonly FuncoesSqliteInterceptor Instancia = new();

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        Registrar(connection);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Registrar(connection);
        return Task.CompletedTask;
    }

    private static void Registrar(DbConnection connection)
    {
        if (connection is SqliteConnection sqlite)
            sqlite.CreateFunction("lower", (string? texto) => texto?.ToLowerInvariant(), isDeterministic: true);
    }
}

public sealed class ChaveModeloFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is AppDbContext app
            ? (context.GetType(), app.ChaveModelo, designTime)
            : (context.GetType(), designTime);
}

public static class CaminhoSqlite
{
    // Caminhos relativos são resolvidos a partir da pasta do projeto, e não do diretório de onde o comando foi executado.
    public static string Resolver(string connectionString, string raiz)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var memoria = builder.Mode == SqliteOpenMode.Memory || builder.DataSource == ":memory:";
        if (memoria || string.IsNullOrWhiteSpace(builder.DataSource))
            return builder.ToString();

        if (!Path.IsPathRooted(builder.DataSource))
            builder.DataSource = Path.GetFullPath(Path.Combine(raiz, builder.DataSource));

        if (builder.Mode == SqliteOpenMode.ReadWriteCreate && Path.GetDirectoryName(builder.DataSource) is { Length: > 0 } pasta)
        {
            try
            {
                Directory.CreateDirectory(pasta);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A falha aparece ao abrir a conexão e é reportada pelo health check.
            }
        }

        return builder.ToString();
    }
}
