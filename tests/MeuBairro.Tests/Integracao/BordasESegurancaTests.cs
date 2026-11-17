using System.Net.Http.Json;
using MeuBairro.Api.Contratos;
using MeuBairro.Api.Dados;
using MeuBairro.Api.Dominio;
using MeuBairro.Api.Infra;
using MeuBairro.Tests.Infra;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeuBairro.Tests.Integracao;

public sealed class BordasTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("/ocorrencias/abc", "id", "abc")]
    [InlineData("/ocorrencias/99999999999999999999", "id", "99999999999999999999")]
    [InlineData("/ocorrencias/1.5/historico", "id", "1.5")]
    [InlineData("/categorias/x", "id", "x")]
    [InlineData("/enderecos/%20", "id", " ")]
    public async Task Id_que_nao_e_numero_inteiro_retorna_400_com_o_parametro(string rota, string parametro, string valor)
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();

        var resposta = await cliente.GetAsync(rota);

        Assert.Equal(400, resposta.Codigo());
        Assert.Equal($"Valor inválido para o parâmetro '{parametro}': {valor}", (await resposta.ErroAsync()).Message);
    }

    [Fact]
    public async Task Id_negativo_ou_zero_e_tratado_como_inexistente()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();

        Assert.Equal(404, (await cliente.GetAsync("/ocorrencias/-1")).Codigo());
        Assert.Equal(404, (await cliente.GetAsync("/ocorrencias/0/historico")).Codigo());
        Assert.Equal(404, (await cliente.GetAsync("/categorias/-7")).Codigo());
    }

    [Fact]
    public async Task Pagina_muito_alta_nao_estoura_e_devolve_pagina_vazia()
    {
        var cidadao = await api.NovoCidadaoAsync();
        await cidadao.CriarOcorrenciaAsync();
        using var cliente = cidadao.Cliente();

        var maxima = await cliente.GetAsync($"/ocorrencias/paginado?page={int.MaxValue}&size=50");
        var foraDoInt = await cliente.GetAsync("/ocorrencias/paginado?page=99999999999");

        Assert.Equal(200, maxima.Codigo());
        var pagina = await maxima.LerAsync<PaginaResponse<OcorrenciaResponse>>();
        Assert.Empty(pagina.Conteudo);
        Assert.True(pagina.TotalElementos >= 1);
        Assert.True(pagina.Ultima);
        Assert.Equal(400, foraDoInt.Codigo());
    }

    [Fact]
    public async Task Consultas_sem_resultado_retornam_estruturas_vazias_e_coerentes()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var bairro = Uri.EscapeDataString($"Bairro inexistente {Guid.NewGuid():N}");

        var pagina = await (await cliente.GetAsync($"/ocorrencias/paginado?bairro={bairro}")).LerAsync<PaginaResponse<OcorrenciaResponse>>();
        var painel = await (await cliente.GetAsync($"/ocorrencias/painel?bairro={bairro}")).LerAsync<PainelBairroResponse>();
        var criada = await cidadao.CriarOcorrenciaAsync();
        var historico = await (await cliente.GetAsync($"/ocorrencias/{criada.Id}/historico")).LerAsync<List<HistoricoResponse>>();
        var anexos = await (await cliente.GetAsync($"/ocorrencias/{criada.Id}/anexos")).LerAsync<List<AnexoResponse>>();

        Assert.Empty(pagina.Conteudo);
        Assert.Equal(0, pagina.TotalElementos);
        Assert.Equal(0, pagina.TotalPaginas);
        Assert.True(pagina.Ultima);
        Assert.Equal(0, painel.Total);
        Assert.Null(painel.TempoMedioResolucaoDias);
        Assert.Empty(painel.PorCategoria);
        Assert.Equal(["CRIACAO"], historico.Select(h => h.Tipo));
        Assert.Empty(anexos);
    }

    [Fact]
    public async Task Gestor_nao_edita_ocorrencia_do_cidadao()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();
        using var cliente = gestor.Cliente();

        Assert.Equal(403, (await cliente.PutAsync($"/ocorrencias/{criada.Id}", Dados.Ocorrencia(titulo: "Título trocado pelo gestor"))).Codigo());
    }

    [Fact]
    public async Task Location_aponta_para_o_recurso_criado_e_cadastro_de_gestor_nao_tem_location()
    {
        var gestor = await api.NovoGestorAsync();
        using var anonimo = api.Cliente();
        using var clienteGestor = gestor.Cliente();

        var cadastro = await anonimo.PostAsJsonAsync("/api/auth/register",
            new { nome = "Ana", email = $"ana.{Guid.NewGuid():N}@teste.com", senha = "Senha@123" });
        var novoGestor = await clienteGestor.PostAsJsonAsync("/usuarios/gestores",
            new { nome = "Bia", email = $"bia.{Guid.NewGuid():N}@teste.com", senha = "Gestor@123" });

        Assert.Equal("http://localhost/api/usuarios/me", cadastro.Headers.Location!.ToString());
        Assert.Equal(201, novoGestor.Codigo());
        Assert.Null(novoGestor.Headers.Location);
    }

    [Fact]
    public async Task Edicao_concorrente_com_mudanca_de_status_e_recusada_pelo_token_de_concorrencia()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();
        using var escopoDono = api.Services.CreateScope();
        using var escopoGestor = api.Services.CreateScope();
        var dono = escopoDono.ServiceProvider.GetRequiredService<AppDbContext>();
        var gestor = escopoGestor.ServiceProvider.GetRequiredService<AppDbContext>();

        // O dono carrega a ocorrência ABERTA; antes de ele salvar, outra operação a resolve.
        var carregada = await dono.Solicitacoes.SingleAsync(s => s.Id == criada.Id);
        await gestor.Solicitacoes.Where(s => s.Id == criada.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StatusSolicitacao.RESOLVIDA));
        carregada.Titulo = "Edição que chegou atrasada";

        var erro = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dono.SaveChangesAsync());

        Assert.Equal(409, Erros.Mapear(erro).Status);
        Assert.Equal(criada.Titulo, await api.NoBancoAsync(db => db.Solicitacoes.Where(s => s.Id == criada.Id).Select(s => s.Titulo).SingleAsync()));
    }
}

public sealed class SegurancaComplementarTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("Sênha€😀1!")]
    [InlineData("aspas\"barra\\<tag>&'")]
    [InlineData("      ")]
    public async Task Senhas_unicode_e_com_caracteres_especiais_funcionam_de_ponta_a_ponta(string senha)
    {
        var email = $"unicode.{Guid.NewGuid():N}@teste.com";
        using var cliente = api.Cliente();

        var cadastro = await cliente.PostAsJsonAsync("/auth/register", new { nome = "Unicode", email, senha });
        var certa = await cliente.PostAsJsonAsync("/auth/login", new { email, senha });
        var parecida = await cliente.PostAsJsonAsync("/auth/login", new { email, senha = senha + "x" });

        // Uma senha só com espaços é recusada pelo [Required]; as demais são aceitas exatamente como digitadas.
        if (string.IsNullOrWhiteSpace(senha))
        {
            Assert.Equal(400, cadastro.Codigo());
            return;
        }
        Assert.Equal(201, cadastro.Codigo());
        Assert.Equal(200, certa.Codigo());
        Assert.Equal(401, parecida.Codigo());
    }

    [Fact]
    public async Task Login_com_senha_acima_de_72_bytes_que_comeca_pela_senha_real_e_recusado()
    {
        // 36 caracteres e 72 bytes: o máximo aceito no cadastro. O BCrypt compara só esses 72 bytes.
        var senha = new string('ç', 36);
        var email = $"limite72.{Guid.NewGuid():N}@teste.com";
        using var cliente = api.Cliente();
        Assert.Equal(201, (await cliente.PostAsJsonAsync("/auth/register", new { nome = "Limite", email, senha })).Codigo());

        var exata = await cliente.PostAsJsonAsync("/auth/login", new { email, senha });
        var comSufixo = await cliente.PostAsJsonAsync("/auth/login", new { email, senha = senha + "qualquer-sufixo" });

        Assert.Equal(200, exata.Codigo());
        Assert.Equal(401, comSufixo.Codigo());
        Assert.Equal("E-mail ou senha inválidos", (await comSufixo.ErroAsync()).Message);
    }

    [Fact]
    public async Task Avaliacao_so_e_visivel_para_o_dono_e_para_gestores()
    {
        var dono = await api.NovoCidadaoAsync();
        var vizinho = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await dono.CriarOcorrenciaAsync();
        await gestor.ResolverAsync(criada.Id);
        using (var clienteDono = dono.Cliente())
            Assert.Equal(201, (await clienteDono.PostAsJsonAsync($"/ocorrencias/{criada.Id}/avaliacao",
                new { nota = 2, comentario = "Comentário pessoal do morador" })).Codigo());

        using var clienteVizinho = vizinho.Cliente();
        using var clienteGestor = gestor.Cliente();
        using var clienteDonoDepois = dono.Cliente();
        var doVizinho = await clienteVizinho.GetAsync($"/ocorrencias/{criada.Id}/avaliacao");

        Assert.Equal(403, doVizinho.Codigo());
        Assert.DoesNotContain("Comentário pessoal", await doVizinho.Content.ReadAsStringAsync());
        Assert.Equal(200, (await clienteGestor.GetAsync($"/ocorrencias/{criada.Id}/avaliacao")).Codigo());
        Assert.Equal(200, (await clienteDonoDepois.GetAsync($"/ocorrencias/{criada.Id}/avaliacao")).Codigo());
    }

    [Fact]
    public async Task Preflight_cors_libera_somente_as_origens_configuradas()
    {
        const string origem = "https://painel.meubairro.example";
        await using var comOrigem = new FabricaComConfiguracao(("Cors:Origens:0", origem));
        using var cliente = comOrigem.Cliente();
        using var semOrigens = api.Cliente();

        HttpRequestMessage Preflight(string origemDaRequisicao)
        {
            var requisicao = new HttpRequestMessage(HttpMethod.Options, "/api/ocorrencias");
            requisicao.Headers.Add("Origin", origemDaRequisicao);
            requisicao.Headers.Add("Access-Control-Request-Method", "POST");
            requisicao.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,idempotency-key");
            return requisicao;
        }

        var permitida = await cliente.SendAsync(Preflight(origem));
        var negada = await cliente.SendAsync(Preflight("http://site-malicioso.example"));
        var padrao = await semOrigens.SendAsync(Preflight(origem));

        Assert.Equal(origem, permitida.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("idempotency-key", string.Join(",", permitida.Headers.GetValues("Access-Control-Allow-Headers")),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(negada.Headers.Contains("Access-Control-Allow-Origin"));
        // Sem Cors:Origens configurado (padrão), nenhuma origem de navegador é liberada.
        Assert.False(padrao.Headers.Contains("Access-Control-Allow-Origin"));
    }
}

public sealed class DadosDemonstracaoTests
{
    private static (string, string)[] ComDemonstracao() =>
        [("DadosExemplo:Habilitado", "true"), ("DadosExemplo:Senha", "Teste@123")];

    [Fact]
    public async Task Contas_de_demonstracao_nao_sao_criadas_em_producao_mesmo_se_habilitadas()
    {
        await using var api = new FabricaEmAmbiente("Production", ComDemonstracao());
        using var cliente = api.Cliente();

        var login = await cliente.PostAsJsonAsync("/auth/login", new { email = "cidadao@meubairro.com", senha = "Teste@123" });

        Assert.Equal(401, login.Codigo());
        Assert.False(await api.NoBancoAsync(db => db.Usuarios.AnyAsync(u => u.Email == "cidadao@meubairro.com")));
        Assert.False(await api.NoBancoAsync(db => db.Funcionarios.AnyAsync()));
        Assert.Contains(api.Logs.Mensagens, m => m.Contains("só são criadas em Development"));
    }

    [Fact]
    public async Task Contas_de_demonstracao_existem_em_development()
    {
        await using var api = new FabricaEmAmbiente("Development", ComDemonstracao());
        using var cliente = api.Cliente();

        var cidadao = await cliente.PostAsJsonAsync("/auth/login", new { email = "cidadao@meubairro.com", senha = "Teste@123" });
        var gestor = await cliente.PostAsJsonAsync("/auth/login", new { email = "gestor@meubairro.com", senha = "Teste@123" });

        Assert.Equal(200, cidadao.Codigo());
        Assert.Equal(Perfil.GESTOR, (await gestor.LerAsync<LoginResponse>()).Usuario.Perfil);
        Assert.Equal(2, await api.NoBancoAsync(db => db.Solicitacoes.CountAsync()));
    }
}
