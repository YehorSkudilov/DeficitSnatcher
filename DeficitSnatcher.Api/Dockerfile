# The Deficit Snatcher API, with the Chromium its checkouts and Browser-kind sites use. Chromium always runs with a window
# (headless is easy for bot checks to spot), on a virtual screen: Xvfb, started by xvfb-run below.
#   docker build -t deficitsnatcher-api DeficitSnatcher.Api
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY DeficitSnatcher.Api.csproj ./
RUN dotnet restore DeficitSnatcher.Api.csproj
COPY . .
RUN dotnet publish DeficitSnatcher.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
# Chromium and the system libraries it needs, somewhere the app's user can read (Program.cs: install-browser).
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
# libatomic1: the Node that Playwright's installer runs on needs it, and the slim runtime image doesn't have it.
# The chmod lets the app's (non-root) user run that Node and the browsers: they're only executable by root otherwise.
# xvfb + xauth: the virtual screen Chromium's window is on.
RUN apt-get update && apt-get install -y --no-install-recommends libatomic1 xvfb xauth \
    && dotnet DeficitSnatcher.Api.dll install-browser --with-deps \
    && chmod -R a+rX /ms-playwright /app/.playwright \
    && rm -rf /var/lib/apt/lists/*
# What's deployed, for the site's Deploy tab (docker-compose.yml passes them).
ARG GIT_SHA=dev
ARG BUILT_AT=
ENV GIT_SHA=$GIT_SHA BUILT_AT=$BUILT_AT
USER $APP_UID
ENTRYPOINT ["xvfb-run", "--auto-servernum", "--server-args=-screen 0 1920x1080x24 -nolisten tcp", "dotnet", "DeficitSnatcher.Api.dll"]
