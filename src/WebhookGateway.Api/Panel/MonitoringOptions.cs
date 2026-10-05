namespace WebhookGateway.Api.Panel;

/// <summary>Umbrales del monitor del panel (<c>Gateway:Monitoring</c>).</summary>
public sealed class MonitoringOptions
{
    public const string SectionName = "Gateway:Monitoring";

    /// <summary>
    /// Retraso máximo aceptable de una entrega. Por encima, el panel marca el destino como atrasado.
    /// El vigilante de SQL tiene su propio parámetro (<c>@MaxLagMinutes</c> en el job); conviene que
    /// coincidan.
    /// </summary>
    public int MaxLagMinutes { get; set; } = 15;
}
