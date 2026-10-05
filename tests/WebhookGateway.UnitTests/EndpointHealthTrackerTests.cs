using Shouldly;
using WebhookGateway.Core.Domain;
using WebhookGateway.Core.Notifications;
using WebhookGateway.Dispatcher.Notifications;
using Xunit;

namespace WebhookGateway.UnitTests;

public sealed class EndpointHealthTrackerTests
{
    private const int BreakerThreshold = 5;

    [Fact]
    public void Transiciona_a_Degraded_exactamente_al_tercer_fallo_transitorio()
    {
        // Fallo 1
        var (s1, f1, _, a1) = EndpointHealthRules.EvaluateNextState(
            EndpointHealthStatus.Healthy, currentFailures: 0, currentSuccesses: 0, AttemptVerdict.Retryable, BreakerThreshold);
        s1.ShouldBe(EndpointHealthStatus.Healthy);
        f1.ShouldBe(1);
        a1.ShouldBeNull();

        // Fallo 2
        var (s2, f2, _, a2) = EndpointHealthRules.EvaluateNextState(
            s1, currentFailures: f1, currentSuccesses: 0, AttemptVerdict.Retryable, BreakerThreshold);
        s2.ShouldBe(EndpointHealthStatus.Healthy);
        f2.ShouldBe(2);
        a2.ShouldBeNull();

        // Fallo 3 -> Debe degradar y alertar
        var (s3, f3, _, a3) = EndpointHealthRules.EvaluateNextState(
            s2, currentFailures: f2, currentSuccesses: 0, AttemptVerdict.Retryable, BreakerThreshold);
        s3.ShouldBe(EndpointHealthStatus.Degraded);
        f3.ShouldBe(3);
        a3.ShouldBe(NotificationAlertType.EndpointDegraded);
    }

    [Fact]
    public void Transiciona_a_Down_al_alcanzar_umbral_de_cortacircuitos()
    {
        // Estando en Degraded con 4 fallos
        var (status, failures, _, alert) = EndpointHealthRules.EvaluateNextState(
            EndpointHealthStatus.Degraded, currentFailures: 4, currentSuccesses: 0, AttemptVerdict.Retryable, BreakerThreshold);

        status.ShouldBe(EndpointHealthStatus.Down);
        failures.ShouldBe(5);
        alert.ShouldBe(NotificationAlertType.EndpointDown);
    }

    [Fact]
    public void Suprime_spam_mientras_el_endpoint_siga_Down()
    {
        // Si ya está Down y siguen llegando fallos (ej: intento 6, 7, 20)
        var (status, failures, _, alert) = EndpointHealthRules.EvaluateNextState(
            EndpointHealthStatus.Down, currentFailures: 5, currentSuccesses: 0, AttemptVerdict.Retryable, BreakerThreshold);

        status.ShouldBe(EndpointHealthStatus.Down);
        failures.ShouldBe(6);
        alert.ShouldBeNull(); // ¡Cero spam! No emite alerta repetida
    }

    [Fact]
    public void Transiciona_a_Recovered_y_alerta_cuando_responde_con_exito_tras_estar_caido()
    {
        var (status, failures, successes, alert) = EndpointHealthRules.EvaluateNextState(
            EndpointHealthStatus.Down, currentFailures: 10, currentSuccesses: 0, AttemptVerdict.Success, BreakerThreshold);

        status.ShouldBe(EndpointHealthStatus.Recovered);
        failures.ShouldBe(0);
        successes.ShouldBe(1);
        alert.ShouldBe(NotificationAlertType.EndpointRecovered);
    }

    [Fact]
    public void Transiciona_de_Recovered_a_Healthy_sin_alerta_adicional()
    {
        // Con 1 éxito previo en Recovered, llega el segundo éxito
        var (status, _, successes, alert) = EndpointHealthRules.EvaluateNextState(
            EndpointHealthStatus.Recovered, currentFailures: 0, currentSuccesses: 1, AttemptVerdict.Success, BreakerThreshold);

        status.ShouldBe(EndpointHealthStatus.Healthy);
        successes.ShouldBe(2);
        alert.ShouldBeNull(); // No hace falta enviar otro correo, ya se avisó la recuperación
    }

    [Fact]
    public void Fallo_permanente_400_no_afecta_la_salud_del_host()
    {
        var (status, failures, _, alert) = EndpointHealthRules.EvaluateNextState(
            EndpointHealthStatus.Healthy, currentFailures: 1, currentSuccesses: 0, AttemptVerdict.Permanent, BreakerThreshold);

        status.ShouldBe(EndpointHealthStatus.Healthy);
        failures.ShouldBe(1);
        alert.ShouldBeNull();
    }
}
