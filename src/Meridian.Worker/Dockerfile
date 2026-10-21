FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Meridian.Infrastructure/Meridian.Infrastructure.csproj src/Meridian.Infrastructure/
COPY src/Meridian.Worker/Meridian.Worker.csproj src/Meridian.Worker/
RUN dotnet restore src/Meridian.Worker/Meridian.Worker.csproj
COPY src/ src/
RUN dotnet publish src/Meridian.Worker/Meridian.Worker.csproj -c Release -o /app/publish --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Meridian.Worker.dll"]

