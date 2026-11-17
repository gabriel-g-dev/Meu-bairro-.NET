using System.Net.Http.Json;
using MeuBairro.Api.Contratos;
using MeuBairro.Tests.Infra;
using Microsoft.EntityFrameworkCore;

namespace MeuBairro.Tests.Integracao;

public sealed class AvaliacaoTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Avaliacao_so_e_aceita_depois_de_resolvida()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();
        using var cliente = cidadao.Cliente();

        var antes = await cliente.PostAsJsonAsync($"/ocorrencias/{criada.Id}/avaliacao", new { nota = 5 });
        await gestor.ResolverAsync(criada.Id);
        var depois = await cliente.PostAsJsonAsync($"/ocorrencias/{criada.Id}/avaliacao", new { nota = 4, comentario = "  Resolveram rápido  " });

        Assert.Equal(409, antes.Codigo());
        Assert.Equal("Só é possível avaliar ocorrências resolvidas", (await antes.ErroAsync()).Message);
        Assert.Equal(201, depois.Codigo());
        var avaliacao = await depois.LerAsync<AvaliacaoResponse>();
        Assert.Equal(4, avaliacao.Nota);
        Assert.Equal("Resolveram rápido", avaliacao.Comentario);
        var consulta = await (await cliente.GetAsync($"/ocorrencias/{criada.Id}/avaliacao")).LerAsync<AvaliacaoResponse>();
        Assert.Equal(avaliacao.Id, consulta.Id);
    }

    [Fact]
    public async Task Segunda_avaliacao_retorna_409_e_mantem_a_primeira()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();
        await gestor.ResolverAsync(criada.Id);
        using var cliente = cidadao.Cliente();

        Assert.Equal(201, (await cliente.PostAsJsonAsync($"/ocorrencias/{criada.Id}/avaliacao", new { nota = 5 })).Codigo());
        var segunda = await cliente.PostAsJsonAsync($"/ocorrencias/{criada.Id}/avaliacao", new { nota = 1 });

        Assert.Equal(409, segunda.Codigo());
        Assert.Equal("Esta ocorrência já foi avaliada", (await segunda.ErroAsync()).Message);
        Assert.Equal(5, await api.NoBancoAsync(db => db.Avaliacoes.Where(a => a.SolicitacaoId == criada.Id).Select(a => a.Nota).SingleAsync()));
    }

    [Fact]
    public async Task Avaliacoes_simultaneas_gravam_apenas_uma()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();
        await gestor.ResolverAsync(criada.Id);
        using var cliente = cidadao.Cliente();

        var respostas = await Task.WhenAll(Enumerable.Range(1, 5).Select(n =>
            cliente.PostAsJsonAsync($"/ocorrencias/{criada.Id}/avaliacao", new { nota = n })));

        Assert.Equal(1, respostas.Count(r => r.Codigo() == 201));
        Assert.All(respostas.Where(r => r.Codigo() != 201), r => Assert.Equal(409, r.Codigo()));
        Assert.Equal(1, await api.NoBancoAsync(db => db.Avaliacoes.CountAsync(a => a.SolicitacaoId == criada.Id)));
    }

    [Fact]
    public async Task Somente_o_dono_avalia()
    {
        var dono = await api.NovoCidadaoAsync();
        var outro = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await dono.CriarOcorrenciaAsync();
        await gestor.ResolverAsync(criada.Id);
        using var clienteOutro = outro.Cliente();
        using var clienteGestor = gestor.Cliente();

        Assert.Equal(403, (await clienteOutro.PostAsJsonAsync($"/ocorrencias/{criada.Id}/avaliacao", new { nota = 1 })).Codigo());
        Assert.Equal(403, (await clienteGestor.PostAsJsonAsync($"/ocorrencias/{criada.Id}/avaliacao", new { nota = 5 })).Codigo());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(null)]
    public async Task Nota_fora_de_1_a_5_retorna_400(int? nota)
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();
        await gestor.ResolverAsync(criada.Id);
        using var cliente = cidadao.Cliente();

        var resposta = await cliente.PostAsJsonAsync($"/ocorrencias/{criada.Id}/avaliacao", new { nota });

        Assert.Equal(400, resposta.Codigo());
        Assert.Contains((await resposta.ErroAsync()).Fields, f => f.Field == "nota");
    }

    [Fact]
    public async Task Comentario_acima_de_300_caracteres_retorna_400()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();
        await gestor.ResolverAsync(criada.Id);
        using var cliente = cidadao.Cliente();

        var resposta = await cliente.PostAsJsonAsync($"/ocorrencias/{criada.Id}/avaliacao", new { nota = 3, comentario = new string('c', 301) });

        Assert.Equal(400, resposta.Codigo());
    }

    [Fact]
    public async Task Ocorrencia_sem_avaliacao_retorna_404_na_consulta()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();
        using var cliente = cidadao.Cliente();

        Assert.Equal(404, (await cliente.GetAsync($"/ocorrencias/{criada.Id}/avaliacao")).Codigo());
    }
}

public sealed class CategoriaTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private static string NomeUnico() => $"Categoria {Guid.NewGuid().ToString("N")[..8]}";

    [Fact]
    public async Task Catalogo_inicial_tem_as_seis_categorias_padrao()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();

        var categorias = await (await cliente.GetAsync("/api/categorias")).LerAsync<List<CategoriaResponse>>();

        Assert.Superset(new HashSet<string> { "BURACO", "ILUMINACAO", "LIXO", "ALAGAMENTO", "CALCADA", "OUTRO" },
            new HashSet<string>(categorias.Select(c => c.Codigo)));
        Assert.All(categorias, c => Assert.InRange(c.PrazoDias, 1, 999));
        var iluminacao = categorias.Single(c => c.Codigo == "ILUMINACAO");
        Assert.Equal("Iluminação", iluminacao.Nome);
        var detalhe = await (await cliente.GetAsync($"/categorias/{iluminacao.Id}")).LerAsync<CategoriaResponse>();
        Assert.Equal(iluminacao, detalhe);
    }

    [Fact]
    public async Task Categorias_exigem_autenticacao()
    {
        using var cliente = api.Cliente();

        Assert.Equal(401, (await cliente.GetAsync("/categorias")).Codigo());
    }

    [Fact]
    public async Task Cidadao_nao_cria_altera_nem_exclui_categoria()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();

        Assert.Equal(403, (await cliente.PostAsJsonAsync("/categorias", new { nome = NomeUnico(), prazoDias = 5 })).Codigo());
        Assert.Equal(403, (await cliente.PutAsJsonAsync("/categorias/1", new { nome = "Buraco", prazoDias = 5 })).Codigo());
        Assert.Equal(403, (await cliente.DeleteAsync("/categorias/1")).Codigo());
    }

    [Fact]
    public async Task Gestor_executa_o_ciclo_completo_de_uma_categoria_extra()
    {
        var gestor = await api.NovoGestorAsync();
        using var cliente = gestor.Cliente();
        var nome = NomeUnico();

        var criada = await cliente.PostAsJsonAsync("/categorias", new { nome, descricao = "Poda de árvores", prazoDias = 12 });
        Assert.Equal(201, criada.Codigo());
        var categoria = await criada.LerAsync<CategoriaResponse>();
        Assert.EndsWith($"/categorias/{categoria.Id}", criada.Headers.Location!.ToString());
        Assert.Equal(nome.ToUpperInvariant().Replace(' ', '_'), categoria.Codigo);

        var atualizada = await cliente.PutAsJsonAsync($"/categorias/{categoria.Id}", new { nome = nome + " Editada", prazoDias = 20 });
        Assert.Equal(200, atualizada.Codigo());
        Assert.Equal(20, (await atualizada.LerAsync<CategoriaResponse>()).PrazoDias);

        Assert.Equal(204, (await cliente.DeleteAsync($"/categorias/{categoria.Id}")).Codigo());
        Assert.Equal(404, (await cliente.GetAsync($"/categorias/{categoria.Id}")).Codigo());
    }

    [Fact]
    public async Task Nome_duplicado_com_caixa_diferente_retorna_409()
    {
        var gestor = await api.NovoGestorAsync();
        using var cliente = gestor.Cliente();
        var nome = NomeUnico();
        await cliente.PostAsJsonAsync("/categorias", new { nome, prazoDias = 3 });

        var duplicada = await cliente.PostAsJsonAsync("/categorias", new { nome = nome.ToUpperInvariant(), prazoDias = 3 });
        var mesmoCodigo = await cliente.PostAsJsonAsync("/categorias", new { nome = "Iluminacao", prazoDias = 3 });

        Assert.Equal(409, duplicada.Codigo());
        Assert.Equal(409, mesmoCodigo.Codigo());
    }

    [Theory]
    [InlineData(51, 5)]
    [InlineData(1, 5)]
    [InlineData(10, 0)]
    [InlineData(10, 1000)]
    public async Task Dados_invalidos_de_categoria_retornam_400(int tamanhoNome, int prazo)
    {
        var gestor = await api.NovoGestorAsync();
        using var cliente = gestor.Cliente();

        var resposta = await cliente.PostAsJsonAsync("/categorias", new { nome = new string('c', tamanhoNome), prazoDias = prazo });

        Assert.Equal(400, resposta.Codigo());
    }

    [Fact]
    public async Task Descricao_acima_de_200_caracteres_retorna_400()
    {
        var gestor = await api.NovoGestorAsync();
        using var cliente = gestor.Cliente();

        var resposta = await cliente.PostAsJsonAsync("/categorias", new { nome = NomeUnico(), descricao = new string('d', 201), prazoDias = 5 });

        Assert.Equal(400, resposta.Codigo());
    }

    [Fact]
    public async Task Categorias_padrao_nao_sao_excluidas_nem_tem_o_codigo_trocado()
    {
        var gestor = await api.NovoGestorAsync();
        using var cliente = gestor.Cliente();
        var buraco = (await (await cliente.GetAsync("/categorias")).LerAsync<List<CategoriaResponse>>()).Single(c => c.Codigo == "BURACO");

        var excluir = await cliente.DeleteAsync($"/categorias/{buraco.Id}");
        var renomear = await cliente.PutAsJsonAsync($"/categorias/{buraco.Id}", new { nome = "Cratera", prazoDias = 10 });
        var ajustarPrazo = await cliente.PutAsJsonAsync($"/categorias/{buraco.Id}", new { nome = "Buraco", descricao = buraco.Descricao, prazoDias = buraco.PrazoDias });

        Assert.Equal(409, excluir.Codigo());
        Assert.Equal(409, renomear.Codigo());
        Assert.Equal(200, ajustarPrazo.Codigo());
    }

    [Fact]
    public async Task Categoria_com_ocorrencias_nao_pode_ser_excluida()
    {
        var gestor = await api.NovoGestorAsync();
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = gestor.Cliente();
        var nome = NomeUnico();
        var categoria = await (await cliente.PostAsJsonAsync("/categorias", new { nome, prazoDias = 4 })).LerAsync<CategoriaResponse>();
        await cidadao.CriarOcorrenciaAsync(categoria: categoria.Codigo);

        var resposta = await cliente.DeleteAsync($"/categorias/{categoria.Id}");

        Assert.Equal(409, resposta.Codigo());
        Assert.Equal("A categoria tem ocorrências vinculadas e não pode ser excluída", (await resposta.ErroAsync()).Message);
    }

    [Fact]
    public async Task Categoria_inexistente_retorna_404()
    {
        var gestor = await api.NovoGestorAsync();
        using var cliente = gestor.Cliente();

        Assert.Equal(404, (await cliente.GetAsync("/categorias/99999")).Codigo());
        Assert.Equal(404, (await cliente.PutAsJsonAsync("/categorias/99999", new { nome = NomeUnico(), prazoDias = 2 })).Codigo());
        Assert.Equal(404, (await cliente.DeleteAsync("/categorias/99999")).Codigo());
    }
}

public sealed class EnderecoTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Cadastro_sem_endereco_usa_o_padrao_configurado()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = cidadao.Cliente();

        var endereco = await (await cliente.GetAsync("/usuarios/me/endereco")).LerAsync<EnderecoResponse>();

        Assert.Equal("Não informado", endereco.Logradouro);
        Assert.Equal("SP", endereco.Estado);
        Assert.Equal("00000000", endereco.Cep);
    }

    [Fact]
    public async Task Cadastro_com_endereco_completo_e_atualizacao_pelo_cidadao()
    {
        using var anonimo = api.Cliente();
        var email = $"casa.{Guid.NewGuid():N}@teste.com";
        var cadastro = await anonimo.PostAsJsonAsync("/auth/register", new
        {
            nome = "Morador", email, senha = "Senha@123",
            endereco = new { logradouro = "Rua A", numero = "10", bairro = "Centro", cidade = "Santos", estado = "sp", cep = "11010-000", latitude = -23.9608m }
        });
        Assert.Equal(201, cadastro.Codigo());
        var conta = await api.LoginAsync(email, "Senha@123");
        using var cliente = conta.Cliente();

        var atual = await (await cliente.GetAsync("/usuarios/me/endereco")).LerAsync<EnderecoResponse>();
        var alterado = await cliente.PutAsJsonAsync("/usuarios/me/endereco",
            new { logradouro = "Rua B", bairro = "Gonzaga", cidade = "Santos", estado = "SP", cep = "11060001" });

        Assert.Equal("Rua A, 10 - Centro - Santos/SP", atual.Formatado);
        Assert.Equal("11010000", atual.Cep);
        Assert.Equal(200, alterado.Codigo());
        var novo = await alterado.LerAsync<EnderecoResponse>();
        Assert.Equal("Gonzaga", novo.Bairro);
        Assert.Equal(atual.Id, novo.Id);
    }

    [Fact]
    public async Task Endereco_invalido_no_cadastro_retorna_400_com_campo_aninhado()
    {
        using var cliente = api.Cliente();

        var resposta = await cliente.PostAsJsonAsync("/auth/register", new
        {
            nome = "Morador", email = $"x.{Guid.NewGuid():N}@teste.com", senha = "Senha@123",
            endereco = new { logradouro = "Rua A", bairro = "Centro", cidade = "Santos", estado = "SPX", cep = "1" }
        });

        Assert.Equal(400, resposta.Codigo());
        var campos = (await resposta.ErroAsync()).Fields.Select(f => f.Field).ToList();
        Assert.Contains("endereco.estado", campos);
        Assert.Contains("endereco.cep", campos);
    }

    [Theory]
    [InlineData("١٢٣٤٥٦٧٨")]
    [InlineData("۱۲۳۴۵-۶۷۸")]
    public async Task Cep_com_digitos_que_nao_sao_ascii_e_recusado(string cep)
    {
        // O \d do .NET aceita dígitos de outros alfabetos, que seriam descartados e gravariam um CEP vazio.
        using var anonimo = api.Cliente();
        var conta = await api.NovoCidadaoAsync();
        using var cliente = conta.Cliente();

        var cadastro = await anonimo.PostAsJsonAsync("/auth/register", new
        {
            nome = "Morador", email = $"cep.{Guid.NewGuid():N}@teste.com", senha = "Senha@123",
            endereco = new { logradouro = "Rua A", bairro = "Centro", cidade = "Santos", estado = "SP", cep }
        });
        var alteracao = await cliente.PutAsJsonAsync("/usuarios/me/endereco",
            new { logradouro = "Rua B", bairro = "Centro", cidade = "Santos", estado = "SP", cep });

        Assert.Equal(400, cadastro.Codigo());
        Assert.Contains((await cadastro.ErroAsync()).Fields, f => f.Field == "endereco.cep");
        Assert.Equal(400, alteracao.Codigo());
        Assert.Contains((await alteracao.ErroAsync()).Fields, f => f.Field == "cep");
    }

    [Fact]
    public async Task Residencia_de_outro_cidadao_nao_pode_ser_consultada()
    {
        var dono = await api.NovoCidadaoAsync();
        var outro = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var enderecoId = await api.NoBancoAsync(db => db.Usuarios.Where(u => u.Id == dono.Id).Select(u => u.EnderecoId).SingleAsync());
        using var clienteDono = dono.Cliente();
        using var clienteOutro = outro.Cliente();
        using var clienteGestor = gestor.Cliente();

        Assert.Equal(200, (await clienteDono.GetAsync($"/enderecos/{enderecoId}")).Codigo());
        Assert.Equal(403, (await clienteOutro.GetAsync($"/enderecos/{enderecoId}")).Codigo());
        Assert.Equal(200, (await clienteGestor.GetAsync($"/enderecos/{enderecoId}")).Codigo());
        Assert.Equal(404, (await clienteGestor.GetAsync("/enderecos/999999")).Codigo());
    }

    [Fact]
    public async Task Local_de_ocorrencia_e_publico_para_cidadaos()
    {
        var dono = await api.NovoCidadaoAsync();
        var outro = await api.NovoCidadaoAsync();
        var criada = await dono.CriarOcorrenciaAsync();
        using var cliente = outro.Cliente();

        var endereco = await cliente.GetAsync($"/enderecos/{criada.EnderecoId}");

        Assert.Equal(200, endereco.Codigo());
        // E pode ser reutilizado para registrar outro problema no mesmo local.
        var nova = await outro.CriarOcorrenciaAsync(extras: new Dictionary<string, string> { ["enderecoId"] = criada.EnderecoId.ToString() });
        Assert.Equal(criada.EnderecoId, nova.EnderecoId);
    }

    [Fact]
    public async Task Gestor_nao_tem_endereco_de_cadastro()
    {
        var gestor = await api.NovoGestorAsync();
        using var cliente = gestor.Cliente();

        Assert.Equal(403, (await cliente.GetAsync("/usuarios/me/endereco")).Codigo());
    }
}
