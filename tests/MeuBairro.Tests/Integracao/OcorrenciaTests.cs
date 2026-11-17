using System.Net.Http.Json;
using System.Text.Json;
using MeuBairro.Api.Contratos;
using MeuBairro.Api.Dominio;
using MeuBairro.Tests.Infra;
using Microsoft.EntityFrameworkCore;

namespace MeuBairro.Tests.Integracao;

public sealed class OcorrenciaTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Criacao_retorna_201_com_location_e_os_campos_da_ocorrencia()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var titulo = Dados.TituloUnico();

        // Prefixo /api, multipart com endereço em texto livre e coordenadas.
        var resposta = await cliente.PostAsync("/api/ocorrencias", Dados.Ocorrencia(titulo,
            extras: new Dictionary<string, string> { ["latitude"] = "-23.5505199", ["longitude"] = "-46.6333094" }));

        Assert.Equal(201, resposta.Codigo());
        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        var o = json.RootElement;
        foreach (var campo in new[] { "id", "titulo", "descricao", "categoria", "status", "endereco", "latitude", "longitude", "fotoUrl", "criadoEm", "usuarioId" })
            Assert.True(o.TryGetProperty(campo, out _), $"campo {campo} ausente");
        Assert.Equal(titulo, o.GetProperty("titulo").GetString());
        Assert.Equal("BURACO", o.GetProperty("categoria").GetString());
        Assert.Equal("ABERTA", o.GetProperty("status").GetString());
        Assert.Equal("MEDIA", o.GetProperty("prioridade").GetString());
        Assert.Equal(-23.55052m, o.GetProperty("latitude").GetDecimal());
        Assert.Equal(cidadao.Id, o.GetProperty("usuarioId").GetInt64());
        Assert.StartsWith("Rua das Flores, 120", o.GetProperty("endereco").GetString());
        Assert.Equal(JsonValueKind.Null, o.GetProperty("fotoUrl").ValueKind);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}$", o.GetProperty("criadoEm").GetString());

        var id = o.GetProperty("id").GetInt64();
        Assert.EndsWith($"/api/ocorrencias/{id}", resposta.Headers.Location!.ToString());
        var detalhe = await (await cliente.GetAsync($"/ocorrencias/{id}")).LerAsync<OcorrenciaResponse>();
        Assert.Equal(titulo, detalhe.Titulo);
    }

    [Fact]
    public async Task Criacao_grava_endereco_e_solicitacao_conforme_o_esquema_oracle()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var criada = await cidadao.CriarOcorrenciaAsync(extras: new Dictionary<string, string>
        {
            ["logradouro"] = "Avenida Brasil", ["numero"] = "1500", ["bairro"] = "Jardim América",
            ["cidade"] = "São Paulo", ["estado"] = "sp", ["cep"] = "01430-001", ["latitude"] = "-23.1234567"
        });

        var gravada = await api.NoBancoAsync(db => db.Solicitacoes.Include(s => s.Endereco).SingleAsync(s => s.Id == criada.Id));

        Assert.Equal("Avenida Brasil", gravada.Endereco.Logradouro);
        Assert.Equal("1500", gravada.Endereco.Numero);
        Assert.Equal("Jardim América", gravada.Endereco.Bairro);
        Assert.Equal("SP", gravada.Endereco.Estado);
        Assert.Equal("01430001", gravada.Endereco.Cep);
        Assert.Equal(-23.123457m, gravada.Endereco.Latitude);
        Assert.Equal(StatusSolicitacao.ABERTA, gravada.Status);
        Assert.Null(gravada.FuncionarioId);
        Assert.Null(gravada.DataConclusao);
        Assert.Equal(0, gravada.DataAbertura.Millisecond);
    }

    [Fact]
    public async Task Prazo_estimado_usa_o_prazo_da_categoria()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var criada = await cidadao.CriarOcorrenciaAsync(categoria: "LIXO");
        var prazo = await api.NoBancoAsync(db => db.Categorias.Where(c => c.Id == db.Solicitacoes.First(s => s.Id == criada.Id).CategoriaId)
            .Select(c => c.PrazoDias).SingleAsync());

        Assert.Equal(criada.CriadoEm.AddDays(prazo), criada.PrazoEstimado);
        Assert.False(criada.Atrasada);
    }

    [Fact]
    public async Task Campos_obrigatorios_ausentes_retornam_400_por_campo()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();

        var resposta = await cliente.PostAsync("/ocorrencias", new MultipartFormDataContent { { new StringContent("x"), "outro" } });

        Assert.Equal(400, resposta.Codigo());
        var campos = (await resposta.ErroAsync()).Fields.Select(f => f.Field).ToHashSet();
        Assert.Superset(new HashSet<string> { "titulo", "descricao", "categoria", "endereco" }, campos);
    }

    [Theory]
    [InlineData("titulo", 101)]
    [InlineData("titulo", 4)]
    [InlineData("descricao", 501)]
    [InlineData("descricao", 9)]
    [InlineData("endereco", 101)]
    public async Task Textos_fora_do_tamanho_das_colunas_retornam_400(string campo, int tamanho)
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();
        var texto = new string('x', tamanho);

        var form = campo switch
        {
            "titulo" => Dados.Ocorrencia(titulo: texto),
            "descricao" => Dados.Ocorrencia(descricao: texto),
            _ => Dados.Ocorrencia(endereco: texto)
        };
        var resposta = await cliente.PostAsync("/ocorrencias", form);

        Assert.Equal(400, resposta.Codigo());
        Assert.Contains((await resposta.ErroAsync()).Fields, f => f.Field == campo);
    }

    [Fact]
    public async Task Textos_no_limite_das_colunas_sao_aceitos()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();

        var resposta = await cliente.PostAsync("/ocorrencias",
            Dados.Ocorrencia(titulo: new string('t', 100), descricao: new string('d', 500), endereco: new string('e', 100)));

        Assert.Equal(201, resposta.Codigo());
    }

    [Fact]
    public async Task Categoria_inexistente_retorna_400_com_as_opcoes_validas()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();

        var resposta = await cliente.PostAsync("/ocorrencias", Dados.Ocorrencia(categoria: "VULCAO"));

        Assert.Equal(400, resposta.Codigo());
        var erro = await resposta.ErroAsync();
        Assert.StartsWith("Categoria inválida: VULCAO", erro.Message);
        Assert.Contains("BURACO", erro.Message);
    }

    [Theory]
    [InlineData("latitude", "90.0001")]
    [InlineData("latitude", "-90.0001")]
    [InlineData("latitude", "abc")]
    [InlineData("longitude", "180.0001")]
    [InlineData("longitude", "-180.0001")]
    [InlineData("estado", "SPP")]
    [InlineData("cep", "123")]
    public async Task Endereco_invalido_retorna_400(string campo, string valor)
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();

        var resposta = await cliente.PostAsync("/ocorrencias", Dados.Ocorrencia(extras: new Dictionary<string, string> { [campo] = valor }));

        Assert.Equal(400, resposta.Codigo());
        Assert.Contains((await resposta.ErroAsync()).Fields, f => f.Field == campo);
    }

    [Fact]
    public async Task EnderecoId_inexistente_ou_residencia_de_outra_pessoa_retorna_400()
    {
        var vizinho = await api.NovoCidadaoAsync();
        var enderecoDoVizinho = await api.NoBancoAsync(db => db.Usuarios.Where(u => u.Id == vizinho.Id).Select(u => u.EnderecoId).SingleAsync());
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();

        var inexistente = await cliente.PostAsync("/ocorrencias",
            Dados.Ocorrencia(endereco: null, extras: new Dictionary<string, string> { ["enderecoId"] = "987654" }));
        var alheio = await cliente.PostAsync("/ocorrencias",
            Dados.Ocorrencia(endereco: null, extras: new Dictionary<string, string> { ["enderecoId"] = enderecoDoVizinho.ToString() }));

        Assert.Equal(400, inexistente.Codigo());
        Assert.Equal(400, alheio.Codigo());
        Assert.StartsWith("Endereço inválido", (await alheio.ErroAsync()).Message);
    }

    [Fact]
    public async Task Cidadao_pode_registrar_no_proprio_endereco_de_cadastro()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var enderecoId = await api.NoBancoAsync(db => db.Usuarios.Where(u => u.Id == cidadao.Id).Select(u => u.EnderecoId).SingleAsync());

        var criada = await cidadao.CriarOcorrenciaAsync(extras: new Dictionary<string, string> { ["enderecoId"] = enderecoId.ToString() });

        Assert.Equal(enderecoId, criada.EnderecoId);
    }

    [Fact]
    public async Task Gestor_nao_registra_ocorrencia()
    {
        var gestor = await api.NovoGestorAsync();
        using var cliente = gestor.Cliente();

        Assert.Equal(403, (await cliente.PostAsync("/ocorrencias", Dados.Ocorrencia())).Codigo());
    }

    [Fact]
    public async Task Ocorrencia_inexistente_retorna_404_em_todas_as_rotas()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();

        Assert.Equal(404, (await cliente.GetAsync("/ocorrencias/999999")).Codigo());
        Assert.Equal(404, (await cliente.GetAsync("/ocorrencias/999999/historico")).Codigo());
        Assert.Equal(404, (await cliente.GetAsync("/ocorrencias/999999/anexos")).Codigo());
        Assert.Equal(404, (await cliente.PutAsync("/ocorrencias/999999", Dados.Ocorrencia())).Codigo());
        Assert.Equal(404, (await cliente.DeleteAsync("/ocorrencias/999999")).Codigo());
        Assert.Equal("Ocorrência 999999 não encontrada", (await (await cliente.GetAsync("/ocorrencias/999999")).ErroAsync()).Message);
    }

    [Fact]
    public async Task Dono_atualiza_ocorrencia_aberta_e_reenvio_do_endereco_formatado_nao_o_altera()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var criada = await cidadao.CriarOcorrenciaAsync(extras: new Dictionary<string, string>
            { ["bairro"] = "Centro", ["latitude"] = "-23.5", ["longitude"] = "-46.6" });
        using var cliente = cidadao.Cliente();

        // Reenvia o endereço formatado da resposta e não manda as coordenadas: os valores atuais são mantidos.
        var resposta = await cliente.PutAsync($"/ocorrencias/{criada.Id}",
            Dados.Ocorrencia("Título revisado da ocorrência", "Descrição revisada com mais detalhes", "CALCADA", criada.Endereco));

        Assert.Equal(200, resposta.Codigo());
        var atualizada = await resposta.LerAsync<OcorrenciaResponse>();
        Assert.Equal("Título revisado da ocorrência", atualizada.Titulo);
        Assert.Equal("CALCADA", atualizada.Categoria);
        Assert.Equal(criada.Endereco, atualizada.Endereco);
        Assert.Equal(criada.EnderecoId, atualizada.EnderecoId);
        Assert.Equal(-23.5m, atualizada.Latitude);
    }

    [Fact]
    public async Task Atualizar_endereco_compartilhado_cria_um_novo_sem_mudar_o_cadastro()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var enderecoCasa = await api.NoBancoAsync(db => db.Usuarios.Where(u => u.Id == cidadao.Id).Select(u => u.EnderecoId).SingleAsync());
        var criada = await cidadao.CriarOcorrenciaAsync(extras: new Dictionary<string, string> { ["enderecoId"] = enderecoCasa.ToString() });
        using var cliente = cidadao.Cliente();

        var resposta = await cliente.PutAsync($"/ocorrencias/{criada.Id}",
            Dados.Ocorrencia(criada.Titulo, criada.Descricao, "BURACO", "Rua Nova da Ocorrência"));

        var atualizada = await resposta.LerAsync<OcorrenciaResponse>();
        Assert.NotEqual(enderecoCasa, atualizada.EnderecoId);
        var logradouroCasa = await api.NoBancoAsync(db => db.Enderecos.Where(e => e.Id == enderecoCasa).Select(e => e.Logradouro).SingleAsync());
        Assert.NotEqual("Rua Nova da Ocorrência", logradouroCasa);
    }

    [Fact]
    public async Task Outro_cidadao_nao_altera_nem_exclui_mas_pode_consultar()
    {
        var dono = await api.NovoCidadaoAsync();
        var outro = await api.NovoCidadaoAsync();
        var criada = await dono.CriarOcorrenciaAsync();
        using var cliente = outro.Cliente();

        var alterar = await cliente.PutAsync($"/ocorrencias/{criada.Id}", Dados.Ocorrencia(titulo: "Alteração indevida aqui"));
        var excluir = await cliente.DeleteAsync($"/ocorrencias/{criada.Id}");
        var consultar = await cliente.GetAsync($"/ocorrencias/{criada.Id}");

        Assert.Equal(403, alterar.Codigo());
        Assert.Equal(403, excluir.Codigo());
        Assert.Equal(200, consultar.Codigo());
        var titulo = await api.NoBancoAsync(db => db.Solicitacoes.Where(s => s.Id == criada.Id).Select(s => s.Titulo).SingleAsync());
        Assert.Equal(criada.Titulo, titulo);
    }

    [Fact]
    public async Task Dono_nao_altera_nem_exclui_ocorrencia_resolvida_mas_gestor_exclui()
    {
        var dono = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await dono.CriarOcorrenciaAsync();
        await gestor.ResolverAsync(criada.Id);
        using var clienteDono = dono.Cliente();
        using var clienteGestor = gestor.Cliente();

        Assert.Equal(409, (await clienteDono.PutAsync($"/ocorrencias/{criada.Id}", Dados.Ocorrencia())).Codigo());
        Assert.Equal(409, (await clienteDono.DeleteAsync($"/ocorrencias/{criada.Id}")).Codigo());
        Assert.Equal(204, (await clienteGestor.DeleteAsync($"/ocorrencias/{criada.Id}")).Codigo());

        Assert.Equal(404, (await clienteDono.GetAsync($"/ocorrencias/{criada.Id}")).Codigo());
        Assert.Equal(404, (await clienteDono.GetAsync($"/ocorrencias/{criada.Id}/historico")).Codigo());
        Assert.Equal(0, await api.NoBancoAsync(db => db.Andamentos.CountAsync(a => a.SolicitacaoId == criada.Id)));
    }

    [Fact]
    public async Task Exclusao_pelo_dono_remove_solicitacao_anexos_e_endereco_orfao()
    {
        var dono = await api.NovoCidadaoAsync();
        var criada = await dono.CriarOcorrenciaAsync(foto: Dados.PngValido);
        var arquivo = Path.Combine(api.PastaFotos, criada.FotoUrl!.Split('/').Last());
        Assert.True(File.Exists(arquivo));
        using var cliente = dono.Cliente();

        Assert.Equal(204, (await cliente.DeleteAsync($"/ocorrencias/{criada.Id}")).Codigo());

        Assert.False(await api.NoBancoAsync(db => db.Solicitacoes.AnyAsync(s => s.Id == criada.Id)));
        Assert.False(await api.NoBancoAsync(db => db.Anexos.AnyAsync(a => a.SolicitacaoId == criada.Id)));
        Assert.False(await api.NoBancoAsync(db => db.Enderecos.AnyAsync(e => e.Id == criada.EnderecoId)));
        Assert.False(File.Exists(arquivo));
    }

    [Fact]
    public async Task Campos_de_controle_enviados_pelo_cliente_sao_ignorados()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var outro = await api.NovoCidadaoAsync();

        var criada = await cidadao.CriarOcorrenciaAsync(extras: new Dictionary<string, string>
        {
            ["status"] = "RESOLVIDA", ["prioridade"] = "ALTA", ["usuarioId"] = outro.Id.ToString(),
            ["id"] = "424242", ["funcionarioId"] = "1", ["dataConclusao"] = "2020-01-01"
        });

        Assert.Equal(StatusSolicitacao.ABERTA, criada.Status);
        Assert.Equal(Prioridade.MEDIA, criada.Prioridade);
        Assert.Equal(cidadao.Id, criada.UsuarioId);
        Assert.NotEqual(424242, criada.Id);
        Assert.Null(criada.ResponsavelId);
        Assert.Null(criada.DataConclusao);
    }

    [Fact]
    public async Task Mesma_ocorrencia_aberta_enviada_duas_vezes_retorna_409()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var titulo = Dados.TituloUnico();
        var primeira = await cidadao.CriarOcorrenciaAsync(titulo);
        using var cliente = cidadao.Cliente();

        var repetida = await cliente.PostAsync("/ocorrencias", Dados.Ocorrencia(titulo.ToUpperInvariant()));

        Assert.Equal(409, repetida.Codigo());
        Assert.Contains($"id {primeira.Id}", (await repetida.ErroAsync()).Message);
        Assert.Equal(1, await api.NoBancoAsync(db => db.Solicitacoes.CountAsync(s => s.UsuarioId == cidadao.Id)));
    }

    [Fact]
    public async Task Duplicidade_e_detectada_em_titulo_e_bairro_com_letras_acentuadas()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var titulo = $"Árvore caída {Guid.NewGuid().ToString("N")[..6]}";
        var primeira = await cidadao.CriarOcorrenciaAsync(titulo, extras: new Dictionary<string, string> { ["bairro"] = "Éden" });
        using var cliente = cidadao.Cliente();

        var exata = await cliente.PostAsync("/ocorrencias",
            Dados.Ocorrencia(titulo, extras: new Dictionary<string, string> { ["bairro"] = "Éden" }));
        var maiusculas = await cliente.PostAsync("/ocorrencias",
            Dados.Ocorrencia(titulo.ToUpperInvariant(), extras: new Dictionary<string, string> { ["bairro"] = "ÉDEN" }));

        Assert.Equal(409, exata.Codigo());
        Assert.Equal(409, maiusculas.Codigo());
        Assert.Contains($"id {primeira.Id}", (await maiusculas.ErroAsync()).Message);
        Assert.Equal(1, await api.ContarSolicitacoesAsync(cidadao.Id));
    }

    [Fact]
    public async Task Paginacao_usa_tamanho_padrao_10_e_maximo_50()
    {
        var cidadao = await api.NovoCidadaoAsync();
        for (var i = 0; i < 12; i++)
            await cidadao.CriarOcorrenciaAsync();
        using var cliente = cidadao.Cliente();

        var padrao = await (await cliente.GetAsync("/ocorrencias/paginado?minhas=true")).LerAsync<PaginaResponse<OcorrenciaResponse>>();
        var acima = await (await cliente.GetAsync("/ocorrencias/paginado?minhas=true&size=51")).LerAsync<PaginaResponse<OcorrenciaResponse>>();
        var segunda = await (await cliente.GetAsync("/ocorrencias/paginado?minhas=true&page=1")).LerAsync<PaginaResponse<OcorrenciaResponse>>();

        Assert.Equal(10, padrao.Tamanho);
        Assert.Equal(10, padrao.Conteudo.Count);
        Assert.Equal(12, padrao.TotalElementos);
        Assert.Equal(2, padrao.TotalPaginas);
        Assert.False(padrao.Ultima);
        Assert.Equal(50, acima.Tamanho);
        Assert.Equal(12, acima.Conteudo.Count);
        Assert.Equal(2, segunda.Conteudo.Count);
        Assert.True(segunda.Ultima);
        Assert.Empty(padrao.Conteudo.Select(o => o.Id).Intersect(segunda.Conteudo.Select(o => o.Id)));
    }

    [Fact]
    public async Task Ordenacao_por_id_crescente_gera_paginas_sem_repeticao()
    {
        var cidadao = await api.NovoCidadaoAsync();
        for (var i = 0; i < 5; i++)
            await cidadao.CriarOcorrenciaAsync();
        using var cliente = cidadao.Cliente();

        var p0 = await (await cliente.GetAsync("/ocorrencias/paginado?minhas=true&size=2&page=0&sort=id,asc")).LerAsync<PaginaResponse<OcorrenciaResponse>>();
        var p1 = await (await cliente.GetAsync("/ocorrencias/paginado?minhas=true&size=2&page=1&sort=id,asc")).LerAsync<PaginaResponse<OcorrenciaResponse>>();
        var desc = await (await cliente.GetAsync("/ocorrencias/paginado?minhas=true&size=5&sort=id,desc")).LerAsync<PaginaResponse<OcorrenciaResponse>>();

        var ids = p0.Conteudo.Concat(p1.Conteudo).Select(o => o.Id).ToList();
        Assert.Equal(ids.Order(), ids);
        Assert.Equal(4, ids.Distinct().Count());
        Assert.Equal(desc.Conteudo.Select(o => o.Id).OrderDescending(), desc.Conteudo.Select(o => o.Id));
    }

    [Fact]
    public async Task Ordenacao_por_prioridade_segue_a_importancia_e_nao_o_codigo_gravado()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var bairro = $"Prioridades {Guid.NewGuid().ToString("N")[..6]}";
        var extras = new Dictionary<string, string> { ["bairro"] = bairro };
        var baixa = await cidadao.CriarOcorrenciaAsync(extras: extras);
        var media = await cidadao.CriarOcorrenciaAsync(extras: extras);
        var alta = await cidadao.CriarOcorrenciaAsync(extras: extras);
        using (var clienteGestor = gestor.Cliente())
        {
            Assert.Equal(200, (await clienteGestor.PatchAsJsonAsync($"/ocorrencias/{baixa.Id}/prioridade", new { prioridade = "BAIXA" })).Codigo());
            Assert.Equal(200, (await clienteGestor.PatchAsJsonAsync($"/ocorrencias/{alta.Id}/prioridade", new { prioridade = "ALTA" })).Codigo());
        }
        using var cliente = cidadao.Cliente();

        async Task<List<long>> Ordenar(string sort) =>
            (await (await cliente.GetAsync($"/ocorrencias/paginado?bairro={Uri.EscapeDataString(bairro)}&sort={sort}"))
                .LerAsync<PaginaResponse<OcorrenciaResponse>>()).Conteudo.Select(o => o.Id).ToList();

        // Os códigos gravados em CHAR(1) (A = ALTA, B = BAIXA, M = MEDIA) não seguem a ordem de importância.
        Assert.Equal([alta.Id, media.Id, baixa.Id], await Ordenar("prioridade,desc"));
        Assert.Equal([baixa.Id, media.Id, alta.Id], await Ordenar("prioridade,asc"));
    }

    [Theory]
    [InlineData("status=INEXISTENTE")]
    [InlineData("prioridade=URGENTE")]
    [InlineData("sort=campoInexistente,asc")]
    [InlineData("sort=id,lateral")]
    [InlineData("categoria=NAO_EXISTE")]
    [InlineData("page=abc")]
    public async Task Parametros_invalidos_de_listagem_retornam_400(string parametro)
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();

        Assert.Equal(400, (await cliente.GetAsync($"/ocorrencias/paginado?{parametro}")).Codigo());
    }

    [Fact]
    public async Task Filtros_por_bairro_categoria_status_e_prioridade()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var bairro = $"Vila {Guid.NewGuid().ToString("N")[..6]}";
        var noBairro = new Dictionary<string, string> { ["bairro"] = bairro };
        var buraco = await cidadao.CriarOcorrenciaAsync(categoria: "BURACO", extras: noBairro);
        var lixo = await cidadao.CriarOcorrenciaAsync(categoria: "LIXO", extras: noBairro);
        await cidadao.CriarOcorrenciaAsync(categoria: "LIXO", extras: new Dictionary<string, string> { ["bairro"] = "Outro Bairro" });
        await gestor.AlterarStatusAsync(lixo.Id, "EM_ANALISE");
        using (var g = gestor.Cliente())
            await g.PatchAsJsonAsync($"/ocorrencias/{buraco.Id}/prioridade", new { prioridade = "ALTA" });
        using var cliente = cidadao.Cliente();

        async Task<List<long>> Buscar(string filtro) =>
            (await (await cliente.GetAsync($"/ocorrencias/paginado?size=50&{filtro}")).LerAsync<PaginaResponse<OcorrenciaResponse>>())
            .Conteudo.Select(o => o.Id).ToList();

        Assert.Equal([lixo.Id, buraco.Id], await Buscar($"bairro={Uri.EscapeDataString(bairro.ToUpperInvariant())}"));
        Assert.Equal([lixo.Id], await Buscar($"bairro={Uri.EscapeDataString(bairro)}&categoria=lixo"));
        Assert.Equal([lixo.Id], await Buscar($"bairro={Uri.EscapeDataString(bairro)}&status=EM_ANALISE"));
        Assert.Equal([buraco.Id], await Buscar($"bairro={Uri.EscapeDataString(bairro)}&prioridade=ALTA"));
        Assert.Empty(await Buscar($"bairro={Uri.EscapeDataString(bairro)}&status=RESOLVIDA"));
    }

    [Fact]
    public async Task Filtro_por_bairro_e_painel_ignoram_caixa_em_letras_acentuadas()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var bairro = $"Água Branca {Guid.NewGuid().ToString("N")[..6]}";
        var criada = await cidadao.CriarOcorrenciaAsync(extras: new Dictionary<string, string> { ["bairro"] = bairro });
        using var cliente = cidadao.Cliente();

        async Task<List<long>> Buscar(string filtro) =>
            (await (await cliente.GetAsync($"/ocorrencias/paginado?bairro={Uri.EscapeDataString(filtro)}"))
                .LerAsync<PaginaResponse<OcorrenciaResponse>>()).Conteudo.Select(o => o.Id).ToList();

        // O lower() nativo do SQLite não converte "Á": sem a função Unicode da API, nem o texto exato era encontrado.
        Assert.Equal([criada.Id], await Buscar(bairro));
        Assert.Equal([criada.Id], await Buscar(bairro.ToUpperInvariant()));
        Assert.Equal([criada.Id], await Buscar(bairro.ToLowerInvariant()));
        var painel = await (await cliente.GetAsync($"/ocorrencias/painel?bairro={Uri.EscapeDataString(bairro.ToUpperInvariant())}"))
            .LerAsync<PainelBairroResponse>();
        Assert.Equal(1, painel.Total);
    }

    [Fact]
    public async Task Listagem_completa_vem_das_mais_recentes_para_as_mais_antigas()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var primeira = await cidadao.CriarOcorrenciaAsync();
        var segunda = await cidadao.CriarOcorrenciaAsync();
        using var cliente = cidadao.Cliente();

        var lista = await (await cliente.GetAsync("/ocorrencias")).LerAsync<List<OcorrenciaResponse>>();

        Assert.True(lista.FindIndex(o => o.Id == segunda.Id) < lista.FindIndex(o => o.Id == primeira.Id));
    }

    [Fact]
    public async Task Painel_do_bairro_mostra_situacao_e_atrasos()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var bairro = $"Painel {Guid.NewGuid().ToString("N")[..6]}";
        var extras = new Dictionary<string, string> { ["bairro"] = bairro };
        var aberta = await cidadao.CriarOcorrenciaAsync(categoria: "LIXO", extras: extras);
        var resolvida = await cidadao.CriarOcorrenciaAsync(categoria: "BURACO", extras: extras);
        await gestor.ResolverAsync(resolvida.Id);
        // Abertura antiga para simular atraso em relação ao prazo da categoria.
        await api.NoBancoAsync(db => db.Solicitacoes.Where(s => s.Id == aberta.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DataAbertura, x => x.DataAbertura.AddDays(-60))));
        using var cliente = cidadao.Cliente();

        var painel = await (await cliente.GetAsync($"/ocorrencias/painel?bairro={Uri.EscapeDataString(bairro)}")).LerAsync<PainelBairroResponse>();

        Assert.Equal(2, painel.Total);
        Assert.Equal(1, painel.Abertas);
        Assert.Equal(1, painel.Resolvidas);
        Assert.Equal(1, painel.Atrasadas);
        Assert.Equal(1, painel.ResolvidasNoPrazo);
        Assert.Equal(0, painel.TempoMedioResolucaoDias);
        Assert.Contains(painel.PorCategoria, c => c.Codigo == "LIXO" && c.Pendentes == 1);
        var detalhe = await (await cliente.GetAsync($"/ocorrencias/{aberta.Id}")).LerAsync<OcorrenciaResponse>();
        Assert.True(detalhe.Atrasada);
    }

    [Fact]
    public async Task Resumo_por_status_e_prioridade_e_exclusivo_do_gestor()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        await cidadao.CriarOcorrenciaAsync();
        using var clienteCidadao = cidadao.Cliente();
        using var clienteGestor = gestor.Cliente();

        Assert.Equal(403, (await clienteCidadao.GetAsync("/ocorrencias/resumo")).Codigo());
        var resumo = await (await clienteGestor.GetAsync("/ocorrencias/resumo")).LerAsync<ResumoOcorrenciasResponse>();
        Assert.Equal(resumo.Total, resumo.Abertas + resumo.EmAnalise + resumo.Resolvidas);
        Assert.Equal(resumo.Total, resumo.PrioridadeAlta + resumo.PrioridadeMedia + resumo.PrioridadeBaixa);
        Assert.True(resumo.Total >= 1);
    }
}

// Os códigos CHAR(1) do Oracle são configuráveis (Banco:Codigos), então a ordenação não pode depender deles.
public sealed class OrdenacaoComCodigosConfiguradosTests
{
    [Fact]
    public async Task Ordenacao_por_status_segue_o_ciclo_da_ocorrencia_com_qualquer_codigo()
    {
        await using var api = new FabricaComConfiguracao(
            ("Banco:Codigos:Status:ABERTA", "P"), ("Banco:Codigos:Status:EM_ANALISE", "A"), ("Banco:Codigos:Status:RESOLVIDA", "C"));
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var aberta = await cidadao.CriarOcorrenciaAsync();
        var emAnalise = await cidadao.CriarOcorrenciaAsync();
        var resolvida = await cidadao.CriarOcorrenciaAsync();
        Assert.Equal(200, (await gestor.AlterarStatusAsync(emAnalise.Id, "EM_ANALISE")).Codigo());
        await gestor.ResolverAsync(resolvida.Id);
        using var cliente = cidadao.Cliente();

        async Task<List<long>> Ordenar(string sort) =>
            (await (await cliente.GetAsync($"/ocorrencias/paginado?minhas=true&sort={sort}"))
                .LerAsync<PaginaResponse<OcorrenciaResponse>>()).Conteudo.Select(o => o.Id).ToList();

        Assert.Equal("C", await api.NoBancoAsync(db => db.Database
            .SqlQueryRaw<string>("SELECT SOLICITACAO_STATUS AS \"Value\" FROM SOLICITACOES WHERE SOLICITACAO_ID = {0}", resolvida.Id)
            .SingleAsync()));
        Assert.Equal([aberta.Id, emAnalise.Id, resolvida.Id], await Ordenar("status,asc"));
        Assert.Equal([resolvida.Id, emAnalise.Id, aberta.Id], await Ordenar("status,desc"));
    }
}
