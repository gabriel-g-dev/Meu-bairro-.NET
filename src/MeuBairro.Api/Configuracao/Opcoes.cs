namespace MeuBairro.Api.Configuracao;

public enum ProvedorBanco { Sqlite, Oracle }

public enum EstrategiaId { Identity, Sequence }

public sealed class BancoOptions
{
    public const string Secao = "Banco";

    public ProvedorBanco Provedor { get; set; } = ProvedorBanco.Sqlite;

    // Cria as tabelas no SQLite local na inicialização. Nunca é aplicado ao Oracle.
    public bool InicializarSqlite { get; set; } = true;

    public int TimeoutVerificacaoSegundos { get; set; } = 3;

    // Acima desse tempo a verificação de saúde do banco passa a "Degraded".
    public int LimiteLatenciaMs { get; set; } = 1500;

    public OracleOptions Oracle { get; set; } = new();

    public CodigosOptions Codigos { get; set; } = new();
}

public sealed class OracleOptions
{
    public string? Esquema { get; set; }

    // Enquanto for false, qualquer INSERT/UPDATE/DELETE/DDL é bloqueado antes de chegar ao Oracle.
    public bool PermitirEscrita { get; set; }

    public EstrategiaId EstrategiaId { get; set; } = EstrategiaId.Identity;

    public int VersaoCompatibilidade { get; set; } = 19;

    // Nome da sequence por tabela (ex.: "SOLICITACOES": "SEQ_SOLICITACOES"), usado com EstrategiaId=Sequence.
    public Dictionary<string, string> Sequencias { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

// Os valores gravados nas colunas CHAR(1) ainda não foram confirmados no Oracle do grupo,
// por isso ficam configuráveis. Os padrões abaixo valem para o SQLite local.
public sealed class CodigosOptions
{
    public Dictionary<string, string> Status { get; set; } = new()
    {
        ["ABERTA"] = "A",
        ["EM_ANALISE"] = "E",
        ["RESOLVIDA"] = "R"
    };

    public Dictionary<string, string> Prioridade { get; set; } = new()
    {
        ["BAIXA"] = "B",
        ["MEDIA"] = "M",
        ["ALTA"] = "A"
    };
}

public sealed class JwtOptions
{
    public const string Secao = "Jwt";

    public string? Segredo { get; set; }
    public string Emissor { get; set; } = "meu-bairro-api-dotnet";
    public string Audiencia { get; set; } = "meu-bairro-clientes";
    public int ExpiracaoMinutos { get; set; } = 120;
    public int ToleranciaSegundos { get; set; } = 30;
}

public sealed class SenhaOptions
{
    public const string Secao = "Senhas";

    public int FatorTrabalho { get; set; } = 10;
}

public sealed class AplicacaoOptions
{
    public const string Secao = "Aplicacao";

    public string FusoHorario { get; set; } = "America/Sao_Paulo";
}

public sealed class FotosOptions
{
    public const string Secao = "Fotos";

    public string Pasta { get; set; } = "data/fotos";
    public long TamanhoMaximoBytes { get; set; } = 5 * 1024 * 1024;
    public int MaximoAnexosPorOcorrencia { get; set; } = 5;
}

// Completa o endereço quando a requisição traz só o texto do logradouro (ou nenhum endereço, no cadastro),
// já que ENDERECOS exige bairro, cidade, estado e CEP.
public sealed class EnderecoPadraoOptions
{
    public const string Secao = "EnderecoPadrao";

    public string? Logradouro { get; set; }
    public string? Bairro { get; set; }
    public string? Cidade { get; set; }
    public string? Estado { get; set; }
    public string? Cep { get; set; }
}

public sealed class CategoriasOptions
{
    public const string Secao = "Categorias";

    // Nome da categoria no banco -> código exposto pela API (ex.: "Iluminação pública": "ILUMINACAO").
    public Dictionary<string, string> Codigos { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class IdempotenciaOptions
{
    public const string Secao = "Idempotencia";

    public int RetencaoHoras { get; set; } = 24;
    public int BloqueioSegundos { get; set; } = 30;
    public int TamanhoMaximoChave { get; set; } = 100;
    public int TamanhoMaximoRespostaBytes { get; set; } = 256 * 1024;
    public int IntervaloLimpezaMinutos { get; set; } = 30;
}

public sealed class LimitesOptions
{
    public const string Secao = "LimiteRequisicoes";

    public JanelaOptions Global { get; set; } = new() { Limite = 300, JanelaSegundos = 60 };
    public JanelaOptions Autenticacao { get; set; } = new() { Limite = 10, JanelaSegundos = 60 };
    public JanelaOptions Leitura { get; set; } = new() { Limite = 120, JanelaSegundos = 60 };
    public JanelaOptions Escrita { get; set; } = new() { Limite = 30, JanelaSegundos = 60 };
}

public sealed class JanelaOptions
{
    public int Limite { get; set; }
    public int JanelaSegundos { get; set; }
}

// Proxies reversos cujos cabeçalhos X-Forwarded-For/Proto são aceitos. Vazio = nenhum (padrão).
public sealed class ProxyOptions
{
    public const string Secao = "Proxy";

    public string[] IpsConfiaveis { get; set; } = [];
    public string[] RedesConfiaveis { get; set; } = [];
}

public sealed class DadosExemploOptions
{
    public const string Secao = "DadosExemplo";

    public bool Habilitado { get; set; }
    public string? Senha { get; set; }
}
