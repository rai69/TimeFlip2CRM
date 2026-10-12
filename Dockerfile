# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/TimeflipCrmSync/TimeflipCrmSync.csproj src/TimeflipCrmSync/
RUN dotnet restore src/TimeflipCrmSync/TimeflipCrmSync.csproj

COPY src/ src/
RUN dotnet publish src/TimeflipCrmSync/TimeflipCrmSync.csproj \
    -c Release -o /app --no-restore

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Non-root user
RUN useradd --uid 1000 --create-home appuser
COPY --from=build /app .

# State (SQLite: sync state + mapping rules) lives on a mounted volume.
RUN mkdir -p /data && chown -R appuser:appuser /data
USER appuser

ENV DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Self-contained probe (no curl/wget needed in the image).
HEALTHCHECK --interval=30s --timeout=6s --start-period=30s --retries=3 \
    CMD ["dotnet", "TimeflipCrmSync.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "TimeflipCrmSync.dll"]
