# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Bouwplannen.slnx ./
COPY src/Bouwplannen.Api/Bouwplannen.Api.csproj src/Bouwplannen.Api/
RUN dotnet restore src/Bouwplannen.Api/Bouwplannen.Api.csproj
COPY src/ src/
RUN dotnet publish src/Bouwplannen.Api/Bouwplannen.Api.csproj -c Release -o /app --no-restore

# Run
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Storage__DataDirectory=/data
VOLUME /data
EXPOSE 8080
# The aspnet image ships a non-root "app" user; /data must be writable for it.
RUN mkdir -p /data && chown app /data
USER app
ENTRYPOINT ["dotnet", "Bouwplannen.Api.dll"]
