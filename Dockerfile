# Sim i:  docker build -t sim-i .
#   docker run --rm -p 127.0.0.1:2323:2323 -p 127.0.0.1:8474:8474 -p 127.0.0.1:8475:8475 -p 127.0.0.1:8476:8476 -p 127.0.0.1:449:449 sim-i
# 5250 Telnet on 2323; Network Print, remote command, sign-on and the port mapper on IBM's own
# ports: 8474, 8475, 8476 and 449. The mapper tells clients the ports inside the container, so
# publish them unchanged.
# (Docker lets a non-root process in a container listen below 1024.)
# TLS on IBM's ports too: Telnet 992, Network Print 9474, remote command 9475, sign-on 9476. The certificate is made
# in /certs on first start; mount a named volume there (-v sim-i-certs:/certs) to keep it,
# and copy /certs/sim-i.crt out for clients to trust.
# Everything (each library a schema: source, programs, tables; QGPL also the users and spooled files) is in the
# PostgreSQL database SIM_I_DATABASE names (-e SIM_I_DATABASE="Host=db;Database=simi;Username=...;Password=...").
# To load source, mount a folder with a folder for each library and set SIM_I_SOURCE_DIR to it
# (-v "$PWD/source:/source:ro" -e SIM_I_SOURCE_DIR=/source).
# The web source editor is on 8080 (sign on with a user profile), and the IFS's root is /ifs: mount a
# named volume there (-v sim-i-ifs:/ifs) to keep it. compose.yaml runs all of this, with PostgreSQL.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/SimI/SimI.csproj src/SimI/
RUN dotnet restore src/SimI/SimI.csproj
COPY src/ src/
RUN dotnet publish src/SimI/SimI.csproj -c Release --no-restore -o /app

# The ASP.NET runtime: the web source editor runs on it.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
RUN mkdir /certs /ifs && chown app /certs /ifs
USER app
EXPOSE 2323 992 8474 9474 8475 9475 8476 9476 8473 9473 449 8080 4711
ENTRYPOINT ["dotnet", "SimI.dll", "--bind", "0.0.0.0", "--port", "2323", "--print-port", "8474", "--signon-port", "8476", "--command-port", "8475", "--file-port", "8473", "--mapper-port", "449", \
    "--web-port", "8080", "--debug-port", "4711", "--ifs-dir", "/ifs", \
    "--tls-port", "992", "--print-tls-port", "9474", "--signon-tls-port", "9476", "--command-tls-port", "9475", "--file-tls-port", "9473", "--tls-cert", "/certs/sim-i.pfx"]
