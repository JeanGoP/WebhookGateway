using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebhookGateway.Core.Abstractions;
using WebhookGateway.Data.Configuration;
using WebhookGateway.Data.Db;
using WebhookGateway.Data.Payloads;
using WebhookGateway.Data.Security;
using WebhookGateway.Data.Traffic;

namespace WebhookGateway.Data;

public static class DependencyInjection
{
    /// <summary>
    /// Registra el acceso a datos: EF Core para la configuración, Dapper para el tráfico,
    /// cifrado de secretos y almacenamiento de cuerpos.
    /// </summary>
    public static IServiceCollection AddGatewayData(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SqlOptions>()
            .Bind(configuration.GetSection(SqlOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<SecretProtectionOptions>()
            .Bind(configuration.GetSection(SecretProtectionOptions.SectionName))
            .Validate(
                o => { o.Decode(); return true; },
                "Las claves de cifrado no son válidas. Revisa Gateway:Secrets.")
            .ValidateOnStart();

        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        services.AddSingleton<AuthConfigCodec>();
        services.AddSingleton<IPayloadStore, GzipPayloadStore>();

        // Acceso al tráfico con Dapper. Sin estado, así que singleton.
        services.AddSingleton<TrafficWriter>();
        services.AddSingleton<MessagePayloadReader>();
        services.AddSingleton<DeliveryRetryWriter>();

        // Consultas de configuración con EF Core: siguen el ámbito del DbContext.
        services.AddScoped<InboundEndpointLookup>();

        services.AddDbContext<GatewayDbContext>((provider, options) =>
        {
            var sql = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SqlOptions>>().Value;

            options.UseSqlServer(sql.Build(), b => b.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorNumbersToAdd: null));

            /*
                El CRUD de configuración es de lectura casi siempre y de escritura
                puntual, así que no hace falta que EF vaya siguiendo entidades. Las
                escrituras piden seguimiento explícito con AsTracking().
            */
            options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        });

        return services;
    }
}
