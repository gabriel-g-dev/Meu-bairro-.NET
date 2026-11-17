using MeuBairro.Api.Contratos;
using MeuBairro.Api.Dados;
using MeuBairro.Api.Dominio;
using MeuBairro.Api.Infra;
using MeuBairro.Api.Seguranca;
using Microsoft.EntityFrameworkCore;

namespace MeuBairro.Api.Servicos;

// Cidadãos ficam em USUARIOS e funcionários (perfil GESTOR) em FUNCIONARIOS, como no esquema Oracle.
public sealed class ContaService(
    AppDbContext db,
    SenhaService senhas,
    TokenService tokens,
    EnderecoService enderecos,
    Relogio relogio,
    UnidadeDeTrabalho unidade)
{
    private const string MensagemCredenciais = "E-mail ou senha inválidos";

    public static string NormalizarEmail(string email) => email.Trim().ToLowerInvariant();

    public async Task<UsuarioResponse> RegistrarCidadaoAsync(CadastroUsuarioRequest dados, CancellationToken ct)
    {
        var email = NormalizarEmail(dados.Email!);
        // O BCrypt é a parte mais demorada: calculado antes de abrir a transação.
        var hash = senhas.Gerar(dados.Senha!);
        var endereco = dados.Endereco is not null ? EnderecoService.Novo(dados.Endereco) : enderecos.PadraoParaCadastro();

        await using var escopo = await unidade.IniciarAsync("email:" + email, ct);
        if (await db.Usuarios.AnyAsync(u => u.Email.ToLower() == email, ct))
            throw new RegraNegocioException("Já existe um usuário cadastrado com este e-mail");

        var usuario = new Usuario
        {
            Nome = dados.Nome!.Trim(),
            Email = email,
            Senha = hash,
            Telefone = Texto.Limpar(dados.Telefone),
            DataCadastro = relogio.Agora(),
            Endereco = endereco
        };
        db.Usuarios.Add(usuario);
        await SalvarComUnicidadeAsync("Já existe um usuário cadastrado com este e-mail", ct);
        await escopo.ConfirmarAsync(ct);
        return new UsuarioResponse(usuario.Id, usuario.Nome, usuario.Email, Perfil.CIDADAO);
    }

    public async Task<LoginResponse> EntrarAsync(LoginRequest dados, CancellationToken ct)
    {
        var email = NormalizarEmail(dados.Email!);
        Perfil? restricao = dados.Perfil is null ? null : Enums.Ler<Perfil>(dados.Perfil, "perfil");
        var contaEncontrada = false;

        if (restricao != Perfil.GESTOR)
        {
            var cidadao = await db.Usuarios.AsNoTracking()
                .Where(u => u.Email.ToLower() == email)
                .Select(u => new { u.Id, u.Nome, u.Email, u.Senha })
                .FirstOrDefaultAsync(ct);
            if (cidadao is not null)
            {
                contaEncontrada = true;
                if (senhas.Verificar(dados.Senha!, cidadao.Senha))
                    return Resposta(cidadao.Id, cidadao.Nome, cidadao.Email, Perfil.CIDADAO);
            }
        }

        if (restricao != Perfil.CIDADAO)
        {
            var funcionario = await db.Funcionarios.AsNoTracking()
                .Where(f => f.Email.ToLower() == email)
                .Select(f => new { f.Id, f.Nome, f.Email, f.Senha })
                .FirstOrDefaultAsync(ct);
            if (funcionario is not null)
            {
                contaEncontrada = true;
                if (senhas.Verificar(dados.Senha!, funcionario.Senha))
                    return Resposta(funcionario.Id, funcionario.Nome, funcionario.Email, Perfil.GESTOR);
            }
        }

        if (!contaEncontrada)
            senhas.VerificarFicticio(dados.Senha!);
        throw new ApiException(StatusCodes.Status401Unauthorized, MensagemCredenciais);
    }

    public async Task<PerfilResponse> CadastrarGestorAsync(GestorRequest dados, CancellationToken ct)
    {
        var email = NormalizarEmail(dados.Email!);
        var hash = senhas.Gerar(dados.Senha!);
        await using var escopo = await unidade.IniciarAsync("email:" + email, ct);
        if (await db.Funcionarios.AnyAsync(f => f.Email.ToLower() == email, ct))
            throw new RegraNegocioException("Já existe um funcionário cadastrado com este e-mail");

        // FUNCIONARIO_SETOR e FUNCIONARIO_CARGO são obrigatórios no esquema; quando não informados, recebem um valor padrão.
        var funcionario = new Funcionario
        {
            Nome = dados.Nome!.Trim(),
            Email = email,
            Senha = hash,
            Setor = Texto.Limpar(dados.Setor) ?? "Zeladoria",
            Cargo = Texto.Limpar(dados.Cargo) ?? "Gestor",
            Telefone = Texto.Limpar(dados.Telefone)
        };
        db.Funcionarios.Add(funcionario);
        await SalvarComUnicidadeAsync("Já existe um funcionário cadastrado com este e-mail", ct);
        await escopo.ConfirmarAsync(ct);
        return new PerfilResponse(funcionario.Id, funcionario.Nome, funcionario.Email, Perfil.GESTOR,
            funcionario.Telefone, null, null, funcionario.Setor, funcionario.Cargo);
    }

    public async Task<PerfilResponse> PerfilAsync(UsuarioAtual atual, CancellationToken ct)
    {
        if (atual.EhGestor)
        {
            var f = await db.Funcionarios.AsNoTracking().FirstOrDefaultAsync(x => x.Id == atual.Id, ct)
                    ?? throw new RecursoNaoEncontradoException("Funcionário não encontrado");
            return new PerfilResponse(f.Id, f.Nome, f.Email, Perfil.GESTOR, f.Telefone, null, null, f.Setor, f.Cargo);
        }

        var u = await db.Usuarios.AsNoTracking().FirstOrDefaultAsync(x => x.Id == atual.Id, ct)
                ?? throw new RecursoNaoEncontradoException("Usuário não encontrado");
        return new PerfilResponse(u.Id, u.Nome, u.Email, Perfil.CIDADAO, u.Telefone, u.DataCadastro, u.EnderecoId, null, null);
    }

    // Só nome e telefone podem ser alterados; e-mail, perfil e senha não fazem parte do contrato.
    public async Task<PerfilResponse> AtualizarPerfilAsync(UsuarioAtual atual, AtualizacaoPerfilRequest dados, CancellationToken ct)
    {
        if (atual.EhGestor)
        {
            var f = await db.Funcionarios.FirstAsync(x => x.Id == atual.Id, ct);
            f.Nome = dados.Nome!.Trim();
            f.Telefone = Texto.Limpar(dados.Telefone);
        }
        else
        {
            var u = await db.Usuarios.FirstAsync(x => x.Id == atual.Id, ct);
            u.Nome = dados.Nome!.Trim();
            u.Telefone = Texto.Limpar(dados.Telefone);
        }
        await db.SaveChangesAsync(ct);
        return await PerfilAsync(atual, ct);
    }

    public async Task<EnderecoResponse> EnderecoDoCidadaoAsync(UsuarioAtual atual, CancellationToken ct)
    {
        var endereco = await db.Usuarios.AsNoTracking().Where(u => u.Id == atual.Id).Select(u => u.Endereco)
                           .FirstOrDefaultAsync(ct)
                       ?? throw new RecursoNaoEncontradoException("Endereço não encontrado");
        return EnderecoService.ParaResposta(endereco);
    }

    // Se o endereço atual também for o local de alguma ocorrência, cria um novo em vez de alterar o compartilhado.
    public async Task<EnderecoResponse> AtualizarEnderecoDoCidadaoAsync(UsuarioAtual atual, EnderecoRequest dados, CancellationToken ct)
    {
        await using var escopo = await unidade.IniciarAsync(null, ct);
        var usuario = await db.Usuarios.Include(u => u.Endereco).FirstAsync(u => u.Id == atual.Id, ct);
        var novo = EnderecoService.Novo(dados);
        if (await enderecos.EhExclusivoAsync(usuario.EnderecoId, null, usuario.Id, ct))
            usuario.Endereco.CopiarDe(novo);
        else
            usuario.Endereco = novo;

        await db.SaveChangesAsync(ct);
        await escopo.ConfirmarAsync(ct);
        return EnderecoService.ParaResposta(usuario.Endereco);
    }

    private LoginResponse Resposta(long id, string nome, string email, Perfil perfil)
    {
        var token = tokens.Gerar(id, nome, email, perfil);
        return new LoginResponse(token.Token, "Bearer", token.ExpiraEm, new UsuarioResponse(id, nome, email, perfil));
    }

    private async Task SalvarComUnicidadeAsync(string mensagem, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Erros.ViolacaoUnicidade(ex))
        {
            throw new RegraNegocioException(mensagem);
        }
    }
}
