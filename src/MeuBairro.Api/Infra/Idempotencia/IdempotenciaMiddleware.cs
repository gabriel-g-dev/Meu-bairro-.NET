using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MeuBairro.Api.Configuracao;
using MeuBairro.Api.Dados;
using MeuBairro.Api.Dominio;
using MeuBairro.Api.Seguranca;
using MeuBairro.Api.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace MeuBairro.Api.Infra.Idempotencia;

public sealed class IdempotenteMetadata;

public sealed partial class IdempotenciaMiddleware(RequestDelegate next, ILogger<IdempotenciaMiddleware> log)
{
    public const string Cabecalho = "Idempotency-Key";
    public const string CabecalhoRepeticao = "Idempotent-Replayed";

    public async Task InvokeAsync(HttpContext http, IdempotenciaService servico, IOptions<IdempotenciaOptions> opcoes)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<IdempotenteMetadata>() is null
            || !http.Request.Headers.TryGetValue(Cabecalho, out var valores))
        {
            await next(http);
            return;
        }

        var cfg = opcoes.Value;
        if (valores.Count > 1)
            throw new RequisicaoInvalidaException("Envie apenas um cabeçalho Idempotency-Key");

        // Cabeçalho enviado vazio também é recusado: ignorá-lo deixaria a operação sem proteção contra repetição.
        var chave = valores.ToString().Trim();
        if (chave.Length == 0 || chave.Length > cfg.TamanhoMaximoChave || !ChaveValida().IsMatch(chave))
            throw new RequisicaoInvalidaException(
                $"O cabeçalho Idempotency-Key deve ter de 1 a {cfg.TamanhoMaximoChave} caracteres visíveis, sem espaços");

        var escopo = Escopo(http);
        var hash = await HashRequisicao.CalcularAsync(http.Request, http.RequestAborted);
        var inicio = await servico.IniciarAsync(escopo, chave, hash, http.RequestAborted);

        switch (inicio.Resultado)
        {
            case ResultadoInicio.Repetida:
                await ReproduzirAsync(http, inicio.Registro!);
                return;
            case ResultadoInicio.PayloadDivergente:
                throw new RegraNegocioException(
                    "Esta Idempotency-Key já foi usada com outro conteúdo. Gere uma nova chave para uma nova operação");
            case ResultadoInicio.EmAndamento:
                http.Response.Headers.RetryAfter = inicio.EsperaSegundos.ToString();
                throw new RegraNegocioException(
                    "Uma requisição com esta Idempotency-Key ainda está em processamento. Tente novamente em instantes");
        }

        // No modo atômico o endpoint roda dentro desta transação: os dados do negócio e o resultado da chave
        // são confirmados juntos. Se o processo cair antes do commit, nada do negócio fica gravado e a chave
        // continua "Processando" até o fim do bloqueio, quando uma nova tentativa pode assumi-la com segurança.
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var arquivos = http.RequestServices.GetRequiredService<ArquivosDaRequisicao>();
        await using var transacao = servico.ConclusaoAtomica
            ? await AbrirTransacaoAsync(db, servico, inicio, http.RequestAborted)
            : null;

        var corpoOriginal = http.Response.Body;
        using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        byte[] corpo;
        try
        {
            await next(http);
            await http.Response.BodyWriter.FlushAsync(http.RequestAborted);
            http.Response.Body = corpoOriginal;
            corpo = buffer.ToArray();

            var status = http.Response.StatusCode;
            if (status is >= 200 and < 300)
            {
                // Respostas muito grandes ficam sem corpo guardado; o status e o Location continuam valendo.
                var guardado = corpo.Length <= cfg.TamanhoMaximoRespostaBytes ? corpo : null;
                var location = http.Response.Headers.Location.ToString();
                if (transacao is not null)
                {
                    var concluiu = await servico.ConcluirNaTransacaoAsync(transacao.GetDbTransaction(), inicio.Id, inicio.Token!,
                        status, http.Response.ContentType, string.IsNullOrEmpty(location) ? null : location, guardado);
                    if (!concluiu)
                        throw new ChavePerdidaException();
                    await transacao.CommitAsync(CancellationToken.None);
                }
                else
                {
                    // Sem atomicidade (tabela de controle em outro banco) o negócio já foi confirmado: a resposta
                    // de sucesso é devolvida mesmo que o registro do resultado falhe. A chave segue "Processando"
                    // até o fim do bloqueio, recusando repetições nesse intervalo.
                    try
                    {
                        await servico.ConcluirAsync(inicio.Id, inicio.Token!, status,
                            http.Response.ContentType, string.IsNullOrEmpty(location) ? null : location, guardado);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning("Resultado da Idempotency-Key não registrado ({Tipo}); a chave expira ao fim do bloqueio",
                            ex.GetType().Name);
                    }
                }
            }
            else
            {
                // Erro sem exceção: nada do que o endpoint tenha feito é mantido e a chave é liberada.
                if (transacao is not null)
                    await DesfazerAsync(transacao, arquivos);
                await DescartarAsync(servico, inicio);
            }
        }
        catch (ChavePerdidaException)
        {
            http.Response.Body = corpoOriginal;
            await DesfazerAsync(transacao!, arquivos);
            throw new RegraNegocioException(
                "Esta Idempotency-Key foi assumida por outra tentativa enquanto a requisição era processada. Nada foi gravado");
        }
        catch
        {
            http.Response.Body = corpoOriginal;
            if (transacao is not null)
                await DesfazerAsync(transacao, arquivos);
            await DescartarAsync(servico, inicio);
            throw;
        }

        http.Response.Headers[CabecalhoRepeticao] = "false";
        if (corpo.Length > 0)
            await corpoOriginal.WriteAsync(corpo, http.RequestAborted);
    }

    private sealed class ChavePerdidaException : Exception;

    // Se a transação nem chega a abrir, nada do negócio foi executado: a chave é liberada para uma nova tentativa.
    private async Task<IDbContextTransaction> AbrirTransacaoAsync(AppDbContext db, IdempotenciaService servico,
        InicioIdempotencia inicio, CancellationToken ct)
    {
        try
        {
            return await db.Database.BeginTransactionAsync(ct);
        }
        catch
        {
            await DescartarAsync(servico, inicio);
            throw;
        }
    }

    private async Task DesfazerAsync(IDbContextTransaction transacao, ArquivosDaRequisicao arquivos)
    {
        try
        {
            await transacao.RollbackAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogWarning("Não foi possível desfazer a transação da requisição idempotente: {Tipo}", ex.GetType().Name);
        }
        arquivos.Descartar();
    }

    // A chave vale apenas para o mesmo usuário, método e rota.
    internal static string Escopo(HttpContext http)
    {
        var id = http.User.FindFirstValue(TokenService.ClaimId);
        var perfil = http.User.FindFirstValue(TokenService.ClaimPerfil);
        var quem = id is null ? "anonimo" : $"{perfil}:{id}";
        var rota = (http.Request.Path.Value ?? "/").TrimEnd('/').ToLowerInvariant();
        return $"{quem}|{http.Request.Method}|{rota}";
    }

    private static async Task ReproduzirAsync(HttpContext http, RegistroIdempotencia registro)
    {
        http.Response.StatusCode = registro.StatusResposta ?? StatusCodes.Status200OK;
        if (registro.TipoConteudo is not null)
            http.Response.ContentType = registro.TipoConteudo;
        if (registro.Location is not null)
            http.Response.Headers.Location = registro.Location;
        http.Response.Headers[CabecalhoRepeticao] = "true";
        if (registro.CorpoResposta is { Length: > 0 } corpo)
            await http.Response.Body.WriteAsync(corpo, http.RequestAborted);
    }

    private async Task DescartarAsync(IdempotenciaService servico, InicioIdempotencia inicio)
    {
        try
        {
            await servico.DescartarAsync(inicio.Id, inicio.Token!);
        }
        catch (Exception ex)
        {
            // Se nem isso funcionar, a chave expira sozinha ao fim do tempo de bloqueio.
            log.LogWarning("Não foi possível liberar a chave de idempotência: {Tipo}", ex.GetType().Name);
        }
    }

    [GeneratedRegex(@"^[\x21-\x7E]+$")]
    private static partial Regex ChaveValida();
}

// Hash canônico do conteúdo: em multipart o "boundary" muda a cada envio, então são comparados
// os campos e o SHA-256 de cada arquivo, e não os bytes brutos.
public static class HashRequisicao
{
    public static async Task<string> CalcularAsync(HttpRequest request, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        if (request.HasFormContentType)
        {
            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync(ct);
            }
            catch (IOException ex) when (ex is not BadHttpRequestException)
            {
                // Multipart incompleto (ex.: sem o delimitador final). O binding de formulários também responde 400.
                // BadHttpRequestException (também uma IOException) segue adiante com o próprio status, como o 413.
                throw new RequisicaoInvalidaException(Erros.MensagemFormularioInvalido);
            }

            foreach (var chave in form.Keys.OrderBy(k => k, StringComparer.Ordinal))
                Adicionar(hash, $"campo:{chave}={string.Join('\u001f', form[chave].ToArray())}\n");

            foreach (var arquivo in form.Files.OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Length))
            {
                await using var conteudo = arquivo.OpenReadStream();
                var sha = await SHA256.HashDataAsync(conteudo, ct);
                Adicionar(hash, $"arquivo:{arquivo.Name}:{arquivo.ContentType}:{arquivo.Length}:{Convert.ToHexString(sha)}\n");
            }
        }
        else
        {
            request.EnableBuffering();
            Adicionar(hash, $"tipo:{request.ContentType}\n");
            var bloco = new byte[8192];
            int lidos;
            while ((lidos = await request.Body.ReadAsync(bloco, ct)) > 0)
                hash.AppendData(bloco, 0, lidos);
            request.Body.Position = 0;
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Adicionar(IncrementalHash hash, string texto) => hash.AppendData(Encoding.UTF8.GetBytes(texto));
}
