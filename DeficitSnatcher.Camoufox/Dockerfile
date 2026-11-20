# Camoufox (a Firefox that hides automation) behind a small HTTP service (server.py), for "Camoufox"-kind pages.
# Started by docker compose only with the camoufox profile (COMPOSE_PROFILES=camoufox in .env).
#   docker build -t deficitsnatcher-camoufox DeficitSnatcher.Camoufox
FROM python:3.12-slim-bookworm
ENV PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1
# Firefox's libraries, a virtual screen (Xvfb: it runs with a window, like the API's Chromium) and some fonts.
RUN apt-get update && apt-get install -y --no-install-recommends \
        libgtk-3-0 libx11-xcb1 libasound2 libdbus-glib-1-2 libxtst6 libpci3 xvfb fonts-liberation fonts-noto-color-emoji \
    && rm -rf /var/lib/apt/lists/*
RUN useradd --create-home --uid 1000 app
USER app
WORKDIR /home/app
COPY requirements.txt .
RUN pip install --user -r requirements.txt && python -m camoufox fetch
COPY server.py .
EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/health', timeout=4)"
CMD ["python", "server.py"]
