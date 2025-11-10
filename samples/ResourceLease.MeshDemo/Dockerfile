FROM mcr.microsoft.com/dotnet/nightly/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
ARG DOTNET_RUNTIME_VERSION=10.0.0-rc.2.25502.107
WORKDIR /src
COPY . .

RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

RUN dotnet publish samples/ResourceLease.MeshDemo/ResourceLease.MeshDemo.csproj \
    --configuration $BUILD_CONFIGURATION \
    --output /app/publish \
    -r linux-x64 \
    -p:PublishAot=true \
    -p:StripSymbols=true \
    -p:InvariantGlobalization=true \
    -p:RuntimeFrameworkVersion=$DOTNET_RUNTIME_VERSION \
    -p:AspNetCoreFrameworkVersion=$DOTNET_RUNTIME_VERSION \
    -p:MicrosoftNETCoreAppRuntimeVersion=$DOTNET_RUNTIME_VERSION \
    --self-contained true

FROM mcr.microsoft.com/dotnet/nightly/runtime-deps:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    DOTNET_CLI_ALLOW_PREVIEW=true
EXPOSE 8080
EXPOSE 7420

ENTRYPOINT ["./ResourceLease.MeshDemo"]
