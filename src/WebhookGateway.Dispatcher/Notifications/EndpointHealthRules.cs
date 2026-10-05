using WebhookGateway.Core.Domain;
using WebhookGateway.Core.Notifications;

namespace WebhookGateway.Dispatcher.Notifications;

/// <summary>
/// La máquina de estados de salud de un destino, sin estado propio: dado dónde está y cómo fue el
/// último intento, dónde pasa a estar y si eso merece un aviso. Quien la guarda y la coordina entre
/// entregas concurrentes es <see cref="EndpointHealthTracker"/>.
/// </summary>
public static class EndpointHealthRules
{
    private const int DegradedThreshold = 3;

    public static (EndpointHealthStatus Status, int Failures, int Successes, NotificationAlertType? Alert) EvaluateNextState(
        EndpointHealthStatus currentStatus, int currentFailures, int currentSuccesses, AttemptVerdict verdict, int downThreshold)
    {
        if (verdict == AttemptVerdict.Retryable)
        {
            var failures = currentFailures + 1;

            if (failures >= downThreshold)
            {
                var alert = currentStatus != EndpointHealthStatus.Down ? NotificationAlertType.EndpointDown : (NotificationAlertType?)null;
                return (EndpointHealthStatus.Down, failures, 0, alert);
            }

            if (failures >= DegradedThreshold && currentStatus == EndpointHealthStatus.Healthy)
            {
                return (EndpointHealthStatus.Degraded, failures, 0, NotificationAlertType.EndpointDegraded);
            }

            return (currentStatus, failures, 0, null);
        }

        if (verdict == AttemptVerdict.Success)
        {
            var successes = currentSuccesses + 1;

            if (currentStatus is EndpointHealthStatus.Down or EndpointHealthStatus.Degraded)
            {
                return (EndpointHealthStatus.Recovered, 0, successes, NotificationAlertType.EndpointRecovered);
            }

            if (currentStatus == EndpointHealthStatus.Recovered && successes >= 2)
            {
                return (EndpointHealthStatus.Healthy, 0, successes, null);
            }

            return (currentStatus, 0, successes, null);
        }

        // Permanent failure (ej: 400 Bad Request): no cambia la salud del host
        return (currentStatus, currentFailures, currentSuccesses, null);
    }
}
