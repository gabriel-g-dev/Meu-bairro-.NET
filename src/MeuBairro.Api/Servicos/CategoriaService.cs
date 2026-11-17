using MeuBairro.Api.Configuracao;
using MeuBairro.Api.Contratos;
using MeuBairro.Api.Dados;
using MeuBairro.Api.Dominio;
using MeuBairro.Api.Infra;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeuBairro.Api.Servicos;

public sealed class CategoriaService(AppDbContext db, IOptions<CategoriasOptions> opcoes, UnidadeDeTrabalho unidade)
{
    public string Codigo(string nome) => CategoriaCodigo.Gerar(nome, opcoes.Value.Codigos);

    public CategoriaResponse ParaResposta(Categoria c) => new(c.Id, Codigo(c.Nome), c.Nome, c.Descricao, c.PrazoDias);

    public async Task<List<CategoriaResponse>> ListarAsync(CancellationToken ct) =>
        (await TodasAsync(ct)).Select(ParaResposta).ToList();

    public async Task<CategoriaResponse> BuscarAsync(long id, CancellationToken ct) =>
        ParaResposta(await db.Categorias.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct)
                     ?? throw new RecursoNaoEncontradoException($"Categoria {id} não encontrada"));

    // Aceita o código (BURACO), o nome da categoria ou o ID numérico.
    public async Task<Categoria> ResolverAsync(string valor, CancellationToken ct)
    {
        var todas = await TodasAsync(ct);
        var texto = valor.Trim();
        var categoria = long.TryParse(texto, out var id)
            ? todas.FirstOrDefault(c => c.Id == id)
            : todas.FirstOrDefault(c => string.Equals(Codigo(c.Nome), texto, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(c.Nome.Trim(), texto, StringComparison.OrdinalIgnoreCase));
        return categoria ?? throw new RequisicaoInvalidaException(
            $"Categoria inválida: {texto}. Use uma destas: {string.Join(", ", todas.Select(c => Codigo(c.Nome)))}");
    }

    public async Task<CategoriaResponse> CriarAsync(CategoriaRequest dados, CancellationToken ct)
    {
        var nome = dados.Nome!.Trim();
        await using var escopo = await unidade.IniciarAsync("categoria", ct);
        await VerificarConflitosAsync(nome, null, ct);

        var categoria = new Categoria { Nome = nome, Descricao = Texto.Limpar(dados.Descricao), PrazoDias = dados.PrazoDias!.Value };
        db.Categorias.Add(categoria);
        await SalvarAsync(ct);
        await escopo.ConfirmarAsync(ct);
        return ParaResposta(categoria);
    }

    public async Task<CategoriaResponse> AtualizarAsync(long id, CategoriaRequest dados, CancellationToken ct)
    {
        await using var escopo = await unidade.IniciarAsync("categoria", ct);
        var categoria = await db.Categorias.FirstOrDefaultAsync(c => c.Id == id, ct)
                        ?? throw new RecursoNaoEncontradoException($"Categoria {id} não encontrada");
        var nome = dados.Nome!.Trim();
        var codigoAtual = Codigo(categoria.Nome);
        if (CategoriaCodigo.CodigosPadrao.Contains(codigoAtual) && Codigo(nome) != codigoAtual)
            throw new RegraNegocioException("O código das categorias padrão não pode ser alterado");

        await VerificarConflitosAsync(nome, id, ct);
        categoria.Nome = nome;
        categoria.Descricao = Texto.Limpar(dados.Descricao);
        categoria.PrazoDias = dados.PrazoDias!.Value;
        await SalvarAsync(ct);
        await escopo.ConfirmarAsync(ct);
        return ParaResposta(categoria);
    }

    public async Task ExcluirAsync(long id, CancellationToken ct)
    {
        await using var escopo = await unidade.IniciarAsync("categoria", ct);
        var categoria = await db.Categorias.FirstOrDefaultAsync(c => c.Id == id, ct)
                        ?? throw new RecursoNaoEncontradoException($"Categoria {id} não encontrada");
        if (CategoriaCodigo.CodigosPadrao.Contains(Codigo(categoria.Nome)))
            throw new RegraNegocioException("As categorias padrão não podem ser excluídas");
        if (await db.Solicitacoes.AnyAsync(s => s.CategoriaId == id, ct))
            throw new RegraNegocioException("A categoria tem ocorrências vinculadas e não pode ser excluída");

        db.Categorias.Remove(categoria);
        await db.SaveChangesAsync(ct);
        await escopo.ConfirmarAsync(ct);
    }

    private async Task<List<Categoria>> TodasAsync(CancellationToken ct) =>
        await db.Categorias.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct);

    private async Task VerificarConflitosAsync(string nome, long? ignorarId, CancellationToken ct)
    {
        var codigo = Codigo(nome);
        foreach (var outra in await TodasAsync(ct))
        {
            if (outra.Id == ignorarId)
                continue;
            if (string.Equals(outra.Nome.Trim(), nome, StringComparison.OrdinalIgnoreCase))
                throw new RegraNegocioException($"Já existe uma categoria com o nome {nome}");
            if (Codigo(outra.Nome) == codigo)
                throw new RegraNegocioException($"Já existe uma categoria com o código {codigo}");
        }
    }

    private async Task SalvarAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Erros.ViolacaoUnicidade(ex))
        {
            throw new RegraNegocioException("Já existe uma categoria com este nome");
        }
    }
}
