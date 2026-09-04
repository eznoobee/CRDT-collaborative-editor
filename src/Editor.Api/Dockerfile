# Build context is the repository root, so Directory.Build.props,
# Directory.Packages.props, global.json and client/ are all in scope.
#
# The client is built HERE rather than copied in from a previous step. An image
# that is only correct when whoever built it remembered to run a script is the
# class of defect §4's deployment shape exists to close: for eleven phases the
# published image contained no application at all and nothing said so, because
# nothing asked the image what was in it (§13.27, register row 18).
# Debian rather than Alpine, deliberately. package-lock.json is resolved on
# glibc, and the toolchain pulls platform-specific native binaries (the bundler's
# among them); on musl those optional packages are absent and the build fails
# with "command not found", which says nothing about the cause. This stage is
# discarded — only dist/ reaches the final image — so its size buys nothing.
FROM node:22 AS client
WORKDIR /client

# NODE_ENV is set explicitly and is not a formality. React resolves through its
# development export condition when it is anything other than production, and
# the result is a bundle that runs Strict Mode's double-invocation of effects —
# an artefact no user receives (§13.26). The assertion after the build is the
# durable half: setting a variable fixes today's cause, checking the output
# fails whatever tomorrow's turns out to be.
ENV NODE_ENV=production

# Playwright is a devDependency of the client and `npm ci` installs it: the
# build needs typescript and vite from the same set. Its browser download is
# ~150 MB that this image will never run.
ENV PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1

COPY client/package.json client/package-lock.json ./
RUN npm ci

COPY client/ ./
RUN npm run build \
 && if grep -rq 'Download the React DevTools' dist/assets/; then \
        echo "FAIL: dist contains React's development build (NODE_ENV=$NODE_ENV)." >&2; \
        exit 1; \
    fi

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

# The migration bundle, built here and shipped in the same image (§4). A
# self-contained executable that applies the schema and exits — so the migrator
# and the API are provably the same build, which a separate SDK-based image
# would not be. `dotnet ef` itself needs the SDK; the bundle does not.
COPY dotnet-tools.json ./
RUN dotnet tool restore \
 && dotnet ef migrations bundle \
        --project src/Editor.Infrastructure \
        --startup-project src/Editor.Infrastructure \
        --configuration Release \
        --output /app/migrate --force

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
COPY --from=client /client/dist ./wwwroot

# §9's single-page application, served from this origin. Defaulted here rather
# than left to the deployment: the files are in the image, so the path that
# finds them is a property of the image too, and a stack that forgot to set it
# would serve the API alone and look healthy (§13.28).
ENV Spa__RootPath=/app/wwwroot

# Runs as the non-root 'app' user provided by the base image.
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Editor.Api.dll"]
