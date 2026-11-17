using System.Security.Claims;
using MeuBairro.Api.Contratos;
using MeuBairro.Api.Dominio;
using MeuBairro.Api.Infra;
using MeuBairro.Api.Seguranca;
using MeuBairro.Api.Servicos;
using Microsoft.AspNetCore.Mvc;

namespace MeuBairro.Api.Endpoints;

public static class OcorrenciaEndpoints
{
    public static void MapOcorrencias(this IEndpointRouteBuilder app)
    {
        var ocorrencias = app.MapGroup("/ocorrencias")
            .WithTags("Ocorrências")
            .RequireAuthorization()
            .RequireRateLimiting(Limites.Leitura);

        ocorrencias.MapGet("", async (OcorrenciaService servico, HttpRequest request, CancellationToken ct) =>
                TypedResults.Ok(await servico.ListarAsync(request.UrlBase(), ct)))
            .WithSummary("Lista todas as ocorrências, das mais recentes para as mais antigas")
            .ComErros(401);

        ocorrencias.MapGet("/paginado", async ([AsParameters] FiltroOcorrencias filtro, ClaimsPrincipal user,
                    OcorrenciaService servico, HttpRequest request, CancellationToken ct) =>
                TypedResults.Ok(await servico.ListarPaginadoAsync(filtro, UsuarioAtual.De(user), request.UrlBase(), ct)))
            .WithSummary("Lista as ocorrências com paginação e filtros",
                "Filtros opcionais: status, categoria, prioridade, bairro e minhas=true. Paginação: page (a partir de 0), "
                + "size (padrão 10, máximo 50) e sort=campo,asc|desc, com os campos id, titulo, status, prioridade, "
                + "criadoEm (ou dataAbertura) e dataConclusao.")
            .ComErros(400, 401);

        ocorrencias.MapGet("/resumo", async (OcorrenciaService servico, CancellationToken ct) =>
                TypedResults.Ok(await servico.ResumoAsync(ct)))
            .RequireAuthorization(Acesso.Gestor)
            .WithSummary("Totais de ocorrências por status e prioridade (GESTOR)")
            .ComErros(401, 403);

        ocorrencias.MapGet("/painel", async (string? bairro, OcorrenciaService servico, CancellationToken ct) =>
                TypedResults.Ok(await servico.PainelAsync(bairro, ct)))
            .WithSummary("Painel de transparência do bairro",
                "Quantidade por status, ocorrências atrasadas em relação ao prazo da categoria e tempo médio de resolução.")
            .ComErros(401);

        ocorrencias.MapGet("/{id}", async (long id, OcorrenciaService servico, HttpRequest request, CancellationToken ct) =>
                TypedResults.Ok(await servico.BuscarAsync(id, request.UrlBase(), ct)))
            .WithSummary("Detalha uma ocorrência")
            .ComErros(401, 404);

        ocorrencias.MapPost("", async ([FromForm] OcorrenciaForm? form, ClaimsPrincipal user, OcorrenciaService servico,
                HttpRequest request, CancellationToken ct) =>
            {
                var ocorrencia = await servico.CriarAsync(form!, UsuarioAtual.De(user), request.UrlBase(), ct);
                return TypedResults.Created($"{request.UrlBase()}/ocorrencias/{ocorrencia.Id}", ocorrencia);
            })
            .DisableAntiforgery()
            .Validar<OcorrenciaForm>()
            .RequireAuthorization(Acesso.Cidadao)
            .RequireRateLimiting(Limites.Escrita)
            .Idempotente()
            .WithSummary("Registra uma ocorrência (CIDADAO)",
                "Recebe multipart/form-data, com a foto opcional no campo foto. O endereço pode ser informado em texto livre "
                + "(campo endereco), em campos detalhados (logradouro, numero, bairro, cidade, estado, cep) ou por enderecoId.")
            .ComErros(400, 401, 403, 409, 413);

        ocorrencias.MapPut("/{id}", async (long id, [FromForm] OcorrenciaForm? form, ClaimsPrincipal user,
                    OcorrenciaService servico, HttpRequest request, CancellationToken ct) =>
                TypedResults.Ok(await servico.AtualizarAsync(id, form!, UsuarioAtual.De(user), request.UrlBase(), ct)))
            .DisableAntiforgery()
            .Validar<OcorrenciaForm>()
            .RequireAuthorization(Acesso.Cidadao)
            .RequireRateLimiting(Limites.Escrita)
            .WithSummary("Atualiza uma ocorrência própria com status ABERTA (CIDADAO)",
                "Latitude, longitude e foto não enviadas mantêm os valores atuais.")
            .ComErros(400, 401, 403, 404, 409, 413);

        ocorrencias.MapDelete("/{id}", async (long id, ClaimsPrincipal user, OcorrenciaService servico, CancellationToken ct) =>
            {
                await servico.ExcluirAsync(id, UsuarioAtual.De(user), ct);
                return TypedResults.NoContent();
            })
            .RequireRateLimiting(Limites.Escrita)
            .WithSummary("Exclui uma ocorrência",
                "O cidadão exclui as próprias ocorrências com status ABERTA; o gestor pode excluir qualquer uma.")
            .ComErros(401, 403, 404, 409);

        ocorrencias.MapPatch("/{id}/status", async (long id, AtualizacaoStatusRequest dados, ClaimsPrincipal user,
                    OcorrenciaService servico, HttpRequest request, CancellationToken ct) =>
                TypedResults.Ok(await servico.AlterarStatusAsync(id, dados, UsuarioAtual.De(user), request.UrlBase(), ct)))
            .Validar<AtualizacaoStatusRequest>()
            .RequireAuthorization(Acesso.Gestor)
            .RequireRateLimiting(Limites.Escrita)
            .WithSummary("Altera o status da ocorrência (GESTOR)",
                $"Status aceitos: {nameof(StatusSolicitacao.ABERTA)}, {nameof(StatusSolicitacao.EM_ANALISE)} e "
                + $"{nameof(StatusSolicitacao.RESOLVIDA)}. Uma ocorrência resolvida não muda mais de status.")
            .ComErros(400, 401, 403, 404, 409);

        ocorrencias.MapPatch("/{id}/prioridade", async (long id, AtualizacaoPrioridadeRequest dados, ClaimsPrincipal user,
                    OcorrenciaService servico, HttpRequest request, CancellationToken ct) =>
                TypedResults.Ok(await servico.AlterarPrioridadeAsync(id, dados, UsuarioAtual.De(user), request.UrlBase(), ct)))
            .Validar<AtualizacaoPrioridadeRequest>()
            .RequireAuthorization(Acesso.Gestor)
            .RequireRateLimiting(Limites.Escrita)
            .WithSummary("Define a prioridade da ocorrência (GESTOR)")
            .ComErros(400, 401, 403, 404, 409);

        ocorrencias.MapGet("/{id}/historico", async (long id, OcorrenciaService servico, CancellationToken ct) =>
                TypedResults.Ok(await servico.HistoricoAsync(id, ct)))
            .WithSummary("Histórico de atendimento da ocorrência")
            .ComErros(401, 404);

        ocorrencias.MapPost("/{id}/andamentos", async (long id, AndamentoRequest dados, ClaimsPrincipal user,
                OcorrenciaService servico, HttpRequest request, CancellationToken ct) =>
            {
                var andamento = await servico.RegistrarAndamentoAsync(id, dados, UsuarioAtual.De(user), ct);
                return TypedResults.Created($"{request.UrlBase()}/ocorrencias/{id}/historico", andamento);
            })
            .Validar<AndamentoRequest>()
            .RequireAuthorization(Acesso.Gestor)
            .RequireRateLimiting(Limites.Escrita)
            .Idempotente()
            .WithSummary("Registra um andamento no atendimento sem mudar o status (GESTOR)")
            .ComErros(400, 401, 403, 404, 409);

        ocorrencias.MapGet("/{id}/anexos", async (long id, OcorrenciaService servico, HttpRequest request, CancellationToken ct) =>
                TypedResults.Ok(await servico.AnexosAsync(id, request.UrlBase(), ct)))
            .WithSummary("Lista as fotos e anexos da ocorrência")
            .ComErros(401, 404);

        ocorrencias.MapPost("/{id}/anexos", async (long id, [FromForm] AnexoForm? form, ClaimsPrincipal user,
                OcorrenciaService servico, HttpRequest request, CancellationToken ct) =>
            {
                var anexo = await servico.AdicionarAnexoAsync(id, form!.Arquivo, UsuarioAtual.De(user), request.UrlBase(), ct);
                return TypedResults.Created(anexo.Url, anexo);
            })
            .DisableAntiforgery()
            .Validar<AnexoForm>()
            .RequireAuthorization(Acesso.Cidadao)
            .RequireRateLimiting(Limites.Escrita)
            .Idempotente()
            .WithSummary("Envia mais uma foto para a ocorrência (CIDADAO, campo 'arquivo')")
            .ComErros(400, 401, 403, 404, 409, 413);

        ocorrencias.MapGet("/{id}/avaliacao", async (long id, ClaimsPrincipal user, OcorrenciaService servico, CancellationToken ct) =>
                TypedResults.Ok(await servico.BuscarAvaliacaoAsync(id, UsuarioAtual.De(user), ct)))
            .WithSummary("Consulta a avaliação do atendimento (dono da ocorrência ou GESTOR)")
            .ComErros(400, 401, 403, 404);

        ocorrencias.MapPost("/{id}/avaliacao", async (long id, AvaliacaoRequest dados, ClaimsPrincipal user,
                OcorrenciaService servico, HttpRequest request, CancellationToken ct) =>
            {
                var avaliacao = await servico.AvaliarAsync(id, dados, UsuarioAtual.De(user), ct);
                return TypedResults.Created($"{request.UrlBase()}/ocorrencias/{id}/avaliacao", avaliacao);
            })
            .Validar<AvaliacaoRequest>()
            .RequireAuthorization(Acesso.Cidadao)
            .RequireRateLimiting(Limites.Escrita)
            .Idempotente()
            .WithSummary("Avalia o atendimento de uma ocorrência resolvida (CIDADAO, uma vez por ocorrência)")
            .ComErros(400, 401, 403, 404, 409);
    }

    public static void MapFotos(this IEndpointRouteBuilder app)
    {
        app.MapGet("/fotos/{arquivo}", (string arquivo, ArmazenamentoFotos fotos) =>
            {
                var caminho = fotos.Caminho(arquivo) ?? throw new RecursoNaoEncontradoException("Foto não encontrada");
                return TypedResults.PhysicalFile(caminho, ArmazenamentoFotos.TipoPorExtensao(caminho));
            })
            .AllowAnonymous()
            .WithTags("Fotos")
            .WithSummary("Devolve a foto de uma ocorrência",
                "Rota pública usada pela fotoUrl: a imagem abre diretamente (por exemplo, numa tag <img>), sem token.")
            .ComErros(404);
    }
}
