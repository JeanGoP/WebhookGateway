using System.Net;

namespace WebhookGateway.Api.Panel;

public enum AlertHtmlType
{
    Success,
    Error,
    Info
}

/// <summary>
/// Generador de páginas HTML autocontenidas y responsivas para confirmación y desuscripción de alertas.
/// No depende de frameworks de frontend, CSS externo ni JavaScript.
/// </summary>
public static class AlertHtmlRenderer
{
    public static string Render(string title, string message, AlertHtmlType type, string? hint = null)
    {
        var safeTitle = WebUtility.HtmlEncode(title);
        var safeMessage = WebUtility.HtmlEncode(message);
        var safeHint = hint is not null ? WebUtility.HtmlEncode(hint) : null;

        var (iconSvg, badgeText, badgeBg, badgeColor) = type switch
        {
            AlertHtmlType.Success => (
                """<svg width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="#16a34a" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 11.08V12a10 10 0 1 1-5.93-9.14"/><polyline points="22 4 12 14.01 9 11.01"/></svg>""",
                "CONFIRMADO", "#dcfce7", "#166534"
            ),
            AlertHtmlType.Error => (
                """<svg width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="#dc2626" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><line x1="15" y1="9" x2="9" y2="15"/><line x1="9" y1="9" x2="15" y2="15"/></svg>""",
                "ENLACE NO VÁLIDO", "#fee2e2", "#991b1b"
            ),
            _ => (
                """<svg width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="#475569" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9"/><path d="M13.73 21a2 2 0 0 1-3.46 0"/><line x1="2" y1="2" x2="22" y2="22"/></svg>""",
                "BAJA REGISTRADA", "#f1f5f9", "#334155"
            )
        };

        var hintHtml = safeHint is not null
            ? $"""<p style="font-size: 12px; color: #64748b; line-height: 1.5; margin: 16px 0 0 0; padding-top: 14px; border-top: 1px solid #f1f5f9;">{safeHint}</p>"""
            : string.Empty;

        return $$"""
        <!DOCTYPE html>
        <html lang="es">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1.0">
          <title>{{safeTitle}} - Webhook Gateway</title>
          <style>
            * { box-sizing: border-box; margin: 0; padding: 0; }
            body {
              font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif;
              background-color: #f8fafc;
              color: #0f172a;
              min-height: 100vh;
              display: flex;
              align-items: center;
              justify-content: center;
              padding: 20px;
            }
            .card {
              background: #ffffff;
              max-width: 440px;
              width: 100%;
              padding: 36px 32px;
              border-radius: 12px;
              border: 1px solid #e2e8f0;
              box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.05), 0 2px 4px -1px rgba(0, 0, 0, 0.03);
              text-align: center;
            }
            .brand {
              display: inline-flex;
              align-items: center;
              justify-content: center;
              width: 44px;
              height: 44px;
              background-color: #0f172a;
              color: #ffffff;
              font-weight: 700;
              font-size: 15px;
              border-radius: 10px;
              margin-bottom: 20px;
              letter-spacing: -0.5px;
            }
            .badge {
              display: inline-block;
              background-color: {{badgeBg}};
              color: {{badgeColor}};
              font-size: 11px;
              font-weight: 700;
              padding: 3px 10px;
              border-radius: 9999px;
              letter-spacing: 0.5px;
              margin-bottom: 16px;
            }
            .icon-wrapper {
              margin-bottom: 16px;
            }
            h1 {
              font-size: 20px;
              font-weight: 700;
              color: #0f172a;
              margin-bottom: 10px;
              line-height: 1.3;
            }
            p.message {
              font-size: 14px;
              color: #475569;
              line-height: 1.6;
            }
            .footer {
              margin-top: 24px;
              font-size: 11px;
              color: #94a3b8;
            }
          </style>
        </head>
        <body>
          <div class="card">
            <div class="brand">WG</div>
            <div><span class="badge">{{badgeText}}</span></div>
            <div class="icon-wrapper">{{iconSvg}}</div>
            <h1>{{safeTitle}}</h1>
            <p class="message">{{safeMessage}}</p>
            {{hintHtml}}
            <div class="footer">Webhook Gateway &bull; Notificaciones del Sistema</div>
          </div>
        </body>
        </html>
        """;
    }
}
