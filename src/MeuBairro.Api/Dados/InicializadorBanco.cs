using MeuBairro.Api.Configuracao;
using MeuBairro.Api.Dominio;
using MeuBairro.Api.Seguranca;
using MeuBairro.Api.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeuBairro.Api.Dados;

public static class InicializadorBanco
{
    public static async Task ExecutarAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("MeuBairro.Inicializacao");
        var banco = sp.GetRequiredService<IOptions<BancoOptions>>().Value;

        // Configuração inválida (Oracle sem conexão, códigos CHAR(1) ou sequences incorretos) impede a API de subir.
        // Montar o modelo não abre conexão com o banco.
        var db = sp.GetRequiredService<AppDbContext>();
        _ = db.Model;

        if (banco.Provedor == ProvedorBanco.Oracle)
        {
            // Nunca cria, altera ou popula tabelas no Oracle compartilhado.
            log.LogInformation("Provedor Oracle configurado (escrita permitida: {Escrita}). Nenhuma estrutura será criada.",
                banco.Oracle.PermitirEscrita);
        }
        else if (banco.InicializarSqlite)
        {
            try
            {
                await db.Database.EnsureCreatedAsync(ct);
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
                await CriarCategoriasAsync(db, log, ct);

                var exemplo = sp.GetRequiredService<IOptions<DadosExemploOptions>>().Value;
                var ambiente = sp.GetRequiredService<IHostEnvironment>();
                if (exemplo.Habilitado && (ambiente.IsDevelopment() || ambiente.IsEnvironment("Testing")))
                    await CriarDadosExemploAsync(db, sp, exemplo, log, ct);
                else if (exemplo.Habilitado)
                    log.LogWarning("DadosExemplo:Habilitado ignorado: contas de demonstração só são criadas em Development");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Não foi possível inicializar o banco SQLite local");
            }
        }

        // Depois das tabelas do negócio: o EnsureCreated não cria nada num arquivo que já tenha alguma tabela.
        try
        {
            await using var controle = await sp.GetRequiredService<IDbContextFactory<ControleDbContext>>().CreateDbContextAsync(ct);
            await CriarTabelaControleAsync(controle, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Não foi possível preparar o armazenamento de idempotência");
        }
    }

    // Cria a tabela de idempotência mesmo quando ela divide o arquivo SQLite com as tabelas do negócio.
    public static async Task CriarTabelaControleAsync(ControleDbContext controle, CancellationToken ct)
    {
        var script = controle.Database.GenerateCreateScript()
            .Replace("CREATE TABLE \"", "CREATE TABLE IF NOT EXISTS \"")
            .Replace("CREATE UNIQUE INDEX \"", "CREATE UNIQUE INDEX IF NOT EXISTS \"")
            .Replace("CREATE INDEX \"", "CREATE INDEX IF NOT EXISTS \"");
        await controle.Database.ExecuteSqlRawAsync(script, ct);
        await controle.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }

    private static async Task CriarCategoriasAsync(AppDbContext db, ILogger log, CancellationToken ct)
    {
        (string Nome, string Descricao, int Prazo)[] categorias =
        [
            ("Buraco", "Buracos, afundamentos e desníveis no asfalto", 15),
            ("Iluminação", "Postes apagados, lâmpadas queimadas ou piscando", 7),
            ("Lixo", "Lixo acumulado, entulho e descarte irregular", 5),
            ("Alagamento", "Ruas alagadas, bueiros entupidos e problemas de drenagem", 10),
            ("Calçada", "Calçadas quebradas, com degraus ou sem acessibilidade", 30),
            ("Outro", "Problemas que não se encaixam nas demais categorias", 20)
        ];

        var existentes = await db.Categorias.Select(c => c.Nome).ToListAsync(ct);
        foreach (var (nome, descricao, prazo) in categorias)
        {
            if (existentes.Contains(nome, StringComparer.OrdinalIgnoreCase))
                continue;
            db.Categorias.Add(new Categoria { Nome = nome, Descricao = descricao, PrazoDias = prazo });
            log.LogInformation("Categoria cadastrada: {Categoria}", nome);
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task CriarDadosExemploAsync(
        AppDbContext db, IServiceProvider sp, DadosExemploOptions exemplo, ILogger log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(exemplo.Senha))
        {
            log.LogWarning("Dados de exemplo não criados: defina DadosExemplo:Senha");
            return;
        }

        var senhas = sp.GetRequiredService<SenhaService>();
        var relogio = sp.GetRequiredService<Relogio>();
        const string emailCidadao = "cidadao@meubairro.com";
        const string emailGestor = "gestor@meubairro.com";

        if (!await db.Funcionarios.AnyAsync(f => f.Email == emailGestor, ct))
        {
            db.Funcionarios.Add(new Funcionario
            {
                Nome = "Gestor de Teste",
                Setor = "Zeladoria",
                Cargo = "Gestor de atendimento",
                Email = emailGestor,
                Senha = senhas.Gerar(exemplo.Senha)
            });
            log.LogInformation("Funcionário de exemplo criado: {Email}", emailGestor);
        }

        if (await db.Usuarios.AnyAsync(u => u.Email == emailCidadao, ct))
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        var agora = relogio.Agora();
        var cidadao = new Usuario
        {
            Nome = "Cidadão de Teste",
            Email = emailCidadao,
            Senha = senhas.Gerar(exemplo.Senha),
            Telefone = "11999990000",
            DataCadastro = agora,
            Endereco = new Endereco
            {
                Logradouro = "Avenida Paulista", Numero = "1106", Bairro = "Bela Vista",
                Cidade = "São Paulo", Estado = "SP", Cep = "01310100"
            }
        };
        db.Usuarios.Add(cidadao);

        var categorias = await db.Categorias.ToListAsync(ct);
        var buraco = categorias.First(c => CategoriaCodigo.Gerar(c.Nome) == "BURACO");
        var iluminacao = categorias.First(c => CategoriaCodigo.Gerar(c.Nome) == "ILUMINACAO");

        db.Solicitacoes.AddRange(
            new Solicitacao
            {
                Titulo = "Buraco em frente ao número 1200",
                Descricao = "Buraco grande na faixa da direita, carros desviando bruscamente.",
                Status = StatusSolicitacao.ABERTA,
                Prioridade = Prioridade.MEDIA,
                DataAbertura = agora.AddDays(-3),
                Usuario = cidadao,
                CategoriaId = buraco.Id,
                Endereco = new Endereco
                {
                    Logradouro = "Avenida Paulista", Numero = "1200", Bairro = "Bela Vista", Cidade = "São Paulo",
                    Estado = "SP", Cep = "01310100", Latitude = -23.561414m, Longitude = -46.655881m
                }
            },
            new Solicitacao
            {
                Titulo = "Poste apagado na esquina",
                Descricao = "O poste da esquina está apagado há uma semana e a rua fica escura.",
                Status = StatusSolicitacao.ABERTA,
                Prioridade = Prioridade.MEDIA,
                DataAbertura = agora.AddDays(-10),
                Usuario = cidadao,
                CategoriaId = iluminacao.Id,
                Endereco = new Endereco
                {
                    Logradouro = "Rua Augusta", Numero = "500", Bairro = "Consolação", Cidade = "São Paulo",
                    Estado = "SP", Cep = "01305000", Latitude = -23.553450m, Longitude = -46.652310m
                }
            });

        await db.SaveChangesAsync(ct);
        log.LogInformation("Dados de exemplo criados: {Email} e duas ocorrências", emailCidadao);
    }
}
