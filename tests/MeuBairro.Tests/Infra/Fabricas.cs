using System.Data.Common;
using System.Net;
using MeuBairro.Api.Dados;
using MeuBairro.Api.Dominio;
using MeuBairro.Api.Seguranca;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace MeuBairro.Tests.Infra;

// Injeta falhas reais no nível do comando SQL para testar rollback, liberação de chaves e respostas de erro.
public sealed class FalhaControlada : DbCommandInterceptor
{
    public volatile string? InsercaoComFalha;
    public volatile bool ExcecaoInesperadaEmCategorias;
    public volatile bool FalharConclusaoIdempotencia;

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Verificar(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Verificar(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Verificar(command);
        return ValueTask.FromResult(result);
    }

    private void Verificar(DbCommand command)
    {
        if (InsercaoComFalha is { } tabela && command.CommandText.Contains($"INSERT INTO \"{tabela}\"", StringComparison.Ordinal))
            throw new SqliteException("falha simulada de gravação", 10);
        if (ExcecaoInesperadaEmCategorias && command.CommandText.Contains("FROM \"CATEGORIAS\"", StringComparison.Ordinal))
            throw new InvalidOperationException("detalhe-interno-que-nao-pode-vazar");
        if (FalharConclusaoIdempotencia && command.CommandText.Contains("UPDATE \"IDEMPOTENCIA_REQUISICOES\"", StringComparison.Ordinal))
            throw new SqliteException("falha simulada ao registrar o resultado da chave", 10);
    }
}

// Falha ao abrir a transação do banco principal (ex.: erro de disco), depois que a Idempotency-Key já foi registrada.
public sealed class FalhaAoIniciarTransacao : DbTransactionInterceptor
{
    public volatile bool Ativa;

    public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
        TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default) =>
        Ativa && eventData.Context is AppDbContext
            ? throw new SqliteException("falha simulada ao iniciar a transação", 10)
            : ValueTask.FromResult(result);
}

public sealed class FabricaComFalhas : ApiFactory
{
    public FalhaControlada Falhas { get; } = new();
    public FalhaAoIniciarTransacao FalhaTransacao { get; } = new();

    protected override void ConfigurarServicos(IServiceCollection services)
    {
        services.AddSingleton<IInterceptor>(Falhas);
        services.AddSingleton<IInterceptor>(FalhaTransacao);
    }
}

public sealed class FabricaComRelogio : ApiFactory
{
    public FakeTimeProvider Tempo { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigurarServicos(IServiceCollection services) =>
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Tempo));
}

public sealed class FabricaComLimites(int autenticacao = 100000, int leitura = 100000, int escrita = 100000, int global = 100000)
    : ApiFactory
{
    protected override void Configurar(Dictionary<string, string?> configuracao)
    {
        configuracao["LimiteRequisicoes:Autenticacao:Limite"] = autenticacao.ToString();
        configuracao["LimiteRequisicoes:Leitura:Limite"] = leitura.ToString();
        configuracao["LimiteRequisicoes:Escrita:Limite"] = escrita.ToString();
        configuracao["LimiteRequisicoes:Global:Limite"] = global.ToString();
    }

    // Cria a conta direto no banco e emite o token pelo serviço da API, sem consumir o limite de login.
    public async Task<Conta> CidadaoSemLoginAsync()
    {
        var email = $"limite.{Guid.NewGuid():N}@teste.com";
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var usuario = new Usuario
        {
            Nome = "Cidadão Limite", Email = email, Senha = "$2a$04$naoutilizada", DataCadastro = DateTime.Now,
            Endereco = new Endereco { Logradouro = "Rua", Bairro = "Centro", Cidade = "São Paulo", Estado = "SP", Cep = "01001000" }
        };
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();
        var token = scope.ServiceProvider.GetRequiredService<TokenService>().Gerar(usuario.Id, usuario.Nome, email, Perfil.CIDADAO);
        return new Conta(usuario.Id, email, token.Token, this);
    }
}

// Simula a conexão TCP vinda de um IP específico (o TestServer não tem conexão real).
public sealed class FabricaComProxy(int autenticacao) : ApiFactory
{
    public const string CabecalhoIpConexao = "X-Teste-Ip-Conexao";
    public const string IpDoProxy = "10.0.0.1";

    protected override void Configurar(Dictionary<string, string?> configuracao)
    {
        configuracao["LimiteRequisicoes:Autenticacao:Limite"] = autenticacao.ToString();
        configuracao["Proxy:IpsConfiaveis:0"] = IpDoProxy;
    }

    protected override void ConfigurarServicos(IServiceCollection services) =>
        services.AddSingleton<IStartupFilter, ConexaoSimulada>();

    private sealed class ConexaoSimulada : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, proximo) =>
            {
                if (http.Request.Headers.TryGetValue(CabecalhoIpConexao, out var ip))
                    http.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                return proximo(http);
            });
            next(app);
        };
    }
}

public sealed class FabricaBancoIndisponivel : ApiFactory
{
    protected override void Configurar(Dictionary<string, string?> configuracao)
    {
        configuracao["ConnectionStrings:MeuBairro"] =
            $"Data Source={Path.Combine(Pasta, "nao-existe", "banco.db")};Mode=ReadOnly";
        configuracao["Banco:InicializarSqlite"] = "false";
    }
}

// Tabela de idempotência em arquivo separado, como acontece quando o banco principal é o Oracle.
public sealed class FabricaControleSeparado : ApiFactory
{
    public FalhaControlada Falhas { get; } = new();

    protected override void Configurar(Dictionary<string, string?> configuracao) =>
        configuracao["ConnectionStrings:Controle"] = $"Data Source={Path.Combine(Pasta, "controle.db")}";

    protected override void ConfigurarServicos(IServiceCollection services) =>
        services.AddSingleton<IInterceptor>(Falhas);
}

public sealed class FabricaControleIndisponivel : ApiFactory
{
    protected override void Configurar(Dictionary<string, string?> configuracao) =>
        configuracao["ConnectionStrings:Controle"] = $"Data Source={Path.Combine(Pasta, "nao-existe", "controle.db")};Mode=ReadOnly";
}

public sealed class FabricaReutilizavel(string pasta) : ApiFactory(pasta);

// Provedor Oracle com a tabela de idempotência num SQLite temporário (nunca na pasta do projeto).
public sealed class FabricaOracle(string? conexao) : ApiFactory
{
    protected override void Configurar(Dictionary<string, string?> configuracao)
    {
        configuracao["Banco:Provedor"] = "Oracle";
        configuracao["ConnectionStrings:Oracle"] = conexao;
        configuracao["ConnectionStrings:Controle"] = $"Data Source={Path.Combine(Pasta, "controle.db")}";
    }
}

public sealed class FabricaComConfiguracao(params (string Chave, string Valor)[] valores) : ApiFactory
{
    protected override void Configurar(Dictionary<string, string?> configuracao)
    {
        foreach (var (chave, valor) in valores)
            configuracao[chave] = valor;
    }
}

public sealed class FabricaEmAmbiente(string ambiente, params (string Chave, string Valor)[] valores) : ApiFactory
{
    protected override string Ambiente => ambiente;

    protected override void Configurar(Dictionary<string, string?> configuracao)
    {
        foreach (var (chave, valor) in valores)
            configuracao[chave] = valor;
    }
}

public static class FabricaExtensions
{
    public static Task<int> ContarSolicitacoesAsync(this ApiFactory api, long usuarioId) =>
        api.NoBancoAsync(db => db.Solicitacoes.CountAsync(s => s.UsuarioId == usuarioId));

    public static int ContarFotos(this ApiFactory api) =>
        Directory.Exists(api.PastaFotos) ? Directory.GetFiles(api.PastaFotos).Length : 0;
}
