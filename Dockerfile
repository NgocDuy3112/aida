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
    DOTNET_NOLOGO=true

COPY --from=build /out .

# Npgsql's native GSSAPI support dlopens libgssapi_krb5.so.2, which the slim
# aspnet image doesn't ship — without it the first DB connection logs
# "Cannot load library libgssapi_krb5.so.2". Harmless to add (~200 KB).
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /app/App_Data/documents && chown -R $APP_UID:$APP_UID /app/App_Data

USER $APP_UID
EXPOSE 8080

ENTRYPOINT ["dotnet", "aida.dll"]
