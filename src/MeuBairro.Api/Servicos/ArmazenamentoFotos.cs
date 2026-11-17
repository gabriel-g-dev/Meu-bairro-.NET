using System.Text.RegularExpressions;
using MeuBairro.Api.Configuracao;
using MeuBairro.Api.Dominio;
using Microsoft.Extensions.Options;

namespace MeuBairro.Api.Servicos;

public sealed record FotoSalva(string NomeOriginal, string Tipo, string Url);

public static class TipoImagem
{
    private static readonly byte[] AssinaturaPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Identifica o formato pelo conteúdo do arquivo, e não pela extensão ou Content-Type enviados pelo cliente.
    public static (string Tipo, string Extensao)? Detectar(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ("image/jpeg", ".jpg");
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(AssinaturaPng))
            return ("image/png", ".png");
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
            return ("image/webp", ".webp");
        return null;
    }
}

public sealed partial class ArmazenamentoFotos
{
    public const string PrefixoUrl = "/fotos/";

    private readonly FotosOptions opcoes;
    private readonly ILogger<ArmazenamentoFotos> log;

    public ArmazenamentoFotos(IOptions<FotosOptions> opcoes, IHostEnvironment ambiente, ILogger<ArmazenamentoFotos> log)
    {
        this.opcoes = opcoes.Value;
        this.log = log;
        var pasta = this.opcoes.Pasta;
        Pasta = Path.GetFullPath(Path.IsPathRooted(pasta) ? pasta : Path.Combine(ambiente.ContentRootPath, pasta));
    }

    public string Pasta { get; }

    public async Task<FotoSalva> SalvarAsync(IFormFile arquivo, CancellationToken ct)
    {
        if (arquivo.Length == 0)
            throw new RequisicaoInvalidaException("O arquivo enviado está vazio");
        if (arquivo.Length > opcoes.TamanhoMaximoBytes)
            throw new ApiException(StatusCodes.Status413PayloadTooLarge,
                $"A foto deve ter no máximo {opcoes.TamanhoMaximoBytes / (1024 * 1024)} MB");

        using var memoria = new MemoryStream((int)arquivo.Length);
        await arquivo.CopyToAsync(memoria, ct);
        var bytes = memoria.ToArray();
        var tipo = TipoImagem.Detectar(bytes)
                   ?? throw new RequisicaoInvalidaException("Formato de foto inválido. Envie uma imagem JPEG, PNG ou WEBP");

        // O nome gravado em disco é sempre gerado pela API; o nome enviado pelo cliente nunca vira caminho.
        var nome = Guid.NewGuid().ToString("N") + tipo.Extensao;
        try
        {
            Directory.CreateDirectory(Pasta);
            await File.WriteAllBytesAsync(Path.Combine(Pasta, nome), bytes, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Falha ao gravar a foto na pasta configurada ({Tipo})", ex.GetType().Name);
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "Não foi possível salvar a foto. Tente novamente mais tarde");
        }

        return new FotoSalva(NomeSeguro(arquivo.FileName, nome), tipo.Tipo, PrefixoUrl + nome);
    }

    public string? Caminho(string arquivo)
    {
        if (!NomeArquivo().IsMatch(arquivo))
            return null;
        var caminho = Path.GetFullPath(Path.Combine(Pasta, arquivo));
        var dentroDaPasta = caminho.StartsWith(Pasta + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return dentroDaPasta && File.Exists(caminho) ? caminho : null;
    }

    public static string TipoPorExtensao(string arquivo) => Path.GetExtension(arquivo) switch
    {
        ".jpg" => "image/jpeg",
        ".png" => "image/png",
        _ => "image/webp"
    };

    public void Excluir(string? url)
    {
        if (url is null || !url.StartsWith(PrefixoUrl, StringComparison.Ordinal))
            return;
        var nome = url[PrefixoUrl.Length..];
        if (!NomeArquivo().IsMatch(nome))
            return;
        try
        {
            File.Delete(Path.Combine(Pasta, nome));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Não foi possível excluir a foto {Arquivo} ({Tipo})", nome, ex.GetType().Name);
        }
    }

    public bool PodeGravar()
    {
        try
        {
            Directory.CreateDirectory(Pasta);
            var teste = Path.Combine(Pasta, $".verificacao-{Guid.NewGuid():N}");
            File.WriteAllText(teste, "ok");
            File.Delete(teste);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Guarda só o nome do arquivo (sem pastas, ".." ou caracteres de controle) para exibição em ANEXO_NOME.
    public static string NomeSeguro(string? original, string alternativo)
    {
        var nome = Path.GetFileName((original ?? "").Replace('\\', '/'));
        var invalidos = Path.GetInvalidFileNameChars();
        nome = new string(nome.Where(c => !char.IsControl(c) && !invalidos.Contains(c) && c != '/').ToArray())
            .Trim().TrimStart('.');
        if (nome.Length == 0)
            return alternativo;
        if (nome.Length <= 100)
            return nome;
        // Mantém o final (com a extensão) sem começar pela metade de um par substituto, como um emoji.
        var final = nome[^100..];
        return char.IsLowSurrogate(final[0]) ? final[1..] : final;
    }

    [GeneratedRegex("^[0-9a-f]{32}\\.(jpg|png|webp)$")]
    private static partial Regex NomeArquivo();
}
