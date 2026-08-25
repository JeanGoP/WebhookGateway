using Xunit;

namespace WebhookGateway.IntegrationTests;

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
/// Como <c>[Fact]</c>, pero se salta la prueba —no la falla— si no hay Docker. Los tests de
/// integración levantan SQL Server en contenedor; sin Docker se ejecutan en CI o en el
/// despliegue, no en una máquina de desarrollo que no lo tiene. Así un <c>dotnet test</c> de
/// toda la solución sigue en verde: estos aparecen como omitidos, no como fallidos.
/// </summary>
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute()
    {
        if (!DockerEnvironment.IsAvailable)
        {
            Skip = "Docker no está disponible: esta suite se ejecuta en CI o en el despliegue.";
        }
    }
}
