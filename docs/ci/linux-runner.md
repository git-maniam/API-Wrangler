# Linux Container Runner Integration

API Wrangler CLI is fully cross-platform and runs headlessly on Linux x64 distributions (Debian, Ubuntu, Alpine, RedHat).

## Headless Execution Without Display Servers

Because the CLI (`ApiWrangler.Cli`) does not depend on WPF or X11, it can be run directly inside standard Docker containers without `xvfb`, `X11`, or windowing software.

## Minimal Dockerfile Example

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app
COPY . .
RUN dotnet build src/ApiWrangler.Cli -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/out ./
ENTRYPOINT ["dotnet", "api-wrangler.dll"]
```

## Running inside Container

```bash
docker run --rm \
  -v $(pwd)/api-wrangler:/project \
  -v $(pwd)/artifacts:/artifacts \
  -e API_WRANGLER_SECRET_TOKEN="my-secret-token" \
  api-wrangler:latest run \
    --project /project \
    --collection "Orders" \
    --reporters console,junit,json \
    --report-dir /artifacts/reports \
    --output /artifacts/runs
```
