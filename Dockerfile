# Multi-arch: the SDK runs natively on the build machine and cross-publishes for the target (amd64 or arm64).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY nuget.config ReolinkMailBridge.csproj ./
RUN dotnet restore -a $TARGETARCH
COPY . .
RUN dotnet publish -c Release -a $TARGETARCH -o /app --no-restore --self-contained false

FROM mcr.microsoft.com/dotnet/runtime:10.0
# Alert emails carry local times without a zone; TZ (e.g. Europe/Bratislava) needs the zone database.
USER root
RUN apt-get update && apt-get install -y --no-install-recommends tzdata && rm -rf /var/lib/apt/lists/*
USER $APP_UID
WORKDIR /app
COPY --from=build /app .
# The image runs as a non-root user, which cannot bind port 25, so SMTP listens on 2525 (cameras: Server Port 2525).
ENV Bridge__Smtp__Port=2525 \
    Bridge__DataDirectory=/data \
    Bridge__SaveMailDirectory=/data/mail
EXPOSE 2525
VOLUME /data
ENTRYPOINT ["dotnet", "ReolinkMailBridge.dll"]
