using System.Net.Http.Json;
using System.Text.Json;
using MeuBairro.Tests.Infra;

namespace MeuBairro.Tests.Integracao;

public sealed class LimiteConcorrenciaEProxyTests
{
    [Fact]
    public async Task Rajada_concorrente_libera_exatamente_o_limite_configurado()
    {
        await using var api = new FabricaComLimites(autenticacao: 5);
        using var cliente = api.Cliente();

        var respostas = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            cliente.PostAsJsonAsync("/auth/login", new { email = "rajada@teste.com", senha = "Errada@123" })));

        Assert.Equal(5, respostas.Count(r => r.Codigo() == 401));
        Assert.Equal(15, respostas.Count(r => r.Codigo() == 429));
        Assert.All(respostas.Where(r => r.Codigo() == 429), r => Assert.NotNull(r.Headers.RetryAfter));
    }

    private static async Task<int> LoginAsync(HttpClient cliente, string ipConexao, string? encaminhado)
    {
        var requisicao = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = JsonContent.Create(new { email = "proxy@teste.com", senha = "Errada@123" })
        };
        requisicao.Headers.Add(FabricaComProxy.CabecalhoIpConexao, ipConexao);
        if (encaminhado is not null)
            requisicao.Headers.Add("X-Forwarded-For", encaminhado);
        return (await cliente.SendAsync(requisicao)).Codigo();
    }

    [Fact]
    public async Task Atras_de_proxy_confiavel_cada_cliente_tem_a_propria_cota()
    {
        await using var api = new FabricaComProxy(autenticacao: 2);
        using var cliente = api.Cliente();
        const string proxy = FabricaComProxy.IpDoProxy;

        var clienteA = new[]
        {
            await LoginAsync(cliente, proxy, "203.0.113.10"),
            await LoginAsync(cliente, proxy, "203.0.113.10"),
            await LoginAsync(cliente, proxy, "203.0.113.10")
        };
        var clienteB = await LoginAsync(cliente, proxy, "203.0.113.20");

        Assert.Equal([401, 401, 429], clienteA);
        Assert.Equal(401, clienteB);
    }

    [Fact]
    public async Task Conexao_que_nao_vem_do_proxy_nao_troca_de_ip_pelo_cabecalho()
    {
        await using var api = new FabricaComProxy(autenticacao: 2);
        using var cliente = api.Cliente();
        const string atacante = "198.51.100.7";

        var codigos = new[]
        {
            await LoginAsync(cliente, atacante, "203.0.113.1"),
            await LoginAsync(cliente, atacante, "203.0.113.2"),
            await LoginAsync(cliente, atacante, "203.0.113.3")
        };

        Assert.Equal([401, 401, 429], codigos);
    }
}

public sealed class SaudeDependenciasTests
{
    private static async Task<JsonElement> RelatorioAsync(HttpResponseMessage resposta) =>
        JsonDocument.Parse(await resposta.Content.ReadAsStringAsync()).RootElement;

    private static string Status(JsonElement relatorio, string verificacao) =>
        relatorio.GetProperty("verificacoes").EnumerateArray()
            .Single(v => v.GetProperty("nome").GetString() == verificacao).GetProperty("status").GetString()!;

    [Fact]
    public async Task Armazenamento_de_idempotencia_fora_do_ar_degrada_sem_parar_a_api()
    {
        await using var api = new FabricaControleIndisponivel();
        using var anonimo = api.Cliente();

        var saude = await anonimo.GetAsync("/health/ready");

        Assert.Equal(200, saude.Codigo());
        var relatorio = await RelatorioAsync(saude);
        Assert.Equal("Degraded", relatorio.GetProperty("status").GetString());
        Assert.Equal("Healthy", Status(relatorio, "banco"));
        Assert.Equal("Degraded", Status(relatorio, "idempotencia"));

        // Sem a chave a operação segue normalmente; com a chave a API recusa em vez de arriscar duplicar.
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        Assert.Equal(201, (await cliente.PostAsync("/ocorrencias", Dados.Ocorrencia())).Codigo());
        var comChave = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia()).ComChave(Guid.NewGuid().ToString()));
        Assert.Equal(503, comChave.Codigo());
        Assert.Equal(1, await api.ContarSolicitacoesAsync(cidadao.Id));
    }

    [Fact]
    public async Task Oracle_configurado_mas_inacessivel_responde_503_sem_derrubar_a_api()
    {
        // Endereço local sem nenhum serviço: nenhuma conexão sai da máquina.
        await using var api = new FabricaOracle("User Id=teste;Password=teste;Data Source=127.0.0.1:1/NAOEXISTE;Connection Timeout=3");
        using var cliente = api.Cliente();

        var vivo = await cliente.GetAsync("/health/live");
        var pronto = await cliente.GetAsync("/health/ready");
        var login = await cliente.PostAsJsonAsync("/auth/login", new { email = "maria@teste.com", senha = "Senha@123" });

        Assert.Equal(200, vivo.Codigo());
        Assert.Equal(503, pronto.Codigo());
        var relatorio = await RelatorioAsync(pronto);
        Assert.Equal("Unhealthy", Status(relatorio, "banco"));
        Assert.Contains("Oracle", relatorio.ToString());
        Assert.DoesNotContain("Password", relatorio.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(503, login.Codigo());
    }

    [Fact]
    public async Task Oracle_sem_string_de_conexao_impede_a_api_de_iniciar()
    {
        await using var api = new FabricaOracle(null);

        var erro = Assert.ThrowsAny<Exception>(() => api.CreateClient());

        Assert.Contains("ConnectionStrings:Oracle", erro.ToString());
    }
}
