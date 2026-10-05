using Microsoft.Data.SqlClient;
using Microsoft.Win32;
using Xunit;

namespace WebhookGateway.IntegrationTests;

/// <summary>
/// Dónde corren las pruebas de integración. Por orden de preferencia:
/// <list type="number">
///   <item>La cadena de <c>WEBHOOKGATEWAY_TEST_SQL</c>, si está definida.</item>
///   <item>Un SQL Server en contenedor, si hay Docker.</item>
///   <item>SQL Server LocalDB, si está instalado (viene con Visual Studio).</item>
/// </list>
/// Sin ninguno de los tres, las pruebas se omiten en vez de fallar.
/// </summary>
/// <remarks>
/// Solo se aceptan servidores locales. La fixture borra y recrea su base al arrancar y vacía
/// tablas entre pruebas: apuntarla al SQL Server compartido con producción sería un accidente
/// esperando a ocurrir, así que se rechaza con un error en vez de confiar en que nadie lo haga.
/// </remarks>
internal static class TestSqlServer
{
    public const string ConnectionStringVariable = "WEBHOOKGATEWAY_TEST_SQL";

    private const string LocalDbDefault =
        @"Server=(localdb)\MSSQLLocalDB;Integrated Security=True;TrustServerCertificate=True";

    /// <summary>
    /// Cadena a <c>master</c> de un servidor ya existente, o <see langword="null"/> si toca
    /// levantar uno en contenedor.
    /// </summary>
    public static readonly string? ExistingServer = ResolveExistingServer();

    public static readonly bool IsAvailable = ExistingServer is not null || DockerEnvironment.IsAvailable;

    private static string? ResolveExistingServer()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return EnsureLocal(configured);
        }

        return !DockerEnvironment.IsAvailable && LocalDbInstalled() ? LocalDbDefault : null;
    }

    private static string EnsureLocal(string connectionString)
    {
        var server = new SqlConnectionStringBuilder(connectionString).DataSource.Trim();
        var host = server.Split('\\', ',')[0].Trim();

        var local = server.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase)
            || host is "." or "(local)" or "127.0.0.1"
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

        return local
            ? connectionString
            : throw new InvalidOperationException(
                $"{ConnectionStringVariable} apunta a '{server}', que no es un servidor local. Las pruebas borran " +
                "y recrean su base: usa LocalDB, localhost o un contenedor, nunca el servidor compartido.");
    }

    private static bool LocalDbInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var versions = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Microsoft SQL Server Local DB\Installed Versions");

        return versions is { SubKeyCount: > 0 };
    }
}

/// <summary>
/// Detecta si hay un Docker accesible. Comprobación barata: no arranca contenedores ni
/// lanza procesos, solo mira el socket/pipe y <c>DOCKER_HOST</c>. Se evalúa una vez.
/// </summary>
internal static class DockerEnvironment
{
    public static readonly bool IsAvailable = Probe();

    private static bool Probe()
    {
        // En CI el daemon suele exponerse por TCP con esta variable.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            return true;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Los named pipes no responden a File.Exists; hay que enumerar el directorio.
                return Directory.GetFiles(@"\\.\pipe\")
                    .Any(p => p.Contains("docker_engine", StringComparison.OrdinalIgnoreCase));
            }

            return File.Exists("/var/run/docker.sock");
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Como <c>[Fact]</c>, pero se salta la prueba —no la falla— si no hay ningún SQL Server donde
/// correrla (ver <see cref="TestSqlServer"/>). Así un <c>dotnet test</c> de toda la solución sigue
/// en verde en una máquina sin nada instalado: estas aparecen como omitidas, no como fallidas.
/// </summary>
public sealed class RequiresSqlServerFactAttribute : FactAttribute
{
    public RequiresSqlServerFactAttribute()
    {
        if (!TestSqlServer.IsAvailable)
        {
            Skip = "No hay SQL Server para las pruebas de integración: instala LocalDB, arranca Docker " +
                   $"o define {TestSqlServer.ConnectionStringVariable} con un servidor local.";
        }
    }
}
