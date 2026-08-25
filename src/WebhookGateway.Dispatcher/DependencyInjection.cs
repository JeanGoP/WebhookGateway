using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebhookGateway.Core.Abstractions;
using WebhookGateway.Core.Auth;
using WebhookGateway.Core.Auth.Providers;
using WebhookGateway.Dispatcher.Claiming;
using WebhookGateway.Dispatcher.Queue;
using WebhookGateway.Dispatcher.Recording;
using WebhookGateway.Dispatcher.Sending;
using WebhookGateway.Dispatcher.Throttling;

namespace WebhookGateway.Dispatcher;

public static class DependencyInjection
{
    /// <summary>
    /// Registra la señal de trabajo entre recepción y despacho. Va aparte de
    /// <see cref="AddGatewayDispatcher"/> porque la recepción la necesita siempre, incluso
    /// en una instancia que solo reciba y no despache.
    /// </summary>
    public static IServiceCollection AddDeliveryQueue(this IServiceCollection services)
    {
        services.AddSingleton<IDeliveryQueue, ChannelDeliveryQueue>();
        return services;
    }

    /// <summary>
    /// Registra el despachador completo: reclamación con lease, control de ritmo por destino,
    /// cortacircuitos, envío y registro en bloque de los resultados.
    /// </summary>
    public static IServiceCollection AddGatewayDispatcher(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DispatcherOptions>()
            .Bind(configuration.GetSection(DispatcherOptions.SectionName))
            .ValidateOnStart();

        // Una implementación por OutboundAuthType. OAuth2 llega en F5: es la única con
        // estado —caché de token, refresco y protección contra estampidas— y por eso no
        // cabe en el mismo molde que las demás.
        services.AddSingleton<IOutboundAuthProvider, NoOutboundAuthProvider>();
        services.AddSingleton<IOutboundAuthProvider, ApiKeyOutboundProvider>();
        services.AddSingleton<IOutboundAuthProvider, BasicOutboundProvider>();
        services.AddSingleton<IOutboundAuthProvider, BearerOutboundProvider>();
        services.AddSingleton<IOutboundAuthProvider, HmacOutboundProvider>();

        services.AddHttpClient(DeliverySender.ClientName)
            .ConfigureHttpClient(client =>
            {
                // El tiempo de espera lo pone cada destino, no el cliente compartido.
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("WebhookGateway/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // Reciclar conexiones periódicamente es lo que hace que un cambio de DNS del
                // destino se note sin reiniciar.
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = System.Net.DecompressionMethods.All,

                // No seguimos redirecciones: un destino mal configurado no se arregla
                // siguiéndolas, y hacerlo puede reenviar credenciales a otro host.
                AllowAutoRedirect = false,
            });

        services.AddSingleton<DeliveryClaimer>();
        services.AddSingleton<DeliveryRecorder>();
        services.AddSingleton<OutboundTargetCache>();
        services.AddSingleton<DeliverySender>();
        services.AddSingleton<EndpointThrottles>();
        services.AddSingleton<EndpointBreakers>();
        services.AddSingleton<DeliveryDispatcher>();

        services.AddHostedService<DispatcherWorker>();

        return services;
    }
}
