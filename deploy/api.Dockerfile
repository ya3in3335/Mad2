# Build context: repository root.  docker build -f deploy/api.Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY modules ./modules
COPY packages/contracts ./packages/contracts
COPY services ./services
RUN dotnet publish services/api/Veltrix.Api -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends postgresql-client && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 10001 --create-home veltrix && mkdir -p /data && chown veltrix /data
WORKDIR /app
COPY --from=build /out .
USER veltrix
ENV ASPNETCORE_URLS=http://+:8080 \
    Veltrix__DataDirectory=/data \
    Storage__Root=/data/files \
    DataProtection__KeysDirectory=/data/dp-keys \
    Backup__Directory=/data/backups
EXPOSE 8080
VOLUME ["/data"]
HEALTHCHECK --interval=30s --timeout=5s CMD ["sh", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 || exit 1"]
ENTRYPOINT ["dotnet", "Veltrix.Api.dll"]
