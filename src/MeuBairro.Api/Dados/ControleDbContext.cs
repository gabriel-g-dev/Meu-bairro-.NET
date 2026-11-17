using Microsoft.EntityFrameworkCore;

namespace MeuBairro.Api.Dados;

public enum SituacaoIdempotencia { Processando, Concluida }

public sealed class RegistroIdempotencia
{
    public long Id { get; set; }
    public string Escopo { get; set; } = "";
    public string Chave { get; set; } = "";
    public string HashRequisicao { get; set; } = "";
    public SituacaoIdempotencia Situacao { get; set; }
    public string Token { get; set; } = "";
    public long CriadoEmMs { get; set; }
    public long BloqueadoAteMs { get; set; }
    public long ExpiraEmMs { get; set; }
    public int? StatusResposta { get; set; }
    public string? TipoConteudo { get; set; }
    public string? Location { get; set; }
    public byte[]? CorpoResposta { get; set; }
}

// Armazenamento auxiliar local (sempre SQLite). Fica separado do banco principal para não
// acrescentar tabelas ao esquema Oracle compartilhado.
public sealed class ControleDbContext(DbContextOptions<ControleDbContext> options) : DbContext(options)
{
    public DbSet<RegistroIdempotencia> Idempotencia => Set<RegistroIdempotencia>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RegistroIdempotencia>(e =>
        {
            e.ToTable("IDEMPOTENCIA_REQUISICOES");
            e.HasKey(x => x.Id);
            e.Property(x => x.Escopo).HasMaxLength(300).IsRequired();
            e.Property(x => x.Chave).HasMaxLength(100).IsRequired();
            e.Property(x => x.HashRequisicao).HasMaxLength(64).IsRequired();
            e.Property(x => x.Situacao).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Token).HasMaxLength(32).IsRequired();
            e.Property(x => x.TipoConteudo).HasMaxLength(100);
            e.Property(x => x.Location).HasMaxLength(500);
            e.HasIndex(x => new { x.Escopo, x.Chave }).IsUnique();
            e.HasIndex(x => x.ExpiraEmMs);
        });
    }
}
