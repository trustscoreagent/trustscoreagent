FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files first (layer caching)
COPY TrustScore.sln ./
COPY src/TrustScore.Api/TrustScore.Api.csproj src/TrustScore.Api/
COPY src/TrustScore.Core/TrustScore.Core.csproj src/TrustScore.Core/
COPY src/TrustScore.Tests/TrustScore.Tests.csproj src/TrustScore.Tests/
RUN dotnet restore

# Copy everything and build
COPY . .
RUN dotnet publish src/TrustScore.Api/TrustScore.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# Copy migrations
COPY migrations/ ./migrations/

# Copy static assets served by the app (/llms.txt and /.well-known/agent.json)
COPY public/ ./public/

# Copy published app
COPY --from=build /app/publish .

# Cloud Run uses PORT env variable
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Drop root: the .NET base images ship a non-root "app" user.
USER app

ENTRYPOINT ["dotnet", "TrustScore.Api.dll"]
