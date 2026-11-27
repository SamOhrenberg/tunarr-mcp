# syntax=docker/dockerfile:1

# Build on the builder's own architecture and cross-compile for the target, so multi-arch builds don't need emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
WORKDIR /src

# Restore from the project files alone, so this layer stays cached until dependencies change.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/TunarrMcp.Server/TunarrMcp.Server.csproj src/TunarrMcp.Server/
COPY src/TunarrMcp.Tunarr/TunarrMcp.Tunarr.csproj src/TunarrMcp.Tunarr/
RUN dotnet restore src/TunarrMcp.Server -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/TunarrMcp.Server -c Release -a $TARGETARCH --no-restore -o /app /p:UseAppHost=false

# The data directory is created here (the runtime image has no shell) and handed to the app user below,
# so a named volume mounted on it starts out writable.
RUN mkdir /data

# Chiseled: no shell or package manager, non-root by default. "-extra" adds tzdata, which TZ=... needs
# for tool output in local time (and ICU, which this app doesn't use).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app .
COPY --from=build --chown=$APP_UID /data /data

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Data__Path=/data
EXPOSE 8080
VOLUME /data
USER $APP_UID

# No curl in the image: the app probes its own /healthz.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "TunarrMcp.Server.dll", "--healthcheck"]

# Arguments pass through, e.g. `docker run --rm -it IMAGE setup --out /secrets`.
ENTRYPOINT ["dotnet", "TunarrMcp.Server.dll"]
