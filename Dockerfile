# syntax=docker/dockerfile:1
# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/CalorieTracker.Web/CalorieTracker.Web.csproj src/CalorieTracker.Web/
RUN dotnet restore src/CalorieTracker.Web/CalorieTracker.Web.csproj
COPY src/ src/
RUN dotnet publish src/CalorieTracker.Web/CalorieTracker.Web.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_NOLOGO=1
COPY --from=build /app .
# Run as the image's built-in non-root user. The container is stateless: no files are written at runtime.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "CalorieTracker.Web.dll"]
