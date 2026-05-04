FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY KuCoinFuturesReporter.csproj .
RUN dotnet restore KuCoinFuturesReporter.csproj
COPY . .
RUN dotnet publish KuCoinFuturesReporter.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "KuCoinFuturesReporter.dll"]
