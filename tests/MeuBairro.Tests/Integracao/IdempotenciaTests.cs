using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using MeuBairro.Api.Contratos;
using MeuBairro.Api.Dados;
using MeuBairro.Tests.Infra;
using MeuBairro.Api.Infra.Idempotencia;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeuBairro.Tests.Integracao;

public sealed class IdempotenciaTests(FabricaComFalhas api) : IClassFixture<FabricaComFalhas>
{
    private static string Chave() => Guid.NewGuid().ToString();

    [Fact]
    public async Task Mesma_chave_e_mesmo_conteudo_devolvem_a_resposta_original_sem_duplicar()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var chave = Chave();
        var titulo = Dados.TituloUnico();

        var fotosAntes = api.ContarFotos();

        // Cada MultipartFormDataContent gera um boundary diferente, como acontece numa nova tentativa do cliente.
        var primeira = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo, foto: Dados.PngValido)).ComChave(chave));
        var repetida = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo, foto: Dados.PngValido)).ComChave(chave));

        Assert.Equal(201, primeira.Codigo());
        Assert.Equal(201, repetida.Codigo());
        Assert.Equal("false", primeira.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal("true", repetida.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(primeira.Headers.Location, repetida.Headers.Location);
        var a = await primeira.LerAsync<OcorrenciaResponse>();
        var b = await repetida.LerAsync<OcorrenciaResponse>();
        Assert.Equal(a, b);
        Assert.NotNull(a.FotoUrl);
        Assert.Equal(1, await api.ContarSolicitacoesAsync(cidadao.Id));
        Assert.Equal(fotosAntes + 1, api.ContarFotos());
        Assert.Equal(1, await api.NoBancoAsync(db => db.Anexos.CountAsync(x => x.SolicitacaoId == a.Id)));
    }

    [Fact]
    public async Task Chaves_diferentes_sao_operacoes_diferentes()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var titulo = Dados.TituloUnico();

        var primeira = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo)).ComChave(Chave()));
        // Mesmo conteúdo com outra chave é uma nova operação: cai na regra de ocorrência duplicada.
        var mesmoConteudo = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo)).ComChave(Chave()));
        var outroConteudo = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia()).ComChave(Chave()));

        Assert.Equal(201, primeira.Codigo());
        Assert.Equal(409, mesmoConteudo.Codigo());
        Assert.Equal(201, outroConteudo.Codigo());
        Assert.Equal(2, await api.ContarSolicitacoesAsync(cidadao.Id));
    }

    [Fact]
    public async Task Falha_ao_registrar_o_resultado_da_chave_desfaz_a_operacao_inteira()
    {
        Assert.True(api.Services.GetRequiredService<IdempotenciaService>().ConclusaoAtomica);
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var chave = Chave();
        var titulo = Dados.TituloUnico();
        var fotosAntes = api.ContarFotos();
        var enderecosAntes = await api.NoBancoAsync(db => db.Enderecos.CountAsync());

        // A ocorrência é gravada com sucesso, mas a gravação do resultado da chave falha logo depois.
        api.Falhas.FalharConclusaoIdempotencia = true;
        HttpResponseMessage falha;
        try
        {
            falha = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo, foto: Dados.PngValido)).ComChave(chave));
        }
        finally
        {
            api.Falhas.FalharConclusaoIdempotencia = false;
        }

        Assert.Equal(503, falha.Codigo());
        Assert.Null(falha.Headers.Location);
        Assert.Equal(0, await api.ContarSolicitacoesAsync(cidadao.Id));
        Assert.Equal(enderecosAntes, await api.NoBancoAsync(db => db.Enderecos.CountAsync()));
        Assert.Equal(fotosAntes, api.ContarFotos());
        Assert.Equal(0, await ContarChavesAsync(chave));

        var novaTentativa = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo, foto: Dados.PngValido)).ComChave(chave));
        Assert.Equal(201, novaTentativa.Codigo());
        Assert.Equal(1, await api.ContarSolicitacoesAsync(cidadao.Id));
        Assert.Equal(fotosAntes + 1, api.ContarFotos());
    }

    [Fact]
    public async Task Mesma_chave_com_conteudo_diferente_retorna_409_sem_criar_registro()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var chave = Chave();

        var primeira = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia()).ComChave(chave));
        var divergente = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia()).ComChave(chave));

        Assert.Equal(201, primeira.Codigo());
        Assert.Equal(409, divergente.Codigo());
        Assert.StartsWith("Esta Idempotency-Key já foi usada com outro conteúdo", (await divergente.ErroAsync()).Message);
        Assert.Equal(1, await api.ContarSolicitacoesAsync(cidadao.Id));
    }

    [Fact]
    public async Task Requisicoes_concorrentes_com_a_mesma_chave_criam_um_unico_registro()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var chave = Chave();
        var titulo = Dados.TituloUnico();

        var respostas = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo)).ComChave(chave))));

        Assert.All(respostas, r => Assert.Contains(r.Codigo(), new[] { 201, 409 }));
        var criadas = respostas.Where(r => r.Codigo() == 201).ToList();
        Assert.NotEmpty(criadas);
        var ids = await Task.WhenAll(criadas.Select(async r => (await r.LerAsync<OcorrenciaResponse>()).Id));
        Assert.Single(ids.Distinct());
        foreach (var emAndamento in respostas.Where(r => r.Codigo() == 409))
            Assert.NotNull(emAndamento.Headers.RetryAfter);
        Assert.Equal(1, await api.ContarSolicitacoesAsync(cidadao.Id));
    }

    [Fact]
    public async Task A_chave_e_isolada_por_usuario()
    {
        var maria = await api.NovoCidadaoAsync();
        var joao = await api.NovoCidadaoAsync();
        var chave = Chave();
        var titulo = Dados.TituloUnico();
        using var clienteMaria = maria.Cliente();
        using var clienteJoao = joao.Cliente();

        var daMaria = await (await clienteMaria.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo)).ComChave(chave)))
            .LerAsync<OcorrenciaResponse>();
        var doJoao = await (await clienteJoao.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo)).ComChave(chave)))
            .LerAsync<OcorrenciaResponse>();

        Assert.NotEqual(daMaria.Id, doJoao.Id);
        Assert.Equal(joao.Id, doJoao.UsuarioId);
    }

    [Fact]
    public async Task A_chave_e_isolada_por_operacao()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var chave = Chave();
        var criada = await (await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia()).ComChave(chave)))
            .LerAsync<OcorrenciaResponse>();

        var anexo = new MultipartFormDataContent { { new ByteArrayContent(Dados.JpegValido), "arquivo", "extra.jpg" } };
        var resposta = await cliente.SendAsync(Dados.Post($"/ocorrencias/{criada.Id}/anexos", anexo).ComChave(chave));

        Assert.Equal(201, resposta.Codigo());
        Assert.Equal("false", resposta.Headers.GetValues("Idempotent-Replayed").Single());
    }

    [Fact]
    public async Task Falha_durante_o_processamento_libera_a_chave_e_nao_deixa_registro_parcial()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var chave = Chave();
        var titulo = Dados.TituloUnico();

        api.Falhas.InsercaoComFalha = "SOLICITACOES";
        HttpResponseMessage falha;
        try
        {
            falha = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo)).ComChave(chave));
        }
        finally
        {
            api.Falhas.InsercaoComFalha = null;
        }

        Assert.Equal(503, falha.Codigo());
        Assert.Equal(0, await api.ContarSolicitacoesAsync(cidadao.Id));
        Assert.Equal(0, await ContarChavesAsync(chave));

        var novaTentativa = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo)).ComChave(chave));
        Assert.Equal(201, novaTentativa.Codigo());
        Assert.Equal("false", novaTentativa.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(1, await api.ContarSolicitacoesAsync(cidadao.Id));
    }

    [Fact]
    public async Task Erro_de_validacao_nao_fica_guardado_na_chave()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var chave = Chave();

        var invalida = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo: "abc")).ComChave(chave));
        var corrigida = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia()).ComChave(chave));

        Assert.Equal(400, invalida.Codigo());
        Assert.Equal(201, corrigida.Codigo());
    }

    [Fact]
    public async Task Repeticao_idempotente_de_avaliacao_nao_vira_conflito()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();
        await gestor.ResolverAsync(criada.Id);
        using var cliente = cidadao.Cliente();
        var chave = Chave();

        HttpRequestMessage Avaliar() =>
            Dados.Post($"/ocorrencias/{criada.Id}/avaliacao", JsonContent.Create(new { nota = 5 })).ComChave(chave);
        var primeira = await cliente.SendAsync(Avaliar());
        var repetida = await cliente.SendAsync(Avaliar());
        var semChave = await cliente.PostAsJsonAsync($"/ocorrencias/{criada.Id}/avaliacao", new { nota = 5 });

        Assert.Equal(201, primeira.Codigo());
        Assert.Equal(201, repetida.Codigo());
        Assert.Equal(409, semChave.Codigo());
        Assert.Equal(1, await api.NoBancoAsync(db => db.Avaliacoes.CountAsync(a => a.SolicitacaoId == criada.Id)));
    }

    [Fact]
    public async Task Criacao_de_categoria_em_json_tambem_e_idempotente()
    {
        var gestor = await api.NovoGestorAsync();
        using var cliente = gestor.Cliente();
        var chave = Chave();
        var nome = $"Poda {Guid.NewGuid().ToString("N")[..6]}";

        HttpRequestMessage Criar() => Dados.Post("/categorias", JsonContent.Create(new { nome, prazoDias = 9 })).ComChave(chave);
        var a = await (await cliente.SendAsync(Criar())).LerAsync<CategoriaResponse>();
        var b = await (await cliente.SendAsync(Criar())).LerAsync<CategoriaResponse>();

        Assert.Equal(a.Id, b.Id);
        Assert.Equal(1, await api.NoBancoAsync(db => db.Categorias.CountAsync(c => c.Nome == nome)));
    }

    [Fact]
    public async Task Falha_ao_abrir_a_transacao_libera_a_chave_para_nova_tentativa()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var chave = Chave();
        var titulo = Dados.TituloUnico();

        api.FalhaTransacao.Ativa = true;
        HttpResponseMessage falha;
        try
        {
            falha = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo)).ComChave(chave));
        }
        finally
        {
            api.FalhaTransacao.Ativa = false;
        }

        Assert.Equal(503, falha.Codigo());
        Assert.Equal(0, await ContarChavesAsync(chave));
        // Sem a correção, a chave ficava "em processamento" e a nova tentativa recebia 409 durante o bloqueio.
        var novaTentativa = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo)).ComChave(chave));
        await novaTentativa.AssertStatusAsync(201);
        Assert.Equal(1, await api.ContarSolicitacoesAsync(cidadao.Id));
    }

    [Fact]
    public async Task Multipart_incompleto_com_chave_retorna_400_sem_guardar_a_chave()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var chave = Chave();
        // Termina antes do delimitador final, como num envio interrompido.
        var corpo = new ByteArrayContent(Encoding.UTF8.GetBytes(
            "--LIMITE\r\nContent-Disposition: form-data; name=\"titulo\"\r\n\r\nBuraco grande na rua\r\n"
            + "--LIMITE\r\nContent-Disposition: form-data; name=\"descricao\"\r\n\r\nDescrição sem fim"));
        corpo.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=LIMITE");

        var resposta = await cliente.SendAsync(Dados.Post("/ocorrencias", corpo).ComChave(chave));

        await resposta.AssertStatusAsync(400);
        Assert.Equal("Não foi possível ler os dados do formulário multipart", (await resposta.ErroAsync()).Message);
        Assert.Equal(0, await ContarChavesAsync(chave));
        Assert.Equal(0, await api.ContarSolicitacoesAsync(cidadao.Id));
    }

    [Fact]
    public async Task Chave_vazia_retorna_400_em_vez_de_desligar_a_protecao()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();

        // Direto no TestServer: pelo HttpClient o cabeçalho vazio é descartado antes de chegar à API (o Kestrel o entrega).
        Task<HttpContext> Andamento(string? chave) => api.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = $"/ocorrencias/{criada.Id}/andamentos";
            c.Request.Headers.Authorization = $"Bearer {gestor.Token}";
            if (chave is not null)
                c.Request.Headers["Idempotency-Key"] = chave;
            c.Request.ContentType = "application/json";
            c.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"descricao\":\"Equipe enviada\"}"));
            c.Features.Set<IHttpRequestBodyDetectionFeature>(new ComCorpo());
        });

        var vazia = await Andamento("");
        var semCabecalho = await Andamento(null);

        Assert.Equal(400, vazia.Response.StatusCode);
        Assert.Equal(201, semCabecalho.Response.StatusCode);
        Assert.Equal(1, await api.NoBancoAsync(db => db.Andamentos.CountAsync(a => a.SolicitacaoId == criada.Id)));
    }

    private sealed class ComCorpo : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    [Theory]
    [InlineData("chave com espaço")]
    [InlineData("chave-com-acentuação")]
    public async Task Chave_com_formato_invalido_retorna_400(string chave)
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var requisicao = Dados.Post("/ocorrencias", Dados.Ocorrencia());
        requisicao.Headers.TryAddWithoutValidation("Idempotency-Key", chave);

        var resposta = await cliente.SendAsync(requisicao);

        Assert.Equal(400, resposta.Codigo());
        Assert.Equal(0, await api.ContarSolicitacoesAsync(cidadao.Id));
    }

    [Fact]
    public async Task Chave_acima_do_tamanho_maximo_retorna_400()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();

        var resposta = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia()).ComChave(new string('k', 101)));

        Assert.Equal(400, resposta.Codigo());
    }

    private async Task<int> ContarChavesAsync(string chave)
    {
        await using var db = await api.Services.GetRequiredService<IDbContextFactory<ControleDbContext>>().CreateDbContextAsync();
        return await db.Idempotencia.CountAsync(r => r.Chave == chave);
    }
}

public sealed class IdempotenciaRetencaoTests(FabricaComRelogio api) : IClassFixture<FabricaComRelogio>
{
    [Fact]
    public async Task Servico_de_limpeza_remove_as_chaves_expiradas_sozinho()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var chave = Guid.NewGuid().ToString();
        Assert.Equal(201, (await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia()).ComChave(chave))).Codigo());
        Assert.Equal(1, await ContarAsync(chave));

        // Passa a retenção (24 h) e o intervalo da limpeza (30 min): o BackgroundService roda pelo TimeProvider.
        api.Tempo.Advance(TimeSpan.FromHours(25));

        var limite = DateTime.UtcNow.AddSeconds(10);
        while (await ContarAsync(chave) > 0 && DateTime.UtcNow < limite)
            await Task.Delay(50);
        Assert.Equal(0, await ContarAsync(chave));
    }

    private async Task<int> ContarAsync(string chave)
    {
        await using var db = await api.Services.GetRequiredService<IDbContextFactory<ControleDbContext>>().CreateDbContextAsync();
        return await db.Idempotencia.CountAsync(r => r.Chave == chave);
    }

    [Fact]
    public async Task Chave_expirada_pode_ser_reutilizada_com_outro_conteudo()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var chave = Guid.NewGuid().ToString();

        var primeira = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia()).ComChave(chave));
        var dentroDoPrazo = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia()).ComChave(chave));
        api.Tempo.Advance(TimeSpan.FromHours(25));
        var aposExpirar = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia()).ComChave(chave));

        Assert.Equal(201, primeira.Codigo());
        Assert.Equal(409, dentroDoPrazo.Codigo());
        Assert.Equal(201, aposExpirar.Codigo());
        Assert.Equal(2, await api.ContarSolicitacoesAsync(cidadao.Id));
    }
}

public sealed class IdempotenciaReinicioTests
{
    [Fact]
    public async Task Chave_continua_valendo_depois_de_reiniciar_a_api()
    {
        var pasta = Path.Combine(Path.GetTempPath(), "meubairro-testes", $"reinicio-{Guid.NewGuid():N}");
        var chave = Guid.NewGuid().ToString();
        var titulo = Dados.TituloUnico();
        try
        {
            Conta cidadao;
            OcorrenciaResponse original;
            await using (var antes = new FabricaReutilizavel(pasta))
            {
                cidadao = await antes.NovoCidadaoAsync();
                using var cliente = cidadao.Cliente();
                original = await (await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo)).ComChave(chave)))
                    .LerAsync<OcorrenciaResponse>();
            }

            await using var depois = new FabricaReutilizavel(pasta);
            // O mesmo token continua válido: o segredo JWT vem da configuração, não da memória do processo.
            using var clienteDepois = depois.Cliente(cidadao.Token);
            var repetida = await clienteDepois.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo)).ComChave(chave));

            Assert.Equal(201, repetida.Codigo());
            Assert.Equal("true", repetida.Headers.GetValues("Idempotent-Replayed").Single());
            Assert.Equal(original.Id, (await repetida.LerAsync<OcorrenciaResponse>()).Id);
            Assert.Equal(1, await depois.ContarSolicitacoesAsync(cidadao.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(pasta))
                Directory.Delete(pasta, recursive: true);
        }
    }
}

// Mesmo comportamento quando a tabela de controle fica em outro arquivo (caso do Oracle como banco principal).
public sealed class IdempotenciaControleSeparadoTests(FabricaControleSeparado api) : IClassFixture<FabricaControleSeparado>
{
    [Fact]
    public async Task Repeticao_e_concorrencia_com_tabela_de_controle_separada()
    {
        Assert.False(api.Services.GetRequiredService<IdempotenciaService>().ConclusaoAtomica);
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var chave = Guid.NewGuid().ToString();
        var titulo = Dados.TituloUnico();

        var respostas = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo)).ComChave(chave))));
        var repetida = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo)).ComChave(chave));

        Assert.All(respostas, r => Assert.Contains(r.Codigo(), new[] { 201, 409 }));
        Assert.Equal(201, repetida.Codigo());
        Assert.Equal("true", repetida.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(1, await api.ContarSolicitacoesAsync(cidadao.Id));
    }

    [Fact]
    public async Task Sem_atomicidade_a_falha_ao_registrar_o_resultado_nao_esconde_o_sucesso_nem_permite_duplicar()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var chave = Guid.NewGuid().ToString();
        var titulo = Dados.TituloUnico();

        api.Falhas.FalharConclusaoIdempotencia = true;
        HttpResponseMessage primeira;
        try
        {
            primeira = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo)).ComChave(chave));
        }
        finally
        {
            api.Falhas.FalharConclusaoIdempotencia = false;
        }
        var repetida = await cliente.SendAsync(Dados.Post("/ocorrencias", Dados.Ocorrencia(titulo)).ComChave(chave));

        // O negócio foi confirmado, então o cliente recebe o sucesso real; a repetição é barrada enquanto a chave está bloqueada.
        Assert.Equal(201, primeira.Codigo());
        Assert.Equal(409, repetida.Codigo());
        Assert.NotNull(repetida.Headers.RetryAfter);
        Assert.Equal(1, await api.ContarSolicitacoesAsync(cidadao.Id));
    }
}
