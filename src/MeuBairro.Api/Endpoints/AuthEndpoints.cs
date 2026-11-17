using System.Security.Claims;
using MeuBairro.Api.Contratos;
using MeuBairro.Api.Infra;
using MeuBairro.Api.Seguranca;
using MeuBairro.Api.Servicos;

namespace MeuBairro.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth")
            .WithTags("Autenticação")
            .AllowAnonymous()
            .RequireRateLimiting(Limites.Autenticacao);

        auth.MapPost("/register", async (CadastroUsuarioRequest dados, ContaService contas, HttpRequest request, CancellationToken ct) =>
                TypedResults.Created($"{request.UrlBase()}/usuarios/me", await contas.RegistrarCidadaoAsync(dados, ct)))
            .Validar<CadastroUsuarioRequest>()
            .WithSummary("Cadastra um cidadão")
            .WithDescription("Todo cadastro público recebe o perfil CIDADAO. O endereço é opcional e pode ser informado "
                             + "depois em PUT /usuarios/me/endereco.")
            .ComErros(400, 409, 429);

        auth.MapPost("/login", async (LoginRequest dados, ContaService contas, CancellationToken ct) =>
                TypedResults.Ok(await contas.EntrarAsync(dados, ct)))
            .Validar<LoginRequest>()
            .WithSummary("Faz login de cidadão ou funcionário e devolve o token JWT")
            .WithDescription("Procura o e-mail em USUARIOS (perfil CIDADAO) e em FUNCIONARIOS (perfil GESTOR). "
                             + "O campo opcional 'perfil' restringe a busca a um dos dois.")
            .ComErros(400, 401, 429);
    }

    public static void MapUsuarios(this IEndpointRouteBuilder app)
    {
        var usuarios = app.MapGroup("/usuarios")
            .WithTags("Usuários")
            .RequireAuthorization()
            .RequireRateLimiting(Limites.Leitura);

        usuarios.MapGet("/me", async (ClaimsPrincipal user, ContaService contas, CancellationToken ct) =>
                TypedResults.Ok(await contas.PerfilAsync(UsuarioAtual.De(user), ct)))
            .WithSummary("Retorna os dados e o perfil do usuário logado")
            .ComErros(401);

        usuarios.MapPut("/me", async (AtualizacaoPerfilRequest dados, ClaimsPrincipal user, ContaService contas, CancellationToken ct) =>
                TypedResults.Ok(await contas.AtualizarPerfilAsync(UsuarioAtual.De(user), dados, ct)))
            .Validar<AtualizacaoPerfilRequest>()
            .RequireRateLimiting(Limites.Escrita)
            .WithSummary("Atualiza nome e telefone do usuário logado")
            .ComErros(400, 401);

        usuarios.MapGet("/me/endereco", async (ClaimsPrincipal user, ContaService contas, CancellationToken ct) =>
                TypedResults.Ok(await contas.EnderecoDoCidadaoAsync(UsuarioAtual.De(user), ct)))
            .RequireAuthorization(Acesso.Cidadao)
            .WithSummary("Endereço de cadastro do cidadão (CIDADAO)")
            .ComErros(401, 403);

        usuarios.MapPut("/me/endereco", async (EnderecoRequest dados, ClaimsPrincipal user, ContaService contas, CancellationToken ct) =>
                TypedResults.Ok(await contas.AtualizarEnderecoDoCidadaoAsync(UsuarioAtual.De(user), dados, ct)))
            .Validar<EnderecoRequest>()
            .RequireAuthorization(Acesso.Cidadao)
            .RequireRateLimiting(Limites.Escrita)
            .WithSummary("Cadastra ou atualiza o endereço do cidadão (CIDADAO)")
            .ComErros(400, 401, 403);

        // Funcionários não têm rota de consulta própria, então a resposta 201 vai sem Location.
        usuarios.MapPost("/gestores", async (GestorRequest dados, ContaService contas, CancellationToken ct) =>
                TypedResults.Created((string?)null, await contas.CadastrarGestorAsync(dados, ct)))
            .Validar<GestorRequest>()
            .RequireAuthorization(Acesso.Gestor)
            .RequireRateLimiting(Limites.Escrita)
            .Idempotente()
            .WithSummary("Cadastra um funcionário com perfil GESTOR (somente GESTOR)")
            .ComErros(400, 401, 403, 409);
    }

    public static void MapEnderecos(this IEndpointRouteBuilder app)
    {
        app.MapGet("/enderecos/{id}", async (long id, ClaimsPrincipal user, EnderecoService enderecos, CancellationToken ct) =>
                TypedResults.Ok(await enderecos.BuscarAsync(id, UsuarioAtual.De(user), ct)))
            .WithTags("Endereços")
            .RequireAuthorization()
            .RequireRateLimiting(Limites.Leitura)
            .WithSummary("Consulta um endereço",
                "O cidadão consulta o próprio endereço de cadastro ou o local de ocorrências; o gestor consulta qualquer um.")
            .ComErros(401, 403, 404);
    }
}
