using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MeuBairro.Api.Contratos;
using MeuBairro.Api.Dominio;

namespace MeuBairro.Api.Infra;

public static class Validacao
{
    public static List<CampoErro> Validar(object objeto)
    {
        var resultados = new List<ValidationResult>();
        var contexto = new ValidationContext(objeto);
        Validator.TryValidateObject(objeto, contexto, resultados, validateAllProperties: true);

        // O Validator só chama IValidatableObject quando os atributos passam; aqui as duas etapas sempre rodam.
        if (resultados.Count > 0 && objeto is IValidatableObject validavel)
            resultados.AddRange(validavel.Validate(contexto));

        return resultados
            .SelectMany(r => (r.MemberNames.Any() ? r.MemberNames : new[] { "" })
                .Select(campo => new CampoErro(NomeCampo(campo), r.ErrorMessage ?? "Valor inválido")))
            .Distinct()
            .ToList();
    }

    public static IEnumerable<ValidationResult> Aninhado(object? filho, string prefixo)
    {
        if (filho is null)
            yield break;

        foreach (var erro in Validar(filho))
            yield return new ValidationResult(erro.Message, [$"{prefixo}.{erro.Field}"]);
    }

    public static IEnumerable<ValidationResult> EnumObrigatorio<T>(string? valor, string campo, string mensagemObrigatorio)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(valor))
            yield return new ValidationResult(mensagemObrigatorio, [campo]);
        else if (!Enums.TentarLer<T>(valor, out _))
            yield return new ValidationResult(
                $"Valor inválido para o campo '{NomeCampo(campo)}'. Valores aceitos: {Enums.Aceitos<T>()}", [campo]);
    }

    public static string NomeCampo(string membro) =>
        string.Join('.', membro.Split('.').Select(p => p.Length == 0 ? p : char.ToLowerInvariant(p[0]) + p[1..]));
}

// Conta o tamanho sem os espaços das pontas, que são removidos antes de gravar.
[AttributeUsage(AttributeTargets.Property)]
public sealed class TamanhoAttribute(int maximo) : ValidationAttribute
{
    public int Minimo { get; init; }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not string texto)
            return ValidationResult.Success;

        var tamanho = texto.Trim().Length;
        if (tamanho == 0 && Minimo == 0)
            return ValidationResult.Success;
        return tamanho < Minimo || tamanho > maximo
            ? new ValidationResult(ErrorMessage, Membro(validationContext))
            : ValidationResult.Success;
    }

    internal static string[] Membro(ValidationContext contexto) =>
        contexto.MemberName is null ? [] : [contexto.MemberName];
}

[AttributeUsage(AttributeTargets.Property)]
public sealed partial class EmailValidoAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) =>
        value is string email && !string.IsNullOrWhiteSpace(email) && !Formato().IsMatch(email.Trim())
            ? new ValidationResult("O e-mail informado é inválido", TamanhoAttribute.Membro(validationContext))
            : ValidationResult.Success;

    [GeneratedRegex(@"^[^@\s'"";<>()]+@[^@\s'"";<>()]+\.[^@\s'"";<>()]+$")]
    private static partial Regex Formato();
}

// O BCrypt considera só os primeiros 72 bytes da senha; acima disso o restante seria ignorado.
[AttributeUsage(AttributeTargets.Property)]
public sealed class SenhaValidaAttribute : ValidationAttribute
{
    public const int MaximoBytes = 72;

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not string senha || senha.Length == 0)
            return ValidationResult.Success;
        if (senha.Length is < 6 or > 60)
            return new ValidationResult("A senha deve ter entre 6 e 60 caracteres", TamanhoAttribute.Membro(validationContext));
        if (Encoding.UTF8.GetByteCount(senha) > MaximoBytes)
            return new ValidationResult("A senha deve ter no máximo 72 bytes (caracteres acentuados ocupam mais de 1 byte)",
                TamanhoAttribute.Membro(validationContext));
        return ValidationResult.Success;
    }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed partial class TelefoneValidoAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) =>
        value is string telefone && !string.IsNullOrWhiteSpace(telefone) && !Formato().IsMatch(telefone.Trim())
            ? new ValidationResult("O telefone deve ter de 8 a 20 caracteres, com dígitos, espaços, parênteses, + ou -",
                TamanhoAttribute.Membro(validationContext))
            : ValidationResult.Success;

    [GeneratedRegex(@"^[0-9 ()+\-]{8,20}$")]
    private static partial Regex Formato();
}

public static class Coordenadas
{
    public static bool TentarLer(string? texto, out decimal? valor)
    {
        valor = null;
        if (string.IsNullOrWhiteSpace(texto))
            return true;
        if (!decimal.TryParse(texto.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var numero))
            return false;
        valor = numero;
        return true;
    }

    public static IEnumerable<ValidationResult> Validar(string? texto, string campo, decimal limite, string mensagemLimite)
    {
        if (!TentarLer(texto, out var valor))
            yield return new ValidationResult($"Valor inválido para o campo '{Validacao.NomeCampo(campo)}'", [campo]);
        else if (valor is { } v && (v < -limite || v > limite))
            yield return new ValidationResult(mensagemLimite, [campo]);
    }

    // NUMBER(9,6) no Oracle: seis casas decimais.
    public static decimal? Arredondar(decimal? valor) =>
        valor is null ? null : Math.Round(valor.Value, 6, MidpointRounding.AwayFromZero);
}

public static class Enums
{
    public static bool TentarLer<T>(string? valor, out T resultado) where T : struct, Enum
    {
        resultado = default;
        if (string.IsNullOrWhiteSpace(valor))
            return false;

        var nome = valor.Trim();
        foreach (var candidato in Enum.GetNames<T>())
        {
            if (string.Equals(candidato, nome, StringComparison.OrdinalIgnoreCase))
            {
                resultado = Enum.Parse<T>(candidato);
                return true;
            }
        }
        return false;
    }

    public static T Ler<T>(string valor, string parametro) where T : struct, Enum =>
        TentarLer<T>(valor, out var resultado)
            ? resultado
            : throw new RequisicaoInvalidaException(
                $"Valor inválido para o parâmetro '{parametro}': {valor}. Valores aceitos: {Aceitos<T>()}");

    public static string Aceitos<T>() where T : struct, Enum => "[" + string.Join(", ", Enum.GetNames<T>()) + "]";
}

public static class Texto
{
    public static string? Limpar(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    public static string SomenteDigitos(string valor) => new(valor.Where(char.IsAsciiDigit).ToArray());

    public static string Limitar(string valor, int maximo) =>
        valor.Length <= maximo ? valor : valor[..(maximo - 3)] + "...";
}

public sealed class ValidacaoFilter<T> : IEndpointFilter where T : class, new()
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var argumento = context.Arguments.OfType<T>().FirstOrDefault();
        if (argumento is null)
        {
            // Formulário sem nenhum campo conhecido chega nulo do binding: valida como vazio para listar os campos faltantes.
            if (!context.HttpContext.Request.HasFormContentType)
                throw new RequisicaoInvalidaException("O corpo da requisição está ausente ou não é um JSON válido");
            argumento = new T();
        }

        var erros = Validacao.Validar(argumento);
        if (erros.Count > 0)
            throw new ValidacaoException(erros);

        return await next(context);
    }
}
