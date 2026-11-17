using MeuBairro.Api.Configuracao;
using MeuBairro.Api.Contratos;
using MeuBairro.Api.Dados;
using MeuBairro.Api.Dominio;
using MeuBairro.Api.Infra;
using MeuBairro.Api.Seguranca;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeuBairro.Api.Servicos;

public sealed class EnderecoService(AppDbContext db, IOptions<EnderecoPadraoOptions> padrao)
{
    public static string Formatar(Endereco e)
    {
        var partes = new List<string>
        {
            string.IsNullOrWhiteSpace(e.Numero) ? e.Logradouro : $"{e.Logradouro}, {e.Numero}"
        };
        if (!string.IsNullOrWhiteSpace(e.Complemento))
            partes.Add(e.Complemento);
        partes.Add(e.Bairro);
        partes.Add($"{e.Cidade}/{e.Estado}");
        return string.Join(" - ", partes);
    }

    public static EnderecoResponse ParaResposta(Endereco e) =>
        new(e.Id, e.Logradouro, e.Numero, e.Complemento, e.Bairro, e.Cidade, e.Estado, e.Cep, e.Latitude, e.Longitude, Formatar(e));

    public static Endereco Novo(EnderecoRequest r) => new()
    {
        Logradouro = r.Logradouro!.Trim(),
        Numero = Texto.Limpar(r.Numero),
        Complemento = Texto.Limpar(r.Complemento),
        Bairro = r.Bairro!.Trim(),
        Cidade = r.Cidade!.Trim(),
        Estado = r.Estado!.Trim().ToUpperInvariant(),
        Cep = Texto.SomenteDigitos(r.Cep!),
        Latitude = Coordenadas.Arredondar(r.Latitude),
        Longitude = Coordenadas.Arredondar(r.Longitude)
    };

    // USUARIOS.ENDERECO_ID é obrigatório, mas o endereço é opcional no cadastro.
    public Endereco PadraoParaCadastro()
    {
        var p = padrao.Value;
        var endereco = new Endereco
        {
            Logradouro = Texto.Limpar(p.Logradouro) ?? "",
            Bairro = Texto.Limpar(p.Bairro) ?? "",
            Cidade = Texto.Limpar(p.Cidade) ?? "",
            Estado = (Texto.Limpar(p.Estado) ?? "").ToUpperInvariant(),
            Cep = Texto.SomenteDigitos(p.Cep ?? "")
        };
        if (!Completo(endereco))
            throw new ValidacaoException([new CampoErro("endereco", "O endereço é obrigatório")]);
        return endereco;
    }

    // Monta o endereço da ocorrência a partir do formulário. Na edição, campos ausentes mantêm o valor atual;
    // na criação, usam os padrões configurados.
    public Endereco Montar(OcorrenciaForm f, Endereco? atual)
    {
        var p = padrao.Value;
        var textoLivre = Texto.Limpar(f.Endereco);

        // Na edição, reenviar o endereço formatado das respostas (ou o próprio logradouro) não altera o logradouro.
        if (atual is not null && textoLivre is not null
            && (textoLivre == Formatar(atual) || string.Equals(textoLivre, atual.Logradouro, StringComparison.OrdinalIgnoreCase)))
            textoLivre = null;

        var endereco = new Endereco
        {
            Logradouro = Texto.Limpar(f.Logradouro) ?? textoLivre ?? atual?.Logradouro ?? "",
            Numero = f.Numero is null ? atual?.Numero : Texto.Limpar(f.Numero),
            Complemento = f.Complemento is null ? atual?.Complemento : Texto.Limpar(f.Complemento),
            Bairro = Texto.Limpar(f.Bairro) ?? atual?.Bairro ?? Texto.Limpar(p.Bairro) ?? "",
            Cidade = Texto.Limpar(f.Cidade) ?? atual?.Cidade ?? Texto.Limpar(p.Cidade) ?? "",
            Estado = (Texto.Limpar(f.Estado) ?? atual?.Estado ?? Texto.Limpar(p.Estado) ?? "").ToUpperInvariant(),
            Cep = Texto.Limpar(f.Cep) is { } cep ? Texto.SomenteDigitos(cep) : atual?.Cep ?? Texto.SomenteDigitos(p.Cep ?? ""),
            Latitude = Coordenadas.Arredondar(f.LerLatitude()) ?? atual?.Latitude,
            Longitude = Coordenadas.Arredondar(f.LerLongitude()) ?? atual?.Longitude
        };

        var erros = new List<CampoErro>();
        if (endereco.Logradouro.Length == 0) erros.Add(new("endereco", "O endereço é obrigatório"));
        if (endereco.Logradouro.Length > 100) erros.Add(new("endereco", "O endereço deve ter no máximo 100 caracteres"));
        if (endereco.Bairro.Length == 0) erros.Add(new("bairro", "O bairro é obrigatório"));
        if (endereco.Cidade.Length == 0) erros.Add(new("cidade", "A cidade é obrigatória"));
        if (endereco.Estado.Length != 2) erros.Add(new("estado", "O estado deve ser a sigla com 2 letras"));
        if (endereco.Cep.Length != 8) erros.Add(new("cep", "O CEP deve ter 8 dígitos"));
        if (erros.Count > 0)
            throw new ValidacaoException(erros);
        return endereco;
    }

    // ENDERECOS não guarda o dono. Um cidadão pode usar o próprio endereço de cadastro ou o local
    // de uma ocorrência já registrada (que já é público), mas nunca o endereço residencial de outra pessoa.
    public async Task<bool> PodeUsarAsync(long enderecoId, UsuarioAtual usuario, CancellationToken ct)
    {
        if (usuario.EhGestor)
            return true;
        return await db.Usuarios.AnyAsync(u => u.Id == usuario.Id && u.EnderecoId == enderecoId, ct)
               || await db.Solicitacoes.AnyAsync(s => s.EnderecoId == enderecoId, ct);
    }

    public async Task<EnderecoResponse> BuscarAsync(long id, UsuarioAtual usuario, CancellationToken ct)
    {
        var endereco = await db.Enderecos.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct)
                       ?? throw new RecursoNaoEncontradoException($"Endereço {id} não encontrado");
        if (!await PodeUsarAsync(id, usuario, ct))
            throw new AcessoNegadoException("Você não tem permissão para consultar este endereço");
        return ParaResposta(endereco);
    }

    public async Task<bool> EhExclusivoAsync(long enderecoId, long? ignorarSolicitacao, long? ignorarUsuario, CancellationToken ct) =>
        !await db.Solicitacoes.AnyAsync(s => s.EnderecoId == enderecoId && s.Id != ignorarSolicitacao, ct)
        && !await db.Usuarios.AnyAsync(u => u.EnderecoId == enderecoId && u.Id != ignorarUsuario, ct);

    public async Task RemoverSeOrfaoAsync(long enderecoId, CancellationToken ct)
    {
        if (await EhExclusivoAsync(enderecoId, null, null, ct))
            await db.Enderecos.Where(e => e.Id == enderecoId).ExecuteDeleteAsync(ct);
    }

    private static bool Completo(Endereco e) =>
        e.Logradouro.Length > 0 && e.Bairro.Length > 0 && e.Cidade.Length > 0 && e.Estado.Length == 2 && e.Cep.Length == 8;
}
