using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MeuBairro.Api.Configuracao;
using MeuBairro.Api.Dominio;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MeuBairro.Api.Seguranca;

public sealed record TokenGerado(string Token, DateTime ExpiraEm);

public sealed class TokenService
{
    public const string ClaimId = "sub";
    public const string ClaimPerfil = "perfil";

    private readonly JwtOptions opcoes;
    private readonly SymmetricSecurityKey chave;

    public TokenService(IOptions<JwtOptions> jwt, IHostEnvironment ambiente, ILogger<TokenService> log)
    {
        opcoes = jwt.Value;
        if (string.IsNullOrWhiteSpace(opcoes.Segredo))
        {
            if (!ambiente.IsDevelopment() && !ambiente.IsEnvironment("Testing"))
                throw new InvalidOperationException(
                    "Configure Jwt:Segredo (mínimo de 32 caracteres) por variável de ambiente ou user-secrets");

            log.LogWarning("Jwt:Segredo não configurado. Usando uma chave aleatória: os tokens deixam de valer quando a API reiniciar.");
            chave = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(opcoes.Segredo);
        if (bytes.Length < 32)
            throw new InvalidOperationException("Jwt:Segredo precisa ter pelo menos 32 caracteres");
        chave = new SymmetricSecurityKey(bytes);
    }

    public TokenGerado Gerar(long id, string nome, string email, Perfil perfil)
    {
        var agora = DateTime.UtcNow;
        var expira = agora.AddMinutes(opcoes.ExpiracaoMinutos);
        var descritor = new SecurityTokenDescriptor
        {
            Issuer = opcoes.Emissor,
            Audience = opcoes.Audiencia,
            IssuedAt = agora,
            NotBefore = agora,
            Expires = expira,
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimId, id.ToString()),
                new Claim(ClaimPerfil, perfil.ToString()),
                new Claim("email", email),
                new Claim("name", nome),
                new Claim("jti", Guid.NewGuid().ToString("N"))
            ]),
            SigningCredentials = new SigningCredentials(chave, SecurityAlgorithms.HmacSha256)
        };
        return new TokenGerado(new JsonWebTokenHandler().CreateToken(descritor), expira);
    }

    public TokenValidationParameters ParametrosValidacao() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = opcoes.Emissor,
        ValidateAudience = true,
        ValidAudience = opcoes.Audiencia,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = chave,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(Math.Max(0, opcoes.ToleranciaSegundos)),
        NameClaimType = "name",
        RoleClaimType = ClaimPerfil
    };
}
