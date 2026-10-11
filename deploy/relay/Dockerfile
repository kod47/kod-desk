# Contextul de build este rădăcina repo-ului (vezi docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/KodDesk.Protocol/KodDesk.Protocol.csproj src/KodDesk.Protocol/
COPY src/KodDesk.Relay/KodDesk.Relay.csproj src/KodDesk.Relay/
RUN dotnet restore src/KodDesk.Relay/KodDesk.Relay.csproj
COPY src/KodDesk.Protocol/ src/KodDesk.Protocol/
COPY src/KodDesk.Relay/ src/KodDesk.Relay/
RUN dotnet publish src/KodDesk.Relay/KodDesk.Relay.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=21120 \
    DATA_DIR=/data
RUN mkdir -p /data && chown $APP_UID /data
VOLUME /data
EXPOSE 21120
USER $APP_UID
ENTRYPOINT ["dotnet", "KodDesk.Relay.dll"]
