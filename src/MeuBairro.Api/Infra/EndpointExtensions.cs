using MeuBairro.Api.Contratos;
using MeuBairro.Api.Infra.Idempotencia;

namespace MeuBairro.Api.Infra;

public static class EndpointExtensions
{
    public static RouteHandlerBuilder Validar<T>(this RouteHandlerBuilder builder) where T : class, new() =>
        builder.AddEndpointFilter<ValidacaoFilter<T>>();

    public static RouteHandlerBuilder Idempotente(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new IdempotenteMetadata());

    public static RouteHandlerBuilder WithSummary(this RouteHandlerBuilder builder, string resumo, string descricao) =>
        builder.WithSummary(resumo).WithDescription(descricao);

    public static RouteHandlerBuilder ComErros(this RouteHandlerBuilder builder, params int[] codigos)
    {
        foreach (var codigo in codigos)
            builder.Produces<ErroResposta>(codigo);
        return builder;
    }

    // Base usada para montar URLs absolutas (fotos e Location), respeitando o prefixo /api quando usado.
    public static string UrlBase(this HttpRequest request) =>
        $"{request.Scheme}://{request.Host.Value}{request.PathBase.Value}";
}
