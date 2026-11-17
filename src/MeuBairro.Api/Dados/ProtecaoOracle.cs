using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace MeuBairro.Api.Dados;

public sealed class EscritaBloqueadaException(string mensagem) : InvalidOperationException(mensagem)
{
    public const string MensagemPadrao =
        "O banco Oracle está em modo somente leitura (Banco:Oracle:PermitirEscrita=false). Nenhuma alteração foi enviada.";
}

// Barra o SaveChanges antes de abrir conexão ou montar qualquer comando.
public sealed class BloqueioEscritaSaveChangesInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
        throw new EscritaBloqueadaException(EscritaBloqueadaException.MensagemPadrao);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
        throw new EscritaBloqueadaException(EscritaBloqueadaException.MensagemPadrao);
}

// Segunda barreira: só deixa passar comandos de leitura (inclui ExecuteUpdate/ExecuteDelete e SQL manual).
public sealed partial class BloqueioEscritaComandoInterceptor : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Verificar(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Verificar(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Verificar(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Verificar(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Verificar(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Verificar(command);
        return ValueTask.FromResult(result);
    }

    public static bool EhSomenteLeitura(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return false;

        var texto = Comentarios().Replace(sql, " ").Trim();
        var inicioValido = texto.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                           || texto.StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
        return inicioValido && !PalavrasProibidas().IsMatch(texto);
    }

    private static void Verificar(DbCommand command)
    {
        if (!EhSomenteLeitura(command.CommandText))
            throw new EscritaBloqueadaException(EscritaBloqueadaException.MensagemPadrao);
    }

    [GeneratedRegex(@"/\*.*?\*/|--[^\r\n]*", RegexOptions.Singleline)]
    private static partial Regex Comentarios();

    // FOR UPDATE trava linhas, NEXTVAL altera sequences e ";" indicaria um segundo comando.
    [GeneratedRegex(@"\bFOR\s+UPDATE\b|\bNEXTVAL\b|;\s*\S", RegexOptions.IgnoreCase)]
    private static partial Regex PalavrasProibidas();
}

// Gera o ID pela sequence quando o Oracle não usa IDENTITY nem trigger (Banco:Oracle:EstrategiaId=Sequence).
public sealed partial class SequenciaOracleGenerator : ValueGenerator<long>
{
    private readonly string sequencia;
    private readonly bool permitirEscrita;

    public SequenciaOracleGenerator(string sequencia, bool permitirEscrita)
    {
        if (!NomeValido().IsMatch(sequencia))
            throw new InvalidOperationException($"Nome de sequence inválido: {sequencia}");
        this.sequencia = sequencia;
        this.permitirEscrita = permitirEscrita;
    }

    public override bool GeneratesTemporaryValues => false;

    public override long Next(EntityEntry entry) => NextAsync(entry).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<long> NextAsync(EntityEntry entry, CancellationToken cancellationToken = default)
    {
        if (!permitirEscrita)
            throw new EscritaBloqueadaException(EscritaBloqueadaException.MensagemPadrao);

        var sql = "SELECT " + sequencia + ".NEXTVAL AS \"Value\" FROM DUAL";
        return await entry.Context.Database.SqlQueryRaw<long>(sql).SingleAsync(cancellationToken);
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_$#]{0,127}(\.[A-Za-z][A-Za-z0-9_$#]{0,127})?$")]
    private static partial Regex NomeValido();
}
