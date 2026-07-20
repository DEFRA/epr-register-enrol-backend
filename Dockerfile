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
COPY --from=publish /app/publish .
EXPOSE 8085
ENTRYPOINT ["dotnet", "EprRegisterEnrolBackend.dll"]
