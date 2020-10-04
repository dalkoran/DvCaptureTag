#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/core/runtime:3.1-buster-slim AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/core/sdk:3.1-buster AS build
WORKDIR /src
COPY ["DVCaptureTag.Core.Console/DVCaptureTag.Core.Console.csproj", "DVCaptureTag.Core.Console/"]
RUN dotnet restore "DVCaptureTag.Core.Console/DVCaptureTag.Core.Console.csproj"
COPY . .
WORKDIR "/src/DVCaptureTag.Core.Console"
RUN dotnet build "DVCaptureTag.Core.Console.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "DVCaptureTag.Core.Console.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

RUN apt-get update
RUN apt-get install -y wget
RUN wget https://packages.microsoft.com/config/ubuntu/20.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
RUN dpkg -i packages-microsoft-prod.deb
RUN apt-get update
RUN apt-get install -y apt-transport-https
#RUN apt-get update
#RUN apt-get install -y dotnet-sdk-3.1

RUN apt-get update
RUN apt-get install -y libzen0v5 libmms0 libssh-4 libssl1.1 openssl zlib1g zlibc libsqlite3-0 libnghttp2-14 librtmp1 curl
RUN apt-get install -y mediainfo

VOLUME /app/data

ENTRYPOINT ["dotnet", "DVCaptureTag.Core.Console.dll", "--folderPath", "/app/data/SCRATCH_1"]