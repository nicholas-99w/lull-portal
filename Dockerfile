# Lull portal. Multi-stage: build with the SDK, run on the slim ASP.NET image.
# Configuration comes only from environment variables (see .env.example).
# The build stage runs on the builder's own platform and cross-compiles for the
# target (TARGETARCH: amd64 or arm64), so publishing both images needs no
# emulated .NET build (.github/workflows/publish.yml).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
# No extra CAs are baked in: a TLS-inspecting firewall must bypass decryption
# for the build's hosts instead (docs/administration.md, "Firewalls that decrypt HTTPS").
WORKDIR /src
COPY src/Lull.Portal/Lull.Portal.csproj src/Lull.Portal/
RUN dotnet restore src/Lull.Portal/Lull.Portal.csproj -a $TARGETARCH
COPY src/ src/
RUN dotnet publish src/Lull.Portal/Lull.Portal.csproj -c Release -a $TARGETARCH -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Cookie-encryption keys live on a volume so sign-ins survive restarts.
RUN mkdir -p /keys && chown app:app /keys
# Port 8080 comes from the base image's ASPNETCORE_HTTP_PORTS; setting
# ASPNETCORE_URLS as well only logs an "Overriding HTTP_PORTS" warning.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Lull__DataProtectionKeysPath=/keys
EXPOSE 8080
# The aspnet image's built-in non-root user.
USER app
# No curl in this image: the app itself checks /healthz (see Program.cs).
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "Lull.Portal.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "Lull.Portal.dll"]
