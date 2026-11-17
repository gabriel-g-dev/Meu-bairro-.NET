using System.Globalization;
using System.Text;

namespace MeuBairro.Api.Dominio;

// A tabela CATEGORIAS não tem coluna de código, mas a API identifica as categorias por códigos como "BURACO".
// O código é derivado do nome (sem acentos, em maiúsculas) ou definido explicitamente na configuração.
public static class CategoriaCodigo
{
    public const int TamanhoMaximo = 20;

    // Categorias criadas na inicialização: não podem ser excluídas nem ter o código alterado.
    public static readonly IReadOnlyList<string> CodigosPadrao =
        ["BURACO", "ILUMINACAO", "LIXO", "ALAGAMENTO", "CALCADA", "OUTRO"];

    public static string Gerar(string nome, IReadOnlyDictionary<string, string>? apelidos = null)
    {
        var limpo = nome.Trim();
        if (apelidos is not null)
        {
            foreach (var (nomeConfigurado, codigo) in apelidos)
            {
                if (string.Equals(nomeConfigurado.Trim(), limpo, StringComparison.OrdinalIgnoreCase))
                    return codigo.Trim().ToUpperInvariant();
            }
        }

        var sb = new StringBuilder(limpo.Length);
        foreach (var caractere in limpo.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(caractere) == UnicodeCategory.NonSpacingMark)
                continue;

            var c = char.ToUpperInvariant(caractere);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
                sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '_')
                sb.Append('_');
        }

        var resultado = sb.ToString().TrimEnd('_');
        if (resultado.Length > TamanhoMaximo)
            resultado = resultado[..TamanhoMaximo].TrimEnd('_');
        return resultado.Length == 0 ? "CATEGORIA" : resultado;
    }
}
