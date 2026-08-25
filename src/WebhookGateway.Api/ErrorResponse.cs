namespace WebhookGateway.Api;

/// <summary>
/// Forma única de los errores de la API.
/// </summary>
/// <remarks>
/// Existe para que el documento OpenAPI declare también las respuestas de fallo. Con
/// objetos anónimos el generador de tipos del frontend no ve nada y el cliente acaba
/// tratando cualquier error como <c>unknown</c>, que es justo lo que la regla de generar
/// los tipos desde el OpenAPI pretende evitar.
/// </remarks>
public sealed record ErrorResponse(string Error);
