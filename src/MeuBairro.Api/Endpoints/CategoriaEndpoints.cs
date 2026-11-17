using MeuBairro.Api.Contratos;
using MeuBairro.Api.Infra;
using MeuBairro.Api.Seguranca;
using MeuBairro.Api.Servicos;

namespace MeuBairro.Api.Endpoints;

public static class CategoriaEndpoints
{
    public static void MapCategorias(this IEndpointRouteBuilder app)
    {
        var categorias = app.MapGroup("/categorias")
            .WithTags("Categorias")
            .RequireAuthorization()
            .RequireRateLimiting(Limites.Leitura);

        categorias.MapGet("", async (CategoriaService servico, CancellationToken ct) =>
                TypedResults.Ok(await servico.ListarAsync(ct)))
            .WithSummary("Lista as categorias com código, nome e prazo de atendimento")
            .ComErros(401);

        categorias.MapGet("/{id}", async (long id, CategoriaService servico, CancellationToken ct) =>
                TypedResults.Ok(await servico.BuscarAsync(id, ct)))
            .WithSummary("Detalha uma categoria")
            .ComErros(401, 404);

        categorias.MapPost("", async (CategoriaRequest dados, CategoriaService servico, HttpRequest request, CancellationToken ct) =>
            {
                var categoria = await servico.CriarAsync(dados, ct);
                return TypedResults.Created($"{request.UrlBase()}/categorias/{categoria.Id}", categoria);
            })
            .Validar<CategoriaRequest>()
            .RequireAuthorization(Acesso.Gestor)
            .RequireRateLimiting(Limites.Escrita)
            .Idempotente()
            .WithSummary("Cria uma categoria (GESTOR)")
            .ComErros(400, 401, 403, 409);

        categorias.MapPut("/{id}", async (long id, CategoriaRequest dados, CategoriaService servico, CancellationToken ct) =>
                TypedResults.Ok(await servico.AtualizarAsync(id, dados, ct)))
            .Validar<CategoriaRequest>()
            .RequireAuthorization(Acesso.Gestor)
            .RequireRateLimiting(Limites.Escrita)
            .WithSummary("Atualiza uma categoria (GESTOR)",
                "O código das seis categorias padrão (BURACO, ILUMINACAO, LIXO, ALAGAMENTO, CALCADA e OUTRO) não pode mudar.")
            .ComErros(400, 401, 403, 404, 409);

        categorias.MapDelete("/{id}", async (long id, CategoriaService servico, CancellationToken ct) =>
            {
                await servico.ExcluirAsync(id, ct);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Acesso.Gestor)
            .RequireRateLimiting(Limites.Escrita)
            .WithSummary("Exclui uma categoria sem ocorrências (GESTOR)", "As seis categorias padrão não podem ser excluídas.")
            .ComErros(401, 403, 404, 409);
    }
}
