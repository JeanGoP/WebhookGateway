using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using WebhookGateway.Core.Notifications;

namespace WebhookGateway.Dispatcher.Notifications;

/// <summary>Resultado de un intento de despacho de notificación.</summary>
public sealed record SmtpSendResult(
    bool Success,
    string? MessageId,
    string? ErrorMessage,
    bool IsRetryable);

/// <summary>
/// Despachador de correos electrónicos vía Turbo SMTP con soporte de DryRun para desarrollo.
/// </summary>
public sealed class SmtpNotificationSender(
    IOptions<NotificationOptions> options,
    ILogger<SmtpNotificationSender> logger)
{
    private readonly SmtpOptions _smtp = options.Value.Smtp;

    public async Task<SmtpSendResult> SendEmailAsync(
        string recipient,
        string subject,
        string htmlBody,
        string textBody,
        CancellationToken cancellationToken)
    {
        if (_smtp.DryRun)
        {
            var simulatedId = $"<dryrun-{Guid.NewGuid():N}@webhookgateway.local>";
            logger.LogInformation(
                "[DryRun] Notificación simulada a {Recipient} | Asunto: {Subject} | SimId: {Id}",
                recipient, subject, simulatedId);

            return new SmtpSendResult(Success: true, MessageId: simulatedId, ErrorMessage: null, IsRetryable: false);
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_smtp.FromName, _smtp.FromEmail));
            message.To.Add(MailboxAddress.Parse(recipient));
            message.Subject = subject;

            var builder = new BodyBuilder
            {
                HtmlBody = htmlBody,
                TextBody = textBody
            };

            message.Body = builder.ToMessageBody();

            using var client = new SmtpClient();
            client.Timeout = 15_000; // 15 segundos máximo por intento

            var secureOption = _smtp.Port == 465
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTlsWhenAvailable;

            await client.ConnectAsync(_smtp.Host, _smtp.Port, secureOption, cancellationToken);

            if (!string.IsNullOrWhiteSpace(_smtp.Username))
            {
                await client.AuthenticateAsync(_smtp.Username, _smtp.Password, cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);

            var messageId = message.MessageId ?? $"<sent-{Guid.NewGuid():N}@turbo-smtp.com>";

            logger.LogInformation(
                "Notificación enviada con éxito a {Recipient} vía Turbo SMTP. MessageId: {MessageId}",
                recipient, messageId);

            return new SmtpSendResult(Success: true, MessageId: messageId, ErrorMessage: null, IsRetryable: false);
        }
        catch (SmtpCommandException ex)
        {
            // Códigos 4xx de SMTP son fallos transitorios (ej: 421 servicio temporalmente no disponible)
            // Códigos 5xx son definitivos (ej: 550 buzón no existe, rechazo permanente)
            var isRetryable = (int)ex.StatusCode >= 400 && (int)ex.StatusCode < 500;
            var error = $"Error SMTP {ex.StatusCode}: {ex.Message}";

            logger.LogWarning(ex, "Fallo al enviar correo a {Recipient} vía SMTP (Reintentable: {IsRetryable})", recipient, isRetryable);
            return new SmtpSendResult(Success: false, MessageId: null, ErrorMessage: error, IsRetryable: isRetryable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = $"Fallo de conexión SMTP: {ex.Message}";
            logger.LogWarning(ex, "Excepción de red al conectar con Turbo SMTP para {Recipient}", recipient);
            return new SmtpSendResult(Success: false, MessageId: null, ErrorMessage: error, IsRetryable: true);
        }
    }
}
