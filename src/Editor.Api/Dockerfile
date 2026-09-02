# Build context is the repository root, so Directory.Build.props,
# Directory.Packages.props and global.json are all in scope.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Restore first, on just the files that affect it, so a source-only change
# does not invalidate the restore layer.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Crdt.Core/Crdt.Core.csproj                     src/Crdt.Core/
COPY src/Editor.Domain/Editor.Domain.csproj             src/Editor.Domain/
COPY src/Editor.Infrastructure/Editor.Infrastructure.csproj src/Editor.Infrastructure/
COPY src/Editor.Api/Editor.Api.csproj                   src/Editor.Api/
RUN dotnet restore src/Editor.Api/Editor.Api.csproj

COPY src/ src/
RUN dotnet publish src/Editor.Api/Editor.Api.csproj \
        --no-restore -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# Runs as the non-root 'app' user provided by the base image.
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Editor.Api.dll"]
