FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY Apps /apps
RUN install -d -o app -g app -m 0700 /var/lib/drawliar/outbox
USER app
