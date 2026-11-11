FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ src/
RUN dotnet publish src/GameServer.Host/GameServer.Host.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# Game modules are mounted here: /modules/<Name>/<Name>.dll
ENV GameServer__ModulesPath=/modules
EXPOSE 8080 7351
EXPOSE 7360/udp
ENTRYPOINT ["dotnet", "GameServer.Host.dll"]
