using System.Text;
using MeuBairro.Api.Configuracao;
using MeuBairro.Api.Dados;
using MeuBairro.Api.Dominio;
using MeuBairro.Api.Infra.Idempotencia;
using MeuBairro.Api.Seguranca;
using MeuBairro.Api.Servicos;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;

namespace MeuBairro.Tests.Unitarios;

public sealed class MapeamentoOracleTests
{
    private static AppDbContext ContextoOracle(BancoOptions? banco = null)
    {
        banco ??= new BancoOptions { Provedor = ProvedorBanco.Oracle };
        var options = new DbContextOptionsBuilder<AppDbContext>();
        // Nenhuma conexão é aberta: o modelo é montado em memória a partir do mapeamento.
        BancoExtensions.ConfigurarOracle(options, "User Id=teste;Password=teste;Data Source=localhost:1521/NAOEXISTE", banco);
        options.ReplaceService<IModelCacheKeyFactory, ChaveModeloFactory>();
        return new AppDbContext(options.Options, Options.Create(banco));
    }

    // Tipo exatamente como mapeado. (Na leitura, o provedor exibe NUMBER de um long como NUMBER(20,0), que lê o mesmo valor.)
    private static (string Coluna, string? Tipo, bool Nulo, int? Tamanho) Coluna<T>(AppDbContext db, string propriedade)
    {
        var p = db.Model.FindEntityType(typeof(T))!.FindProperty(propriedade)!;
        return (p.GetColumnName(), (string?)p[RelationalAnnotationNames.ColumnType], p.IsNullable, p.GetMaxLength());
    }

    [Fact]
    public void Tabelas_seguem_os_nomes_do_oracle()
    {
        using var db = ContextoOracle();

        var tabelas = db.Model.GetEntityTypes().Select(e => e.GetTableName()).Order();

        Assert.Equal(["ANDAMENTOS", "ANEXOS", "AVALIACOES", "CATEGORIAS", "ENDERECOS", "FUNCIONARIOS", "SOLICITACOES", "USUARIOS"], tabelas);
    }

    [Fact]
    public void Colunas_tipos_tamanhos_e_nulidade_conferem_com_os_metadados()
    {
        using var db = ContextoOracle();

        Assert.Equal(("USUARIO_ID", "NUMBER", false, null), Coluna<Usuario>(db, nameof(Usuario.Id)));
        Assert.Equal(("USUARIO_EMAIL", "VARCHAR2(100)", false, 100), Coluna<Usuario>(db, nameof(Usuario.Email)));
        Assert.Equal(("USUARIO_SENHA", "VARCHAR2(255)", false, 255), Coluna<Usuario>(db, nameof(Usuario.Senha)));
        Assert.Equal(("USUARIO_TELEFONE", "VARCHAR2(20)", true, 20), Coluna<Usuario>(db, nameof(Usuario.Telefone)));
        Assert.Equal(("USUARIO_DATA_CADASTRO", "DATE", false, null), Coluna<Usuario>(db, nameof(Usuario.DataCadastro)));
        Assert.Equal(("ENDERECO_ID", "NUMBER", false, null), Coluna<Usuario>(db, nameof(Usuario.EnderecoId)));
        Assert.Equal(("FUNCIONARIO_SETOR", "VARCHAR2(50)", false, 50), Coluna<Funcionario>(db, nameof(Funcionario.Setor)));
        Assert.Equal(("ENDERECO_ESTADO", "CHAR(2)", false, 2), Coluna<Endereco>(db, nameof(Endereco.Estado)));
        Assert.Equal(("ENDERECO_CEP", "CHAR(8)", false, 8), Coluna<Endereco>(db, nameof(Endereco.Cep)));
        Assert.Equal(("ENDERECO_LATITUDE", "NUMBER(9,6)", true, null), Coluna<Endereco>(db, nameof(Endereco.Latitude)));
        Assert.Equal(("ENDERECO_NUMERO", "VARCHAR2(10)", true, 10), Coluna<Endereco>(db, nameof(Endereco.Numero)));
        Assert.Equal(("CATEGORIA_NOME", "VARCHAR2(50)", false, 50), Coluna<Categoria>(db, nameof(Categoria.Nome)));
        Assert.Equal(("CATEGORIA_PRAZO_DIAS", "NUMBER(3,0)", false, null), Coluna<Categoria>(db, nameof(Categoria.PrazoDias)));
        Assert.Equal(("SOLICITACAO_TITULO", "VARCHAR2(100)", false, 100), Coluna<Solicitacao>(db, nameof(Solicitacao.Titulo)));
        Assert.Equal(("SOLICITACAO_DESCRICAO", "VARCHAR2(500)", false, 500), Coluna<Solicitacao>(db, nameof(Solicitacao.Descricao)));
        Assert.Equal(("SOLICITACAO_STATUS", "CHAR(1)", false, 1), Coluna<Solicitacao>(db, nameof(Solicitacao.Status)));
        Assert.Equal(("SOLICITACAO_PRIORIDADE", "CHAR(1)", false, 1), Coluna<Solicitacao>(db, nameof(Solicitacao.Prioridade)));
        Assert.Equal(("SOLICITACAO_DATA_CONCLUSAO", "DATE", true, null), Coluna<Solicitacao>(db, nameof(Solicitacao.DataConclusao)));
        Assert.Equal(("FUNCIONARIO_ID", "NUMBER", true, null), Coluna<Solicitacao>(db, nameof(Solicitacao.FuncionarioId)));
        Assert.Equal(("ANDAMENTO_DESCRICAO", "VARCHAR2(300)", false, 300), Coluna<Andamento>(db, nameof(Andamento.Descricao)));
        Assert.Equal(("FUNCIONARIO_ID", "NUMBER", false, null), Coluna<Andamento>(db, nameof(Andamento.FuncionarioId)));
        Assert.Equal(("ANEXO_TIPO", "VARCHAR2(30)", false, 30), Coluna<Anexo>(db, nameof(Anexo.Tipo)));
        Assert.Equal(("ANEXO_URL", "VARCHAR2(300)", false, 300), Coluna<Anexo>(db, nameof(Anexo.Url)));
        Assert.Equal(("AVALIACAO_NOTA", "NUMBER(1,0)", false, null), Coluna<Avaliacao>(db, nameof(Avaliacao.Nota)));
        Assert.Equal(("AVALIACAO_COMENTARIO", "VARCHAR2(300)", true, 300), Coluna<Avaliacao>(db, nameof(Avaliacao.Comentario)));
    }

    [Fact]
    public void Unicidades_e_chaves_estrangeiras_refletem_o_esquema()
    {
        using var db = ContextoOracle();
        bool Unico<T>(string propriedade) =>
            db.Model.FindEntityType(typeof(T))!.GetIndexes().Any(i => i.IsUnique && i.Properties.Single().Name == propriedade);

        Assert.True(Unico<Usuario>(nameof(Usuario.Email)));
        Assert.True(Unico<Funcionario>(nameof(Funcionario.Email)));
        Assert.True(Unico<Categoria>(nameof(Categoria.Nome)));
        Assert.True(Unico<Avaliacao>(nameof(Avaliacao.SolicitacaoId)));

        List<(string?, string, bool)> Fks<T>() => db.Model.FindEntityType(typeof(T))!.GetForeignKeys()
            .Select(f => (f.PrincipalEntityType.GetTableName(), f.Properties.Single().GetColumnName(), f.IsRequired)).ToList();

        Assert.Equal(
            [("CATEGORIAS", "CATEGORIA_ID", true), ("ENDERECOS", "ENDERECO_ID", true), ("FUNCIONARIOS", "FUNCIONARIO_ID", false), ("USUARIOS", "USUARIO_ID", true)],
            Fks<Solicitacao>().Order());
        Assert.Equal([("ENDERECOS", "ENDERECO_ID", true)], Fks<Usuario>());
        Assert.Equal([("FUNCIONARIOS", "FUNCIONARIO_ID", true), ("SOLICITACOES", "SOLICITACAO_ID", true)], Fks<Andamento>().Order());
        Assert.Equal([("SOLICITACOES", "SOLICITACAO_ID", true)], Fks<Anexo>());
        Assert.Equal([("SOLICITACOES", "SOLICITACAO_ID", true)], Fks<Avaliacao>());
        Assert.Empty(Fks<Endereco>());
        Assert.Empty(Fks<Funcionario>());
        Assert.Empty(Fks<Categoria>());
        // Nenhuma exclusão em cascata: os filhos são removidos explicitamente pela aplicação.
        Assert.All(db.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()),
            f => Assert.Equal(DeleteBehavior.Restrict, f.DeleteBehavior));
    }

    [Fact]
    public void Status_da_solicitacao_e_token_de_concorrencia_sem_coluna_extra()
    {
        using var db = ContextoOracle();
        var solicitacao = db.Model.FindEntityType(typeof(Solicitacao))!;

        Assert.True(solicitacao.FindProperty(nameof(Solicitacao.Status))!.IsConcurrencyToken);
        Assert.Equal(11, solicitacao.GetProperties().Count());
    }

    [Fact]
    public void Esquema_e_estrategia_de_sequence_sao_configuraveis()
    {
        var banco = new BancoOptions { Provedor = ProvedorBanco.Oracle };
        banco.Oracle.Esquema = "rm556000";
        banco.Oracle.EstrategiaId = EstrategiaId.Sequence;
        foreach (var tabela in new[] { "USUARIOS", "FUNCIONARIOS", "ENDERECOS", "CATEGORIAS", "SOLICITACOES", "ANDAMENTOS", "ANEXOS", "AVALIACOES" })
            banco.Oracle.Sequencias[tabela] = $"SEQ_{tabela}";
        using var db = ContextoOracle(banco);

        var solicitacao = db.Model.FindEntityType(typeof(Solicitacao))!;

        Assert.Equal("RM556000", solicitacao.GetSchema());
        Assert.NotNull(solicitacao.FindProperty(nameof(Solicitacao.Id))!.GetValueGeneratorFactory());
    }

    [Fact]
    public void Sequence_sem_nome_configurado_falha_ao_montar_o_modelo()
    {
        var banco = new BancoOptions { Provedor = ProvedorBanco.Oracle };
        banco.Oracle.EstrategiaId = EstrategiaId.Sequence;
        using var db = ContextoOracle(banco);

        Assert.Throws<InvalidOperationException>(() => db.Model);
    }

    [Fact]
    public void No_sqlite_o_mesmo_modelo_nao_usa_tipos_do_oracle()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:")
            .ReplaceService<IModelCacheKeyFactory, ChaveModeloFactory>().Options;
        using var db = new AppDbContext(options, Options.Create(new BancoOptions()));

        var titulo = db.Model.FindEntityType(typeof(Solicitacao))!.FindProperty(nameof(Solicitacao.Titulo))!;

        Assert.Equal("SOLICITACAO_TITULO", titulo.GetColumnName());
        Assert.DoesNotContain("VARCHAR2", titulo.GetColumnType() ?? "");
        Assert.Equal(100, titulo.GetMaxLength());
    }
}

public sealed class ProtecaoOracleTests
{
    [Fact]
    public async Task SaveChanges_no_oracle_somente_leitura_e_barrado_antes_de_conectar()
    {
        var banco = new BancoOptions { Provedor = ProvedorBanco.Oracle };
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        BancoExtensions.ConfigurarOracle(builder, "User Id=teste;Password=teste;Data Source=localhost:1/NAOEXISTE", banco)
            .AddInterceptors(new BloqueioEscritaSaveChangesInterceptor(), new BloqueioEscritaComandoInterceptor())
            .ReplaceService<IModelCacheKeyFactory, ChaveModeloFactory>();
        await using var db = new AppDbContext(builder.Options, Options.Create(banco));
        db.Categorias.Add(new Categoria { Nome = "Teste", PrazoDias = 1 });

        var erro = await Assert.ThrowsAsync<EscritaBloqueadaException>(() => db.SaveChangesAsync());
        Assert.Contains("somente leitura", erro.Message);
        Assert.Throws<EscritaBloqueadaException>(() => db.SaveChanges());
    }

    [Theory]
    [InlineData("SELECT * FROM SOLICITACOES", true)]
    [InlineData("  -- comentário\n select 1 from dual", true)]
    [InlineData("WITH x AS (SELECT 1 FROM DUAL) SELECT * FROM x", true)]
    [InlineData("INSERT INTO SOLICITACOES VALUES (1)", false)]
    [InlineData("UPDATE USUARIOS SET USUARIO_SENHA = 'x'", false)]
    [InlineData("DELETE FROM ANEXOS", false)]
    [InlineData("MERGE INTO CATEGORIAS c USING DUAL ON (1=1)", false)]
    [InlineData("TRUNCATE TABLE AVALIACOES", false)]
    [InlineData("DROP TABLE ENDERECOS", false)]
    [InlineData("ALTER TABLE USUARIOS ADD X NUMBER", false)]
    [InlineData("CREATE TABLE T (ID NUMBER)", false)]
    [InlineData("BEGIN INSERT INTO ANEXOS VALUES (1); END;", false)]
    [InlineData("SELECT 1 FROM DUAL; DELETE FROM ANEXOS", false)]
    [InlineData("SELECT * FROM SOLICITACOES FOR UPDATE", false)]
    [InlineData("SELECT SEQ_X.NEXTVAL FROM DUAL", false)]
    [InlineData("/* SELECT */ DELETE FROM ANEXOS", false)]
    [InlineData("", false)]
    public void Somente_comandos_de_leitura_passam(string sql, bool permitido) =>
        Assert.Equal(permitido, BloqueioEscritaComandoInterceptor.EhSomenteLeitura(sql));

    [Fact]
    public async Task Interceptador_de_comandos_barra_execute_delete_e_permite_consulta()
    {
        await using var conexao = new SqliteConnection("Data Source=:memory:");
        await conexao.OpenAsync();
        var semBloqueio = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conexao)
            .ReplaceService<IModelCacheKeyFactory, ChaveModeloFactory>().Options;
        await using (var preparo = new AppDbContext(semBloqueio, Options.Create(new BancoOptions())))
        {
            await preparo.Database.EnsureCreatedAsync();
            preparo.Categorias.Add(new Categoria { Nome = "Buraco", PrazoDias = 3 });
            await preparo.SaveChangesAsync();
        }

        var comBloqueio = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conexao)
            .AddInterceptors(new BloqueioEscritaComandoInterceptor())
            .ReplaceService<IModelCacheKeyFactory, ChaveModeloFactory>().Options;
        await using var db = new AppDbContext(comBloqueio, Options.Create(new BancoOptions()));

        Assert.Equal(1, await db.Categorias.CountAsync());
        await Assert.ThrowsAsync<EscritaBloqueadaException>(() => db.Categorias.ExecuteDeleteAsync());
        await Assert.ThrowsAsync<EscritaBloqueadaException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM CATEGORIAS"));
        Assert.Equal(1, await db.Categorias.CountAsync());
    }

    [Fact]
    public async Task Gerador_por_sequence_nao_consome_sequence_com_escrita_bloqueada()
    {
        var gerador = new SequenciaOracleGenerator("SEQ_SOLICITACOES", permitirEscrita: false);

        await Assert.ThrowsAsync<EscritaBloqueadaException>(() => gerador.NextAsync(null!).AsTask());
        Assert.Throws<InvalidOperationException>(() => new SequenciaOracleGenerator("SEQ; DROP TABLE X", true));
    }
}

public sealed class SegurancaUnitariaTests
{
    private static SenhaService Senhas() => new(Options.Create(new SenhaOptions { FatorTrabalho = 4 }));

    [Fact]
    public void Hash_de_senha_usa_bcrypt_com_sal_aleatorio()
    {
        var senhas = Senhas();

        var a = senhas.Gerar("Senha@123");
        var b = senhas.Gerar("Senha@123");

        Assert.StartsWith("$2", a);
        Assert.NotEqual(a, b);
        Assert.True(senhas.Verificar("Senha@123", a));
        Assert.False(senhas.Verificar("senha@123", a));
    }

    [Theory]
    [InlineData("Senha@123")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("$2a$10$hashcorrompido")]
    public void Senha_guardada_fora_do_formato_bcrypt_nunca_e_aceita(string? guardada) =>
        Assert.False(Senhas().Verificar("Senha@123", guardada));

    [Fact]
    public void Hash_bcrypt_com_prefixo_2a_tambem_e_aceito()
    {
        // Variante "$2a$" com fator 10, formato comum em outras bibliotecas BCrypt.
        var hash2a = BCrypt.Net.BCrypt.HashPassword("Teste@123", BCrypt.Net.BCrypt.GenerateSalt(10, 'a'));

        Assert.StartsWith("$2a$10$", hash2a);
        Assert.True(Senhas().Verificar("Teste@123", hash2a));
    }

    private static TokenService Tokens(string? segredo, string ambiente = "Testing") =>
        new(Options.Create(new JwtOptions { Segredo = segredo, ExpiracaoMinutos = 60 }),
            new Infra.AmbienteFalso(Path.GetTempPath(), ambiente), NullLogger<TokenService>.Instance);

    [Fact]
    public void Bcrypt_nao_trunca_no_byte_nulo_mas_ignora_o_que_passa_de_72_bytes()
    {
        var comNulo = BCrypt.Net.BCrypt.HashPassword("abc\0def", 4);
        var longa = BCrypt.Net.BCrypt.HashPassword(new string('a', 72) + "X", 4);

        Assert.False(BCrypt.Net.BCrypt.Verify("abc\0xyz", comNulo));
        Assert.False(BCrypt.Net.BCrypt.Verify("abc", comNulo));
        // Por isso a API recusa senhas acima de 72 bytes: o excedente não seria conferido.
        Assert.True(BCrypt.Net.BCrypt.Verify(new string('a', 72) + "Y", longa));
    }

    [Fact]
    public void Senha_acima_de_72_bytes_nunca_confere_mesmo_comecando_pela_senha_correta()
    {
        var senhas = Senhas();
        var senha = new string('ç', 36); // 36 caracteres e 72 bytes em UTF-8, o máximo aceito no cadastro
        var hash = senhas.Gerar(senha);

        Assert.True(senhas.Verificar(senha, hash));
        // Sozinho, o BCrypt aceitaria qualquer sufixo depois dos 72 bytes.
        Assert.True(BCrypt.Net.BCrypt.Verify(senha + "sufixo", hash));
        Assert.False(senhas.Verificar(senha + "sufixo", hash));
    }

    [Fact]
    public void Token_gerado_tem_perfil_emissor_audiencia_e_expiracao()
    {
        var gerado = Tokens(Infra.ApiFactory.SegredoJwt).Gerar(7, "Ana", "ana@teste.com", Perfil.GESTOR);

        var jwt = new JsonWebToken(gerado.Token);
        Assert.Equal("7", jwt.Subject);
        Assert.Equal("GESTOR", jwt.GetClaim("perfil").Value);
        Assert.Equal("meu-bairro-api-dotnet", jwt.Issuer);
        Assert.Contains("meu-bairro-clientes", jwt.Audiences);
        Assert.Equal("HS256", jwt.Alg);
        Assert.InRange((jwt.ValidTo - DateTime.UtcNow).TotalMinutes, 59, 61);
        Assert.False(jwt.TryGetClaim("senha", out _));
    }

    [Fact]
    public void Segredo_curto_e_rejeitado() =>
        Assert.Throws<InvalidOperationException>(() => Tokens("curto"));

    [Fact]
    public void Producao_sem_segredo_nao_inicia() =>
        Assert.Throws<InvalidOperationException>(() => Tokens(null, Environments.Production));

    [Fact]
    public async Task Desenvolvimento_sem_segredo_usa_chave_aleatoria_valida_so_nesta_execucao()
    {
        var servico = Tokens(null, Environments.Development);
        var outro = Tokens(null, Environments.Development);
        var token = servico.Gerar(1, "Ana", "ana@teste.com", Perfil.CIDADAO).Token;

        var valido = await new JsonWebTokenHandler().ValidateTokenAsync(token, servico.ParametrosValidacao());
        var deOutraExecucao = await new JsonWebTokenHandler().ValidateTokenAsync(token, outro.ParametrosValidacao());

        Assert.True(valido.IsValid);
        Assert.False(deOutraExecucao.IsValid);
    }

}

public sealed class ArquivosTests
{
    [Fact]
    public void Formatos_sao_detectados_pelo_conteudo()
    {
        Assert.Equal(("image/png", ".png"), TipoImagem.Detectar(Infra.Dados.PngValido));
        Assert.Equal(("image/jpeg", ".jpg"), TipoImagem.Detectar(Infra.Dados.JpegValido));
        Assert.Equal(("image/webp", ".webp"), TipoImagem.Detectar("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Null(TipoImagem.Detectar("GIF89a"u8));
        Assert.Null(TipoImagem.Detectar([]));
        Assert.Null(TipoImagem.Detectar("%PDF-1.7"u8));
    }

    [Theory]
    [InlineData("../../etc/passwd.png", "passwd.png")]
    [InlineData("..\\..\\Windows\\win.ini", "win.ini")]
    [InlineData("/var/www/../../foto.jpg", "foto.jpg")]
    [InlineData("..", "gerado.png")]
    [InlineData(".htaccess", "htaccess")]
    [InlineData("foto\u0000.png", "foto.png")]
    [InlineData("", "gerado.png")]
    [InlineData(null, "gerado.png")]
    public void Nome_original_e_saneado(string? original, string esperado) =>
        Assert.Equal(esperado, ArmazenamentoFotos.NomeSeguro(original, "gerado.png"));

    [Fact]
    public void Nome_muito_longo_mantem_os_ultimos_100_caracteres()
    {
        var nome = ArmazenamentoFotos.NomeSeguro(new string('a', 150) + ".png", "x");

        Assert.Equal(100, nome.Length);
        Assert.EndsWith(".png", nome);
    }

    [Fact]
    public void Corte_do_nome_longo_nao_separa_o_par_substituto_de_um_emoji()
    {
        // 150 caracteres: os últimos 100 começariam pela segunda metade do emoji.
        var nome = ArmazenamentoFotos.NomeSeguro(new string('a', 49) + "😀" + new string('b', 95) + ".png", "x");

        Assert.False(char.IsLowSurrogate(nome[0]));
        Assert.Equal(new string('b', 95) + ".png", nome);
    }
}

public sealed class HashRequisicaoTests
{
    private static async Task<string> HashFormAsync(params (string Chave, string Valor)[] campos)
    {
        var contexto = new DefaultHttpContext();
        contexto.Request.ContentType = "application/x-www-form-urlencoded";
        var corpo = string.Join("&", campos.Select(c => $"{Uri.EscapeDataString(c.Chave)}={Uri.EscapeDataString(c.Valor)}"));
        contexto.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(corpo));
        return await HashRequisicao.CalcularAsync(contexto.Request, CancellationToken.None);
    }

    private static async Task<(string Hash, string CorpoRelido)> HashJsonAsync(string json)
    {
        var contexto = new DefaultHttpContext();
        contexto.Request.ContentType = "application/json";
        contexto.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var hash = await HashRequisicao.CalcularAsync(contexto.Request, CancellationToken.None);
        return (hash, await new StreamReader(contexto.Request.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task Ordem_dos_campos_do_formulario_nao_muda_o_hash() =>
        Assert.Equal(await HashFormAsync(("a", "1"), ("b", "2")), await HashFormAsync(("b", "2"), ("a", "1")));

    [Fact]
    public async Task Valor_diferente_muda_o_hash() =>
        Assert.NotEqual(await HashFormAsync(("titulo", "Buraco")), await HashFormAsync(("titulo", "Buraco!")));

    [Fact]
    public async Task Json_e_relido_pelo_endpoint_depois_do_hash()
    {
        var (hash, corpo) = await HashJsonAsync("{\"nota\":5}");
        var (outro, _) = await HashJsonAsync("{\"nota\":4}");

        Assert.Equal("{\"nota\":5}", corpo);
        Assert.NotEqual(hash, outro);
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public async Task Multipart_incompleto_vira_400_e_corpo_acima_do_limite_do_servidor_mantem_o_413()
    {
        static Task<string> HashComFalha(Exception falha)
        {
            var contexto = new DefaultHttpContext();
            contexto.Request.ContentType = "multipart/form-data; boundary=X";
            contexto.Request.Body = new CorpoComFalha(falha);
            return HashRequisicao.CalcularAsync(contexto.Request, CancellationToken.None);
        }

        var incompleto = await Assert.ThrowsAsync<RequisicaoInvalidaException>(() =>
            HashComFalha(new IOException("Unexpected end of Stream")));
        // BadHttpRequestException também é uma IOException, mas carrega o status certo (ex.: 413 do Kestrel).
        var grande = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            HashComFalha(new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge)));

        Assert.Equal(400, incompleto.Status);
        Assert.Equal(413, grande.StatusCode);
    }

    private sealed class CorpoComFalha(Exception falha) : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw falha;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw falha;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw falha;
    }
}
