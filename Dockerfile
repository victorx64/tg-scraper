FROM python:3.12-slim

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY scraper.py .

# Session file and output JSONs go here (mount as volume)
VOLUME ["/data"]

ENTRYPOINT ["python", "scraper.py"]
