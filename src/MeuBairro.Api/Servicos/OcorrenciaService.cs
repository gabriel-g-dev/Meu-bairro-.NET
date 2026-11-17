using System.Linq.Expressions;
using MeuBairro.Api.Configuracao;
using MeuBairro.Api.Contratos;
using MeuBairro.Api.Dados;
using MeuBairro.Api.Dominio;
using MeuBairro.Api.Infra;
using MeuBairro.Api.Seguranca;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeuBairro.Api.Servicos;

public sealed class OcorrenciaService(
    AppDbContext db,
    CategoriaService categorias,
    EnderecoService enderecos,
    ArmazenamentoFotos fotos,
    Relogio relogio,
    UnidadeDeTrabalho unidade,
    ArquivosDaRequisicao arquivos,
    IOptions<FotosOptions> opcoesFotos)
{
    private const int TamanhoPadrao = 10;
    private const int TamanhoMaximo = 50;
    private const int TamanhoAndamento = 300;

    public async Task<OcorrenciaResponse> CriarAsync(OcorrenciaForm form, UsuarioAtual cidadao, string urlBase, CancellationToken ct)
    {
        var categoria = await categorias.ResolverAsync(form.Categoria!, ct);
        await using var escopo = await unidade.IniciarAsync($"ocorrencias:{cidadao.Id}", ct);

        var endereco = await EnderecoParaNovaOcorrenciaAsync(form, cidadao, ct);
        var titulo = form.Titulo!.Trim();
        await VerificarDuplicidadeAsync(cidadao.Id, categoria.Id, titulo, endereco, null, ct);

        // Uma parte "foto" vazia é ignorada.
        var foto = await SalvarFotoAsync(form.Foto is { Length: > 0 } enviada ? enviada : null, ct);
        try
        {
            var solicitacao = new Solicitacao
            {
                Titulo = titulo,
                Descricao = form.Descricao!.Trim(),
                Status = StatusSolicitacao.ABERTA,
                Prioridade = Prioridade.MEDIA,
                DataAbertura = relogio.Agora(),
                UsuarioId = cidadao.Id,
                CategoriaId = categoria.Id
            };
            if (endereco.Id == 0)
                solicitacao.Endereco = endereco;
            else
                solicitacao.EnderecoId = endereco.Id;
            if (foto is not null)
                solicitacao.Anexos.Add(new Anexo { Nome = foto.NomeOriginal, Tipo = foto.Tipo, Url = foto.Url });

            // Endereço, solicitação e anexo são gravados na mesma transação.
            db.Solicitacoes.Add(solicitacao);
            await db.SaveChangesAsync(ct);
            await escopo.ConfirmarAsync(ct);
            return await DetalharAsync(solicitacao.Id, urlBase, ct);
        }
        catch
        {
            fotos.Excluir(foto?.Url);
            throw;
        }
    }

    public async Task<List<OcorrenciaResponse>> ListarAsync(string urlBase, CancellationToken ct)
    {
        var itens = await ComDetalhes(db.Solicitacoes.AsNoTracking())
            .OrderByDescending(s => s.DataAbertura).ThenByDescending(s => s.Id)
            .ToListAsync(ct);
        return await MapearAsync(itens, urlBase, ct);
    }

    public async Task<PaginaResponse<OcorrenciaResponse>> ListarPaginadoAsync(
        FiltroOcorrencias filtro, UsuarioAtual usuario, string urlBase, CancellationToken ct)
    {
        var ordenacao = Ordenacao.Ler(filtro.Sort);
        var tamanho = filtro.Size is null or < 1 ? TamanhoPadrao : Math.Min(filtro.Size.Value, TamanhoMaximo);
        var pagina = filtro.Page is null or < 0 ? 0 : filtro.Page.Value;

        IQueryable<Solicitacao> consulta = db.Solicitacoes.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filtro.Status))
        {
            var status = Enums.Ler<StatusSolicitacao>(filtro.Status, "status");
            consulta = consulta.Where(s => s.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filtro.Prioridade))
        {
            var prioridade = Enums.Ler<Prioridade>(filtro.Prioridade, "prioridade");
            consulta = consulta.Where(s => s.Prioridade == prioridade);
        }
        if (!string.IsNullOrWhiteSpace(filtro.Categoria))
        {
            var categoriaId = (await categorias.ResolverAsync(filtro.Categoria, ct)).Id;
            consulta = consulta.Where(s => s.CategoriaId == categoriaId);
        }
        if (!string.IsNullOrWhiteSpace(filtro.Bairro))
        {
            var bairro = filtro.Bairro.Trim().ToLowerInvariant();
            consulta = consulta.Where(s => s.Endereco.Bairro.ToLower() == bairro);
        }
        if (filtro.Minhas == true)
        {
            // Para o cidadão: as que ele registrou. Para o gestor: as que estão sob sua responsabilidade.
            consulta = usuario.EhGestor
                ? consulta.Where(s => s.FuncionarioId == usuario.Id)
                : consulta.Where(s => s.UsuarioId == usuario.Id);
        }

        var total = await consulta.LongCountAsync(ct);
        // Calculado em long: page * size em int estoura para páginas muito altas.
        var deslocamento = (long)pagina * tamanho;
        var itens = deslocamento >= total
            ? []
            : await Ordenacao.Aplicar(ComDetalhes(consulta), ordenacao)
                .Skip((int)deslocamento).Take(tamanho)
                .ToListAsync(ct);

        var totalPaginas = (int)Math.Ceiling(total / (double)tamanho);
        return new PaginaResponse<OcorrenciaResponse>(
            await MapearAsync(itens, urlBase, ct), pagina, tamanho, total, totalPaginas, pagina >= totalPaginas - 1);
    }

    public Task<OcorrenciaResponse> BuscarAsync(long id, string urlBase, CancellationToken ct) => DetalharAsync(id, urlBase, ct);

    public async Task<OcorrenciaResponse> AtualizarAsync(
        long id, OcorrenciaForm form, UsuarioAtual cidadao, string urlBase, CancellationToken ct)
    {
        await using var escopo = await unidade.IniciarAsync($"ocorrencias:{cidadao.Id}", ct);
        var solicitacao = await db.Solicitacoes.Include(s => s.Endereco).Include(s => s.Anexos)
                              .FirstOrDefaultAsync(s => s.Id == id, ct)
                          ?? throw NaoEncontrada(id);
        if (cidadao.EhGestor || solicitacao.UsuarioId != cidadao.Id)
            throw new AcessoNegadoException("Somente o cidadão que registrou a ocorrência pode alterá-la");
        if (!solicitacao.EstaAberta)
            throw new RegraNegocioException("Só é possível alterar ocorrências com status ABERTA");

        var categoria = await categorias.ResolverAsync(form.Categoria!, ct);
        var enderecoAnterior = solicitacao.EnderecoId;
        await AplicarEnderecoAsync(solicitacao, form, cidadao, ct);

        var titulo = form.Titulo!.Trim();
        await VerificarDuplicidadeAsync(cidadao.Id, categoria.Id, titulo, solicitacao.Endereco, solicitacao.Id, ct);
        solicitacao.Titulo = titulo;
        solicitacao.Descricao = form.Descricao!.Trim();
        solicitacao.CategoriaId = categoria.Id;

        var novaFoto = await SalvarFotoAsync(form.Foto is { Length: > 0 } enviada ? enviada : null, ct);
        string? fotoSubstituida = null;
        try
        {
            if (novaFoto is not null)
            {
                // A foto principal é o primeiro anexo de imagem; ao trocar a foto, o registro é reaproveitado.
                var principal = solicitacao.Anexos.Where(a => a.EhImagem).OrderBy(a => a.Id).FirstOrDefault();
                if (principal is null)
                {
                    solicitacao.Anexos.Add(new Anexo { Nome = novaFoto.NomeOriginal, Tipo = novaFoto.Tipo, Url = novaFoto.Url });
                }
                else
                {
                    fotoSubstituida = principal.Url;
                    principal.Nome = novaFoto.NomeOriginal;
                    principal.Tipo = novaFoto.Tipo;
                    principal.Url = novaFoto.Url;
                }
            }

            await db.SaveChangesAsync(ct);
            if (enderecoAnterior != solicitacao.EnderecoId)
                await enderecos.RemoverSeOrfaoAsync(enderecoAnterior, ct);
            await escopo.ConfirmarAsync(ct);
        }
        catch
        {
            fotos.Excluir(novaFoto?.Url);
            throw;
        }

        fotos.Excluir(fotoSubstituida);
        return await DetalharAsync(id, urlBase, ct);
    }

    public async Task ExcluirAsync(long id, UsuarioAtual usuario, CancellationToken ct)
    {
        await using var escopo = await unidade.IniciarAsync(null, ct);
        var solicitacao = await db.Solicitacoes.AsNoTracking()
                              .Where(s => s.Id == id)
                              .Select(s => new { s.UsuarioId, s.Status, s.EnderecoId })
                              .FirstOrDefaultAsync(ct)
                          ?? throw NaoEncontrada(id);
        if (!usuario.EhGestor)
        {
            if (solicitacao.UsuarioId != usuario.Id)
                throw new AcessoNegadoException("Somente o cidadão que registrou a ocorrência pode excluí-la");
            if (solicitacao.Status != StatusSolicitacao.ABERTA)
                throw new RegraNegocioException("Só é possível excluir ocorrências com status ABERTA");
        }

        var urls = await db.Anexos.Where(a => a.SolicitacaoId == id).Select(a => a.Url).ToListAsync(ct);
        await db.Avaliacoes.Where(a => a.SolicitacaoId == id).ExecuteDeleteAsync(ct);
        await db.Andamentos.Where(a => a.SolicitacaoId == id).ExecuteDeleteAsync(ct);
        await db.Anexos.Where(a => a.SolicitacaoId == id).ExecuteDeleteAsync(ct);
        await db.Solicitacoes.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
        await enderecos.RemoverSeOrfaoAsync(solicitacao.EnderecoId, ct);
        await escopo.ConfirmarAsync(ct);

        foreach (var url in urls)
            fotos.Excluir(url);
    }

    public async Task<OcorrenciaResponse> AlterarStatusAsync(
        long id, AtualizacaoStatusRequest dados, UsuarioAtual gestor, string urlBase, CancellationToken ct)
    {
        var novo = Enums.Ler<StatusSolicitacao>(dados.Status!, "status");
        await using var escopo = await unidade.IniciarAsync(null, ct);
        var atual = await db.Solicitacoes.AsNoTracking().Where(s => s.Id == id).Select(s => (StatusSolicitacao?)s.Status)
                        .FirstOrDefaultAsync(ct)
                    ?? throw NaoEncontrada(id);
        Solicitacao.ValidarTransicao(atual, novo);

        var agora = relogio.Agora();
        DateTime? conclusao = novo == StatusSolicitacao.RESOLVIDA ? agora : null;

        // Atualização condicional: se outra requisição mudou o status no meio do caminho, nada é gravado.
        var alteradas = await db.Solicitacoes
            .Where(s => s.Id == id && s.Status == atual)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, novo)
                .SetProperty(x => x.DataConclusao, conclusao)
                .SetProperty(x => x.FuncionarioId, x => x.FuncionarioId ?? gestor.Id), ct);
        if (alteradas == 0)
            throw new RegraNegocioException("A ocorrência foi alterada por outra operação. Consulte o status atual e tente novamente");

        var observacao = Texto.Limpar(dados.Observacao);
        db.Andamentos.Add(new Andamento
        {
            SolicitacaoId = id,
            FuncionarioId = gestor.Id,
            Status = novo,
            Data = agora,
            Descricao = Texto.Limitar(observacao ?? $"Status alterado de {atual} para {novo}", TamanhoAndamento)
        });
        await db.SaveChangesAsync(ct);
        await escopo.ConfirmarAsync(ct);
        return await DetalharAsync(id, urlBase, ct);
    }

    public async Task<OcorrenciaResponse> AlterarPrioridadeAsync(
        long id, AtualizacaoPrioridadeRequest dados, UsuarioAtual gestor, string urlBase, CancellationToken ct)
    {
        var nova = Enums.Ler<Prioridade>(dados.Prioridade!, "prioridade");
        await using var escopo = await unidade.IniciarAsync(null, ct);
        var atual = await db.Solicitacoes.AsNoTracking().Where(s => s.Id == id)
                        .Select(s => new { s.Status, s.Prioridade })
                        .FirstOrDefaultAsync(ct)
                    ?? throw NaoEncontrada(id);
        if (atual.Status == StatusSolicitacao.RESOLVIDA)
            throw new RegraNegocioException("A ocorrência já foi resolvida e não pode mudar de prioridade");

        await db.Solicitacoes.Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Prioridade, nova)
                .SetProperty(x => x.FuncionarioId, x => x.FuncionarioId ?? gestor.Id), ct);
        db.Andamentos.Add(new Andamento
        {
            SolicitacaoId = id,
            FuncionarioId = gestor.Id,
            Status = atual.Status,
            Data = relogio.Agora(),
            Descricao = $"Prioridade definida pelo gestor: de {atual.Prioridade} para {nova}"
        });
        await db.SaveChangesAsync(ct);
        await escopo.ConfirmarAsync(ct);
        return await DetalharAsync(id, urlBase, ct);
    }

    public async Task<HistoricoResponse> RegistrarAndamentoAsync(
        long id, AndamentoRequest dados, UsuarioAtual gestor, CancellationToken ct)
    {
        await using var escopo = await unidade.IniciarAsync(null, ct);
        var status = await db.Solicitacoes.AsNoTracking().Where(s => s.Id == id).Select(s => (StatusSolicitacao?)s.Status)
                         .FirstOrDefaultAsync(ct)
                     ?? throw NaoEncontrada(id);
        if (status == StatusSolicitacao.RESOLVIDA)
            throw new RegraNegocioException("A ocorrência já foi resolvida e não recebe novos andamentos");

        var andamento = new Andamento
        {
            SolicitacaoId = id,
            FuncionarioId = gestor.Id,
            Status = status,
            Data = relogio.Agora(),
            Descricao = dados.Descricao!.Trim()
        };
        db.Andamentos.Add(andamento);
        await db.SaveChangesAsync(ct);
        await escopo.ConfirmarAsync(ct);
        var setor = await db.Funcionarios.Where(f => f.Id == gestor.Id).Select(f => f.Setor).FirstAsync(ct);
        return new HistoricoResponse(andamento.Id, "ANDAMENTO", andamento.Descricao, andamento.Data, andamento.Status, setor);
    }

    // O histórico começa pela abertura (SOLICITACAO_DATA_ABERTURA) e segue com os registros de ANDAMENTOS.
    public async Task<List<HistoricoResponse>> HistoricoAsync(long id, CancellationToken ct)
    {
        var abertura = await db.Solicitacoes.AsNoTracking().Where(s => s.Id == id).Select(s => (DateTime?)s.DataAbertura)
                           .FirstOrDefaultAsync(ct)
                       ?? throw NaoEncontrada(id);
        var andamentos = await db.Andamentos.AsNoTracking()
            .Where(a => a.SolicitacaoId == id)
            .OrderBy(a => a.Data).ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.Descricao, a.Status, a.Data, a.Funcionario.Setor })
            .ToListAsync(ct);

        var historico = new List<HistoricoResponse>
        {
            new(0, "CRIACAO", "Ocorrência registrada pelo cidadão", abertura, StatusSolicitacao.ABERTA, null)
        };
        var anterior = StatusSolicitacao.ABERTA;
        foreach (var a in andamentos)
        {
            var tipo = a.Status != anterior ? "STATUS" : "ANDAMENTO";
            historico.Add(new HistoricoResponse(a.Id, tipo, a.Descricao, a.Data, a.Status, a.Setor));
            anterior = a.Status;
        }
        return historico;
    }

    public async Task<List<AnexoResponse>> AnexosAsync(long id, string urlBase, CancellationToken ct)
    {
        await GarantirExistenciaAsync(id, ct);
        return (await db.Anexos.AsNoTracking().Where(a => a.SolicitacaoId == id).OrderBy(a => a.Id).ToListAsync(ct))
            .Select(a => new AnexoResponse(a.Id, a.Nome, a.Tipo, UrlPublica(a.Url, urlBase)!))
            .ToList();
    }

    public async Task<AnexoResponse> AdicionarAnexoAsync(
        long id, IFormFile? arquivo, UsuarioAtual cidadao, string urlBase, CancellationToken ct)
    {
        if (arquivo is null)
            throw new ValidacaoException([new CampoErro("arquivo", "O arquivo é obrigatório")]);

        await using var escopo = await unidade.IniciarAsync($"anexos:{id}", ct);
        var solicitacao = await db.Solicitacoes.AsNoTracking().Where(s => s.Id == id)
                              .Select(s => new { s.UsuarioId, s.Status, Quantidade = s.Anexos.Count })
                              .FirstOrDefaultAsync(ct)
                          ?? throw NaoEncontrada(id);
        if (cidadao.EhGestor || solicitacao.UsuarioId != cidadao.Id)
            throw new AcessoNegadoException("Somente o cidadão que registrou a ocorrência pode enviar anexos");
        if (solicitacao.Status != StatusSolicitacao.ABERTA)
            throw new RegraNegocioException("Só é possível enviar anexos para ocorrências com status ABERTA");
        if (solicitacao.Quantidade >= opcoesFotos.Value.MaximoAnexosPorOcorrencia)
            throw new RegraNegocioException(
                $"A ocorrência já tem o máximo de {opcoesFotos.Value.MaximoAnexosPorOcorrencia} anexos");

        var foto = (await SalvarFotoAsync(arquivo, ct))!;
        var anexo = new Anexo { SolicitacaoId = id, Nome = foto.NomeOriginal, Tipo = foto.Tipo, Url = foto.Url };
        try
        {
            db.Anexos.Add(anexo);
            await db.SaveChangesAsync(ct);
            await escopo.ConfirmarAsync(ct);
        }
        catch
        {
            fotos.Excluir(foto.Url);
            throw;
        }
        return new AnexoResponse(anexo.Id, anexo.Nome, anexo.Tipo, UrlPublica(anexo.Url, urlBase)!);
    }

    public async Task<AvaliacaoResponse> AvaliarAsync(long id, AvaliacaoRequest dados, UsuarioAtual cidadao, CancellationToken ct)
    {
        await using var escopo = await unidade.IniciarAsync($"avaliacao:{id}", ct);
        var solicitacao = await db.Solicitacoes.AsNoTracking().Where(s => s.Id == id)
                              .Select(s => new { s.UsuarioId, s.Status, Avaliada = s.Avaliacao != null })
                              .FirstOrDefaultAsync(ct)
                          ?? throw NaoEncontrada(id);
        if (cidadao.EhGestor || solicitacao.UsuarioId != cidadao.Id)
            throw new AcessoNegadoException("Somente o cidadão que registrou a ocorrência pode avaliá-la");
        if (solicitacao.Status != StatusSolicitacao.RESOLVIDA)
            throw new RegraNegocioException("Só é possível avaliar ocorrências resolvidas");
        if (solicitacao.Avaliada)
            throw new RegraNegocioException("Esta ocorrência já foi avaliada");

        var avaliacao = new Avaliacao
        {
            SolicitacaoId = id,
            Nota = dados.Nota!.Value,
            Comentario = Texto.Limpar(dados.Comentario),
            Data = relogio.Agora()
        };
        db.Avaliacoes.Add(avaliacao);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Erros.ViolacaoUnicidade(ex))
        {
            throw new RegraNegocioException("Esta ocorrência já foi avaliada");
        }
        await escopo.ConfirmarAsync(ct);
        return new AvaliacaoResponse(avaliacao.Id, id, avaliacao.Nota, avaliacao.Comentario, avaliacao.Data);
    }

    // O comentário da avaliação é do cidadão: só ele e a equipe da prefeitura o consultam.
    public async Task<AvaliacaoResponse> BuscarAvaliacaoAsync(long id, UsuarioAtual usuario, CancellationToken ct)
    {
        var dono = await db.Solicitacoes.AsNoTracking().Where(s => s.Id == id).Select(s => (long?)s.UsuarioId)
                       .FirstOrDefaultAsync(ct)
                   ?? throw NaoEncontrada(id);
        if (!usuario.EhGestor && dono != usuario.Id)
            throw new AcessoNegadoException("Somente o cidadão que registrou a ocorrência ou um gestor pode consultar a avaliação");
        var a = await db.Avaliacoes.AsNoTracking().FirstOrDefaultAsync(x => x.SolicitacaoId == id, ct)
                ?? throw new RecursoNaoEncontradoException($"A ocorrência {id} ainda não foi avaliada");
        return new AvaliacaoResponse(a.Id, id, a.Nota, a.Comentario, a.Data);
    }

    public async Task<ResumoOcorrenciasResponse> ResumoAsync(CancellationToken ct)
    {
        var porStatus = await db.Solicitacoes.GroupBy(s => s.Status)
            .Select(g => new { g.Key, Total = g.LongCount() }).ToListAsync(ct);
        var porPrioridade = await db.Solicitacoes.GroupBy(s => s.Prioridade)
            .Select(g => new { g.Key, Total = g.LongCount() }).ToListAsync(ct);

        long Status(StatusSolicitacao s) => porStatus.FirstOrDefault(x => x.Key == s)?.Total ?? 0;
        long Prio(Prioridade p) => porPrioridade.FirstOrDefault(x => x.Key == p)?.Total ?? 0;

        return new ResumoOcorrenciasResponse(porStatus.Sum(x => x.Total),
            Status(StatusSolicitacao.ABERTA), Status(StatusSolicitacao.EM_ANALISE), Status(StatusSolicitacao.RESOLVIDA),
            Prio(Prioridade.ALTA), Prio(Prioridade.MEDIA), Prio(Prioridade.BAIXA));
    }

    // Painel de transparência: situação das ocorrências do bairro, atrasos e tempo médio de atendimento.
    public async Task<PainelBairroResponse> PainelAsync(string? bairro, CancellationToken ct)
    {
        IQueryable<Solicitacao> consulta = db.Solicitacoes.AsNoTracking();
        var filtro = Texto.Limpar(bairro);
        if (filtro is not null)
        {
            var normalizado = filtro.ToLowerInvariant();
            consulta = consulta.Where(s => s.Endereco.Bairro.ToLower() == normalizado);
        }

        var linhas = await consulta
            .Select(s => new { s.Status, s.DataAbertura, s.DataConclusao, s.Categoria.PrazoDias, s.Categoria.Nome })
            .ToListAsync(ct);
        var agora = relogio.Agora();
        var resolvidas = linhas.Where(l => l.Status == StatusSolicitacao.RESOLVIDA && l.DataConclusao is not null).ToList();

        return new PainelBairroResponse(
            filtro,
            linhas.Count,
            linhas.Count(l => l.Status == StatusSolicitacao.ABERTA),
            linhas.Count(l => l.Status == StatusSolicitacao.EM_ANALISE),
            linhas.Count(l => l.Status == StatusSolicitacao.RESOLVIDA),
            linhas.Count(l => Solicitacao.EstaAtrasada(l.Status, l.DataAbertura, l.PrazoDias, agora)),
            resolvidas.Count(l => l.DataConclusao <= Solicitacao.PrazoEstimado(l.DataAbertura, l.PrazoDias)),
            resolvidas.Count == 0
                ? null
                : Math.Round(resolvidas.Average(l => (l.DataConclusao!.Value - l.DataAbertura).TotalDays), 1),
            linhas.GroupBy(l => l.Nome)
                .Select(g => new PainelCategoriaResponse(categorias.Codigo(g.Key), g.Key, g.Count(),
                    g.Count(l => l.Status != StatusSolicitacao.RESOLVIDA)))
                .OrderByDescending(c => c.Total).ThenBy(c => c.Codigo)
                .ToList());
    }

    // Toda foto gravada fica registrada na requisição para ser apagada se a transação for desfeita depois.
    private async Task<FotoSalva?> SalvarFotoAsync(IFormFile? arquivo, CancellationToken ct)
    {
        if (arquivo is null)
            return null;
        var foto = await fotos.SalvarAsync(arquivo, ct);
        arquivos.Registrar(foto.Url);
        return foto;
    }

    public static string? UrlPublica(string? url, string urlBase) =>
        url is not null && url.StartsWith('/') ? urlBase + url : url;

    private async Task<Endereco> EnderecoParaNovaOcorrenciaAsync(OcorrenciaForm form, UsuarioAtual cidadao, CancellationToken ct)
    {
        if (form.EnderecoId is not { } enderecoId)
            return enderecos.Montar(form, null);

        var existente = await db.Enderecos.AsNoTracking().FirstOrDefaultAsync(e => e.Id == enderecoId, ct)
                        ?? throw new RequisicaoInvalidaException($"Endereço inválido: o endereço {enderecoId} não existe");
        if (!await enderecos.PodeUsarAsync(enderecoId, cidadao, ct))
            throw new RequisicaoInvalidaException(
                "Endereço inválido: use o seu endereço de cadastro ou o local de uma ocorrência já registrada");
        return existente;
    }

    private async Task AplicarEnderecoAsync(Solicitacao solicitacao, OcorrenciaForm form, UsuarioAtual cidadao, CancellationToken ct)
    {
        if (form.EnderecoId is { } enderecoId && enderecoId != solicitacao.EnderecoId)
        {
            var novo = await db.Enderecos.FirstOrDefaultAsync(e => e.Id == enderecoId, ct)
                       ?? throw new RequisicaoInvalidaException($"Endereço inválido: o endereço {enderecoId} não existe");
            if (!await enderecos.PodeUsarAsync(enderecoId, cidadao, ct))
                throw new RequisicaoInvalidaException(
                    "Endereço inválido: use o seu endereço de cadastro ou o local de uma ocorrência já registrada");
            solicitacao.Endereco = novo;
            return;
        }
        if (form.EnderecoId is not null)
            return;

        var desejado = enderecos.Montar(form, solicitacao.Endereco);
        if (desejado.MesmosDados(solicitacao.Endereco))
            return;

        // Um endereço compartilhado (cadastro de alguém ou outra ocorrência) nunca é alterado no lugar.
        if (await enderecos.EhExclusivoAsync(solicitacao.EnderecoId, solicitacao.Id, null, ct))
            solicitacao.Endereco.CopiarDe(desejado);
        else
            solicitacao.Endereco = desejado;
    }

    private async Task VerificarDuplicidadeAsync(
        long usuarioId, long categoriaId, string titulo, Endereco endereco, long? ignorarId, CancellationToken ct)
    {
        var tituloNormalizado = titulo.ToLowerInvariant();
        var logradouro = endereco.Logradouro.ToLowerInvariant();
        var bairro = endereco.Bairro.ToLowerInvariant();
        var duplicada = await db.Solicitacoes.AsNoTracking()
            .Where(s => s.UsuarioId == usuarioId
                        && s.CategoriaId == categoriaId
                        && s.Status != StatusSolicitacao.RESOLVIDA
                        && s.Id != ignorarId
                        && s.Titulo.ToLower() == tituloNormalizado
                        && s.Endereco.Logradouro.ToLower() == logradouro
                        && s.Endereco.Bairro.ToLower() == bairro)
            .Select(s => (long?)s.Id)
            .FirstOrDefaultAsync(ct);
        if (duplicada is not null)
            throw new RegraNegocioException(
                $"Você já registrou esta ocorrência (id {duplicada}) e ela ainda não foi resolvida");
    }

    private async Task<OcorrenciaResponse> DetalharAsync(long id, string urlBase, CancellationToken ct)
    {
        var solicitacao = await ComDetalhes(db.Solicitacoes.AsNoTracking()).FirstOrDefaultAsync(s => s.Id == id, ct)
                          ?? throw NaoEncontrada(id);
        return (await MapearAsync([solicitacao], urlBase, ct))[0];
    }

    private async Task<List<OcorrenciaResponse>> MapearAsync(List<Solicitacao> itens, string urlBase, CancellationToken ct)
    {
        // Em lotes: a lista completa não é paginada e o Oracle aceita no máximo 1000 valores num IN.
        var imagens = new List<(long SolicitacaoId, long Id, string Url)>();
        foreach (var lote in itens.Select(s => s.Id).Chunk(1000))
        {
            var encontradas = await db.Anexos.AsNoTracking()
                .Where(a => lote.Contains(a.SolicitacaoId) && a.Tipo.StartsWith("image/"))
                .Select(a => new { a.SolicitacaoId, a.Id, a.Url })
                .ToListAsync(ct);
            imagens.AddRange(encontradas.Select(a => (a.SolicitacaoId, a.Id, a.Url)));
        }
        var fotosPrincipais = imagens
            .GroupBy(a => a.SolicitacaoId)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.Id).First().Url);

        var agora = relogio.Agora();
        return itens.Select(s => new OcorrenciaResponse(
            s.Id,
            s.Titulo,
            s.Descricao,
            categorias.Codigo(s.Categoria.Nome),
            s.Categoria.Nome,
            s.Status,
            s.Prioridade,
            EnderecoService.Formatar(s.Endereco),
            s.Endereco.Latitude,
            s.Endereco.Longitude,
            s.EnderecoId,
            s.Endereco.Bairro,
            s.Endereco.Cidade,
            s.Endereco.Estado,
            s.Endereco.Cep,
            UrlPublica(fotosPrincipais.GetValueOrDefault(s.Id), urlBase),
            s.DataAbertura,
            s.DataConclusao,
            Solicitacao.PrazoEstimado(s.DataAbertura, s.Categoria.PrazoDias),
            Solicitacao.EstaAtrasada(s.Status, s.DataAbertura, s.Categoria.PrazoDias, agora),
            s.UsuarioId,
            s.FuncionarioId)).ToList();
    }

    private async Task GarantirExistenciaAsync(long id, CancellationToken ct)
    {
        if (!await db.Solicitacoes.AnyAsync(s => s.Id == id, ct))
            throw NaoEncontrada(id);
    }

    private static IQueryable<Solicitacao> ComDetalhes(IQueryable<Solicitacao> consulta) =>
        consulta.Include(s => s.Categoria).Include(s => s.Endereco);

    private static RecursoNaoEncontradoException NaoEncontrada(long id) => new($"Ocorrência {id} não encontrada");
}

// Ordenação no formato sort=campo,direcao (ex.: sort=id,asc); o parâmetro pode ser repetido.
public static class Ordenacao
{
    private static readonly Dictionary<string, string> Campos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "id",
        ["titulo"] = "titulo",
        ["status"] = "status",
        ["prioridade"] = "prioridade",
        ["criadoEm"] = "abertura",
        ["dataAbertura"] = "abertura",
        ["dataConclusao"] = "conclusao"
    };

    public static List<(string Campo, bool Decrescente)> Ler(string[]? parametros)
    {
        var ordens = new List<(string, bool)>();
        foreach (var parametro in parametros ?? [])
        {
            if (string.IsNullOrWhiteSpace(parametro))
                continue;
            var partes = parametro.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (partes.Length == 0)
                continue;
            if (!Campos.TryGetValue(partes[0], out var campo))
                throw new RequisicaoInvalidaException(
                    $"Campo de ordenação inválido: {partes[0]}. Use um destes: {string.Join(", ", Campos.Keys.Order())}");

            var decrescente = partes.Length >= 2 && partes[1].ToLowerInvariant() switch
            {
                "asc" => false,
                "desc" => true,
                _ => throw new RequisicaoInvalidaException($"Direção de ordenação inválida: {partes[1]}. Use asc ou desc")
            };
            ordens.Add((campo, decrescente));
        }

        if (ordens.Count == 0)
            ordens.Add(("abertura", true));
        return ordens;
    }

    public static IQueryable<Solicitacao> Aplicar(IQueryable<Solicitacao> consulta, List<(string Campo, bool Decrescente)> ordens)
    {
        IOrderedQueryable<Solicitacao>? ordenada = null;
        foreach (var (campo, decrescente) in ordens)
        {
            // Status e prioridade seguem a ordem do negócio (ABERTA → EM_ANALISE → RESOLVIDA; BAIXA → MEDIA → ALTA),
            // e não a dos códigos CHAR(1) gravados: com os padrões, ALTA = "A" viria antes de BAIXA = "B".
            ordenada = campo switch
            {
                "id" => Ordenar(consulta, ordenada, s => s.Id, decrescente),
                "titulo" => Ordenar(consulta, ordenada, s => s.Titulo, decrescente),
                "status" => Ordenar(consulta, ordenada,
                    s => s.Status == StatusSolicitacao.ABERTA ? 0 : s.Status == StatusSolicitacao.EM_ANALISE ? 1 : 2, decrescente),
                "prioridade" => Ordenar(consulta, ordenada,
                    s => s.Prioridade == Prioridade.BAIXA ? 0 : s.Prioridade == Prioridade.MEDIA ? 1 : 2, decrescente),
                "conclusao" => Ordenar(consulta, ordenada, s => s.DataConclusao, decrescente),
                _ => Ordenar(consulta, ordenada, s => s.DataAbertura, decrescente)
            };
        }

        // Desempate pelo id para que as páginas não repitam nem pulem registros.
        return ordens[^1].Decrescente ? ordenada!.ThenByDescending(s => s.Id) : ordenada!.ThenBy(s => s.Id);
    }

    private static IOrderedQueryable<Solicitacao> Ordenar<TChave>(
        IQueryable<Solicitacao> consulta, IOrderedQueryable<Solicitacao>? ordenada,
        Expression<Func<Solicitacao, TChave>> chave, bool decrescente) =>
        ordenada is null
            ? decrescente ? consulta.OrderByDescending(chave) : consulta.OrderBy(chave)
            : decrescente ? ordenada.ThenByDescending(chave) : ordenada.ThenBy(chave);
}
