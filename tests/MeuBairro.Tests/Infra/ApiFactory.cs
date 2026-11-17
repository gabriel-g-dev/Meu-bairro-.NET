using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeuBairro.Api.Contratos;
using MeuBairro.Api.Dados;
using MeuBairro.Api.Dominio;
using MeuBairro.Api.Seguranca;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeuBairro.Tests.Infra;

// Sobe a API completa em memória (TestServer) com bancos SQLite temporários e exclusivos de cada fábrica.
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string SegredoJwt = "segredo-exclusivo-dos-testes-com-mais-de-32-caracteres";
    public const string SenhaPadrao = "Senha@123";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly bool apagarPasta;

    public ApiFactory() : this(null) { }

    // Com uma pasta informada, os bancos sobrevivem ao descarte da fábrica (simula reinício da API).
    protected ApiFactory(string? pasta)
    {
        Pasta = pasta ?? Path.Combine(Path.GetTempPath(), "meubairro-testes", Guid.NewGuid().ToString("N"));
        apagarPasta = pasta is null;
    }

    public string Pasta { get; }
    public ColetorLogs Logs { get; } = new();

    protected virtual string Ambiente => "Testing";

    public string PastaFotos => Path.Combine(Pasta, "fotos");

    protected virtual void Configurar(Dictionary<string, string?> configuracao) { }

    protected virtual void ConfigurarServicos(IServiceCollection services) { }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(Pasta);
        var configuracao = new Dictionary<string, string?>
        {
            // Sem ConnectionStrings:Controle, como no appsettings: a idempotência fica no mesmo arquivo (modo atômico).
            ["ConnectionStrings:MeuBairro"] = $"Data Source={Path.Combine(Pasta, "meubairro.db")}",
            ["Fotos:Pasta"] = PastaFotos,
            ["Jwt:Segredo"] = SegredoJwt,
            ["Senhas:FatorTrabalho"] = "4",
            ["DadosExemplo:Habilitado"] = "false",
            ["LimiteRequisicoes:Global:Limite"] = "100000",
            ["LimiteRequisicoes:Autenticacao:Limite"] = "100000",
            ["LimiteRequisicoes:Leitura:Limite"] = "100000",
            ["LimiteRequisicoes:Escrita:Limite"] = "100000"
        };
        Configurar(configuracao);

        builder.UseEnvironment(Ambiente);
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(configuracao));
        builder.ConfigureLogging(l => l.AddProvider(Logs).AddFilter<ColetorLogs>(null, LogLevel.Trace));
        builder.ConfigureTestServices(ConfigurarServicos);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        if (!apagarPasta)
            return;
        try
        {
            Directory.Delete(Pasta, recursive: true);
        }
        catch (IOException)
        {
            // arquivos ainda presos pelo SO; ficam na pasta temporária
        }
    }

    public HttpClient Cliente(string? token = null)
    {
        var cliente = CreateClient();
        if (token is not null)
            cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return cliente;
    }

    public async Task<Conta> NovoCidadaoAsync(string? email = null, string senha = SenhaPadrao, string nome = "Cidadã de Teste")
    {
        email ??= $"cidadao.{Guid.NewGuid():N}@teste.com";
        using var cliente = Cliente();
        var cadastro = await cliente.PostAsJsonAsync("/auth/register", new { nome, email, senha });
        Assert.Equal(201, (int)cadastro.StatusCode);
        return await LoginAsync(email, senha);
    }

    // Funcionários não têm cadastro público: o primeiro gestor é criado direto no banco de teste.
    public async Task<Conta> NovoGestorAsync(string? email = null)
    {
        email ??= $"gestor.{Guid.NewGuid():N}@teste.com";
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var senhas = scope.ServiceProvider.GetRequiredService<SenhaService>();
            db.Funcionarios.Add(new Funcionario
            {
                Nome = "Gestora de Teste", Setor = "Zeladoria", Cargo = "Gestora", Email = email, Senha = senhas.Gerar(SenhaPadrao)
            });
            await db.SaveChangesAsync();
        }
        return await LoginAsync(email, SenhaPadrao);
    }

    public async Task<Conta> LoginAsync(string email, string senha)
    {
        using var cliente = Cliente();
        var resposta = await cliente.PostAsJsonAsync("/auth/login", new { email, senha });
        Assert.Equal(200, (int)resposta.StatusCode);
        var login = await resposta.LerAsync<LoginResponse>();
        return new Conta(login.Usuario.Id, login.Usuario.Email, login.Token, this);
    }

    public async Task<T> NoBancoAsync<T>(Func<AppDbContext, Task<T>> consulta)
    {
        using var scope = Services.CreateScope();
        return await consulta(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}

public sealed record Conta(long Id, string Email, string Token, ApiFactory Fabrica)
{
    public HttpClient Cliente() => Fabrica.Cliente(Token);
}

public sealed class ColetorLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> mensagens = new();

    public IReadOnlyCollection<string> Mensagens => mensagens.ToArray();

    public ILogger CreateLogger(string categoryName) => new Registrador(categoryName, mensagens);

    public void Dispose() { }

    private sealed class Registrador(string categoria, ConcurrentQueue<string> destino) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            destino.Enqueue($"{logLevel} {categoria}: {formatter(state, exception)} {exception}");
    }
}

public static class HttpExtensions
{
    public static async Task<T> LerAsync<T>(this HttpResponseMessage resposta)
    {
        // Permite ler o mesmo corpo mais de uma vez no teste.
        await resposta.Content.LoadIntoBufferAsync();
        return (await resposta.Content.ReadFromJsonAsync<T>(ApiFactory.Json))!;
    }

    public static async Task AssertStatusAsync(this HttpResponseMessage resposta, int esperado)
    {
        await resposta.Content.LoadIntoBufferAsync();
        Assert.True((int)resposta.StatusCode == esperado,
            $"Esperado {esperado}, recebido {(int)resposta.StatusCode}: {await resposta.Content.ReadAsStringAsync()}");
    }

    public static Task<ErroResposta> ErroAsync(this HttpResponseMessage resposta) => resposta.LerAsync<ErroResposta>();

    public static int Codigo(this HttpResponseMessage resposta) => (int)resposta.StatusCode;
}
