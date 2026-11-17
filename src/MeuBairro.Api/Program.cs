using System.Net;
using System.Text.Json.Serialization;
using MeuBairro.Api.Configuracao;
using MeuBairro.Api.Dados;
using MeuBairro.Api.Endpoints;
using MeuBairro.Api.Infra;
using MeuBairro.Api.Infra.Idempotencia;
using MeuBairro.Api.Seguranca;
using MeuBairro.Api.Servicos;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var servicos = builder.Services;
var configuracao = builder.Configuration;

servicos.AddOptions<BancoOptions>().Bind(configuracao.GetSection(BancoOptions.Secao));
servicos.AddOptions<JwtOptions>().Bind(configuracao.GetSection(JwtOptions.Secao));
servicos.AddOptions<SenhaOptions>().Bind(configuracao.GetSection(SenhaOptions.Secao));
servicos.AddOptions<AplicacaoOptions>().Bind(configuracao.GetSection(AplicacaoOptions.Secao));
servicos.AddOptions<FotosOptions>().Bind(configuracao.GetSection(FotosOptions.Secao));
servicos.AddOptions<EnderecoPadraoOptions>().Bind(configuracao.GetSection(EnderecoPadraoOptions.Secao));
servicos.AddOptions<CategoriasOptions>().Bind(configuracao.GetSection(CategoriasOptions.Secao));
servicos.AddOptions<IdempotenciaOptions>().Bind(configuracao.GetSection(IdempotenciaOptions.Secao));
servicos.AddOptions<LimitesOptions>().Bind(configuracao.GetSection(LimitesOptions.Secao));
servicos.AddOptions<DadosExemploOptions>().Bind(configuracao.GetSection(DadosExemploOptions.Secao));

servicos.AddSingleton(TimeProvider.System);
servicos.AddSingleton<Relogio>();
servicos.AddSingleton<TravasPorChave>();
servicos.AddSingleton<ArmazenamentoFotos>();
servicos.AddSingleton<SenhaService>();
servicos.AddSingleton<TokenService>();
servicos.AddSingleton<IdempotenciaService>();
servicos.AddHostedService<LimpezaIdempotencia>();
servicos.AddScoped<UnidadeDeTrabalho>();
servicos.AddScoped<ArquivosDaRequisicao>();
servicos.AddScoped<CategoriaService>();
servicos.AddScoped<EnderecoService>();
servicos.AddScoped<ContaService>();
servicos.AddScoped<OcorrenciaService>();

servicos.AddBancoDeDados();
servicos.AddSegurancaJwt();
servicos.AddLimitesDeRequisicao();
servicos.AddVerificacoesDeSaude();
servicos.AddDocumentacaoOpenApi();

servicos.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
servicos.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
servicos.AddOptions<FormOptions>().Configure<IOptions<FotosOptions>>((form, fotos) =>
    form.MultipartBodyLengthLimit = fotos.Value.TamanhoMaximoBytes + 1024 * 1024);

servicos.AddCors();
servicos.AddOptions<CorsOptions>().Configure<IConfiguration>((cors, cfg) =>
    cors.AddDefaultPolicy(p => p
        .WithOrigins(cfg.GetSection("Cors:Origens").Get<string[]>() ?? [])
        .AllowAnyMethod()
        .WithHeaders("Authorization", "Content-Type", IdempotenciaMiddleware.Cabecalho)
        .WithExposedHeaders("Location", "Retry-After", IdempotenciaMiddleware.CabecalhoRepeticao)));

var app = builder.Build();

// Valida o segredo JWT já na inicialização; sem isso, o erro só apareceria na primeira requisição.
_ = app.Services.GetRequiredService<TokenService>();

await InicializadorBanco.ExecutarAsync(app.Services);

// Atrás de um proxy reverso, o IP do cliente só é lido de X-Forwarded-For quando a conexão vem de um proxy
// listado em Proxy:IpsConfiaveis/RedesConfiaveis. Sem configuração, o cabeçalho é ignorado.
var proxy = app.Configuration.GetSection(ProxyOptions.Secao).Get<ProxyOptions>() ?? new ProxyOptions();
if (proxy.IpsConfiaveis.Length > 0 || proxy.RedesConfiaveis.Length > 0)
{
    var encaminhados = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = 1
    };
    encaminhados.KnownProxies.Clear();
    encaminhados.KnownIPNetworks.Clear();
    foreach (var ip in proxy.IpsConfiaveis)
        encaminhados.KnownProxies.Add(IPAddress.Parse(ip));
    foreach (var rede in proxy.RedesConfiaveis)
        encaminhados.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(rede));
    app.UseForwardedHeaders(encaminhados);
}

// Todas as rotas respondem com e sem o prefixo /api.
app.UsePathBase("/api");
app.Use((http, next) =>
{
    // Impede que o navegador "adivinhe" outro tipo para as fotos enviadas pelos usuários.
    http.Response.Headers.XContentTypeOptions = "nosniff";
    return next(http);
});
app.UseMiddleware<ErroMiddleware>();
app.UseStatusCodePages(Erros.EscreverStatusSemCorpoAsync);

if (app.Configuration.GetValue("Swagger:Habilitado", true))
{
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/openapi/v1.json", "Meu Bairro API v1");
        o.RoutePrefix = "swagger";
        o.DocumentTitle = "Meu Bairro API";
    });
}

app.UseCors();
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.UseMiddleware<IdempotenciaMiddleware>();

app.MapOpenApi().AllowAnonymous();
app.MapGet("/", (HttpRequest request) => Results.Redirect($"{request.PathBase}/swagger"))
    .AllowAnonymous()
    .ExcludeFromDescription();

app.MapAuth();
app.MapUsuarios();
app.MapEnderecos();
app.MapCategorias();
app.MapOcorrencias();
app.MapFotos();
app.MapSaude();

app.Run();

public partial class Program;
