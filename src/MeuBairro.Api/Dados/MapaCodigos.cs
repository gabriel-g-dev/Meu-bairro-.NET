using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MeuBairro.Api.Dados;

// Converte os enums da API para os códigos de 1 caractere das colunas CHAR(1) do Oracle.
public sealed class MapaCodigos<TEnum> where TEnum : struct, Enum
{
    private readonly Dictionary<TEnum, string> paraCodigo = [];
    private readonly Dictionary<string, TEnum> paraValor = new(StringComparer.Ordinal);

    public MapaCodigos(IReadOnlyDictionary<string, string> configuracao)
    {
        var porNome = new Dictionary<string, string>(configuracao, StringComparer.OrdinalIgnoreCase);
        foreach (var valor in Enum.GetValues<TEnum>())
        {
            if (!porNome.TryGetValue(valor.ToString(), out var codigo) || codigo is null || codigo.Trim().Length != 1)
                throw new InvalidOperationException(
                    $"Configure um código de exatamente 1 caractere para {typeof(TEnum).Name}.{valor} em Banco:Codigos");

            codigo = codigo.Trim();
            if (!paraValor.TryAdd(codigo, valor))
                throw new InvalidOperationException($"O código '{codigo}' está repetido em Banco:Codigos:{typeof(TEnum).Name}");
            paraCodigo[valor] = codigo;
        }
    }

    public string Assinatura => string.Join(",", paraCodigo.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));

    public string ParaCodigo(TEnum valor) => paraCodigo[valor];

    public TEnum ParaValor(string codigo) =>
        paraValor.TryGetValue(codigo.Trim(), out var valor)
            ? valor
            : throw new InvalidOperationException(
                $"Código '{codigo.Trim()}' desconhecido para {typeof(TEnum).Name}. Ajuste Banco:Codigos:{typeof(TEnum).Name}");

    public ValueConverter<TEnum, string> Conversor() => new(v => ParaCodigo(v), c => ParaValor(c));
}
