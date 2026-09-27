FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files first (layer caching), then restore just what the image needs.
#
# Restoring the solution instead would pull in every project it lists, including ones that have
# no business in a runtime image and whose csproj is deliberately not copied here. That coupling
# broke the build the moment a tool project was added to the solution: the image does not ship
# it, so it should not have to know it exists. Restoring the API walks its own reference chain
# (Core) and nothing else.
COPY src/TrustScore.Api/TrustScore.Api.csproj src/TrustScore.Api/
COPY src/TrustScore.Core/TrustScore.Core.csproj src/TrustScore.Core/
RUN dotnet restore src/TrustScore.Api/TrustScore.Api.csproj

# Copy everything and build. The image is built without .git, so the deployed commit is passed
# in and stamped into InformationalVersion ("0.2.3+<sha>"), which /health reports.
ARG SOURCE_COMMIT=""
COPY . .
RUN dotnet publish src/TrustScore.Api/TrustScore.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    -p:SourceRevisionId="$SOURCE_COMMIT"

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
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
