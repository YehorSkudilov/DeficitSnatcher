# Camoufox (a Firefox that hides automation) as a Playwright server (serve.py): the API's browser by default.
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
COPY serve.py .
EXPOSE 4444
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s CMD python -c "import socket; socket.create_connection(('127.0.0.1', 4444), 4)"
CMD ["python", "serve.py"]
