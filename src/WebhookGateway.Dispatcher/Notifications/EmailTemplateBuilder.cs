using System.Net;
using WebhookGateway.Core.Notifications;

namespace WebhookGateway.Dispatcher.Notifications;

/// <summary>
/// Generador de correos electrónicos en formato HTML responsive con respaldo en texto plano.
/// Sin dependencias externas de plantillas: ultra-ligero y seguro contra inyecciones HTML.
/// </summary>
public static class EmailTemplateBuilder
{
    public static (string HtmlBody, string TextBody) BuildIncidentEmail(
        string subject,
        NotificationAlertType alertType,
        IncidentDetails details)
    {
        var (badgeText, badgeBg, badgeColor) = alertType switch
        {
            NotificationAlertType.EndpointDown => ("ENDPOINT CAÍDO", "#fee2e2", "#991b1b"),
            NotificationAlertType.EndpointDegraded => ("ENDPOINT DEGRADADO", "#fef3c7", "#92400e"),
            NotificationAlertType.EndpointRecovered => ("ENDPOINT RECUPERADO", "#dcfce7", "#166534"),
            NotificationAlertType.InboundError => ("ERROR DE RECEPCIÓN", "#fef3c7", "#92400e"),
            _ => ("EVENTO DESCARTADO (DEAD LETTER)", "#fee2e2", "#991b1b")
        };

        var safeIntegration = WebUtility.HtmlEncode(details.IntegrationName);
        var safeEndpoint = WebUtility.HtmlEncode(details.EndpointName);
        var safeTargetUrl = WebUtility.HtmlEncode(details.TargetUrl);
        var safeError = WebUtility.HtmlEncode(details.ErrorSummary ?? "Sin detalle de error");
        var safeUrl = WebUtility.HtmlEncode(details.DashboardUrl);
        var statusText = details.StatusCode.HasValue ? $"HTTP {details.StatusCode}" : "Error de Red / Conexión";

        var html = $$"""
        <!DOCTYPE html>
        <html lang="es">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1.0">
          <title>{{WebUtility.HtmlEncode(subject)}}</title>
        </head>
        <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f4f5f7; margin: 0; padding: 24px; color: #1e293b;">
          <table align="center" border="0" cellpadding="0" cellspacing="0" width="100%" style="max-width: 600px; background-color: #ffffff; border-radius: 8px; overflow: hidden; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1);">
            <tr>
              <td style="background-color: #0f172a; padding: 20px 24px; text-align: left;">
                <span style="color: #ffffff; font-size: 18px; font-weight: 700; letter-spacing: -0.5px;">Webhook Gateway</span>
              </td>
            </tr>
            <tr>
              <td style="padding: 24px;">
                <div style="display: inline-block; background-color: {{badgeBg}}; color: {{badgeColor}}; padding: 4px 10px; border-radius: 4px; font-size: 12px; font-weight: 700; margin-bottom: 16px;">
                  {{badgeText}}
                </div>
                <h1 style="font-size: 20px; font-weight: 600; margin: 0 0 16px 0; color: #0f172a;">{{WebUtility.HtmlEncode(subject)}}</h1>
                <p style="font-size: 14px; line-height: 1.5; color: #475569; margin: 0 0 20px 0;">
                  Se ha registrado un incidente en la entrega de webhooks que requiere su atención.
                </p>
                <table width="100%" cellpadding="8" cellspacing="0" style="background-color: #f8fafc; border-radius: 6px; font-size: 13px; border: 1px solid #e2e8f0; margin-bottom: 24px;">
                  <tr>
                    <td style="color: #64748b; width: 35%; border-bottom: 1px solid #e2e8f0;"><strong>Integración:</strong></td>
                    <td style="color: #0f172a; border-bottom: 1px solid #e2e8f0;">{{safeIntegration}}</td>
                  </tr>
                  <tr>
                    <td style="color: #64748b; border-bottom: 1px solid #e2e8f0;"><strong>Endpoint:</strong></td>
                    <td style="color: #0f172a; border-bottom: 1px solid #e2e8f0;">{{safeEndpoint}}</td>
                  </tr>
                  <tr>
                    <td style="color: #64748b; border-bottom: 1px solid #e2e8f0;"><strong>Estado HTTP:</strong></td>
                    <td style="color: #0f172a; border-bottom: 1px solid #e2e8f0;"><strong>{{statusText}}</strong></td>
                  </tr>
                  <tr>
                    <td style="color: #64748b; border-bottom: 1px solid #e2e8f0;"><strong>Intentos:</strong></td>
                    <td style="color: #0f172a; border-bottom: 1px solid #e2e8f0;">{{details.AttemptCount}}</td>
                  </tr>
                  <tr>
                    <td style="color: #64748b; border-bottom: 1px solid #e2e8f0;"><strong>Fecha y Hora:</strong></td>
                    <td style="color: #0f172a; border-bottom: 1px solid #e2e8f0;">{{details.OccurredAtUtc:yyyy-MM-dd HH:mm:ss}} UTC</td>
                  </tr>
                  <tr>
                    <td style="color: #64748b; border-bottom: 1px solid #e2e8f0;"><strong>URL Destino:</strong></td>
                    <td style="color: #0f172a; font-family: monospace; font-size: 12px; word-break: break-all; border-bottom: 1px solid #e2e8f0;">{{safeTargetUrl}}</td>
                  </tr>
                  <tr>
                    <td style="color: #64748b; vertical-align: top;"><strong>Último Error:</strong></td>
                    <td style="color: #b91c1c; font-family: monospace; font-size: 12px; word-break: break-all;">{{safeError}}</td>
                  </tr>
                </table>
                <div style="text-align: center; margin-bottom: 24px;">
                  <a href="{{safeUrl}}" style="display: inline-block; background-color: #2563eb; color: #ffffff; text-decoration: none; padding: 10px 20px; border-radius: 6px; font-size: 14px; font-weight: 500;">
                    Ver Incidente en el Panel
                  </a>
                </div>
              </td>
            </tr>
            <tr>
              <td style="background-color: #f8fafc; padding: 16px 24px; text-align: center; font-size: 12px; color: #94a3b8; border-top: 1px solid #e2e8f0;">
                Notificación automática generada por Webhook Gateway. Por favor no responda a este correo.
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;

        var text = $"""
        [Webhook Gateway] {subject}
        ------------------------------------------------------------
        Tipo: {badgeText}
        Integración: {details.IntegrationName}
        Endpoint: {details.EndpointName}
        Estado: {statusText}
        Intentos: {details.AttemptCount}
        Fecha (UTC): {details.OccurredAtUtc:yyyy-MM-dd HH:mm:ss} UTC
        URL Destino: {details.TargetUrl}
        Último Error: {details.ErrorSummary ?? "Sin detalle"}

        Para ver el detalle completo o reenviar el mensaje, acceda al panel:
        {details.DashboardUrl}
        ------------------------------------------------------------
        """;

        return (html, text);
    }

    public static (string HtmlBody, string TextBody) BuildVerificationEmail(
        string subject,
        string integrationName,
        string verifyUrl)
    {
        var safeIntegration = WebUtility.HtmlEncode(integrationName);
        var safeUrl = WebUtility.HtmlEncode(verifyUrl);

        var html = $$"""
        <!DOCTYPE html>
        <html lang="es">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1.0">
          <title>{{WebUtility.HtmlEncode(subject)}}</title>
        </head>
        <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f4f5f7; margin: 0; padding: 24px; color: #1e293b;">
          <table align="center" border="0" cellpadding="0" cellspacing="0" width="100%" style="max-width: 600px; background-color: #ffffff; border-radius: 8px; overflow: hidden; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1);">
            <tr>
              <td style="background-color: #0f172a; padding: 20px 24px; text-align: left;">
                <span style="color: #ffffff; font-size: 18px; font-weight: 700; letter-spacing: -0.5px;">Webhook Gateway</span>
              </td>
            </tr>
            <tr>
              <td style="padding: 24px;">
                <div style="display: inline-block; background-color: #ecfdf5; color: #065f46; padding: 4px 10px; border-radius: 4px; font-size: 12px; font-weight: 700; margin-bottom: 16px;">
                  CONFIRMACIÓN DE SUSCRIPCIÓN
                </div>
                <h1 style="font-size: 20px; font-weight: 600; margin: 0 0 16px 0; color: #0f172a;">{{WebUtility.HtmlEncode(subject)}}</h1>
                <p style="font-size: 14px; line-height: 1.6; color: #475569; margin: 0 0 16px 0;">
                  Has sido registrado para recibir alertas e incidentes técnicos de la integración <strong>{{safeIntegration}}</strong> en Webhook Gateway.
                </p>
                <p style="font-size: 14px; line-height: 1.6; color: #475569; margin: 0 0 24px 0;">
                  Para activar la recepción de alertas, por favor confirma tu suscripción haciendo clic en el siguiente botón:
                </p>
                <div style="text-align: center; margin-bottom: 24px;">
                  <a href="{{safeUrl}}" style="display: inline-block; background-color: #16a34a; color: #ffffff; text-decoration: none; padding: 12px 28px; border-radius: 6px; font-size: 14px; font-weight: 600;">
                    Confirmar Suscripción a Alertas
                  </a>
                </div>
                <p style="font-size: 12px; line-height: 1.5; color: #94a3b8; margin: 0; text-align: center;">
                  Este enlace es válido durante 48 horas. Si no reconoces esta invitación, puedes ignorar este mensaje (Ley 1581 / RFC 8058).
                </p>
              </td>
            </tr>
            <tr>
              <td style="background-color: #f8fafc; padding: 16px 24px; text-align: center; font-size: 12px; color: #94a3b8; border-top: 1px solid #e2e8f0;">
                Notificación generada por Webhook Gateway.
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;

        var text = $"""
        [Webhook Gateway] {subject}
        ------------------------------------------------------------
        Has sido registrado para recibir notificaciones y alertas de la integración {integrationName}.

        Para confirmar tu correo y activar la recepción de alertas, ingresa al siguiente enlace:
        {verifyUrl}

        Este enlace es válido por 48 horas. Si no solicitaste esta suscripción, puedes ignorar este mensaje.
        ------------------------------------------------------------
        """;

        return (html, text);
    }
}
