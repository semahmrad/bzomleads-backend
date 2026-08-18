FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Backend.csproj", "./"]
RUN dotnet restore "Backend.csproj"

COPY . .
RUN dotnet publish "Backend.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 10000
ENV ASPNETCORE_URLS=http://0.0.0.0:10000
ENV DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false
ENV DOTNET_USE_POLLING_FILE_WATCHER=1

ENTRYPOINT ["dotnet", "Backend.dll"]
