FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props NuGet.config ./
COPY src/SampleLegacyApp.Core/SampleLegacyApp.Core.csproj src/SampleLegacyApp.Core/
COPY src/SampleLegacyApp.Api/SampleLegacyApp.Api.csproj src/SampleLegacyApp.Api/
RUN dotnet restore src/SampleLegacyApp.Api/SampleLegacyApp.Api.csproj
COPY src/ src/
RUN dotnet publish src/SampleLegacyApp.Api/SampleLegacyApp.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS final
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "SampleLegacyApp.Api.dll"]
