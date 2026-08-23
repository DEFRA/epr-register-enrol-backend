# Base dotnet image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

# Add curl to template.
# CDP PLATFORM HEALTHCHECK REQUIREMENT
RUN apt update && \
    apt install curl -y && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Build stage image
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
WORKDIR "/src"

# unit test and code coverage
RUN dotnet test EprRegisterEnrolBackend.Test

# Development image: runs `dotnet watch` for hot reload during local development.
# Used by docker compose --watch via the `develop:` block in compose.yml.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS development
WORKDIR /src
COPY . .
WORKDIR /src/EprRegisterEnrolBackend
RUN dotnet restore
EXPOSE 8080
ENV DOTNET_USE_POLLING_FILE_WATCHER=1
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "watch", "run", "--no-launch-profile", "--non-interactive"]

FROM build AS publish
RUN dotnet publish EprRegisterEnrolBackend -c Release -o /app/publish /p:UseAppHost=false


ENV ASPNETCORE_FORWARDEDHEADERS_ENABLED=true

# Final production image
FROM base AS final
WORKDIR /app
COPY --from=publish --chown=$APP_UID:$APP_UID /app/publish .
# S6471: the published app must not run as root. The aspnet base image already ships a
# non-root "app" account (UID/GID 1654, exported as APP_UID), so /app is handed to it and
# the process drops to it. Nothing here needs privilege: the listen port is 8080
# (ASPNETCORE_HTTP_PORTS, set by the base image), above the privileged range, and the only
# runtime write is the ASP.NET Data Protection key ring under $HOME (/home/app), which the
# base image already creates owned by this account. Verified by running the built image:
# starts as uid 1654, binds 8080, /health returns 200.
RUN chown "$APP_UID:$APP_UID" /app
USER $APP_UID
EXPOSE 8085
ENTRYPOINT ["dotnet", "EprRegisterEnrolBackend.dll"]
