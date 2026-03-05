FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY scraper.csproj .
RUN dotnet restore -r linux-arm64
COPY Program.cs .
RUN dotnet publish -c Release -r linux-arm64 --self-contained true \
    -p:PublishSingleFile=true -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:9.0
WORKDIR /app
COPY --from=build /app/scraper .

# Session file and output JSONs go here (mount as volume)
VOLUME ["/data"]

ENTRYPOINT ["/app/scraper"]
