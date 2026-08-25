using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Testcontainers.MsSql;
using WebhookGateway.Core.Domain;
using WebhookGateway.Data.Db;
using WebhookGateway.Dispatcher.Claiming;
using Xunit;

namespace WebhookGateway.IntegrationTests;

/// <summary>
/// Levanta un SQL Server real en contenedor y aplica el esquema de tráfico tal cual está
/// en <c>db/</c>. El claim usa <c>READPAST/UPDLOCK</c> y <c>ROW_NUMBER</c> por destino: eso
/// no se puede simular en memoria, solo un motor real reproduce el comportamiento bajo
/// concurrencia. Compartida por toda la colección: arrancar SQL Server es lento.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string DbName = "WebhookGatewayTest";
    private readonly MsSqlContainer _container = new MsSqlBuilder().Build();

    public ISqlConnectionFactory ConnectionFactory { get; private set; } = default!;
    public DeliveryClaimer Claimer { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // El contenedor entrega una conexión a master. Creamos una base propia y la
        // ponemos en Read Committed Snapshot, como hace db/00 en producción.
        var master = new SqlConnectionStringBuilder(_container.GetConnectionString());
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync($"IF DB_ID('{DbName}') IS NULL CREATE DATABASE {DbName};");
            await conn.ExecuteAsync(
                $"ALTER DATABASE {DbName} SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;");
        }

        var appConn = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = DbName };
        var options = Options.Create(new SqlOptions { ConnectionString = appConn.ConnectionString });
        ConnectionFactory = new SqlConnectionFactory(options);
        Claimer = new DeliveryClaimer(ConnectionFactory);

        await RunScriptAsync("01-schema.sql");
        await RunScriptAsync("02-traffic-tables.sql");
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Abre una conexión a la base de pruebas (la misma que usa el claim).</summary>
    public Task<IDbConnection> OpenAsync() => ConnectionFactory.OpenAsync(CancellationToken.None);

    /// <summary>Vacía la tabla de entregas entre pruebas. TRUNCATE reinicia la identidad.</summary>
    public async Task ResetDeliveriesAsync()
    {
        using var conn = await OpenAsync();
        await conn.ExecuteAsync("TRUNCATE TABLE dbo.WebhookDelivery;");
    }

    /// <summary>Inserta una entrega y devuelve su Id. Sin FKs en tráfico, el destino es libre.</summary>
    public async Task<long> InsertDeliveryAsync(
        DeliveryStatus status, int outboundEndpointId, DateTime nextAttemptAt, DateTime expiresAt,
        DateTime? leaseUntil = null, string? workerId = null, DateTime? createdAt = null)
    {
        using var conn = await OpenAsync();
        return await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO dbo.WebhookDelivery
                (CreatedAt, MessageId, OutboundEndpointId, Status, NextAttemptAt, ExpiresAt, LeaseUntil, WorkerId)
            OUTPUT INSERTED.Id
            VALUES (@CreatedAt, 1, @Endpoint, @Status, @NextAttemptAt, @ExpiresAt, @LeaseUntil, @WorkerId);
            """,
            new
            {
                CreatedAt = createdAt ?? nextAttemptAt,
                Endpoint = outboundEndpointId,
                Status = (byte)status,
                NextAttemptAt = nextAttemptAt,
                ExpiresAt = expiresAt,
                LeaseUntil = leaseUntil,
                WorkerId = workerId,
            });
    }

    /// <summary>Lee el estado persistido de una entrega para comprobar lo que hizo el claim.</summary>
    public async Task<DeliveryRow> ReadDeliveryAsync(long id)
    {
        using var conn = await OpenAsync();
        return await conn.QuerySingleAsync<DeliveryRow>(
            "SELECT Status, WorkerId, LeaseUntil, CompletedAt FROM dbo.WebhookDelivery WHERE Id = @id;",
            new { id });
    }

    public async Task<int> CountByStatusAsync(DeliveryStatus status)
    {
        using var conn = await OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.WebhookDelivery WHERE Status = @s;", new { s = (byte)status });
    }

    private async Task RunScriptAsync(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "db", fileName);
        var sql = await File.ReadAllTextAsync(path);
        using var conn = await OpenAsync();

        // Dapper/ADO no entienden GO: es un separador de lotes de sqlcmd, no T-SQL.
        foreach (var batch in Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(batch))
            {
                await conn.ExecuteAsync(batch);
            }
        }
    }
}

/// <summary>Fila de <c>WebhookDelivery</c> con lo que verifican los tests.</summary>
public sealed record DeliveryRow(byte Status, string? WorkerId, DateTime? LeaseUntil, DateTime? CompletedAt);

[CollectionDefinition(SqlServerCollectionDefinition.Name)]
public sealed class SqlServerCollectionDefinition : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sql-server";
}
