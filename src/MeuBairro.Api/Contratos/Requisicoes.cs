using System.ComponentModel.DataAnnotations;
using MeuBairro.Api.Dominio;
using MeuBairro.Api.Infra;

namespace MeuBairro.Api.Contratos;

// Os limites de tamanho seguem as colunas do Oracle (ex.: SOLICITACAO_TITULO VARCHAR2(100)).

public sealed class CadastroUsuarioRequest : IValidatableObject
{
    [Required(ErrorMessage = "O nome é obrigatório")]
    [Tamanho(100, ErrorMessage = "O nome deve ter no máximo 100 caracteres")]
    public string? Nome { get; init; }

    [Required(ErrorMessage = "O e-mail é obrigatório")]
    [Tamanho(100, ErrorMessage = "O e-mail deve ter no máximo 100 caracteres")]
    [EmailValido]
    public string? Email { get; init; }

    [Required(ErrorMessage = "A senha é obrigatória")]
    [SenhaValida]
    public string? Senha { get; init; }

    [TelefoneValido]
    public string? Telefone { get; init; }

    public EnderecoRequest? Endereco { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        Validacao.Aninhado(Endereco, nameof(Endereco));
}

public sealed class LoginRequest : IValidatableObject
{
    [Required(ErrorMessage = "O e-mail é obrigatório")]
    [EmailValido]
    public string? Email { get; init; }

    [Required(ErrorMessage = "A senha é obrigatória")]
    public string? Senha { get; init; }

    // Opcional: restringe o login a cidadãos (USUARIOS) ou funcionários (FUNCIONARIOS).
    public string? Perfil { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Perfil is not null && !Enums.TentarLer<Perfil>(Perfil, out _))
            yield return new ValidationResult($"Valor inválido para o campo 'perfil'. Valores aceitos: {Enums.Aceitos<Perfil>()}",
                [nameof(Perfil)]);
    }
}

public sealed class GestorRequest
{
    [Required(ErrorMessage = "O nome é obrigatório")]
    [Tamanho(100, ErrorMessage = "O nome deve ter no máximo 100 caracteres")]
    public string? Nome { get; init; }

    [Required(ErrorMessage = "O e-mail é obrigatório")]
    [Tamanho(100, ErrorMessage = "O e-mail deve ter no máximo 100 caracteres")]
    [EmailValido]
    public string? Email { get; init; }

    [Required(ErrorMessage = "A senha é obrigatória")]
    [SenhaValida]
    public string? Senha { get; init; }

    [Tamanho(50, ErrorMessage = "O setor deve ter no máximo 50 caracteres")]
    public string? Setor { get; init; }

    [Tamanho(50, ErrorMessage = "O cargo deve ter no máximo 50 caracteres")]
    public string? Cargo { get; init; }

    [TelefoneValido]
    public string? Telefone { get; init; }
}

public sealed class AtualizacaoPerfilRequest
{
    [Required(ErrorMessage = "O nome é obrigatório")]
    [Tamanho(100, ErrorMessage = "O nome deve ter no máximo 100 caracteres")]
    public string? Nome { get; init; }

    [TelefoneValido]
    public string? Telefone { get; init; }
}

public sealed class EnderecoRequest
{
    [Required(ErrorMessage = "O logradouro é obrigatório")]
    [Tamanho(100, ErrorMessage = "O logradouro deve ter no máximo 100 caracteres")]
    public string? Logradouro { get; init; }

    [Tamanho(10, ErrorMessage = "O número deve ter no máximo 10 caracteres")]
    public string? Numero { get; init; }

    [Tamanho(50, ErrorMessage = "O complemento deve ter no máximo 50 caracteres")]
    public string? Complemento { get; init; }

    [Required(ErrorMessage = "O bairro é obrigatório")]
    [Tamanho(50, ErrorMessage = "O bairro deve ter no máximo 50 caracteres")]
    public string? Bairro { get; init; }

    [Required(ErrorMessage = "A cidade é obrigatória")]
    [Tamanho(50, ErrorMessage = "A cidade deve ter no máximo 50 caracteres")]
    public string? Cidade { get; init; }

    [Required(ErrorMessage = "O estado é obrigatório")]
    [RegularExpression("^[A-Za-z]{2}$", ErrorMessage = "O estado deve ser a sigla com 2 letras")]
    public string? Estado { get; init; }

    [Required(ErrorMessage = "O CEP é obrigatório")]
    [RegularExpression("^[0-9]{5}-?[0-9]{3}$", ErrorMessage = "O CEP deve ter 8 dígitos")]
    public string? Cep { get; init; }

    [Range(-90.0, 90.0, ErrorMessage = "A latitude deve estar entre -90 e 90")]
    public decimal? Latitude { get; init; }

    [Range(-180.0, 180.0, ErrorMessage = "A longitude deve estar entre -180 e 180")]
    public decimal? Longitude { get; init; }
}

// Recebida como multipart/form-data, com a foto opcional no mesmo envio.
// O endereço pode vir como texto livre (campo "endereco"), detalhado (logradouro, bairro, ...) ou por enderecoId.
public sealed class OcorrenciaForm : IValidatableObject
{
    [Required(ErrorMessage = "O título é obrigatório")]
    [Tamanho(100, Minimo = 5, ErrorMessage = "O título deve ter entre 5 e 100 caracteres")]
    public string? Titulo { get; set; }

    [Required(ErrorMessage = "A descrição é obrigatória")]
    [Tamanho(500, Minimo = 10, ErrorMessage = "A descrição deve ter entre 10 e 500 caracteres")]
    public string? Descricao { get; set; }

    [Required(ErrorMessage = "A categoria é obrigatória")]
    public string? Categoria { get; set; }

    // Até 200 para aceitar de volta, na edição, o endereço formatado das respostas (logradouro, bairro e cidade).
    // O limite de 100 do logradouro é conferido depois, quando o texto realmente muda.
    [Tamanho(200, ErrorMessage = "O endereço deve ter no máximo 200 caracteres")]
    public string? Endereco { get; set; }

    [Tamanho(100, ErrorMessage = "O logradouro deve ter no máximo 100 caracteres")]
    public string? Logradouro { get; set; }

    [Tamanho(10, ErrorMessage = "O número deve ter no máximo 10 caracteres")]
    public string? Numero { get; set; }

    [Tamanho(50, ErrorMessage = "O complemento deve ter no máximo 50 caracteres")]
    public string? Complemento { get; set; }

    [Tamanho(50, ErrorMessage = "O bairro deve ter no máximo 50 caracteres")]
    public string? Bairro { get; set; }

    [Tamanho(50, ErrorMessage = "A cidade deve ter no máximo 50 caracteres")]
    public string? Cidade { get; set; }

    [RegularExpression("^[A-Za-z]{2}$", ErrorMessage = "O estado deve ser a sigla com 2 letras")]
    public string? Estado { get; set; }

    [RegularExpression("^[0-9]{5}-?[0-9]{3}$", ErrorMessage = "O CEP deve ter 8 dígitos")]
    public string? Cep { get; set; }

    public long? EnderecoId { get; set; }

    public string? Latitude { get; set; }

    public string? Longitude { get; set; }

    public IFormFile? Foto { get; set; }

    public decimal? LerLatitude() => Coordenadas.TentarLer(Latitude, out var valor) ? valor : null;

    public decimal? LerLongitude() => Coordenadas.TentarLer(Longitude, out var valor) ? valor : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (EnderecoId is null && string.IsNullOrWhiteSpace(Endereco) && string.IsNullOrWhiteSpace(Logradouro))
            yield return new ValidationResult("O endereço é obrigatório", [nameof(Endereco)]);

        foreach (var erro in Coordenadas.Validar(Latitude, nameof(Latitude), 90, "A latitude deve estar entre -90 e 90"))
            yield return erro;
        foreach (var erro in Coordenadas.Validar(Longitude, nameof(Longitude), 180, "A longitude deve estar entre -180 e 180"))
            yield return erro;
    }
}

public sealed class AnexoForm
{
    [Required(ErrorMessage = "O arquivo é obrigatório")]
    public IFormFile? Arquivo { get; set; }
}

public sealed class FiltroOcorrencias
{
    public string? Status { get; set; }
    public string? Categoria { get; set; }
    public string? Prioridade { get; set; }
    public string? Bairro { get; set; }
    public bool? Minhas { get; set; }
    public int? Page { get; set; }
    public int? Size { get; set; }
    public string[]? Sort { get; set; }
}

public sealed class AtualizacaoStatusRequest : IValidatableObject
{
    public string? Status { get; init; }

    [Tamanho(300, ErrorMessage = "A observação deve ter no máximo 300 caracteres")]
    public string? Observacao { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        Validacao.EnumObrigatorio<StatusSolicitacao>(Status, nameof(Status), "O status é obrigatório");
}

public sealed class AtualizacaoPrioridadeRequest : IValidatableObject
{
    public string? Prioridade { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        Validacao.EnumObrigatorio<Prioridade>(Prioridade, nameof(Prioridade), "A prioridade é obrigatória");
}

public sealed class AndamentoRequest
{
    [Required(ErrorMessage = "A descrição é obrigatória")]
    [Tamanho(300, ErrorMessage = "A descrição deve ter no máximo 300 caracteres")]
    public string? Descricao { get; init; }
}

public sealed class AvaliacaoRequest
{
    [Required(ErrorMessage = "A nota é obrigatória")]
    [Range(1, 5, ErrorMessage = "A nota deve ser um número inteiro de 1 a 5")]
    public int? Nota { get; init; }

    [Tamanho(300, ErrorMessage = "O comentário deve ter no máximo 300 caracteres")]
    public string? Comentario { get; init; }
}

public sealed class CategoriaRequest
{
    [Required(ErrorMessage = "O nome é obrigatório")]
    [Tamanho(50, Minimo = 2, ErrorMessage = "O nome deve ter entre 2 e 50 caracteres")]
    public string? Nome { get; init; }

    [Tamanho(200, ErrorMessage = "A descrição deve ter no máximo 200 caracteres")]
    public string? Descricao { get; init; }

    [Required(ErrorMessage = "O prazo em dias é obrigatório")]
    [Range(1, 999, ErrorMessage = "O prazo deve ser de 1 a 999 dias")]
    public int? PrazoDias { get; init; }
}
