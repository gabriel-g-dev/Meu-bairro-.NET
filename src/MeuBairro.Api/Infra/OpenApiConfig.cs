using MeuBairro.Api.Infra.Idempotencia;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi;

namespace MeuBairro.Api.Infra;

public static class OpenApiConfig
{
    public const string EsquemaSeguranca = "Bearer";

    public static IServiceCollection AddDocumentacaoOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer<DocumentoTransformer>();
            options.AddOperationTransformer<OperacaoTransformer>();
        });
        return services;
    }

    private sealed class DocumentoTransformer : IOpenApiDocumentTransformer
    {
        public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
        {
            document.Info = new OpenApiInfo
            {
                Title = "Meu Bairro API (.NET)",
                Version = "v1",
                Description = "Zeladoria colaborativa (CitizenTech): cidadãos registram problemas urbanos e acompanham "
                              + "o atendimento. Obtenha o token em POST /auth/login e use o botão Authorize."
            };
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[EsquemaSeguranca] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Token JWT obtido em POST /auth/login"
            };
            return Task.CompletedTask;
        }
    }

    private sealed class OperacaoTransformer : IOpenApiOperationTransformer
    {
        public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;

            if (!metadata.OfType<IAllowAnonymous>().Any())
            {
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(EsquemaSeguranca, context.Document)] = []
                });
            }

            // Respostas que podem ocorrer em qualquer rota, além das declaradas em cada endpoint.
            var saude = metadata.OfType<DisableRateLimitingAttribute>().Any();
            if (!saude)
            {
                Resposta(operation, context, "429", "Limite de requisições excedido (ver cabeçalho Retry-After)");
                Resposta(operation, context, "503", "Banco de dados indisponível no momento");
            }
            if (context.Description.ParameterDescriptions.Any(p => p.Source.Id == "Path"))
                Resposta(operation, context, "400", "Parâmetro de rota inválido (ex.: id não numérico)");

            if (metadata.OfType<IdempotenteMetadata>().Any())
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = IdempotenciaMiddleware.Cabecalho,
                    In = ParameterLocation.Header,
                    Required = false,
                    Description = "Chave única da operação (ex.: um UUID). Repetir a mesma chave com o mesmo conteúdo "
                                  + "devolve o resultado original sem criar registros duplicados.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 100 }
                });
            }

            return Task.CompletedTask;
        }

        private static void Resposta(OpenApiOperation operation, OpenApiOperationTransformerContext context, string codigo, string descricao)
        {
            operation.Responses ??= new OpenApiResponses();
            if (operation.Responses.ContainsKey(codigo))
                return;
            operation.Responses[codigo] = new OpenApiResponse
            {
                Description = descricao,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new() { Schema = new OpenApiSchemaReference(nameof(Contratos.ErroResposta), context.Document) }
                }
            };
        }
    }
}
