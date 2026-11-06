# Development verification image only; not the future unattended runner service.
FROM mcr.microsoft.com/dotnet/sdk:10.0.403
RUN apt-get update && apt-get install -y --no-install-recommends git curl ca-certificates xz-utils
ARG NODE_VERSION=26.13.0
RUN curl -fsSLO https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-linux-x64.tar.xz \
    && curl -fsSLO https://nodejs.org/dist/v${NODE_VERSION}/SHASUMS256.txt \
    && grep " node-v${NODE_VERSION}-linux-x64.tar.xz$" SHASUMS256.txt | sha256sum --check \
    && tar -xJf node-v${NODE_VERSION}-linux-x64.tar.xz -C /usr/local --strip-components=1
WORKDIR /workspace
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ApiWrangler.Domain/ src/ApiWrangler.Domain/
COPY src/ApiWrangler.Application/ src/ApiWrangler.Application/
COPY src/ApiWrangler.Execution/ src/ApiWrangler.Execution/
COPY src/ApiWrangler.Cli/ src/ApiWrangler.Cli/
COPY tests/ApiWrangler.IntegrationTests/ tests/ApiWrangler.IntegrationTests/
COPY workers/node/ workers/node/
RUN npm ci --ignore-scripts --prefix workers/node && npm run build --prefix workers/node
RUN dotnet restore tests/ApiWrangler.IntegrationTests --locked-mode \
    && dotnet build tests/ApiWrangler.IntegrationTests -c Release --no-restore \
    && dotnet build src/ApiWrangler.Cli -c Release -p:RestoreLockedMode=true
ENV AWR_NODE=/usr/local/bin/node
ENV AWR_NODE_WORKER=/workspace/workers/node/dist/probe.js
ENTRYPOINT ["dotnet", "test", "tests/ApiWrangler.IntegrationTests", "-c", "Release", "--no-build", "--logger", "trx"]
