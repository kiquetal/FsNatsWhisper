# ==========================================
# Stage 1: Build (Fast Factory)
# ==========================================
# The '--platform=$BUILDPLATFORM' is CRITICAL. 
# It forces this stage to use the Fast Intel CPU.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG TARGETARCH

# REMOVED: RUN apt-get install -y ffmpeg
# We do NOT need video tools just to compile C# code. This saves 5+ minutes.

COPY FsNatsWhisper.fsproj .
RUN dotnet restore "FsNatsWhisper.fsproj" -a $TARGETARCH

COPY . .
RUN dotnet publish "FsNatsWhisper.fsproj" \
    -a $TARGETARCH \
    -c Release \
    -o /app/publish

# ==========================================
# Stage 2: Runtime (Final Product)
# ==========================================
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

# ✅ OPTIMIZED: We add '--no-install-recommends'
# This stops it from downloading Python, X11, and Graphic Drivers.
RUN apt-get update && \
    apt-get install -y --no-install-recommends ffmpeg && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# (Optional) Local testing block
COPY downloads/ ./downloads/

ENTRYPOINT ["dotnet", "FsNatsWhisper.dll"]
