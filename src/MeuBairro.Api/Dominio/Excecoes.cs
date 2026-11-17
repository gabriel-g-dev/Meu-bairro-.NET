using MeuBairro.Api.Contratos;

namespace MeuBairro.Api.Dominio;

public class ApiException(int status, string mensagem) : Exception(mensagem)
{
    public int Status { get; } = status;
}

public sealed class RecursoNaoEncontradoException(string mensagem) : ApiException(404, mensagem);

public sealed class RequisicaoInvalidaException(string mensagem) : ApiException(400, mensagem);

public sealed class RegraNegocioException(string mensagem) : ApiException(409, mensagem);

public sealed class AcessoNegadoException(string mensagem) : ApiException(403, mensagem);

public sealed class ValidacaoException(IReadOnlyList<CampoErro> campos)
    : ApiException(400, "Existem campos inválidos na requisição")
{
    public IReadOnlyList<CampoErro> Campos { get; } = campos;
}
