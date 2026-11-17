using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MeuBairro.Tests.Infra;

// Monta tokens manualmente para simular expiração, chave errada, emissor errado e adulteração.
public static class Tokens
{
    public const string Emissor = "meu-bairro-api-dotnet";
    public const string Audiencia = "meu-bairro-clientes";

    public static string Criar(
        long id,
        string perfil = "CIDADAO",
        string segredo = ApiFactory.SegredoJwt,
        string emissor = Emissor,
        string audiencia = Audiencia,
        DateTime? inicio = null,
        DateTime? expira = null)
    {
        var emitidoEm = inicio ?? DateTime.UtcNow;
        var descritor = new SecurityTokenDescriptor
        {
            Issuer = emissor,
            Audience = audiencia,
            IssuedAt = emitidoEm,
            NotBefore = emitidoEm,
            Expires = expira ?? emitidoEm.AddMinutes(30),
            Subject = new ClaimsIdentity([new Claim("sub", id.ToString()), new Claim("perfil", perfil)]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(segredo)), SecurityAlgorithms.HmacSha256)
        };
        return new JsonWebTokenHandler().CreateToken(descritor);
    }

    public static string SemAssinatura(long id, string perfil = "GESTOR")
    {
        static string Base64Url(string json) =>
            Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(json));

        var expira = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var cabecalho = Base64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var corpo = Base64Url($"{{\"sub\":\"{id}\",\"perfil\":\"{perfil}\",\"iss\":\"{Emissor}\",\"aud\":\"{Audiencia}\",\"exp\":{expira}}}");
        return $"{cabecalho}.{corpo}.";
    }
}
