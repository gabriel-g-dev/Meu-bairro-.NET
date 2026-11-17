using System.Net.Http.Json;
using System.Text.Json;
using MeuBairro.Tests.Infra;
using Microsoft.EntityFrameworkCore;

namespace MeuBairro.Tests.Integracao;

// Cada teste sobe a própria API com limites baixos, para que um não consuma a cota do outro.
public sealed class LimiteRequisicoesTests
{
    [Fact]
    public async Task Login_acima_do_limite_retorna_429_com_retry_after()
    {
        await using var api = new FabricaComLimites(autenticacao: 3);
        using var cliente = api.Cliente();

        var respostas = new List<HttpResponseMessage>();
        for (var i = 0; i < 4; i++)
            respostas.Add(await cliente.PostAsJsonAsync("/auth/login", new { email = "ninguem@teste.com", senha = "Errada@123" }));

        Assert.All(respostas.Take(3), r => Assert.Equal(401, r.Codigo()));
        var bloqueada = respostas[3];
        Assert.Equal(429, bloqueada.Codigo());
        Assert.True(int.Parse(bloqueada.Headers.GetValues("Retry-After").Single()) >= 1);
        var erro = await bloqueada.ErroAsync();
        Assert.Equal(429, erro.Status);
        Assert.StartsWith("Muitas requisições", erro.Message);
    }

    [Fact]
    public async Task Cadastro_e_login_compartilham_a_politica_restritiva()
    {
        await using var api = new FabricaComLimites(autenticacao: 2);
        using var cliente = api.Cliente();

        var primeiro = await cliente.PostAsJsonAsync("/auth/register", new { nome = "A", email = "a@teste.com", senha = "Senha@123" });
        var login = await cliente.PostAsJsonAsync("/auth/login", new { email = "a@teste.com", senha = "Senha@123" });
        var segundo = await cliente.PostAsJsonAsync("/auth/register", new { nome = "B", email = "b@teste.com", senha = "Senha@123" });

        Assert.Equal(201, primeiro.Codigo());
        Assert.Equal(200, login.Codigo());
        Assert.Equal(429, segundo.Codigo());
        Assert.False(await api.NoBancoAsync(db => db.Usuarios.AnyAsync(u => u.Email == "b@teste.com")));
    }

    [Fact]
    public async Task Trocar_X_Forwarded_For_nao_burla_o_limite()
    {
        await using var api = new FabricaComLimites(autenticacao: 2);
        using var cliente = api.Cliente();

        var codigos = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            var requisicao = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
            {
                Content = JsonContent.Create(new { email = "x@teste.com", senha = "Errada@123" })
            };
            requisicao.Headers.Add("X-Forwarded-For", $"10.0.0.{i + 1}");
            requisicao.Headers.Add("X-Real-IP", $"10.0.1.{i + 1}");
            codigos.Add((await cliente.SendAsync(requisicao)).Codigo());
        }

        Assert.Equal([401, 401, 429, 429], codigos);
    }

    [Fact]
    public async Task Limite_de_leitura_e_contado_por_usuario()
    {
        await using var api = new FabricaComLimites(leitura: 5);
        var maria = await api.CidadaoSemLoginAsync();
        var joao = await api.CidadaoSemLoginAsync();
        using var clienteMaria = maria.Cliente();
        using var clienteJoao = joao.Cliente();

        var codigosMaria = new List<int>();
        for (var i = 0; i < 6; i++)
            codigosMaria.Add((await clienteMaria.GetAsync("/categorias")).Codigo());
        var joaoDepois = await clienteJoao.GetAsync("/categorias");

        Assert.Equal([200, 200, 200, 200, 200, 429], codigosMaria);
        Assert.Equal(200, joaoDepois.Codigo());
    }

    [Fact]
    public async Task Requisicao_de_escrita_rejeitada_nao_chega_a_gravar()
    {
        await using var api = new FabricaComLimites(escrita: 2);
        var cidadao = await api.CidadaoSemLoginAsync();
        using var cliente = cidadao.Cliente();

        var codigos = new List<int>();
        for (var i = 0; i < 3; i++)
            codigos.Add((await cliente.PostAsync("/ocorrencias", Dados.Ocorrencia())).Codigo());

        Assert.Equal([201, 201, 429], codigos);
        Assert.Equal(2, await api.ContarSolicitacoesAsync(cidadao.Id));
    }

    [Fact]
    public async Task Limite_global_por_ip_vale_tambem_para_rotas_publicas()
    {
        await using var api = new FabricaComLimites(global: 3);
        using var cliente = api.Cliente();

        var codigos = new List<int>();
        for (var i = 0; i < 4; i++)
            codigos.Add((await cliente.GetAsync($"/fotos/{Guid.NewGuid():N}.png")).Codigo());

        Assert.Equal([404, 404, 404, 429], codigos);
    }

    [Fact]
    public async Task Health_checks_nao_sao_limitados()
    {
        await using var api = new FabricaComLimites(global: 2);
        using var cliente = api.Cliente();

        for (var i = 0; i < 8; i++)
            Assert.Equal(200, (await cliente.GetAsync("/health/live")).Codigo());
    }
}

public sealed class SaudeTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private static async Task<JsonElement> LerAsync(HttpResponseMessage resposta) =>
        JsonDocument.Parse(await resposta.Content.ReadAsStringAsync()).RootElement;

    private static string Status(JsonElement relatorio, string verificacao) =>
        relatorio.GetProperty("verificacoes").EnumerateArray()
            .Single(v => v.GetProperty("nome").GetString() == verificacao).GetProperty("status").GetString()!;

    [Fact]
    public async Task Live_responde_sem_token_e_sem_depender_do_banco()
    {
        using var cliente = api.Cliente();

        var resposta = await cliente.GetAsync("/health/live");

        Assert.Equal(200, resposta.Codigo());
        var relatorio = await LerAsync(resposta);
        Assert.Equal("Healthy", relatorio.GetProperty("status").GetString());
        Assert.Equal(["aplicacao"], relatorio.GetProperty("verificacoes").EnumerateArray().Select(v => v.GetProperty("nome").GetString()));
    }

    [Fact]
    public async Task Ready_com_banco_disponivel_retorna_200_healthy_por_dependencia()
    {
        using var cliente = api.Cliente();

        var resposta = await cliente.GetAsync("/health/ready");

        Assert.Equal(200, resposta.Codigo());
        var relatorio = await LerAsync(resposta);
        Assert.Equal("Healthy", relatorio.GetProperty("status").GetString());
        Assert.Equal("Healthy", Status(relatorio, "banco"));
        Assert.Equal("Healthy", Status(relatorio, "idempotencia"));
        Assert.Equal("Healthy", Status(relatorio, "armazenamento-fotos"));
        Assert.True(resposta.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task Health_completo_tambem_responde_pelo_prefixo_api()
    {
        using var cliente = api.Cliente();

        var resposta = await cliente.GetAsync("/api/health");

        Assert.Equal(200, resposta.Codigo());
        Assert.Equal(4, (await LerAsync(resposta)).GetProperty("verificacoes").GetArrayLength());
    }

}

public sealed class SaudeBancoIndisponivelTests(FabricaBancoIndisponivel api) : IClassFixture<FabricaBancoIndisponivel>
{
    [Fact]
    public async Task Ready_retorna_503_unhealthy_sem_expor_detalhes_da_conexao()
    {
        using var cliente = api.Cliente();

        var resposta = await cliente.GetAsync("/health/ready");

        Assert.Equal(503, resposta.Codigo());
        var corpo = await resposta.Content.ReadAsStringAsync();
        var relatorio = JsonDocument.Parse(corpo).RootElement;
        Assert.Equal("Unhealthy", relatorio.GetProperty("status").GetString());
        Assert.DoesNotContain("Data Source", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("banco.db", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SqliteException", corpo);
    }

    [Fact]
    public async Task Live_continua_saudavel_quando_o_banco_cai()
    {
        using var cliente = api.Cliente();

        Assert.Equal(200, (await cliente.GetAsync("/health/live")).Codigo());
    }

    [Fact]
    public async Task Operacao_que_depende_do_banco_retorna_503_com_mensagem_generica()
    {
        using var cliente = api.Cliente();

        var resposta = await cliente.PostAsJsonAsync("/auth/login", new { email = "maria@teste.com", senha = "Senha@123" });

        Assert.Equal(503, resposta.Codigo());
        Assert.NotNull(resposta.Headers.RetryAfter);
        var erro = await resposta.ErroAsync();
        Assert.Equal("Banco de dados indisponível no momento. Tente novamente mais tarde.", erro.Message);
    }

    [Fact]
    public async Task Token_valido_com_banco_fora_do_ar_retorna_503_e_nao_401()
    {
        using var cliente = api.Cliente(Tokens.Criar(1));

        var resposta = await cliente.GetAsync("/usuarios/me");

        Assert.Equal(503, resposta.Codigo());
    }
}

public sealed class SaudeDegradadaTests
{
    [Fact]
    public async Task Pasta_de_fotos_sem_gravacao_deixa_a_api_degradada_mas_respondendo_200()
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"meubairro-arquivo-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(arquivo, "isto é um arquivo, não uma pasta");
        try
        {
            await using var api = new FabricaComConfiguracao(("Fotos:Pasta", Path.Combine(arquivo, "fotos")));
            using var cliente = api.Cliente();

            var resposta = await cliente.GetAsync("/health/ready");

            Assert.Equal(200, resposta.Codigo());
            var relatorio = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("Degraded", relatorio.GetProperty("status").GetString());
        }
        finally
        {
            File.Delete(arquivo);
        }
    }

    [Fact]
    public async Task Banco_lento_acima_do_limite_configurado_fica_degraded()
    {
        await using var api = new FabricaComConfiguracao(("Banco:LimiteLatenciaMs", "-1"));
        using var cliente = api.Cliente();

        var resposta = await cliente.GetAsync("/health/ready");

        Assert.Equal(200, resposta.Codigo());
        var corpo = await resposta.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Degraded\"", corpo);
        Assert.Contains("limite de -1 ms", corpo);
    }
}
