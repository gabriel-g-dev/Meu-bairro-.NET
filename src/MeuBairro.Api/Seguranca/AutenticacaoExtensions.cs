using System.Data.Common;
using System.Security.Claims;
using MeuBairro.Api.Dados;
using MeuBairro.Api.Dominio;
using MeuBairro.Api.Infra;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace MeuBairro.Api.Seguranca;

public static class Acesso
{
    public const string Cidadao = "Cidadao";
    public const string Gestor = "Gestor";
}

public sealed record UsuarioAtual(long Id, Perfil Perfil)
{
    public bool EhGestor => Perfil == Perfil.GESTOR;

    public static UsuarioAtual De(ClaimsPrincipal principal) =>
        new(long.Parse(principal.FindFirstValue(TokenService.ClaimId)!),
            Enum.Parse<Perfil>(principal.FindFirstValue(TokenService.ClaimPerfil)!));
}

public sealed class ContaInexistenteException() : SecurityTokenValidationException("A conta do token não existe mais");

public static class AutenticacaoExtensions
{
    private const string FalhaBanco = "meubairro.falhaBancoAutenticacao";

    public static IServiceCollection AddSegurancaJwt(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<TokenService>((options, tokens) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = tokens.ParametrosValidacao();
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = ValidarContaAsync,
                    OnChallenge = ResponderNaoAutenticadoAsync,
                    OnForbidden = ResponderProibidoAsync
                };
            });

        // Toda rota mapeada exige token, exceto as marcadas com AllowAnonymous. O que não chega a uma rota (caminho
        // inexistente, ou método e Content-Type recusados pelo roteamento) não pede token: o cliente recebe o erro
        // real (404, 405 ou 415), e não 401, inclusive no login e no cadastro.
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
                .RequireAssertion(c => c.User.Identity?.IsAuthenticated == true
                                       || c.Resource is HttpContext http && http.GetEndpoint() is not RouteEndpoint)
                .Build())
            .AddPolicy(Acesso.Cidadao, p => p.RequireAuthenticatedUser().RequireRole(nameof(Perfil.CIDADAO)))
            .AddPolicy(Acesso.Gestor, p => p.RequireAuthenticatedUser().RequireRole(nameof(Perfil.GESTOR)));

        return services;
    }

    // Os IDs de USUARIOS e FUNCIONARIOS podem coincidir; o perfil do token indica em qual tabela a conta deve existir.
    private static async Task ValidarContaAsync(TokenValidatedContext context)
    {
        var principal = context.Principal!;
        if (!long.TryParse(principal.FindFirstValue(TokenService.ClaimId), out var id)
            || !Enums.TentarLer<Perfil>(principal.FindFirstValue(TokenService.ClaimPerfil), out var perfil))
        {
            context.Fail(new SecurityTokenValidationException("Token sem identificação válida"));
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var ct = context.HttpContext.RequestAborted;
        try
        {
            var existe = perfil == Perfil.GESTOR
                ? await db.Funcionarios.AnyAsync(f => f.Id == id, ct)
                : await db.Usuarios.AnyAsync(u => u.Id == id, ct);
            if (!existe)
                context.Fail(new ContaInexistenteException());
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            context.HttpContext.Items[FalhaBanco] = true;
            context.Fail(ex);
        }
    }

    private static async Task ResponderNaoAutenticadoAsync(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        var http = context.HttpContext;
        if (http.Items.ContainsKey(FalhaBanco))
        {
            http.Response.Headers.RetryAfter = "30";
            await Erros.EscreverAsync(http, StatusCodes.Status503ServiceUnavailable, Erros.MensagemBancoIndisponivel);
            return;
        }

        http.Response.Headers.WWWAuthenticate = context.AuthenticateFailure is null
            ? "Bearer"
            : "Bearer error=\"invalid_token\"";
        var mensagem = context.AuthenticateFailure switch
        {
            null => "Autenticação necessária. Envie o token no cabeçalho Authorization: Bearer <token>",
            ContaInexistenteException => "O usuário do token não existe mais",
            _ => "Token inválido ou expirado"
        };
        await Erros.EscreverAsync(http, StatusCodes.Status401Unauthorized, mensagem);
    }

    private static Task ResponderProibidoAsync(ForbiddenContext context) =>
        Erros.EscreverAsync(context.HttpContext, StatusCodes.Status403Forbidden, "Você não tem permissão para acessar este recurso");
}
