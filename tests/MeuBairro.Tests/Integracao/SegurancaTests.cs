using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MeuBairro.Api.Contratos;
using MeuBairro.Tests.Infra;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeuBairro.Tests.Integracao;

public sealed class SegurancaTests(FabricaComFalhas api) : IClassFixture<FabricaComFalhas>
{
    [Theory]
    [InlineData("bairro=' OR '1'='1")]
    [InlineData("bairro=x'; DROP TABLE SOLICITACOES; --")]
    [InlineData("bairro=%25")]
    public async Task Filtros_com_sql_injection_sao_tratados_como_texto(string filtro)
    {
        var cidadao = await api.NovoCidadaoAsync();
        await cidadao.CriarOcorrenciaAsync();
        using var cliente = cidadao.Cliente();

        var resposta = await cliente.GetAsync($"/ocorrencias/paginado?{filtro}");

        Assert.Equal(200, resposta.Codigo());
        Assert.Equal(0, (await resposta.LerAsync<PaginaResponse<OcorrenciaResponse>>()).TotalElementos);
        Assert.True(await api.NoBancoAsync(db => db.Solicitacoes.AnyAsync()));
    }

    [Theory]
    [InlineData("categoria=' OR 1=1 --")]
    [InlineData("status=ABERTA' OR '1'='1")]
    public async Task Filtros_enumerados_com_injecao_retornam_400(string filtro)
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();

        Assert.Equal(400, (await cliente.GetAsync($"/ocorrencias/paginado?{filtro}")).Codigo());
    }

    [Fact]
    public async Task Login_com_sql_injection_no_email_nao_autentica()
    {
        await api.NovoCidadaoAsync();
        using var cliente = api.Cliente();

        var invalido = await cliente.PostAsJsonAsync("/auth/login", new { email = "' OR '1'='1", senha = "' OR '1'='1" });
        var comAspas = await cliente.PostAsJsonAsync("/auth/login", new { email = "admin@teste.com' --", senha = "x" });

        Assert.Equal(400, invalido.Codigo());
        Assert.Equal(400, comAspas.Codigo());
    }

    [Fact]
    public async Task Texto_malicioso_e_gravado_literalmente_sem_executar()
    {
        var cidadao = await api.NovoCidadaoAsync();
        const string titulo = "Robert'); DROP TABLE SOLICITACOES;--";

        var criada = await cidadao.CriarOcorrenciaAsync(titulo);

        Assert.Equal(titulo, criada.Titulo);
        Assert.Equal(titulo, await api.NoBancoAsync(db => db.Solicitacoes.Where(s => s.Id == criada.Id).Select(s => s.Titulo).SingleAsync()));
    }

    [Fact]
    public async Task Nenhuma_resposta_expoe_senha_ou_hash()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        await cidadao.CriarOcorrenciaAsync();
        using var clienteCidadao = cidadao.Cliente();
        using var clienteGestor = gestor.Cliente();

        var corpos = new List<string>
        {
            await (await clienteCidadao.GetAsync("/usuarios/me")).Content.ReadAsStringAsync(),
            await (await clienteGestor.GetAsync("/usuarios/me")).Content.ReadAsStringAsync(),
            await (await clienteCidadao.GetAsync("/ocorrencias")).Content.ReadAsStringAsync(),
            await (await clienteGestor.PostAsJsonAsync("/usuarios/gestores",
                new { nome = "Outro", email = $"g.{Guid.NewGuid():N}@teste.com", senha = "Gestor@123" })).Content.ReadAsStringAsync(),
            await (await api.Cliente().PostAsJsonAsync("/auth/login", new { email = cidadao.Email, senha = ApiFactory.SenhaPadrao }))
                .Content.ReadAsStringAsync()
        };

        Assert.All(corpos, corpo =>
        {
            Assert.DoesNotContain("senha", corpo, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"$2", corpo);
        });
    }

    [Fact]
    public async Task Logs_nao_registram_senhas_tokens_nem_o_segredo_jwt()
    {
        const string senha = "SenhaMuitoSecreta#2026";
        var email = $"logs.{Guid.NewGuid():N}@teste.com";
        using var cliente = api.Cliente();
        await cliente.PostAsJsonAsync("/auth/register", new { nome = "Logs", email, senha });
        var conta = await api.LoginAsync(email, senha);
        await cliente.PostAsJsonAsync("/auth/login", new { email, senha = senha + "-errada" });
        using var autenticado = conta.Cliente();
        await autenticado.GetAsync("/usuarios/me");
        using var tokenRuim = api.Cliente(conta.Token + "x");
        await tokenRuim.GetAsync("/usuarios/me");

        var logs = api.Logs.Mensagens;

        Assert.NotEmpty(logs);
        Assert.DoesNotContain(logs, l => l.Contains(senha, StringComparison.Ordinal));
        Assert.DoesNotContain(logs, l => l.Contains(conta.Token, StringComparison.Ordinal));
        Assert.DoesNotContain(logs, l => l.Contains(conta.Token.Split('.')[2], StringComparison.Ordinal));
        Assert.DoesNotContain(logs, l => l.Contains(ApiFactory.SegredoJwt, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Excecao_inesperada_retorna_500_generico_sem_detalhes_internos()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();

        api.Falhas.ExcecaoInesperadaEmCategorias = true;
        HttpResponseMessage resposta;
        try
        {
            resposta = await cliente.GetAsync("/categorias");
        }
        finally
        {
            api.Falhas.ExcecaoInesperadaEmCategorias = false;
        }

        Assert.Equal(500, resposta.Codigo());
        var corpo = await resposta.Content.ReadAsStringAsync();
        Assert.DoesNotContain("detalhe-interno", corpo);
        Assert.DoesNotContain("InvalidOperationException", corpo);
        Assert.DoesNotContain(" at ", corpo);
        Assert.Equal("Erro interno no servidor. Tente novamente mais tarde.", (await resposta.ErroAsync()).Message);
    }

    [Fact]
    public async Task Falha_ao_gravar_anexo_desfaz_ocorrencia_endereco_e_remove_a_foto()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var enderecosAntes = await api.NoBancoAsync(db => db.Enderecos.CountAsync());
        var fotosAntes = Directory.Exists(api.PastaFotos) ? Directory.GetFiles(api.PastaFotos).Length : 0;

        api.Falhas.InsercaoComFalha = "ANEXOS";
        HttpResponseMessage resposta;
        try
        {
            resposta = await cliente.PostAsync("/ocorrencias", Dados.Ocorrencia(foto: Dados.PngValido));
        }
        finally
        {
            api.Falhas.InsercaoComFalha = null;
        }

        Assert.Equal(503, resposta.Codigo());
        Assert.Equal(0, await api.ContarSolicitacoesAsync(cidadao.Id));
        Assert.Equal(enderecosAntes, await api.NoBancoAsync(db => db.Enderecos.CountAsync()));
        Assert.Equal(fotosAntes, Directory.Exists(api.PastaFotos) ? Directory.GetFiles(api.PastaFotos).Length : 0);
    }

    [Fact]
    public async Task Json_malformado_ou_ausente_retorna_400_padronizado()
    {
        using var cliente = api.Cliente();

        var malformado = await cliente.PostAsync("/auth/login", new StringContent("{\"email\": ", Encoding.UTF8, "application/json"));
        var vazio = await cliente.PostAsync("/auth/login", new StringContent("", Encoding.UTF8, "application/json"));
        var tipoErrado = await cliente.PostAsync("/auth/login", new StringContent("{\"email\":123}", Encoding.UTF8, "application/json"));

        Assert.Equal(400, malformado.Codigo());
        Assert.Equal("O corpo da requisição está ausente ou não é um JSON válido", (await malformado.ErroAsync()).Message);
        Assert.Equal(400, vazio.Codigo());
        Assert.Equal(400, tipoErrado.Codigo());
    }

    [Fact]
    public async Task Ocorrencia_enviada_como_json_retorna_415()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();

        var resposta = await cliente.PostAsJsonAsync("/ocorrencias", new { titulo = "Buraco enorme", descricao = "Descrição válida", categoria = "BURACO" });

        Assert.Equal(415, resposta.Codigo());
    }

    [Fact]
    public async Task Rota_inexistente_e_metodo_nao_suportado_retornam_json()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();

        var inexistente = await cliente.GetAsync("/rota/que/nao/existe");
        var metodo = await cliente.PatchAsync("/categorias", JsonContent.Create(new { }));

        Assert.Equal(404, inexistente.Codigo());
        Assert.Equal("Recurso não encontrado", (await inexistente.ErroAsync()).Message);
        Assert.Equal(405, metodo.Codigo());
        Assert.Equal("Método HTTP não suportado nesta rota", (await metodo.ErroAsync()).Message);
    }

    [Fact]
    public async Task Foto_valida_fica_publica_com_o_tipo_detectado_pelo_conteudo()
    {
        var cidadao = await api.NovoCidadaoAsync();
        // O cliente diz que é PNG, mas o conteúdo é JPEG: vale o conteúdo.
        var criada = await cidadao.CriarOcorrenciaAsync(foto: Dados.JpegValido);
        using var anonimo = api.Cliente();

        var foto = await anonimo.GetAsync(new Uri(criada.FotoUrl!).PathAndQuery);

        Assert.Equal(200, foto.Codigo());
        Assert.Equal("nosniff", foto.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("image/jpeg", foto.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Dados.JpegValido, await foto.Content.ReadAsByteArrayAsync());
        Assert.Matches(@"/fotos/[0-9a-f]{32}\.jpg$", criada.FotoUrl);
    }

    [Fact]
    public async Task Arquivo_que_nao_e_imagem_e_recusado_sem_criar_registro()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();

        var resposta = await cliente.PostAsync("/ocorrencias",
            Dados.Ocorrencia(foto: Encoding.UTF8.GetBytes("<?php system($_GET['c']); ?>"), nomeFoto: "foto.png"));

        Assert.Equal(400, resposta.Codigo());
        Assert.StartsWith("Formato de foto inválido", (await resposta.ErroAsync()).Message);
        Assert.Equal(0, await api.ContarSolicitacoesAsync(cidadao.Id));
    }

    [Fact]
    public async Task Foto_acima_de_5_mb_retorna_413()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var grande = new byte[5 * 1024 * 1024 + 1];
        Dados.PngValido.CopyTo(grande, 0);
        var enorme = new byte[8 * 1024 * 1024];
        Dados.PngValido.CopyTo(enorme, 0);

        var acima = await cliente.PostAsync("/ocorrencias", Dados.Ocorrencia(foto: grande));
        var muitoAcima = await cliente.PostAsync("/ocorrencias", Dados.Ocorrencia(foto: enorme));

        await acima.AssertStatusAsync(413);
        await muitoAcima.AssertStatusAsync(413);
        Assert.Equal(0, await api.ContarSolicitacoesAsync(cidadao.Id));
    }

    [Theory]
    [InlineData("/fotos/..%2F..%2Fappsettings.json")]
    [InlineData("/fotos/..%5C..%5Cappsettings.json")]
    [InlineData("/fotos/appsettings.json")]
    [InlineData("/fotos/%2e%2e%2fmeubairro.db")]
    [InlineData("/fotos/0123456789abcdef0123456789abcdef.exe")]
    public async Task Path_traversal_na_rota_de_fotos_retorna_404(string rota)
    {
        using var cliente = api.Cliente();

        Assert.Equal(404, (await cliente.GetAsync(rota)).Codigo());
    }

    [Fact]
    public async Task Nome_de_arquivo_com_path_traversal_e_saneado_no_anexo()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();
        using var cliente = cidadao.Cliente();
        var arquivo = new ByteArrayContent(Dados.PngValido);
        arquivo.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var form = new MultipartFormDataContent { { arquivo, "arquivo", "../../../../Windows/System32/evil.png" } };

        var resposta = await cliente.PostAsync($"/ocorrencias/{criada.Id}/anexos", form);

        Assert.Equal(201, resposta.Codigo());
        var anexo = await resposta.LerAsync<AnexoResponse>();
        Assert.Equal("evil.png", anexo.Nome);
        Assert.Matches(@"/fotos/[0-9a-f]{32}\.png$", anexo.Url);
        Assert.True(File.Exists(Path.Combine(api.PastaFotos, anexo.Url.Split('/').Last())));
    }

    [Fact]
    public async Task Outro_cidadao_nao_envia_anexo_e_limite_de_anexos_e_respeitado()
    {
        var dono = await api.NovoCidadaoAsync();
        var outro = await api.NovoCidadaoAsync();
        var criada = await dono.CriarOcorrenciaAsync(foto: Dados.PngValido);
        using var clienteDono = dono.Cliente();
        using var clienteOutro = outro.Cliente();
        MultipartFormDataContent Anexo() => new() { { new ByteArrayContent(Dados.PngValido), "arquivo", "a.png" } };

        Assert.Equal(403, (await clienteOutro.PostAsync($"/ocorrencias/{criada.Id}/anexos", Anexo())).Codigo());
        for (var i = 0; i < 4; i++)
            Assert.Equal(201, (await clienteDono.PostAsync($"/ocorrencias/{criada.Id}/anexos", Anexo())).Codigo());
        Assert.Equal(409, (await clienteDono.PostAsync($"/ocorrencias/{criada.Id}/anexos", Anexo())).Codigo());
        Assert.Equal(400, (await clienteDono.PostAsync($"/ocorrencias/{criada.Id}/anexos", new MultipartFormDataContent { { new StringContent("x"), "outro" } })).Codigo());

        var anexos = await (await clienteOutro.GetAsync($"/ocorrencias/{criada.Id}/anexos")).LerAsync<List<AnexoResponse>>();
        Assert.Equal(5, anexos.Count);
        Assert.Equal(criada.FotoUrl, anexos[0].Url);
    }

    [Fact]
    public async Task Toda_rota_exige_autenticacao_exceto_as_publicas_declaradas()
    {
        var publicas = new HashSet<string>
        {
            "/auth/register", "/auth/login", "/fotos/{arquivo}", "/health", "/health/live", "/health/ready",
            "/openapi/{documentName}.json", "/"
        };
        var endpoints = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        Assert.NotEmpty(endpoints);
        foreach (var endpoint in endpoints)
        {
            var rota = "/" + endpoint.RoutePattern.RawText!.TrimStart('/');
            var anonima = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            if (anonima)
                Assert.Contains(rota, publicas);
            else
                Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        }
    }

    [Fact]
    public async Task Rotas_de_gestor_recusam_cidadao_com_403()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();
        using var cliente = cidadao.Cliente();

        var tentativas = new[]
        {
            await cliente.PatchAsJsonAsync($"/ocorrencias/{criada.Id}/status", new { status = "RESOLVIDA" }),
            await cliente.PatchAsJsonAsync($"/ocorrencias/{criada.Id}/prioridade", new { prioridade = "ALTA" }),
            await cliente.PostAsJsonAsync($"/ocorrencias/{criada.Id}/andamentos", new { descricao = "x" }),
            await cliente.GetAsync("/ocorrencias/resumo"),
            await cliente.PostAsJsonAsync("/categorias", new { nome = "Nova", prazoDias = 3 }),
            await cliente.PostAsJsonAsync("/usuarios/gestores", new { nome = "Eu", email = "eu@teste.com", senha = "Senha@123" })
        };

        Assert.All(tentativas, r => Assert.Equal(403, r.Codigo()));
        using var json = JsonDocument.Parse(await tentativas[0].Content.ReadAsStringAsync());
        Assert.Equal(403, json.RootElement.GetProperty("status").GetInt32());
    }
}

public sealed class OpenApiTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Documento_openapi_descreve_rotas_seguranca_e_idempotencia()
    {
        using var cliente = api.Cliente();

        var resposta = await cliente.GetAsync("/openapi/v1.json");

        Assert.Equal(200, resposta.Codigo());
        using var documento = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        var raiz = documento.RootElement;
        var rotas = raiz.GetProperty("paths");
        foreach (var rota in new[] { "/auth/login", "/auth/register", "/ocorrencias", "/ocorrencias/{id}/status", "/ocorrencias/{id}/historico", "/categorias" })
            Assert.True(rotas.TryGetProperty(rota, out _), $"rota {rota} ausente");

        Assert.Equal("bearer", raiz.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer").GetProperty("scheme").GetString());
        var login = rotas.GetProperty("/auth/login").GetProperty("post");
        Assert.False(login.TryGetProperty("security", out _));
        var criar = rotas.GetProperty("/ocorrencias").GetProperty("post");
        Assert.True(criar.TryGetProperty("security", out _));
        Assert.Contains(criar.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.True(criar.GetProperty("requestBody").GetProperty("content").TryGetProperty("multipart/form-data", out _));
    }

    [Fact]
    public async Task Swagger_ui_carrega_os_arquivos_e_aponta_para_o_documento_openapi()
    {
        using var cliente = api.Cliente();

        var pagina = await cliente.GetAsync("/swagger/index.html");
        var script = await cliente.GetAsync("/swagger/swagger-ui-bundle.js");
        var configuracao = await cliente.GetStringAsync("/swagger/index.js");
        var pelaApi = await cliente.GetAsync("/api/swagger/index.html");

        Assert.Equal(200, pagina.Codigo());
        Assert.Contains("swagger-ui", await pagina.Content.ReadAsStringAsync());
        Assert.Equal(200, script.Codigo());
        Assert.True((await script.Content.ReadAsByteArrayAsync()).Length > 100_000);
        Assert.Contains("/openapi/v1.json", configuracao);
        Assert.Equal(200, pelaApi.Codigo());
    }

    [Fact]
    public async Task Contrato_openapi_tem_parametros_dtos_multipart_e_respostas_possiveis()
    {
        using var cliente = api.Cliente();
        using var documento = JsonDocument.Parse(await cliente.GetStringAsync("/openapi/v1.json"));
        var raiz = documento.RootElement;
        var rotas = raiz.GetProperty("paths");
        var esquemas = raiz.GetProperty("components").GetProperty("schemas");

        // Toda rota mapeada aparece no documento.
        var mapeadas = new[]
        {
            "/auth/register", "/auth/login", "/usuarios/me", "/usuarios/me/endereco", "/usuarios/gestores", "/enderecos/{id}",
            "/categorias", "/categorias/{id}", "/ocorrencias", "/ocorrencias/paginado", "/ocorrencias/resumo", "/ocorrencias/painel",
            "/ocorrencias/{id}", "/ocorrencias/{id}/status", "/ocorrencias/{id}/prioridade", "/ocorrencias/{id}/historico",
            "/ocorrencias/{id}/andamentos", "/ocorrencias/{id}/anexos", "/ocorrencias/{id}/avaliacao", "/fotos/{arquivo}",
            "/health", "/health/live", "/health/ready"
        };
        Assert.All(mapeadas, r => Assert.True(rotas.TryGetProperty(r, out _), $"rota {r} ausente"));

        var paginado = rotas.GetProperty("/ocorrencias/paginado").GetProperty("get");
        var parametros = paginado.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()!.ToLowerInvariant()).ToList();
        Assert.Superset(new HashSet<string> { "status", "categoria", "prioridade", "bairro", "minhas", "page", "size", "sort" }, parametros.ToHashSet());

        var detalhe = rotas.GetProperty("/ocorrencias/{id}").GetProperty("get").GetProperty("responses");
        Assert.Contains("OcorrenciaResponse", detalhe.GetProperty("200").GetRawText());
        Assert.True(esquemas.TryGetProperty("OcorrenciaResponse", out var ocorrencia));
        foreach (var campo in new[] { "id", "titulo", "categoria", "status", "endereco", "latitude", "fotoUrl", "criadoEm", "usuarioId", "prazoEstimado" })
            Assert.True(ocorrencia.GetProperty("properties").TryGetProperty(campo, out _), $"campo {campo} ausente");
        Assert.True(esquemas.TryGetProperty("ErroResposta", out _));

        foreach (var codigo in new[] { "400", "429", "503" })
            Assert.True(detalhe.TryGetProperty(codigo, out _), $"resposta {codigo} ausente em GET /ocorrencias/{{id}}");
        Assert.False(rotas.GetProperty("/health/ready").GetProperty("get").GetProperty("responses").TryGetProperty("429", out _));

        // O corpo multipart referencia o componente do formulário, que descreve a foto como arquivo binário.
        JsonElement Formulario(string rota)
        {
            var referencia = rotas.GetProperty(rota).GetProperty("post").GetProperty("requestBody").GetProperty("content")
                .GetProperty("multipart/form-data").GetProperty("schema").GetProperty("$ref").GetString()!;
            return esquemas.GetProperty(referencia.Split('/').Last()).GetProperty("properties");
        }
        Assert.Equal("binary", esquemas.GetProperty("IFormFile").GetProperty("format").GetString());
        Assert.Contains("#/components/schemas/IFormFile", Formulario("/ocorrencias").GetProperty("foto").GetRawText());
        foreach (var campo in new[] { "titulo", "descricao", "categoria", "endereco", "latitude", "longitude", "enderecoId", "bairro", "cep" })
            Assert.True(Formulario("/ocorrencias").TryGetProperty(campo, out _), $"campo {campo} ausente no formulário");
        Assert.Contains("#/components/schemas/IFormFile", Formulario("/ocorrencias/{id}/anexos").GetProperty("arquivo").GetRawText());

        var criacao = rotas.GetProperty("/ocorrencias").GetProperty("post");
        Assert.True(criacao.GetProperty("responses").TryGetProperty("201", out _));
        Assert.True(criacao.GetProperty("responses").TryGetProperty("413", out _));
        Assert.True(rotas.GetProperty("/ocorrencias/{id}").GetProperty("delete").GetProperty("responses").TryGetProperty("204", out _));
    }
}
