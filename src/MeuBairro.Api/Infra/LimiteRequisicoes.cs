using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using MeuBairro.Api.Configuracao;
using MeuBairro.Api.Seguranca;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace MeuBairro.Api.Infra;

public static class Limites
{
    public const string Autenticacao = "autenticacao";
    public const string Leitura = "leitura";
    public const string Escrita = "escrita";

    public static IServiceCollection AddLimitesDeRequisicao(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = ResponderAsync;

            // Limite geral por IP da conexão, aplicado a todas as rotas (exceto health checks).
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                Janela("ip:" + Ip(http), Config(http).Global));

            // Login e cadastro: mais restritivo, por IP, para dificultar força bruta e cadastros em massa.
            options.AddPolicy(Autenticacao, http => Janela("ip:" + Ip(http), Config(http).Autenticacao));

            // Rotas protegidas: por usuário autenticado (ou IP, se o token não for válido).
            options.AddPolicy(Leitura, http => Janela(Cliente(http), Config(http).Leitura));
            options.AddPolicy(Escrita, http => Janela(Cliente(http), Config(http).Escrita));
        });
        return services;
    }

    // O IP vem da conexão TCP; cabeçalhos como X-Forwarded-For, controlados pelo cliente, não são considerados.
    internal static string Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";

    internal static string Cliente(HttpContext http)
    {
        var id = http.User.FindFirstValue(TokenService.ClaimId);
        return http.User.Identity?.IsAuthenticated == true && id is not null
            ? $"{http.User.FindFirstValue(TokenService.ClaimPerfil)}:{id}"
            : "ip:" + Ip(http);
    }

    private static LimitesOptions Config(HttpContext http) =>
        http.RequestServices.GetRequiredService<IOptions<LimitesOptions>>().Value;

    private static RateLimitPartition<string> Janela(string chave, JanelaOptions janela) =>
        RateLimitPartition.GetFixedWindowLimiter(chave, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, janela.Limite),
            Window = TimeSpan.FromSeconds(Math.Max(1, janela.JanelaSegundos)),
            QueueLimit = 0,
            AutoReplenishment = true
        });

    private static async ValueTask ResponderAsync(OnRejectedContext contexto, CancellationToken ct)
    {
        var http = contexto.HttpContext;
        var segundos = contexto.Lease.TryGetMetadata(MetadataName.RetryAfter, out var espera)
            ? Math.Max(1, (int)Math.Ceiling(espera.TotalSeconds))
            : 60;
        http.Response.Headers.RetryAfter = segundos.ToString(CultureInfo.InvariantCulture);
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("MeuBairro.LimiteRequisicoes")
            .LogWarning("Limite de requisições excedido em {Metodo} {Rota}", http.Request.Method, http.Request.Path.Value);
        await Erros.EscreverAsync(http, StatusCodes.Status429TooManyRequests,
            $"Muitas requisições. Tente novamente em {segundos} segundos.");
    }
}
