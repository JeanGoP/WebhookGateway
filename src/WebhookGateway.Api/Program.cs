using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using WebhookGateway.Api.Auth;
using WebhookGateway.Api.Cors;
using WebhookGateway.Api.Health;
using WebhookGateway.Api.Panel;
using WebhookGateway.Api.Reception;
using WebhookGateway.Core.Auth;
using WebhookGateway.Core.Auth.Validators;
using WebhookGateway.Data;
using WebhookGateway.Data.Db;
using WebhookGateway.Data.Traffic;
using WebhookGateway.Dispatcher;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddGatewayData(builder.Configuration);

// La cola en memoria la necesita la recepción siempre; el despachador solo si esta
// instancia además entrega. Separarlos permite escalar recepción y despacho por su cuenta.
builder.Services.AddDeliveryQueue();
builder.Services.AddGatewayDispatcher(builder.Configuration);

// Recepción: un validador por InboundAuthType, resueltos por tipo en tiempo de petición.
builder.Services.AddScoped<InboundMessageReceiver>();
builder.Services.AddSingleton<IInboundAuthValidator, NoAuthValidator>();
builder.Services.AddSingleton<IInboundAuthValidator, ApiKeyInboundValidator>();
builder.Services.AddSingleton<IInboundAuthValidator, BasicInboundValidator>();
builder.Services.AddSingleton<IInboundAuthValidator, BearerInboundValidator>();
builder.Services.AddSingleton<IInboundAuthValidator, HmacInboundValidator>();
builder.Services.AddSingleton<IInboundAuthValidator, IpAllowlistInboundValidator>();

// --- JWT authentication ---
var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
builder.Services.Configure<JwtOptions>(jwtSection);

var jwtKey = jwtSection["Key"] ?? throw new InvalidOperationException("Gateway:Jwt:Key no está configurada.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(jwtKey)),
            ValidateIssuer = true,
            ValidIssuer = jwtSection["Issuer"] ?? "WebhookGateway",
            ValidateAudience = true,
            ValidAudience = jwtSection["Audience"] ?? "WebhookGateway",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddPanelCors(builder.Configuration);

/*
    Los enums salen y entran como texto. El panel recibe "Hmac" en vez de 4, que es lo que
    ya devolvían los DTO a mano con ToString(); la diferencia es que ahora el documento
    OpenAPI lo declara, y los tipos generados del frontend son uniones de literales en vez
    de números sueltos. En la lectura se siguen aceptando los valores numéricos, así que
    ningún cliente existente se rompe.
*/
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// --- Panel: servicios ---
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<JwtTokenGenerator>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<MessageExplorer>();

builder.Services.AddHealthChecks()
    .AddCheck<SqlHealthCheck>("sql", tags: ["ready"]);

builder.Services.AddOpenApi();

var app = builder.Build();

// A qué servidor vamos a hablar, dicho en voz alta al arrancar. Sin esto, un fallo de
// conexión obliga a adivinar si la cadena que se cargó es la que uno cree haber editado
// —appsettings.Development.json solo se lee si el entorno es Development—. Solo el
// servidor y la base: la cadena entera puede llevar contraseña.
{
    var sql = new SqlConnectionStringBuilder(
        app.Services.GetRequiredService<IOptions<SqlOptions>>().Value.Build());

    app.Logger.LogInformation(
        "SQL Server destino: {DataSource} / {Database} (entorno {Environment})",
        sql.DataSource, sql.InitialCatalog, app.Environment.EnvironmentName);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

/*
    Dos sondas distintas a propósito:

    /health/live   ¿el proceso está vivo? No toca la base de datos. Es lo que mira el
                   supervisor para decidir si reiniciar.

    /health/ready  ¿puede hacer su trabajo? Comprueba SQL Server. Es lo que mira el
                   balanceador para decidir si mandarle tráfico. Si SQL no responde
                   esta instancia no puede persistir, y es mejor que no reciba nada
                   a que acepte webhooks y los pierda.
*/
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

// Antes de la autenticación: un preflight OPTIONS viaja sin cabecera Authorization y
// debe responderse igualmente, o el navegador nunca llega a mandar la petición real.
app.UseCors(CorsSetup.PolicyName);

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { service = "WebhookGateway", status = "ok" }));
app.MapReception();

// Panel de administración.
app.MapSetup();
app.MapAuth();
app.MapIntegrations();
app.MapInboundEndpoints();
app.MapOutboundEndpoints();
app.MapSubscriptions();
app.MapMessages();
app.MapDeliveries();

await app.RunAsync();

/// <summary>Visible para las pruebas de integración con WebApplicationFactory.</summary>
public partial class Program;
