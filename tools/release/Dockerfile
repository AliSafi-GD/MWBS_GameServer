# The release image. Unlike the Dockerfile in the repository root it does not build inside the container, so it needs no
# NuGet access: the build context is a folder made on the build machine with
#   dotnet publish src/GameServer.Host -c Release -r linux-x64 --self-contained false -o <folder>
# Only the base image below has to be on the machine (docker pull once, or docker load from the release bundle).
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY . ./
# Game modules are mounted here: /modules/<Name>/<Name>.dll
ENV GameServer__ModulesPath=/modules
EXPOSE 8080 7351
EXPOSE 7360/udp
ENTRYPOINT ["dotnet", "GameServer.Host.dll"]
