# agent-hq hub image (part 1 of docs/plans/distributed-agent-hq). Built only through the
# deploy script's container mode (scripts/agent-hq-deploy.sh container) — one deploy path.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY AgentHQ.slnx Directory.Build.props ./
COPY src/AgentHQ.Core src/AgentHQ.Core
COPY src/AgentHQ.Models src/AgentHQ.Models
COPY src/AgentHQ.Infrastructure src/AgentHQ.Infrastructure
COPY src/AgentHQ.Providers.ZCode src/AgentHQ.Providers.ZCode
COPY src/AgentHQ.Commands.ZCode src/AgentHQ.Commands.ZCode
COPY src/AgentHQ.Api src/AgentHQ.Api
RUN dotnet publish src/AgentHQ.Api -c Release -o /out --nologo && find /out -name '*.pdb' -delete

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# curl is the compose healthcheck probe; nothing else in the image is non-default.
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out ./
# Container bind is 0.0.0.0 (the compose port mapping keeps it loopback-only on the host);
# widening beyond the host is a recorded security decision, not this file's default.
ENV AGENTHQ_URL=http://0.0.0.0:5317 \
    AGENTHQ_DATA=/data
VOLUME /data
RUN useradd --system --uid 1001 agenthq && mkdir -p /data && chown agenthq /data
USER agenthq
EXPOSE 5317
ENTRYPOINT ["./AgentHQ.Api"]
