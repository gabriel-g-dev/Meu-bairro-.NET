using MeuBairro.Api.Configuracao;
using MeuBairro.Api.Dominio;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Options;

namespace MeuBairro.Api.Dados;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options, IOptions<BancoOptions> banco) : DbContext(options)
{
    public const string ProvedorOracle = "Oracle.EntityFrameworkCore";

    private readonly BancoOptions config = banco.Value;

    public DbSet<Endereco> Enderecos => Set<Endereco>();
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Funcionario> Funcionarios => Set<Funcionario>();
    public DbSet<Categoria> Categorias => Set<Categoria>();
    public DbSet<Solicitacao> Solicitacoes => Set<Solicitacao>();
    public DbSet<Andamento> Andamentos => Set<Andamento>();
    public DbSet<Anexo> Anexos => Set<Anexo>();
    public DbSet<Avaliacao> Avaliacoes => Set<Avaliacao>();

    public bool EhOracle => Database.ProviderName == ProvedorOracle;

    // O modelo muda conforme o provedor e os códigos configurados, então entra na chave do cache do EF.
    internal string ChaveModelo => string.Join("|",
        Database.ProviderName,
        config.Oracle.Esquema,
        config.Oracle.EstrategiaId,
        config.Oracle.PermitirEscrita,
        string.Join(",", config.Oracle.Sequencias.OrderBy(s => s.Key).Select(s => $"{s.Key}={s.Value}")),
        new MapaCodigos<StatusSolicitacao>(config.Codigos.Status).Assinatura,
        new MapaCodigos<Prioridade>(config.Codigos.Prioridade).Assinatura);

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        new MapeamentoOracle(config, EhOracle).Aplicar(modelBuilder);
}

// Mapeamento explícito do esquema Oracle do grupo (tabelas, colunas, tamanhos, nulidade, chaves e unicidade).
// Os tipos Oracle (VARCHAR2, CHAR, NUMBER, DATE) só são aplicados quando o provedor é Oracle;
// no SQLite a mesma estrutura é criada com os tipos nativos dele.
internal sealed class MapeamentoOracle(BancoOptions config, bool oracle)
{
    public void Aplicar(ModelBuilder mb)
    {
        if (oracle && !string.IsNullOrWhiteSpace(config.Oracle.Esquema))
            mb.HasDefaultSchema(config.Oracle.Esquema.Trim().ToUpperInvariant());

        var status = new MapaCodigos<StatusSolicitacao>(config.Codigos.Status).Conversor();
        var prioridade = new MapaCodigos<Prioridade>(config.Codigos.Prioridade).Conversor();

        mb.Entity<Endereco>(e =>
        {
            e.ToTable("ENDERECOS");
            Chave(e, "ENDERECO_ID", "ENDERECOS");
            Texto(e.Property(x => x.Logradouro), "ENDERECO_LOGRADOURO", 100, true);
            Texto(e.Property(x => x.Numero), "ENDERECO_NUMERO", 10, false);
            Texto(e.Property(x => x.Complemento), "ENDERECO_COMPLEMENTO", 50, false);
            Texto(e.Property(x => x.Bairro), "ENDERECO_BAIRRO", 50, true);
            Texto(e.Property(x => x.Cidade), "ENDERECO_CIDADE", 50, true);
            Fixo(e.Property(x => x.Estado), "ENDERECO_ESTADO", 2);
            Fixo(e.Property(x => x.Cep), "ENDERECO_CEP", 8);
            Coordenada(e.Property(x => x.Latitude), "ENDERECO_LATITUDE");
            Coordenada(e.Property(x => x.Longitude), "ENDERECO_LONGITUDE");
            e.HasIndex(x => x.Bairro);
        });

        mb.Entity<Usuario>(e =>
        {
            e.ToTable("USUARIOS");
            Chave(e, "USUARIO_ID", "USUARIOS");
            Texto(e.Property(x => x.Nome), "USUARIO_NOME", 100, true);
            Texto(e.Property(x => x.Email), "USUARIO_EMAIL", 100, true);
            Texto(e.Property(x => x.Senha), "USUARIO_SENHA", 255, true);
            Texto(e.Property(x => x.Telefone), "USUARIO_TELEFONE", 20, false);
            Data(e.Property(x => x.DataCadastro), "USUARIO_DATA_CADASTRO", true);
            Fk(e.Property(x => x.EnderecoId), "ENDERECO_ID", true);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasOne(x => x.Endereco).WithMany().HasForeignKey(x => x.EnderecoId).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<Funcionario>(e =>
        {
            e.ToTable("FUNCIONARIOS");
            Chave(e, "FUNCIONARIO_ID", "FUNCIONARIOS");
            Texto(e.Property(x => x.Nome), "FUNCIONARIO_NOME", 100, true);
            Texto(e.Property(x => x.Setor), "FUNCIONARIO_SETOR", 50, true);
            Texto(e.Property(x => x.Cargo), "FUNCIONARIO_CARGO", 50, true);
            Texto(e.Property(x => x.Email), "FUNCIONARIO_EMAIL", 100, true);
            Texto(e.Property(x => x.Senha), "FUNCIONARIO_SENHA", 255, true);
            Texto(e.Property(x => x.Telefone), "FUNCIONARIO_TELEFONE", 20, false);
            e.HasIndex(x => x.Email).IsUnique();
        });

        mb.Entity<Categoria>(e =>
        {
            e.ToTable("CATEGORIAS");
            Chave(e, "CATEGORIA_ID", "CATEGORIAS");
            var nome = Texto(e.Property(x => x.Nome), "CATEGORIA_NOME", 50, true);
            if (!oracle)
                nome.UseCollation("NOCASE");
            Texto(e.Property(x => x.Descricao), "CATEGORIA_DESCRICAO", 200, false);
            Numero(e.Property(x => x.PrazoDias), "CATEGORIA_PRAZO_DIAS", "NUMBER(3,0)");
            e.HasIndex(x => x.Nome).IsUnique();
        });

        mb.Entity<Solicitacao>(e =>
        {
            e.ToTable("SOLICITACOES");
            Chave(e, "SOLICITACAO_ID", "SOLICITACOES");
            Texto(e.Property(x => x.Titulo), "SOLICITACAO_TITULO", 100, true);
            Texto(e.Property(x => x.Descricao), "SOLICITACAO_DESCRICAO", 500, true);
            Codigo(e.Property(x => x.Status), "SOLICITACAO_STATUS", status);
            // Concorrência otimista sem coluna nova: o UPDATE da edição só vale se o status não mudou desde a leitura.
            e.Property(x => x.Status).IsConcurrencyToken();
            Codigo(e.Property(x => x.Prioridade), "SOLICITACAO_PRIORIDADE", prioridade);
            Data(e.Property(x => x.DataAbertura), "SOLICITACAO_DATA_ABERTURA", true);
            Data(e.Property(x => x.DataConclusao), "SOLICITACAO_DATA_CONCLUSAO", false);
            Fk(e.Property(x => x.UsuarioId), "USUARIO_ID", true);
            Fk(e.Property(x => x.FuncionarioId), "FUNCIONARIO_ID", false);
            Fk(e.Property(x => x.CategoriaId), "CATEGORIA_ID", true);
            Fk(e.Property(x => x.EnderecoId), "ENDERECO_ID", true);

            e.HasOne(x => x.Usuario).WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Funcionario).WithMany().HasForeignKey(x => x.FuncionarioId).IsRequired(false)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Categoria).WithMany().HasForeignKey(x => x.CategoriaId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Endereco).WithMany().HasForeignKey(x => x.EnderecoId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Andamentos).WithOne(a => a.Solicitacao).HasForeignKey(a => a.SolicitacaoId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Anexos).WithOne().HasForeignKey(a => a.SolicitacaoId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Avaliacao).WithOne().HasForeignKey<Avaliacao>(a => a.SolicitacaoId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.DataAbertura);
        });

        mb.Entity<Andamento>(e =>
        {
            e.ToTable("ANDAMENTOS");
            Chave(e, "ANDAMENTO_ID", "ANDAMENTOS");
            Texto(e.Property(x => x.Descricao), "ANDAMENTO_DESCRICAO", 300, true);
            Codigo(e.Property(x => x.Status), "ANDAMENTO_STATUS", status);
            Data(e.Property(x => x.Data), "ANDAMENTO_DATA", true);
            Fk(e.Property(x => x.SolicitacaoId), "SOLICITACAO_ID", true);
            Fk(e.Property(x => x.FuncionarioId), "FUNCIONARIO_ID", true);
            e.HasOne(x => x.Funcionario).WithMany().HasForeignKey(x => x.FuncionarioId).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<Anexo>(e =>
        {
            e.ToTable("ANEXOS");
            Chave(e, "ANEXO_ID", "ANEXOS");
            Texto(e.Property(x => x.Nome), "ANEXO_NOME", 100, true);
            Texto(e.Property(x => x.Tipo), "ANEXO_TIPO", 30, true);
            Texto(e.Property(x => x.Url), "ANEXO_URL", 300, true);
            Fk(e.Property(x => x.SolicitacaoId), "SOLICITACAO_ID", true);
            e.Ignore(x => x.EhImagem);
        });

        mb.Entity<Avaliacao>(e =>
        {
            e.ToTable("AVALIACOES");
            Chave(e, "AVALIACAO_ID", "AVALIACOES");
            Numero(e.Property(x => x.Nota), "AVALIACAO_NOTA", "NUMBER(1,0)");
            Texto(e.Property(x => x.Comentario), "AVALIACAO_COMENTARIO", 300, false);
            Data(e.Property(x => x.Data), "AVALIACAO_DATA", true);
            Fk(e.Property(x => x.SolicitacaoId), "SOLICITACAO_ID", true);
            e.HasIndex(x => x.SolicitacaoId).IsUnique();
        });
    }

    private void Chave<T>(EntityTypeBuilder<T> e, string coluna, string tabela) where T : class
    {
        e.HasKey("Id");
        var id = e.Property<long>("Id").HasColumnName(coluna).ValueGeneratedOnAdd();
        if (!oracle)
            return;

        id.HasColumnType("NUMBER");
        if (config.Oracle.EstrategiaId != EstrategiaId.Sequence)
            return;

        if (!config.Oracle.Sequencias.TryGetValue(tabela, out var sequencia) || string.IsNullOrWhiteSpace(sequencia))
            throw new InvalidOperationException($"Configure Banco:Oracle:Sequencias:{tabela} para usar EstrategiaId=Sequence");

        var permitirEscrita = config.Oracle.PermitirEscrita;
        id.HasValueGenerator((_, _) => new SequenciaOracleGenerator(sequencia.Trim(), permitirEscrita));
    }

    private PropertyBuilder<TProp> Texto<TProp>(PropertyBuilder<TProp> p, string coluna, int tamanho, bool obrigatorio)
    {
        p.HasColumnName(coluna).HasMaxLength(tamanho).IsRequired(obrigatorio);
        if (oracle)
            p.HasColumnType($"VARCHAR2({tamanho})");
        return p;
    }

    private void Fixo(PropertyBuilder<string> p, string coluna, int tamanho)
    {
        p.HasColumnName(coluna).HasMaxLength(tamanho).IsFixedLength().IsRequired();
        if (oracle)
            p.HasColumnType($"CHAR({tamanho})");
    }

    private void Coordenada(PropertyBuilder<decimal?> p, string coluna)
    {
        p.HasColumnName(coluna).HasPrecision(9, 6);
        if (oracle)
            p.HasColumnType("NUMBER(9,6)");
    }

    private void Data<TProp>(PropertyBuilder<TProp> p, string coluna, bool obrigatorio)
    {
        p.HasColumnName(coluna).IsRequired(obrigatorio);
        if (oracle)
            p.HasColumnType("DATE");
    }

    private void Numero(PropertyBuilder<int> p, string coluna, string tipoOracle)
    {
        p.HasColumnName(coluna).IsRequired();
        if (oracle)
            p.HasColumnType(tipoOracle);
    }

    private void Fk<TProp>(PropertyBuilder<TProp> p, string coluna, bool obrigatorio)
    {
        p.HasColumnName(coluna).IsRequired(obrigatorio);
        if (oracle)
            p.HasColumnType("NUMBER");
    }

    private void Codigo<TEnum>(PropertyBuilder<TEnum> p, string coluna, ValueConverter<TEnum, string> conversor)
        where TEnum : struct, Enum
    {
        p.HasColumnName(coluna).HasConversion(conversor).HasMaxLength(1).IsFixedLength().IsRequired();
        if (oracle)
            p.HasColumnType("CHAR(1)");
    }
}
