using Microsoft.EntityFrameworkCore;
using WebhookGateway.Core.Domain;

namespace WebhookGateway.Data;

/// <summary>
/// EF Core cubre <b>solo la configuración</b>: integraciones, endpoints, suscripciones,
/// usuarios y auditoría. Son tablas pequeñas donde la productividad manda.
/// </summary>
/// <remarks>
/// Las tablas de tráfico (mensajes, entregas, intentos) no están aquí a propósito: se
/// acceden con Dapper, porque necesitan SQL exacto —el claim con <c>READPAST</c>, los
/// inserts en batch— y porque sus claves incluyen la columna de partición, lo que
/// complicaría el modelo de EF sin dar nada a cambio.
/// <para>
/// El esquema lo crean los scripts de <c>db/</c>, no las migraciones. Aquí no se genera
/// ninguna: una sola fuente de verdad, sin deriva posible entre las dos.
/// </para>
/// </remarks>
public sealed class GatewayDbContext(DbContextOptions<GatewayDbContext> options) : DbContext(options)
{
    public DbSet<Integration> Integrations => Set<Integration>();

    public DbSet<InboundEndpoint> InboundEndpoints => Set<InboundEndpoint>();

    public DbSet<OutboundEndpoint> OutboundEndpoints => Set<OutboundEndpoint>();

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    public DbSet<AppUser> Users => Set<AppUser>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Integration>(e =>
        {
            e.ToTable("Integration");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Slug).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<InboundEndpoint>(e =>
        {
            e.ToTable("InboundEndpoint");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Slug).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.DedupeSource).HasMaxLength(400);
            // Vista de las dos columnas cifradas. No es una columna.
            e.Ignore(x => x.AuthConfig);
            e.HasIndex(x => new { x.IntegrationId, x.Slug }).IsUnique();
            e.HasOne(x => x.Integration).WithMany(i => i.InboundEndpoints)
                .HasForeignKey(x => x.IntegrationId);
        });

        modelBuilder.Entity<OutboundEndpoint>(e =>
        {
            e.ToTable("OutboundEndpoint");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.TargetUrl).HasMaxLength(2000);
            e.Property(x => x.HttpMethod).HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.BackoffLadderJson).HasMaxLength(500);
            e.Ignore(x => x.AuthConfig);
            e.HasOne(x => x.Integration).WithMany(i => i.OutboundEndpoints)
                .HasForeignKey(x => x.IntegrationId);
        });

        modelBuilder.Entity<Subscription>(e =>
        {
            e.ToTable("Subscription");
            e.HasIndex(x => new { x.InboundEndpointId, x.OutboundEndpointId }).IsUnique();
            e.HasOne(x => x.InboundEndpoint).WithMany(i => i.Subscriptions)
                .HasForeignKey(x => x.InboundEndpointId);
            e.HasOne(x => x.OutboundEndpoint).WithMany(o => o.Subscriptions)
                .HasForeignKey(x => x.OutboundEndpointId);
        });

        modelBuilder.Entity<AppUser>(e =>
        {
            e.ToTable("AppUser");
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.PasswordHash).HasMaxLength(500);
            e.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.ToTable("RefreshToken");
            e.Property(x => x.TokenHash).HasMaxLength(32);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<AuditLog>(e =>
        {
            e.ToTable("AuditLog");
            e.Property(x => x.Action).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.EntityType).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.EntityId).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.SourceIp).HasMaxLength(45).IsUnicode(false);
            e.HasIndex(x => x.OccurredAt);
        });
    }
}
