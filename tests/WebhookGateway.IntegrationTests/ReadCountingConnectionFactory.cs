using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using WebhookGateway.Data.Db;

namespace WebhookGateway.IntegrationTests;

/// <summary>
/// Abre las mismas conexiones que la de producción, pero con <c>SET STATISTICS IO ON</c>, y suma
/// las páginas que el motor dice haber leído. Sirve para medir lo que cuesta una consulta del
/// código real sin copiar su SQL en la prueba.
/// </summary>
internal sealed partial class ReadCountingConnectionFactory(ISqlConnectionFactory inner) : ISqlConnectionFactory
{
    private long _logicalReads;

    /// <summary>Páginas leídas por todo lo ejecutado con conexiones de esta fábrica.</summary>
    public long LogicalReads => Interlocked.Read(ref _logicalReads);

    public async Task<IDbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = (SqlConnection)await inner.OpenAsync(cancellationToken);

        connection.InfoMessage += (_, e) =>
        {
            foreach (Match match in LogicalReadsPattern().Matches(e.Message))
            {
                Interlocked.Add(ref _logicalReads, long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            }
        };

        await connection.ExecuteAsync("SET STATISTICS IO ON;");
        return connection;
    }

    // El mensaje sale en el idioma del inicio de sesión: se aceptan los dos que se usan aquí.
    [GeneratedRegex(@"(?:logical reads|lecturas lógicas) (\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LogicalReadsPattern();
}
