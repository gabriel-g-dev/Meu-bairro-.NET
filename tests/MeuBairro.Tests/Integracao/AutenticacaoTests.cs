using System.Net.Http.Headers;
using System.Net.Http.Json;
using MeuBairro.Api.Contratos;
using MeuBairro.Api.Dominio;
using MeuBairro.Tests.Infra;
using Microsoft.EntityFrameworkCore;

namespace MeuBairro.Tests.Integracao;

public sealed class AutenticacaoTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private static string Email() => $"pessoa.{Guid.NewGuid():N}@teste.com";

    [Fact]
    public async Task Cadastro_valido_retorna_201_com_perfil_cidadao_e_sem_senha()
    {
        using var cliente = api.Cliente();
        var email = Email();

        var resposta = await cliente.PostAsJsonAsync("/auth/register", new { nome = "  Maria Souza  ", email, senha = "Senha@123" });

        Assert.Equal(201, resposta.Codigo());
        var json = await resposta.Content.ReadAsStringAsync();
        Assert.DoesNotContain("senha", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$2", json);
        var usuario = await resposta.LerAsync<UsuarioResponse>();
        Assert.Equal("Maria Souza", usuario.Nome);
        Assert.Equal(Perfil.CIDADAO, usuario.Perfil);

        var hash = await api.NoBancoAsync(db => db.Usuarios.Where(u => u.Id == usuario.Id).Select(u => u.Senha).SingleAsync());
        Assert.StartsWith("$2", hash);
        Assert.NotEqual("Senha@123", hash);
    }

    [Fact]
    public async Task Cadastro_sem_campos_retorna_400_listando_cada_campo()
    {
        using var cliente = api.Cliente();

        var resposta = await cliente.PostAsJsonAsync("/auth/register", new { });

        Assert.Equal(400, resposta.Codigo());
        var erro = await resposta.ErroAsync();
        Assert.Equal("Existem campos inválidos na requisição", erro.Message);
        Assert.Equal(["email", "nome", "senha"], erro.Fields.Select(f => f.Field).Distinct().Order());
    }

    [Theory]
    [InlineData("nome", 101, "pessoa@teste.com", "Senha@123")]
    [InlineData("nome", 0, "pessoa@teste.com", "Senha@123")]
    [InlineData("email", 10, "email-sem-arroba", "Senha@123")]
    [InlineData("email", 10, "maria@", "Senha@123")]
    [InlineData("senha", 10, "pessoa@teste.com", "12345")]
    [InlineData("senha", 10, "pessoa@teste.com", "1234567890123456789012345678901234567890123456789012345678901")]
    [InlineData("senha", 10, "pessoa@teste.com", "çççççççççççççççççççççççççççççççççççççççç")]
    public async Task Cadastro_com_campo_invalido_retorna_400_no_campo(string campo, int tamanhoNome, string email, string senha)
    {
        using var cliente = api.Cliente();

        var resposta = await cliente.PostAsJsonAsync("/auth/register", new { nome = new string('a', tamanhoNome), email, senha });

        Assert.Equal(400, resposta.Codigo());
        Assert.Contains((await resposta.ErroAsync()).Fields, f => f.Field == campo);
    }

    [Theory]
    [InlineData(100, 6)]
    [InlineData(1, 60)]
    public async Task Cadastro_nos_limites_de_tamanho_e_aceito(int tamanhoNome, int tamanhoSenha)
    {
        using var cliente = api.Cliente();

        var resposta = await cliente.PostAsJsonAsync("/auth/register",
            new { nome = new string('n', tamanhoNome), email = Email(), senha = new string('s', tamanhoSenha) });

        Assert.Equal(201, resposta.Codigo());
    }

    [Fact]
    public async Task Email_duplicado_retorna_409_inclusive_com_caixa_diferente_e_preserva_conta_original()
    {
        using var cliente = api.Cliente();
        var email = Email();
        Assert.Equal(201, (await cliente.PostAsJsonAsync("/auth/register", new { nome = "Original", email, senha = "Senha@123" })).Codigo());

        var mesmo = await cliente.PostAsJsonAsync("/auth/register", new { nome = "Copia", email, senha = "Outra@123" });
        var maiusculas = await cliente.PostAsJsonAsync("/auth/register", new { nome = "Copia", email = email.ToUpperInvariant(), senha = "Outra@123" });

        Assert.Equal(409, mesmo.Codigo());
        Assert.Equal(409, maiusculas.Codigo());
        Assert.Equal("Já existe um usuário cadastrado com este e-mail", (await maiusculas.ErroAsync()).Message);
        var conta = await api.LoginAsync(email, "Senha@123");
        Assert.True(conta.Id > 0);
    }

    [Fact]
    public async Task Email_e_gravado_em_minusculas_e_login_aceita_qualquer_caixa()
    {
        using var cliente = api.Cliente();
        var email = $"Maria.{Guid.NewGuid():N}@Teste.COM";

        var cadastro = await (await cliente.PostAsJsonAsync("/auth/register", new { nome = "Maria", email = $"  {email}  ", senha = "Senha@123" }))
            .LerAsync<UsuarioResponse>();
        var login = await cliente.PostAsJsonAsync("/auth/login", new { email = email.ToUpperInvariant(), senha = "Senha@123" });

        Assert.Equal(email.ToLowerInvariant(), cadastro.Email);
        Assert.Equal(200, login.Codigo());
    }

    [Fact]
    public async Task Senha_incorreta_e_email_inexistente_retornam_401_com_a_mesma_mensagem()
    {
        var conta = await api.NovoCidadaoAsync();
        using var cliente = api.Cliente();

        var senhaErrada = await cliente.PostAsJsonAsync("/auth/login", new { email = conta.Email, senha = "Errada@123" });
        var inexistente = await cliente.PostAsJsonAsync("/auth/login", new { email = Email(), senha = "Errada@123" });

        Assert.Equal(401, senhaErrada.Codigo());
        Assert.Equal(401, inexistente.Codigo());
        var erroSenha = await senhaErrada.ErroAsync();
        Assert.Equal("E-mail ou senha inválidos", erroSenha.Message);
        Assert.Equal(erroSenha.Message, (await inexistente.ErroAsync()).Message);
    }

    [Fact]
    public async Task Login_com_dados_ausentes_retorna_400()
    {
        using var cliente = api.Cliente();

        var resposta = await cliente.PostAsJsonAsync("/auth/login", new { email = "", senha = "" });

        Assert.Equal(400, resposta.Codigo());
    }

    [Fact]
    public async Task Rota_protegida_sem_token_retorna_401_com_cabecalho_bearer()
    {
        using var cliente = api.Cliente();

        var resposta = await cliente.GetAsync("/usuarios/me");

        Assert.Equal(401, resposta.Codigo());
        Assert.Equal("Bearer", resposta.Headers.WwwAuthenticate.Single().Scheme);
        Assert.StartsWith("Autenticação necessária", (await resposta.ErroAsync()).Message);
    }

    [Theory]
    [InlineData("isto-nao-e-um-jwt")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.e30.assinatura-falsa")]
    public async Task Token_malformado_retorna_401(string token)
    {
        using var cliente = api.Cliente(token);

        var resposta = await cliente.GetAsync("/usuarios/me");

        Assert.Equal(401, resposta.Codigo());
        Assert.Equal("Token inválido ou expirado", (await resposta.ErroAsync()).Message);
    }

    [Fact]
    public async Task Token_assinado_com_outra_chave_retorna_401()
    {
        var conta = await api.NovoCidadaoAsync();
        using var cliente = api.Cliente(Tokens.Criar(conta.Id, segredo: "outra-chave-qualquer-com-mais-de-32-caracteres!!"));

        Assert.Equal(401, (await cliente.GetAsync("/usuarios/me")).Codigo());
    }

    [Fact]
    public async Task Token_expirado_retorna_401()
    {
        var conta = await api.NovoCidadaoAsync();
        var token = Tokens.Criar(conta.Id, inicio: DateTime.UtcNow.AddHours(-3), expira: DateTime.UtcNow.AddHours(-1));
        using var cliente = api.Cliente(token);

        var resposta = await cliente.GetAsync("/usuarios/me");

        Assert.Equal(401, resposta.Codigo());
        Assert.Equal("Token inválido ou expirado", (await resposta.ErroAsync()).Message);
    }

    [Theory]
    [InlineData("outro-emissor", Tokens.Audiencia)]
    [InlineData(Tokens.Emissor, "outra-audiencia")]
    public async Task Token_com_emissor_ou_audiencia_errados_retorna_401(string emissor, string audiencia)
    {
        var conta = await api.NovoCidadaoAsync();
        using var cliente = api.Cliente(Tokens.Criar(conta.Id, emissor: emissor, audiencia: audiencia));

        Assert.Equal(401, (await cliente.GetAsync("/usuarios/me")).Codigo());
    }

    [Fact]
    public async Task Token_com_perfil_adulterado_para_gestor_retorna_401()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var partes = cidadao.Token.Split('.');
        var payload = System.Text.Encoding.UTF8.GetString(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(partes[1]));
        Assert.Contains("\"perfil\":\"CIDADAO\"", payload);
        var adulterado = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(payload.Replace("\"CIDADAO\"", "\"GESTOR\""));
        using var cliente = api.Cliente($"{partes[0]}.{adulterado}.{partes[2]}");

        var resposta = await cliente.GetAsync("/ocorrencias/resumo");

        Assert.Equal(401, resposta.Codigo());
        Assert.Equal("Token inválido ou expirado", (await resposta.ErroAsync()).Message);
    }

    [Fact]
    public async Task Token_sem_assinatura_com_alg_none_retorna_401()
    {
        var gestor = await api.NovoGestorAsync();
        using var cliente = api.Cliente(Tokens.SemAssinatura(gestor.Id));

        Assert.Equal(401, (await cliente.GetAsync("/ocorrencias/resumo")).Codigo());
    }

    [Fact]
    public async Task Token_valido_de_conta_removida_retorna_401()
    {
        var conta = await api.NovoCidadaoAsync();
        await api.NoBancoAsync(db => db.Usuarios.Where(u => u.Id == conta.Id).ExecuteDeleteAsync());
        using var cliente = conta.Cliente();

        var resposta = await cliente.GetAsync("/usuarios/me");

        Assert.Equal(401, resposta.Codigo());
        Assert.Equal("O usuário do token não existe mais", (await resposta.ErroAsync()).Message);
    }

    [Fact]
    public async Task Token_de_gestor_sem_funcionario_correspondente_retorna_401()
    {
        // Mesmo com assinatura válida, o perfil GESTOR exige que o ID exista em FUNCIONARIOS.
        var maiorFuncionario = await api.NoBancoAsync(db => db.Funcionarios.Select(f => (long?)f.Id).MaxAsync()) ?? 0;
        using var cliente = api.Cliente(Tokens.Criar(maiorFuncionario + 1000, perfil: "GESTOR"));

        Assert.Equal(401, (await cliente.GetAsync("/ocorrencias/resumo")).Codigo());
    }

    [Fact]
    public async Task Gestor_autentica_pela_tabela_de_funcionarios()
    {
        var gestor = await api.NovoGestorAsync();
        using var cliente = gestor.Cliente();

        var perfil = await (await cliente.GetAsync("/usuarios/me")).LerAsync<PerfilResponse>();

        Assert.Equal(Perfil.GESTOR, perfil.Perfil);
        Assert.Equal("Zeladoria", perfil.Setor);
        Assert.Null(perfil.EnderecoId);
    }

    [Fact]
    public async Task Login_restrito_a_gestor_nao_aceita_cidadao()
    {
        var cidadao = await api.NovoCidadaoAsync();
        using var cliente = api.Cliente();

        var resposta = await cliente.PostAsJsonAsync("/auth/login", new { email = cidadao.Email, senha = ApiFactory.SenhaPadrao, perfil = "GESTOR" });

        Assert.Equal(401, resposta.Codigo());
    }

    [Fact]
    public async Task Somente_gestor_cadastra_gestor_e_o_novo_gestor_consegue_entrar()
    {
        var cidadao = await api.NovoCidadaoAsync();
        var gestor = await api.NovoGestorAsync();
        var email = Email();
        var dados = new { nome = "Novo Gestor", email, senha = "Gestor@123", setor = "Iluminação", cargo = "Supervisor" };

        using var clienteCidadao = cidadao.Cliente();
        using var clienteGestor = gestor.Cliente();
        var negado = await clienteCidadao.PostAsJsonAsync("/usuarios/gestores", dados);
        var criado = await clienteGestor.PostAsJsonAsync("/usuarios/gestores", dados);

        Assert.Equal(403, negado.Codigo());
        Assert.Equal("Você não tem permissão para acessar este recurso", (await negado.ErroAsync()).Message);
        Assert.Equal(201, criado.Codigo());
        var novo = await api.LoginAsync(email, "Gestor@123");
        using var clienteNovo = novo.Cliente();
        Assert.Equal(200, (await clienteNovo.GetAsync("/ocorrencias/resumo")).Codigo());
    }

    [Fact]
    public async Task Cadastro_publico_ignora_perfil_e_id_enviados_no_corpo()
    {
        using var cliente = api.Cliente();
        var email = Email();

        var resposta = await cliente.PostAsJsonAsync("/auth/register",
            new { nome = "Tentativa", email, senha = "Senha@123", perfil = "GESTOR", id = 999999, role = "GESTOR" });

        Assert.Equal(201, resposta.Codigo());
        var conta = await api.LoginAsync(email, "Senha@123");
        Assert.NotEqual(999999, conta.Id);
        using var autenticado = conta.Cliente();
        var perfil = await (await autenticado.GetAsync("/usuarios/me")).LerAsync<PerfilResponse>();
        Assert.Equal(Perfil.CIDADAO, perfil.Perfil);
        Assert.Equal(403, (await autenticado.GetAsync("/ocorrencias/resumo")).Codigo());
    }

    [Fact]
    public async Task Atualizacao_de_perfil_altera_so_nome_e_telefone()
    {
        var conta = await api.NovoCidadaoAsync();
        using var cliente = conta.Cliente();

        var resposta = await cliente.PutAsJsonAsync("/usuarios/me",
            new { nome = "Nome Novo", telefone = "(11) 98888-7777", email = "invasor@teste.com", perfil = "GESTOR" });

        Assert.Equal(200, resposta.Codigo());
        var perfil = await resposta.LerAsync<PerfilResponse>();
        Assert.Equal("Nome Novo", perfil.Nome);
        Assert.Equal("(11) 98888-7777", perfil.Telefone);
        Assert.Equal(conta.Email, perfil.Email);
        Assert.Equal(Perfil.CIDADAO, perfil.Perfil);
    }

    [Fact]
    public async Task Rota_publica_com_metodo_ou_tipo_de_conteudo_errado_responde_o_erro_real_e_nao_401()
    {
        using var cliente = api.Cliente();

        // Ex.: corpo "raw → Text" no Postman, que envia text/plain.
        var textoPuro = await cliente.PostAsync("/auth/login",
            new StringContent("{\"email\":\"a@teste.com\",\"senha\":\"Senha@123\"}", System.Text.Encoding.UTF8, "text/plain"));
        var metodo = await cliente.GetAsync("/auth/login");
        var inexistente = await cliente.GetAsync("/rota/que/nao/existe");

        Assert.Equal(415, textoPuro.Codigo());
        Assert.Equal("Tipo de conteúdo não suportado nesta rota", (await textoPuro.ErroAsync()).Message);
        Assert.Equal(405, metodo.Codigo());
        Assert.Equal("Método HTTP não suportado nesta rota", (await metodo.ErroAsync()).Message);
        Assert.Equal(404, inexistente.Codigo());
        // As rotas mapeadas continuam exigindo token.
        Assert.Equal(401, (await cliente.GetAsync("/ocorrencias")).Codigo());
        Assert.Equal(401, (await cliente.PostAsync("/ocorrencias", Dados.Ocorrencia())).Codigo());
    }

    [Fact]
    public async Task Cabecalho_authorization_de_outro_esquema_nao_autentica()
    {
        var conta = await api.NovoCidadaoAsync();
        using var cliente = api.Cliente();
        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", conta.Token);

        Assert.Equal(401, (await cliente.GetAsync("/usuarios/me")).Codigo());
    }

    [Fact]
    public async Task Cadastros_simultaneos_com_o_mesmo_email_criam_uma_unica_conta()
    {
        var email = Email();
        using var cliente = api.Cliente();

        var respostas = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            cliente.PostAsJsonAsync("/auth/register", new { nome = "Concorrente", email, senha = "Senha@123" })));

        Assert.Equal(1, respostas.Count(r => r.Codigo() == 201));
        Assert.All(respostas.Where(r => r.Codigo() != 201), r => Assert.Equal(409, r.Codigo()));
        Assert.Equal(1, await api.NoBancoAsync(db => db.Usuarios.CountAsync(u => u.Email == email)));
    }
}

public sealed class ConfiguracaoJwtTests
{
    [Fact]
    public async Task Fora_de_development_sem_segredo_jwt_a_api_nao_inicia()
    {
        // Antes a API subia e respondia 500 em todas as rotas, inclusive /health/live.
        await using var api = new FabricaEmAmbiente("Production", ("Jwt:Segredo", ""));

        var erro = Assert.ThrowsAny<Exception>(() => api.CreateClient());

        Assert.Contains("Jwt:Segredo", erro.ToString());
    }
}
