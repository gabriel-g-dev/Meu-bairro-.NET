using System.Text;
using MeuBairro.Api.Configuracao;
using MeuBairro.Api.Infra;
using Microsoft.Extensions.Options;

namespace MeuBairro.Api.Seguranca;

public sealed class SenhaService
{
    private readonly int fatorTrabalho;
    private readonly string hashFicticio;

    public SenhaService(IOptions<SenhaOptions> opcoes)
    {
        fatorTrabalho = Math.Clamp(opcoes.Value.FatorTrabalho, 4, 16);
        hashFicticio = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"), fatorTrabalho);
    }

    public string Gerar(string senha) => BCrypt.Net.BCrypt.HashPassword(senha, fatorTrabalho);

    // Senhas guardadas em outro formato (ex.: texto puro) nunca são aceitas.
    public bool Verificar(string senha, string? hash)
    {
        // O BCrypt só compara os primeiros 72 bytes: sem esta regra, a senha real seguida de qualquer sufixo também
        // entraria. Uma senha que o cadastro recusaria nunca confere (o hash fictício mantém o tempo de resposta).
        if (Encoding.UTF8.GetByteCount(senha) > SenhaValidaAttribute.MaximoBytes)
        {
            VerificarFicticio(senha);
            return false;
        }

        if (string.IsNullOrEmpty(hash) || !hash.StartsWith("$2", StringComparison.Ordinal))
            return false;
        try
        {
            return BCrypt.Net.BCrypt.Verify(senha, hash);
        }
        catch (Exception ex) when (ex is BCrypt.Net.SaltParseException or ArgumentException or FormatException)
        {
            // Hash corrompido no banco: trata como credencial inválida em vez de erro 500.
            return false;
        }
    }

    // Mantém o tempo de resposta parecido quando o e-mail não existe, dificultando descobrir contas cadastradas.
    public void VerificarFicticio(string senha) => BCrypt.Net.BCrypt.Verify(senha, hashFicticio);
}
