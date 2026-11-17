using MeuBairro.Api.Configuracao;
using MeuBairro.Api.Contratos;
using MeuBairro.Api.Dados;
using MeuBairro.Api.Dominio;
using MeuBairro.Api.Infra;
using MeuBairro.Api.Servicos;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MeuBairro.Tests.Unitarios;

public sealed class CategoriaCodigoTests
{
    [Theory]
    [InlineData("Buraco", "BURACO")]
    [InlineData("Iluminação", "ILUMINACAO")]
    [InlineData("Calçada", "CALCADA")]
    [InlineData("  lixo  ", "LIXO")]
    [InlineData("Iluminação pública", "ILUMINACAO_PUBLICA")]
    [InlineData("Poda / Árvores!!", "PODA_ARVORES")]
    [InlineData("Sinalização de trânsito danificada", "SINALIZACAO_DE_TRANS")]
    [InlineData("???", "CATEGORIA")]
    public void Gera_codigo_sem_acentos_em_maiusculas(string nome, string esperado) =>
        Assert.Equal(esperado, CategoriaCodigo.Gerar(nome));

    [Fact]
    public void Apelido_configurado_tem_prioridade_sobre_o_nome()
    {
        var apelidos = new Dictionary<string, string> { ["Iluminação pública"] = "iluminacao" };

        Assert.Equal("ILUMINACAO", CategoriaCodigo.Gerar("ILUMINAÇÃO PÚBLICA", apelidos));
        Assert.Equal("BURACO", CategoriaCodigo.Gerar("Buraco", apelidos));
    }

    [Fact]
    public void Codigos_padrao_correspondem_as_categorias_iniciais()
    {
        string[] nomes = ["Buraco", "Iluminação", "Lixo", "Alagamento", "Calçada", "Outro"];

        Assert.Equal(CategoriaCodigo.CodigosPadrao, nomes.Select(n => CategoriaCodigo.Gerar(n)));
    }
}

public sealed class SolicitacaoTests
{
    [Theory]
    [InlineData(StatusSolicitacao.ABERTA, StatusSolicitacao.EM_ANALISE)]
    [InlineData(StatusSolicitacao.ABERTA, StatusSolicitacao.RESOLVIDA)]
    [InlineData(StatusSolicitacao.EM_ANALISE, StatusSolicitacao.ABERTA)]
    [InlineData(StatusSolicitacao.EM_ANALISE, StatusSolicitacao.RESOLVIDA)]
    public void Transicoes_permitidas_nao_lancam_excecao(StatusSolicitacao atual, StatusSolicitacao novo) =>
        Solicitacao.ValidarTransicao(atual, novo);

    [Theory]
    [InlineData(StatusSolicitacao.RESOLVIDA, StatusSolicitacao.ABERTA)]
    [InlineData(StatusSolicitacao.RESOLVIDA, StatusSolicitacao.EM_ANALISE)]
    [InlineData(StatusSolicitacao.RESOLVIDA, StatusSolicitacao.RESOLVIDA)]
    [InlineData(StatusSolicitacao.ABERTA, StatusSolicitacao.ABERTA)]
    [InlineData(StatusSolicitacao.EM_ANALISE, StatusSolicitacao.EM_ANALISE)]
    public void Transicoes_proibidas_lancam_regra_de_negocio(StatusSolicitacao atual, StatusSolicitacao novo)
    {
        var erro = Assert.Throws<RegraNegocioException>(() => Solicitacao.ValidarTransicao(atual, novo));
        Assert.Equal(409, erro.Status);
    }

    [Fact]
    public void Prazo_e_atraso_seguem_o_prazo_da_categoria()
    {
        var abertura = new DateTime(2026, 11, 1, 10, 0, 0);

        Assert.Equal(new DateTime(2026, 11, 8, 10, 0, 0), Solicitacao.PrazoEstimado(abertura, 7));
        Assert.False(Solicitacao.EstaAtrasada(StatusSolicitacao.ABERTA, abertura, 7, new DateTime(2026, 11, 8, 10, 0, 0)));
        Assert.True(Solicitacao.EstaAtrasada(StatusSolicitacao.EM_ANALISE, abertura, 7, new DateTime(2026, 11, 8, 10, 0, 1)));
        Assert.False(Solicitacao.EstaAtrasada(StatusSolicitacao.RESOLVIDA, abertura, 7, new DateTime(2027, 1, 1)));
    }

    [Fact]
    public void Endereco_compara_e_copia_todos_os_campos()
    {
        var a = new Endereco { Logradouro = "Rua A", Numero = "1", Bairro = "Centro", Cidade = "SP", Estado = "SP", Cep = "01001000", Latitude = 1.5m };
        var b = new Endereco { Id = 99 };

        b.CopiarDe(a);

        Assert.True(a.MesmosDados(b));
        Assert.Equal(99, b.Id);
        b.Latitude = 1.6m;
        Assert.False(a.MesmosDados(b));
    }
}

public sealed class MapaCodigosTests
{
    [Fact]
    public void Codigos_padrao_convertem_nos_dois_sentidos()
    {
        var mapa = new MapaCodigos<StatusSolicitacao>(new CodigosOptions().Status);

        Assert.Equal("A", mapa.ParaCodigo(StatusSolicitacao.ABERTA));
        Assert.Equal("E", mapa.ParaCodigo(StatusSolicitacao.EM_ANALISE));
        Assert.Equal("R", mapa.ParaCodigo(StatusSolicitacao.RESOLVIDA));
        Assert.Equal(StatusSolicitacao.RESOLVIDA, mapa.ParaValor("R "));
    }

    [Fact]
    public void Codigos_personalizados_sao_respeitados_pelo_conversor_do_ef()
    {
        var mapa = new MapaCodigos<Prioridade>(new Dictionary<string, string> { ["baixa"] = "1", ["MEDIA"] = "2", ["ALTA"] = "3" });
        var conversor = mapa.Conversor();

        Assert.Equal("3", conversor.ConvertToProvider(Prioridade.ALTA));
        Assert.Equal(Prioridade.BAIXA, conversor.ConvertFromProvider("1"));
    }

    [Theory]
    [InlineData("A", "A", "R")]
    [InlineData("AB", "E", "R")]
    [InlineData("", "E", "R")]
    public void Configuracao_invalida_e_rejeitada(string aberta, string emAnalise, string resolvida) =>
        Assert.Throws<InvalidOperationException>(() => new MapaCodigos<StatusSolicitacao>(new Dictionary<string, string>
        {
            ["ABERTA"] = aberta, ["EM_ANALISE"] = emAnalise, ["RESOLVIDA"] = resolvida
        }));

    [Fact]
    public void Configuracao_incompleta_e_rejeitada() =>
        Assert.Throws<InvalidOperationException>(() =>
            new MapaCodigos<StatusSolicitacao>(new Dictionary<string, string> { ["ABERTA"] = "A" }));

    [Fact]
    public void Codigo_desconhecido_vindo_do_banco_gera_erro_claro()
    {
        var mapa = new MapaCodigos<StatusSolicitacao>(new CodigosOptions().Status);

        var erro = Assert.Throws<InvalidOperationException>(() => mapa.ParaValor("X"));
        Assert.Contains("Banco:Codigos", erro.Message);
    }
}

public sealed class ValidacaoTests
{
    [Fact]
    public void Nomes_de_campos_sao_devolvidos_em_camel_case_inclusive_aninhados()
    {
        var erros = Validacao.Validar(new CadastroUsuarioRequest
        {
            Nome = "Ana", Email = "ana@teste.com", Senha = "Senha@123",
            Endereco = new EnderecoRequest { Logradouro = "Rua", Bairro = "B", Cidade = "C", Estado = "S", Cep = "1" }
        });

        Assert.Equal(["endereco.cep", "endereco.estado"], erros.Select(e => e.Field).Order());
    }

    [Fact]
    public void Tamanho_desconsidera_espacos_nas_pontas()
    {
        var form = new OcorrenciaForm { Titulo = "   abcd   ", Descricao = "descrição suficiente", Categoria = "BURACO", Endereco = "Rua" };

        Assert.Contains(Validacao.Validar(form), e => e.Field == "titulo");
    }

    [Theory]
    [InlineData("-23.5505199", -23.5505199)]
    [InlineData("-23,55", -23.55)]
    [InlineData(" 10 ", 10)]
    public void Coordenadas_aceitam_ponto_ou_virgula(string texto, double esperado)
    {
        Assert.True(Coordenadas.TentarLer(texto, out var valor));
        Assert.Equal((decimal)esperado, valor);
    }

    [Fact]
    public void Coordenadas_sao_arredondadas_para_number_9_6() =>
        Assert.Equal(-23.550520m, Coordenadas.Arredondar(-23.5505199m));

    [Fact]
    public void Validacoes_de_status_e_prioridade_recusam_numeros()
    {
        Assert.Contains(Validacao.Validar(new AtualizacaoStatusRequest { Status = "2" }), e => e.Field == "status");
        Assert.Empty(Validacao.Validar(new AtualizacaoStatusRequest { Status = "em_analise" }));
        Assert.Contains(Validacao.Validar(new AtualizacaoPrioridadeRequest { Prioridade = "URGENTE" }), e => e.Field == "prioridade");
    }

    [Fact]
    public void Senha_considera_o_limite_de_72_bytes_do_bcrypt()
    {
        var acentuada = new CadastroUsuarioRequest { Nome = "A", Email = "a@b.com", Senha = new string('ç', 37) };
        var ascii = new CadastroUsuarioRequest { Nome = "A", Email = "a@b.com", Senha = new string('c', 60) };

        Assert.Contains(Validacao.Validar(acentuada), e => e.Field == "senha");
        Assert.Empty(Validacao.Validar(ascii));
    }
}

public sealed class OrdenacaoTests
{
    [Fact]
    public void Sem_parametro_ordena_pela_abertura_mais_recente() =>
        Assert.Equal([("abertura", true)], Ordenacao.Ler(null));

    [Fact]
    public void Aceita_varios_campos_no_formato_campo_direcao() =>
        Assert.Equal([("status", false), ("id", true)], Ordenacao.Ler(["status", "id,DESC"]));

    [Theory]
    [InlineData("senha,asc")]
    [InlineData("id,cima")]
    public void Campo_ou_direcao_invalidos_geram_400(string sort) =>
        Assert.Equal(400, Assert.Throws<RequisicaoInvalidaException>(() => Ordenacao.Ler([sort])).Status);
}

public sealed class RelogioTests
{
    [Fact]
    public void Usa_o_fuso_configurado_e_descarta_fracoes_de_segundo()
    {
        var tempo = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 15, 30, 45, 789, TimeSpan.Zero));
        var relogio = new Relogio(tempo, Options.Create(new AplicacaoOptions { FusoHorario = "America/Sao_Paulo" }));

        var agora = relogio.Agora();

        Assert.Equal(new DateTime(2026, 11, 17, 12, 30, 45), agora);
        Assert.Equal(DateTimeKind.Unspecified, agora.Kind);
        Assert.Equal(tempo.GetUtcNow().ToUnixTimeMilliseconds(), relogio.AgoraUnixMs());
    }

    [Fact]
    public void Fuso_inexistente_cai_para_utc()
    {
        var tempo = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero));

        var relogio = new Relogio(tempo, Options.Create(new AplicacaoOptions { FusoHorario = "Lugar/Inexistente" }));

        Assert.Equal(new DateTime(2026, 1, 1, 8, 0, 0), relogio.Agora());
    }
}
