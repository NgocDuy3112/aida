FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY aida.csproj ./
RUN dotnet restore

COPY . .
RUN dotnet publish aida.csproj -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=true \
    TESSDATA_PREFIX=/usr/share/tesseract-ocr/5/tessdata

# Local OCR for scanned PDFs (Tesseract, vie+eng).
# The Tesseract 5.2.0 NuGet interop resolves native libraries from <app>/x64 and
# asks for the package's hard-coded names (libtesseract50, libleptonica-1.82.0)
# plus libdl, so alias the distro sonames.
# The build host blocks outbound port 80, so force apt onto HTTPS (443).
RUN set -eux; \
    sed -i 's|http://|https://|g' /etc/apt/sources.list.d/ubuntu.sources; \
    apt-get update; \
    apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-vie tesseract-ocr-eng; \
    libtesseract="$(ldconfig -p | awk '/libtesseract\.so\.5/ {print $NF; exit}')"; \
    liblept="$(ldconfig -p | awk '/liblept\.so\.5/ {print $NF; exit}')"; \
    libdl="$(ldconfig -p | awk '/libdl\.so\.2/ {print $NF; exit}')"; \
    mkdir -p /app/x64; \
    ln -sf "$libtesseract" /app/x64/libtesseract50.so; \
    ln -sf "$liblept" /app/x64/libleptonica-1.82.0.so; \
    ln -sf "$libdl" "$(dirname "$libdl")/libdl.so"; \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /out .

RUN mkdir -p /app/App_Data/documents && chown -R $APP_UID:$APP_UID /app/App_Data

USER $APP_UID
EXPOSE 8080

ENTRYPOINT ["dotnet", "aida.dll"]
