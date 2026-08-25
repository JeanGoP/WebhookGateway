using System.Net;
using WebhookGateway.Core.Common;
using WebhookGateway.Core.Domain;

namespace WebhookGateway.Core.Auth.Validators;

/// <summary>Restringe por IP de origen, en notación CIDR.</summary>
public sealed class IpAllowlistInboundValidator : IInboundAuthValidator
{
    public InboundAuthType Type => InboundAuthType.IpAllowlist;

    public ValueTask<Result> ValidateAsync(in InboundRequest request, InboundAuthConfig config, CancellationToken cancellationToken)
    {
        var auth = (IpAllowlistInboundAuth)config;

        if (!IPAddress.TryParse(request.SourceIp, out var sourceIp))
        {
            return ValueTask.FromResult(Result.Fail("auth.unresolvable_ip", "No se pudo interpretar la IP de origen."));
        }

        foreach (var cidr in auth.AllowedCidrs)
        {
            if (IPNetwork.TryParse(cidr, out var network) && network.Contains(sourceIp))
            {
                return ValueTask.FromResult(Result.Ok());
            }
        }

        return ValueTask.FromResult(Result.Fail("auth.ip_not_allowed", "La IP de origen no está en la lista permitida."));
    }
}
