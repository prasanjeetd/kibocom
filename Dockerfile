# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY InventoryHold.sln ./
COPY src/ ./src/

RUN dotnet restore src/InventoryHold.WebApi/InventoryHold.WebApi.csproj
RUN dotnet publish src/InventoryHold.WebApi/InventoryHold.WebApi.csproj \
    -c Release -o /app/publish --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Non-root by default in the .NET base images.
USER $APP_UID

ENTRYPOINT ["dotnet", "InventoryHold.WebApi.dll"]
