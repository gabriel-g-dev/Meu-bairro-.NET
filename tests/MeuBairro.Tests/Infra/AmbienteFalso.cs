using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace MeuBairro.Tests.Infra;

public sealed class AmbienteFalso(string raiz, string nome = "Testing") : IHostEnvironment
{
    public string EnvironmentName { get; set; } = nome;
    public string ApplicationName { get; set; } = "MeuBairro.Api";
    public string ContentRootPath { get; set; } = raiz;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
