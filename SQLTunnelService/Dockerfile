FROM mcr.microsoft.com/dotnet/runtime:6.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["SQLTunnelService.csproj", "./"]
RUN dotnet restore "SQLTunnelService.csproj"
COPY . .
WORKDIR "/src"
RUN dotnet build "SQLTunnelService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "SQLTunnelService.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
RUN mkdir -p /app/logs

# Set environment variables
ENV DOTNET_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "SQLTunnelService.dll"]