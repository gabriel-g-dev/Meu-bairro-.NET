using System.Net.Http.Headers;
using System.Net.Http.Json;
using MeuBairro.Api.Contratos;

namespace MeuBairro.Tests.Infra;

// Dados fictícios e montagem de requisições usadas pelos testes de integração.
public static class Dados
{
    public static readonly byte[] PngValido = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];
    public static readonly byte[] JpegValido = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0];

    public static string TituloUnico(string prefixo = "Buraco na rua") => $"{prefixo} {Guid.NewGuid().ToString("N")[..8]}";

    public static MultipartFormDataContent Ocorrencia(
        string? titulo = null,
        string descricao = "Buraco grande perto da faixa de pedestres",
        string categoria = "BURACO",
        string? endereco = "Rua das Flores, 120",
        IDictionary<string, string>? extras = null,
        byte[]? foto = null,
        string nomeFoto = "foto.png",
        string tipoFoto = "image/png")
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(titulo ?? TituloUnico()), "titulo");
        form.Add(new StringContent(descricao), "descricao");
        form.Add(new StringContent(categoria), "categoria");
        if (endereco is not null)
            form.Add(new StringContent(endereco), "endereco");
        foreach (var (chave, valor) in extras ?? new Dictionary<string, string>())
            form.Add(new StringContent(valor), chave);
        if (foto is not null)
        {
            var arquivo = new ByteArrayContent(foto);
            arquivo.Headers.ContentType = new MediaTypeHeaderValue(tipoFoto);
            form.Add(arquivo, "foto", nomeFoto);
        }
        return form;
    }

    public static async Task<OcorrenciaResponse> CriarOcorrenciaAsync(this Conta cidadao, string? titulo = null,
        string categoria = "BURACO", IDictionary<string, string>? extras = null, byte[]? foto = null)
    {
        using var cliente = cidadao.Cliente();
        var resposta = await cliente.PostAsync("/ocorrencias", Ocorrencia(titulo, categoria: categoria, extras: extras, foto: foto));
        Assert.Equal(201, resposta.Codigo());
        return await resposta.LerAsync<OcorrenciaResponse>();
    }

    public static async Task<HttpResponseMessage> AlterarStatusAsync(this Conta gestor, long id, string status, string? observacao = null)
    {
        using var cliente = gestor.Cliente();
        return await cliente.PatchAsJsonAsync($"/ocorrencias/{id}/status", new { status, observacao });
    }

    public static async Task<OcorrenciaResponse> ResolverAsync(this Conta gestor, long id)
    {
        var resposta = await gestor.AlterarStatusAsync(id, "RESOLVIDA");
        Assert.Equal(200, resposta.Codigo());
        return await resposta.LerAsync<OcorrenciaResponse>();
    }

    public static HttpRequestMessage ComChave(this HttpRequestMessage requisicao, string chave)
    {
        requisicao.Headers.Add("Idempotency-Key", chave);
        return requisicao;
    }

    public static HttpRequestMessage Post(string rota, HttpContent conteudo) => new(HttpMethod.Post, rota) { Content = conteudo };
}
