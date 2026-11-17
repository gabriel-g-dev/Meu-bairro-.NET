using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeuBairro.Api.Contratos;
using MeuBairro.Api.Dados;
using MeuBairro.Api.Dominio;
using MeuBairro.Api.Servicos;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Oracle.ManagedDataAccess.Client;

namespace MeuBairro.Api.Infra;

public static partial class Erros
{
    public const string MensagemBancoIndisponivel = "Banco de dados indisponível no momento. Tente novamente mais tarde.";
    public const string MensagemErroInterno = "Erro interno no servidor. Tente novamente mais tarde.";
    public const string MensagemCorpoInvalido = "O corpo da requisição está ausente ou não é um JSON válido";
    public const string MensagemFormularioInvalido = "Não foi possível ler os dados do formulário multipart";

    public static Task EscreverAsync(HttpContext http, int status, string mensagem, IReadOnlyList<CampoErro>? campos = null)
    {
        http.Response.StatusCode = status;
        // Mesmo relógio e fuso usados nas datas gravadas (Aplicacao:FusoHorario).
        var corpo = new ErroResposta(
            http.RequestServices.GetRequiredService<Relogio>().Agora(),
            status,
            ReasonPhrases.GetReasonPhrase(status),
            mensagem,
            (http.Request.PathBase + http.Request.Path).Value ?? "/",
            campos ?? []);
        return http.Response.WriteAsJsonAsync(corpo);
    }

    // Respostas de erro sem corpo (rota inexistente, método não suportado) ganham o mesmo formato JSON.
    public static Task EscreverStatusSemCorpoAsync(StatusCodeContext contexto)
    {
        var http = contexto.HttpContext;
        var status = http.Response.StatusCode;
        var mensagem = status switch
        {
            404 => "Recurso não encontrado",
            405 => "Método HTTP não suportado nesta rota",
            415 => "Tipo de conteúdo não suportado nesta rota",
            _ => ReasonPhrases.GetReasonPhrase(status)
        };
        return EscreverAsync(http, status, mensagem);
    }

    public static bool ViolacaoUnicidade(Exception ex) => ex.InnerException switch
    {
        SqliteException s => s.SqliteErrorCode == 19 && s.SqliteExtendedErrorCode is 2067 or 1555,
        OracleException o => o.Number == 1,
        _ => false
    };

    public static bool ViolacaoRelacionamento(Exception ex) => ex.InnerException switch
    {
        SqliteException s => s.SqliteErrorCode == 19 && s.SqliteExtendedErrorCode == 787,
        OracleException o => o.Number is 2291 or 2292,
        _ => false
    };

    internal static (int Status, string Mensagem, IReadOnlyList<CampoErro>? Campos) Mapear(Exception ex) => ex switch
    {
        ValidacaoException v => (400, v.Message, v.Campos),
        ApiException a => (a.Status, a.Message, null),
        BadHttpRequestException { StatusCode: 413 } => (413, MensagemTamanhoExcedido, null),
        BadHttpRequestException { StatusCode: 415 } => (415, "Tipo de conteúdo não suportado nesta rota", null),
        // O binding de formulários embrulha o erro de leitura do multipart (ex.: limite de tamanho excedido).
        BadHttpRequestException { InnerException: InvalidDataException d } => MapearFormulario(d),
        BadHttpRequestException b => (400, MensagemBinding(b), null),
        InvalidDataException d => MapearFormulario(d),
        JsonException => (400, MensagemCorpoInvalido, null),
        EscritaBloqueadaException e => (503, e.Message, null),
        DbUpdateConcurrencyException =>
            (409, "O registro foi alterado por outra operação ao mesmo tempo. Consulte os dados atuais e tente novamente", null),
        DbUpdateException d when ViolacaoUnicidade(d) => (409, "A operação conflita com dados já existentes", null),
        DbUpdateException d when ViolacaoRelacionamento(d) => (409, "A operação viola relacionamentos existentes", null),
        DbUpdateException or DbException or TimeoutException => (503, MensagemBancoIndisponivel, null),
        { InnerException: DbException } => (503, MensagemBancoIndisponivel, null),
        _ => (500, MensagemErroInterno, null)
    };

    private const string MensagemTamanhoExcedido = "A requisição excede o tamanho máximo permitido";

    private static (int, string, IReadOnlyList<CampoErro>?) MapearFormulario(InvalidDataException ex) =>
        ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase)
            ? (413, MensagemTamanhoExcedido, null)
            : (400, MensagemFormularioInvalido, null);

    private static string MensagemBinding(BadHttpRequestException ex)
    {
        if (ex.InnerException is JsonException || ex.Message.Contains("body", StringComparison.OrdinalIgnoreCase))
            return MensagemCorpoInvalido;

        // Ex.: Failed to bind parameter "long id" from "abc".
        var parametro = ParametroInvalido().Match(ex.Message);
        return parametro.Success
            ? $"Valor inválido para o parâmetro '{parametro.Groups["nome"].Value}': {parametro.Groups["valor"].Value}"
            : "Parâmetros inválidos na requisição";
    }

    [GeneratedRegex(@"^Failed to bind parameter ""[^"" ]+ (?<nome>[^""]+)"" from ""(?<valor>.*)""\.$")]
    private static partial Regex ParametroInvalido();
}

// Converte exceções em respostas JSON padronizadas, sem expor detalhes internos ao cliente.
public sealed class ErroMiddleware(RequestDelegate next, ILogger<ErroMiddleware> log)
{
    public async Task InvokeAsync(HttpContext http)
    {
        try
        {
            await next(http);
        }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            if (ex is OperationCanceledException && http.RequestAborted.IsCancellationRequested)
                return;

            var (status, mensagem, campos) = Erros.Mapear(ex);
            if (status >= 500)
                log.LogError(ex, "Falha ao processar {Metodo} {Rota}", http.Request.Method, http.Request.Path.Value);
            else
                log.LogDebug("Requisição recusada com {Status}: {Mensagem} ({Causa}: {Detalhe})", status, mensagem,
                    ex.GetBaseException().GetType().Name, ex.GetBaseException().Message);

            if (status == 503)
                http.Response.Headers.RetryAfter = "30";
            // Uma resposta de erro não aponta para recurso criado.
            http.Response.Headers.Remove("Location");
            await Erros.EscreverAsync(http, status, mensagem, campos);
        }
    }
}
