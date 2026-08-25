using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Data;

namespace WebhookGateway.Data.Db;

/// <summary>Ajustes de conexión a SQL Server.</summary>
public sealed class SqlOptions
{
    public const string SectionName = "Gateway:Sql";

    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Techo del pool. Acotado a propósito: la instancia es compartida y el gateway no
    /// tiene por qué poder monopolizar sus conexiones. Con 33 peticiones por segundo en
    /// el peor pico, 30 sobran.
    /// </summary>
    public int MaxPoolSize { get; set; } = 30;

    /// <summary>Segundos antes de rendirse al abrir. Corto: preferimos fallar y devolver 503.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Nombre con el que la aplicación se identifica ante SQL Server. El clasificador de
    /// Resource Governor enruta por este valor, así que no es cosmético: sin él la carga
    /// del gateway no queda capada en el servidor compartido.
    /// </summary>
    public string ApplicationName { get; set; } = "WebhookGateway";

    public string Build()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException(
                $"Falta la cadena de conexión. Define {SectionName}:ConnectionString.");
        }

        return new SqlConnectionStringBuilder(ConnectionString)
        {
            ApplicationName = ApplicationName,
            MaxPoolSize = MaxPoolSize,
            ConnectTimeout = ConnectTimeoutSeconds,
            Pooling = true,
        }.ConnectionString;
    }
}

/// <summary>
/// Abre conexiones para el camino caliente, que va con Dapper.
/// </summary>
/// <remarks>
/// Existe para que la cadena de conexión se construya en un solo sitio —con el nombre de
/// aplicación y el techo de pool ya aplicados— y para poder sustituirla en los tests.
/// </remarks>
public interface ISqlConnectionFactory
{
    Task<IDbConnection> OpenAsync(CancellationToken cancellationToken);
}

public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(IOptions<SqlOptions> options) => _connectionString = options.Value.Build();

    public async Task<IDbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
