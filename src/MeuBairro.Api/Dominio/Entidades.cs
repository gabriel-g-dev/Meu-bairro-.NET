namespace MeuBairro.Api.Dominio;

public enum Perfil { CIDADAO, GESTOR }

public enum StatusSolicitacao { ABERTA, EM_ANALISE, RESOLVIDA }

public enum Prioridade { BAIXA, MEDIA, ALTA }

// ENDERECOS
public class Endereco
{
    public long Id { get; set; }
    public string Logradouro { get; set; } = "";
    public string? Numero { get; set; }
    public string? Complemento { get; set; }
    public string Bairro { get; set; } = "";
    public string Cidade { get; set; } = "";
    public string Estado { get; set; } = "";
    public string Cep { get; set; } = "";
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    public bool MesmosDados(Endereco outro) =>
        Logradouro == outro.Logradouro && Numero == outro.Numero && Complemento == outro.Complemento
        && Bairro == outro.Bairro && Cidade == outro.Cidade && Estado == outro.Estado && Cep == outro.Cep
        && Latitude == outro.Latitude && Longitude == outro.Longitude;

    public void CopiarDe(Endereco origem)
    {
        Logradouro = origem.Logradouro;
        Numero = origem.Numero;
        Complemento = origem.Complemento;
        Bairro = origem.Bairro;
        Cidade = origem.Cidade;
        Estado = origem.Estado;
        Cep = origem.Cep;
        Latitude = origem.Latitude;
        Longitude = origem.Longitude;
    }
}

// USUARIOS: cidadãos
public class Usuario
{
    public long Id { get; set; }
    public string Nome { get; set; } = "";
    public string Email { get; set; } = "";
    public string Senha { get; set; } = "";
    public string? Telefone { get; set; }
    public DateTime DataCadastro { get; set; }
    public long EnderecoId { get; set; }
    public Endereco Endereco { get; set; } = null!;
}

// FUNCIONARIOS: equipe da prefeitura, autenticada com o perfil GESTOR
public class Funcionario
{
    public long Id { get; set; }
    public string Nome { get; set; } = "";
    public string Setor { get; set; } = "";
    public string Cargo { get; set; } = "";
    public string Email { get; set; } = "";
    public string Senha { get; set; } = "";
    public string? Telefone { get; set; }
}

// CATEGORIAS
public class Categoria
{
    public long Id { get; set; }
    public string Nome { get; set; } = "";
    public string? Descricao { get; set; }
    public int PrazoDias { get; set; }
}

// SOLICITACOES: as ocorrências registradas pelos cidadãos
public class Solicitacao
{
    public long Id { get; set; }
    public string Titulo { get; set; } = "";
    public string Descricao { get; set; } = "";
    public StatusSolicitacao Status { get; set; }
    public Prioridade Prioridade { get; set; }
    public DateTime DataAbertura { get; set; }
    public DateTime? DataConclusao { get; set; }
    public long UsuarioId { get; set; }
    public Usuario Usuario { get; set; } = null!;
    public long? FuncionarioId { get; set; }
    public Funcionario? Funcionario { get; set; }
    public long CategoriaId { get; set; }
    public Categoria Categoria { get; set; } = null!;
    public long EnderecoId { get; set; }
    public Endereco Endereco { get; set; } = null!;
    public List<Andamento> Andamentos { get; set; } = [];
    public List<Anexo> Anexos { get; set; } = [];
    public Avaliacao? Avaliacao { get; set; }

    public bool EstaAberta => Status == StatusSolicitacao.ABERTA;

    // Uma ocorrência resolvida não muda mais de status e não é possível "trocar" para o status atual.
    public static void ValidarTransicao(StatusSolicitacao atual, StatusSolicitacao novo)
    {
        if (atual == StatusSolicitacao.RESOLVIDA)
            throw new RegraNegocioException("A ocorrência já foi resolvida e não pode mudar de status");
        if (atual == novo)
            throw new RegraNegocioException($"A ocorrência já está com o status {atual}");
    }

    public static DateTime PrazoEstimado(DateTime abertura, int prazoDias) => abertura.AddDays(prazoDias);

    public static bool EstaAtrasada(StatusSolicitacao status, DateTime abertura, int prazoDias, DateTime agora) =>
        status != StatusSolicitacao.RESOLVIDA && agora > PrazoEstimado(abertura, prazoDias);
}

// ANDAMENTOS: histórico de atendimento, sempre registrado por um funcionário
public class Andamento
{
    public long Id { get; set; }
    public string Descricao { get; set; } = "";
    public StatusSolicitacao Status { get; set; }
    public DateTime Data { get; set; }
    public long SolicitacaoId { get; set; }
    public Solicitacao Solicitacao { get; set; } = null!;
    public long FuncionarioId { get; set; }
    public Funcionario Funcionario { get; set; } = null!;
}

// ANEXOS: fotos e arquivos da solicitação
public class Anexo
{
    public long Id { get; set; }
    public string Nome { get; set; } = "";
    public string Tipo { get; set; } = "";
    public string Url { get; set; } = "";
    public long SolicitacaoId { get; set; }

    public bool EhImagem => Tipo.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

// AVALIACOES: no máximo uma por solicitação
public class Avaliacao
{
    public long Id { get; set; }
    public int Nota { get; set; }
    public string? Comentario { get; set; }
    public DateTime Data { get; set; }
    public long SolicitacaoId { get; set; }
}
