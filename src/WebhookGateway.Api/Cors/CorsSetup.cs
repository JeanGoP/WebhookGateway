namespace WebhookGateway.Api.Cors;

/// <summary>
/// CORS para el panel. El frontend es un sitio estático servido desde otro origen, así
/// que sin esto el navegador bloquea cualquier llamada a <c>/api/*</c>.
/// </summary>
/// <remarks>
/// La lista vacía es el valor por defecto a propósito: una instancia recién desplegada no
/// acepta ningún origen hasta que alguien diga cuál. Un comodín aquí convertiría el panel
/// en algo que cualquier página puede llamar desde el navegador de un administrador.
/// </remarks>
internal static class CorsSetup
{
    internal const string PolicyName = "panel";

    internal static IServiceCollection AddPanelCors(
        this IServiceCollection services, IConfiguration configuration)
    {
        var origins = configuration.GetSection("Gateway:Cors:AllowedOrigins").Get<string[]>() ?? [];

        return services.AddCors(options => options.AddPolicy(PolicyName, policy =>
        {
            if (origins.Length == 0)
            {
                return;
            }

            /*
                Sin AllowCredentials: la sesión viaja en la cabecera Authorization, no en
                una cookie. Permitir credenciales abriría la puerta a que el navegador
                adjuntase cookies de sesión a peticiones cruzadas sin que nadie lo pida.
            */
            policy.WithOrigins(origins)
                .AllowAnyHeader()
                .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS");
        }));
    }
}
