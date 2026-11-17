FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY CodexAgentPlatform.Application/CodexAgentPlatform.Application.csproj CodexAgentPlatform.Application/
COPY CodexAgentPlatform.Infrastructure/CodexAgentPlatform.Infrastructure.csproj CodexAgentPlatform.Infrastructure/
COPY CodexAgentPlatform/CodexAgentPlatform.csproj CodexAgentPlatform/
RUN dotnet restore CodexAgentPlatform/CodexAgentPlatform.csproj
COPY CodexAgentPlatform.Application/ CodexAgentPlatform.Application/
COPY CodexAgentPlatform.Infrastructure/ CodexAgentPlatform.Infrastructure/
COPY CodexAgentPlatform/ CodexAgentPlatform/
RUN dotnet publish CodexAgentPlatform/CodexAgentPlatform.csproj -c Release --no-restore -o /publish \
    && test -s /publish/wwwroot/_framework/blazor.web.js

FROM node:24-bookworm-slim AS codex
ARG CODEX_VERSION=0.167.0
RUN npm install --global "@openai/codex@${CODEX_VERSION}" \
    && codex --version

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends bubblewrap ca-certificates git python3 python3-venv \
    && rm -rf /var/lib/apt/lists/* \
    && install -d -o app -g app -m 0700 /data /workspaces
COPY --from=codex /usr/local/bin/node /usr/local/bin/node
COPY --from=codex /usr/local/lib/node_modules/@openai/codex /usr/local/lib/node_modules/@openai/codex
RUN ln -s /usr/local/lib/node_modules/@openai/codex/bin/codex.js /usr/local/bin/codex
WORKDIR /app
COPY --from=build /publish/ ./
ENV Platform__Data=/data \
    Platform__Workspaces=/workspaces \
    Platform__CodexExecutable=/usr/local/bin/codex
EXPOSE 8443
USER app
ENTRYPOINT ["dotnet", "CodexAgentPlatform.dll"]
