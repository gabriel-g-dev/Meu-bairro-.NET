using MeuBairro.Api.Dominio;

namespace MeuBairro.Api.Contratos;

public sealed record CampoErro(string Field, string Message);

public sealed record ErroResposta(
    DateTime Timestamp,
    int Status,
    string Error,
    string Message,
    string Path,
    IReadOnlyList<CampoErro> Fields);

public sealed record UsuarioResponse(long Id, string Nome, string Email, Perfil Perfil);

public sealed record LoginResponse(string Token, string Tipo, DateTime ExpiraEm, UsuarioResponse Usuario);

public sealed record PerfilResponse(
    long Id,
    string Nome,
    string Email,
    Perfil Perfil,
    string? Telefone,
    DateTime? DataCadastro,
    long? EnderecoId,
    string? Setor,
    string? Cargo);

public sealed record EnderecoResponse(
    long Id,
    string Logradouro,
    string? Numero,
    string? Complemento,
    string Bairro,
    string Cidade,
    string Estado,
    string Cep,
    decimal? Latitude,
    decimal? Longitude,
    string Formatado);

public sealed record CategoriaResponse(long Id, string Codigo, string Nome, string? Descricao, int PrazoDias);

public sealed record OcorrenciaResponse(
    long Id,
    string Titulo,
    string Descricao,
    string Categoria,
    string CategoriaNome,
    StatusSolicitacao Status,
    Prioridade Prioridade,
    string Endereco,
    decimal? Latitude,
    decimal? Longitude,
    long EnderecoId,
    string Bairro,
    string Cidade,
    string Estado,
    string Cep,
    string? FotoUrl,
    DateTime CriadoEm,
    DateTime? DataConclusao,
    DateTime PrazoEstimado,
    bool Atrasada,
    long UsuarioId,
    long? ResponsavelId);

public sealed record PaginaResponse<T>(
    IReadOnlyList<T> Conteudo,
    int Pagina,
    int Tamanho,
    long TotalElementos,
    int TotalPaginas,
    bool Ultima);

public sealed record HistoricoResponse(
    long Id,
    string Tipo,
    string Descricao,
    DateTime DataRegistro,
    StatusSolicitacao Status,
    string? SetorResponsavel);

public sealed record AnexoResponse(long Id, string Nome, string Tipo, string Url);

public sealed record AvaliacaoResponse(long Id, long OcorrenciaId, int Nota, string? Comentario, DateTime Data);

public sealed record ResumoOcorrenciasResponse(
    long Total,
    long Abertas,
    long EmAnalise,
    long Resolvidas,
    long PrioridadeAlta,
    long PrioridadeMedia,
    long PrioridadeBaixa);

public sealed record PainelCategoriaResponse(string Codigo, string Nome, int Total, int Pendentes);

public sealed record PainelBairroResponse(
    string? Bairro,
    int Total,
    int Abertas,
    int EmAnalise,
    int Resolvidas,
    int Atrasadas,
    int ResolvidasNoPrazo,
    double? TempoMedioResolucaoDias,
    IReadOnlyList<PainelCategoriaResponse> PorCategoria);
