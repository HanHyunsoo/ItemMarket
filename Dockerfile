# syntax=docker/dockerfile:1
# ItemMarket.Api (Orleans co-host) — multi-stage: SDK build/publish → aspnet runtime.
# 모든 설정은 환경변수로 주입한다(ConnectionStrings__Postgres, Redis__ConnectionString,
# Http__Host, Http__Port, Orleans__ClusteringMode …). 컨테이너에서는 Http__Host=0.0.0.0.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 프로젝트 파일만 먼저 복사해 restore 레이어를 캐시한다(소스 변경 시 restore 재사용).
COPY src/ItemMarket.Contracts/ItemMarket.Contracts.csproj src/ItemMarket.Contracts/
COPY src/ItemMarket.Grains/ItemMarket.Grains.csproj src/ItemMarket.Grains/
COPY src/ItemMarket.Api/ItemMarket.Api.csproj src/ItemMarket.Api/
RUN dotnet restore src/ItemMarket.Api/ItemMarket.Api.csproj

# 나머지 소스 복사 후 Release publish.
COPY src/ src/
RUN dotnet publish src/ItemMarket.Api/ItemMarket.Api.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# 기본 컨테이너 설정: 8080 포트, 모든 인터페이스 바인딩. compose/런타임에서 오버라이드 가능.
ENV Http__Host=0.0.0.0 \
    Http__Port=8080 \
    DOTNET_gcServer=1
EXPOSE 8080

ENTRYPOINT ["dotnet", "ItemMarket.Api.dll"]
