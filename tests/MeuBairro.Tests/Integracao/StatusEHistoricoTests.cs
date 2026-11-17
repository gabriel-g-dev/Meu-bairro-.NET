using System.Net.Http.Json;
using MeuBairro.Api.Contratos;
using MeuBairro.Api.Dominio;
using MeuBairro.Tests.Infra;
using Microsoft.EntityFrameworkCore;

namespace MeuBairro.Tests.Integracao;

public sealed class StatusEHistoricoTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Cidadao_nao_altera_status_nem_prioridade_e_nada_muda()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();
        using var cliente = cidadao.Cliente();

        var status = await cliente.PatchAsJsonAsync($"/ocorrencias/{criada.Id}/status", new { status = "RESOLVIDA" });
        var prioridade = await cliente.PatchAsJsonAsync($"/ocorrencias/{criada.Id}/prioridade", new { prioridade = "ALTA" });

        Assert.Equal(403, status.Codigo());
        Assert.Equal(403, prioridade.Codigo());
        var atual = await (await cliente.GetAsync($"/ocorrencias/{criada.Id}")).LerAsync<OcorrenciaResponse>();
        Assert.Equal(StatusSolicitacao.ABERTA, atual.Status);
        Assert.Equal(Prioridade.MEDIA, atual.Prioridade);
    }

    [Fact]
    public async Task Transicoes_validas_ficam_no_historico_com_tipo_e_status()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();

        Assert.Equal(200, (await gestor.AlterarStatusAsync(criada.Id, "EM_ANALISE", "Equipe a caminho")).Codigo());
        Assert.Equal(200, (await gestor.AlterarStatusAsync(criada.Id, "ABERTA")).Codigo());
        Assert.Equal(200, (await gestor.AlterarStatusAsync(criada.Id, "em_analise")).Codigo());
        var resolvida = await gestor.ResolverAsync(criada.Id);

        Assert.Equal(StatusSolicitacao.RESOLVIDA, resolvida.Status);
        Assert.NotNull(resolvida.DataConclusao);
        Assert.Equal(gestor.Id, resolvida.ResponsavelId);
        Assert.False(resolvida.Atrasada);

        using var cliente = cidadao.Cliente();
        var historico = await (await cliente.GetAsync($"/ocorrencias/{criada.Id}/historico")).LerAsync<List<HistoricoResponse>>();
        Assert.Equal(["CRIACAO", "STATUS", "STATUS", "STATUS", "STATUS"], historico.Select(h => h.Tipo));
        Assert.Equal(
            [StatusSolicitacao.ABERTA, StatusSolicitacao.EM_ANALISE, StatusSolicitacao.ABERTA, StatusSolicitacao.EM_ANALISE, StatusSolicitacao.RESOLVIDA],
            historico.Select(h => h.Status));
        Assert.Equal("Equipe a caminho", historico[1].Descricao);
        Assert.Equal("Status alterado de EM_ANALISE para RESOLVIDA", historico[^1].Descricao);
        Assert.Equal("Zeladoria", historico[^1].SetorResponsavel);
        Assert.Equal(historico.Count, historico.Select(h => h.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("ABERTA")]
    [InlineData("EM_ANALISE")]
    [InlineData("RESOLVIDA")]
    public async Task Ocorrencia_resolvida_nao_muda_mais_de_status(string novo)
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();
        await gestor.ResolverAsync(criada.Id);

        var resposta = await gestor.AlterarStatusAsync(criada.Id, novo);

        Assert.Equal(409, resposta.Codigo());
        Assert.Equal("A ocorrência já foi resolvida e não pode mudar de status", (await resposta.ErroAsync()).Message);
        Assert.Equal(1, await api.NoBancoAsync(db => db.Andamentos.CountAsync(a => a.SolicitacaoId == criada.Id)));
    }

    [Fact]
    public async Task Status_repetido_retorna_409_e_nao_gera_andamento()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();

        var resposta = await gestor.AlterarStatusAsync(criada.Id, "ABERTA");

        Assert.Equal(409, resposta.Codigo());
        Assert.Equal("A ocorrência já está com o status ABERTA", (await resposta.ErroAsync()).Message);
        Assert.Equal(0, await api.NoBancoAsync(db => db.Andamentos.CountAsync(a => a.SolicitacaoId == criada.Id)));
    }

    [Theory]
    [InlineData("{\"status\":\"CONCLUIDA\"}", "status")]
    [InlineData("{\"status\":null}", "status")]
    [InlineData("{}", "status")]
    [InlineData("{\"status\":\"1\"}", "status")]
    public async Task Status_invalido_retorna_400(string corpo, string campo)
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();
        using var cliente = gestor.Cliente();

        var resposta = await cliente.PatchAsync($"/ocorrencias/{criada.Id}/status",
            new StringContent(corpo, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(400, resposta.Codigo());
        Assert.Contains((await resposta.ErroAsync()).Fields, f => f.Field == campo);
    }

    [Fact]
    public async Task Observacao_acima_de_300_caracteres_retorna_400()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();

        var resposta = await gestor.AlterarStatusAsync(criada.Id, "EM_ANALISE", new string('o', 301));
        var limite = await gestor.AlterarStatusAsync(criada.Id, "EM_ANALISE", new string('o', 300));

        Assert.Equal(400, resposta.Codigo());
        Assert.Equal(200, limite.Codigo());
    }

    [Fact]
    public async Task Gestor_define_prioridade_e_ajuste_entra_no_historico()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();
        using var cliente = gestor.Cliente();

        var resposta = await cliente.PatchAsJsonAsync($"/ocorrencias/{criada.Id}/prioridade", new { prioridade = "ALTA" });
        var invalida = await cliente.PatchAsJsonAsync($"/ocorrencias/{criada.Id}/prioridade", new { prioridade = "URGENTE" });
        var ausente = await cliente.PatchAsJsonAsync($"/ocorrencias/{criada.Id}/prioridade", new { prioridade = (string?)null });

        Assert.Equal(200, resposta.Codigo());
        Assert.Equal(Prioridade.ALTA, (await resposta.LerAsync<OcorrenciaResponse>()).Prioridade);
        Assert.Equal(400, invalida.Codigo());
        Assert.Equal(400, ausente.Codigo());
        var historico = await (await cliente.GetAsync($"/ocorrencias/{criada.Id}/historico")).LerAsync<List<HistoricoResponse>>();
        Assert.Equal(2, historico.Count);
        Assert.Equal("ANDAMENTO", historico[1].Tipo);
        Assert.Equal("Prioridade definida pelo gestor: de MEDIA para ALTA", historico[1].Descricao);
    }

    [Fact]
    public async Task Prioridade_de_ocorrencia_resolvida_nao_muda()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();
        await gestor.ResolverAsync(criada.Id);
        using var cliente = gestor.Cliente();

        Assert.Equal(409, (await cliente.PatchAsJsonAsync($"/ocorrencias/{criada.Id}/prioridade", new { prioridade = "BAIXA" })).Codigo());
    }

    [Fact]
    public async Task Gestor_registra_andamento_sem_mudar_status()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();
        using var clienteGestor = gestor.Cliente();
        using var clienteCidadao = cidadao.Cliente();

        var resposta = await clienteGestor.PostAsJsonAsync($"/ocorrencias/{criada.Id}/andamentos",
            new { descricao = "Vistoria marcada para amanhã" });
        var doCidadao = await clienteCidadao.PostAsJsonAsync($"/ocorrencias/{criada.Id}/andamentos", new { descricao = "Tentativa" });
        var vazio = await clienteGestor.PostAsJsonAsync($"/ocorrencias/{criada.Id}/andamentos", new { descricao = " " });

        Assert.Equal(201, resposta.Codigo());
        Assert.Equal(403, doCidadao.Codigo());
        Assert.Equal(400, vazio.Codigo());
        var historico = await (await clienteCidadao.GetAsync($"/ocorrencias/{criada.Id}/historico")).LerAsync<List<HistoricoResponse>>();
        Assert.Equal("Vistoria marcada para amanhã", historico[^1].Descricao);
        Assert.Equal(StatusSolicitacao.ABERTA, historico[^1].Status);
    }

    [Fact]
    public async Task Alteracoes_simultaneas_de_status_aplicam_apenas_uma()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var criada = await cidadao.CriarOcorrenciaAsync();

        var respostas = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => gestor.AlterarStatusAsync(criada.Id, "RESOLVIDA")));

        Assert.Equal(1, respostas.Count(r => r.Codigo() == 200));
        Assert.All(respostas.Where(r => r.Codigo() != 200), r => Assert.Equal(409, r.Codigo()));
        Assert.Equal(1, await api.NoBancoAsync(db => db.Andamentos.CountAsync(a => a.SolicitacaoId == criada.Id)));
    }

    [Fact]
    public async Task Status_de_ocorrencia_inexistente_retorna_404()
    {
        var gestor = await api.NovoGestorAsync();

        Assert.Equal(404, (await gestor.AlterarStatusAsync(987654, "EM_ANALISE")).Codigo());
    }
}
