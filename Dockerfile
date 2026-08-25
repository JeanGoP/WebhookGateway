# syntax=docker/dockerfile:1

# ============================ build ============================
# SDK completo para restaurar y publicar. La imagen final NO lleva el SDK.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Primero solo los .csproj y los props de la solución: así la capa de restore queda
# cacheada y no se repite mientras las dependencias no cambien.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/WebhookGateway.Core/WebhookGateway.Core.csproj              src/WebhookGateway.Core/
COPY src/WebhookGateway.Data/WebhookGateway.Data.csproj              src/WebhookGateway.Data/
COPY src/WebhookGateway.Dispatcher/WebhookGateway.Dispatcher.csproj  src/WebhookGateway.Dispatcher/
COPY src/WebhookGateway.Api/WebhookGateway.Api.csproj                src/WebhookGateway.Api/
RUN dotnet restore src/WebhookGateway.Api/WebhookGateway.Api.csproj

# Ahora el código y la publicación en Release.
COPY src/ src/
RUN dotnet publish src/WebhookGateway.Api/WebhookGateway.Api.csproj \
    -c Release -o /app --no-restore

# =========================== runtime ===========================
# aspnet normal (Debian), NO -alpine ni -chiseled: Microsoft.Data.SqlClient necesita ICU
# para abrir la conexión y falla con "Globalization Invariant Mode is not supported" sin él.
# Lo explica Directory.Build.props (InvariantGlobalization=false).
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# reloadConfigOnChange=false: por defecto ASP.NET Core vigila appsettings.json con un
# watcher de archivos (inotify). En contenedores con el límite de inotify bajo —como los
# de Render— ese watcher no arranca y la app se cae en CreateBuilder. En producción la
# config viene de variables de entorno, no de editar el JSON en caliente, así que la
# vigilancia no aporta nada y se desactiva aquí.
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_hostBuilder__reloadConfigOnChange=false

COPY --from=build /app ./

# Render inyecta PORT; en local cae a 8080. Kestrel escucha en todas las interfaces.
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "dotnet WebhookGateway.Api.dll --urls http://+:${PORT:-8080}"]
