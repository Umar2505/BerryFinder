# BerryFinder inference service. Build from the repository root:
#   docker build -f inference/Dockerfile -t berryfinder-inference .
FROM python:3.12-slim
WORKDIR /srv
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 BERRY_MODEL_DIR=/srv/models/nordic-gbif-1.0 BERRY_CACHE_DIR=/var/cache/berryfinder
COPY inference/requirements.txt inference/requirements.txt
RUN pip install --no-cache-dir --extra-index-url https://download.pytorch.org/whl/cpu -r inference/requirements.txt
COPY models/nordic-gbif-1.0 models/nordic-gbif-1.0
COPY inference/berry_inference inference/berry_inference
WORKDIR /srv/inference
VOLUME /var/cache/berryfinder
EXPOSE 8090
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8090/api/v1/health')"
CMD ["uvicorn", "berry_inference.api:app", "--host", "0.0.0.0", "--port", "8090"]
