# ==========================================
# Stage 1: Build the application (Fast Cross-Compilation)
# ==========================================
# We use --platform=$BUILDPLATFORM to run the SDK on the fast Host CPU (Intel),
# even when building for ARM.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# We must declare this ARG so we can tell .NET which architecture to build for.
ARG TARGETARCH

# Install FFmpeg (Only kept here if your Unit Tests run during build)
RUN apt-get update && apt-get install -y ffmpeg

# Copy the project file and restore dependencies
# We add '-a $TARGETARCH' to pull specific native dependencies if needed
COPY FsNatsWhisper.fsproj .
RUN dotnet restore "FsNatsWhisper.fsproj" -a $TARGETARCH

# Copy the rest of the source code
COPY . .

# Publish the application
# We use '-a $TARGETARCH' to cross-compile explicitly.
# This prevents the "QEMU Tax" and makes ARM builds 10x faster.
RUN dotnet publish "FsNatsWhisper.fsproj" \
    -a $TARGETARCH \
    -c Release \
    -o /app/publish

# ==========================================
# Stage 2: Create the final, smaller runtime image
# ==========================================
# Docker automatically pulls the correct architecture (ARM64 or AMD64) here.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

# Install FFmpeg runtime dependency
# Added 'rm -rf' to clean up apt cache and keep the image small
RUN apt-get update && \
    apt-get install -y ffmpeg && \
    rm -rf /var/lib/apt/lists/*

# Copy the published output from the build stage
COPY --from=build /app/publish .

# --- For Local Testing Only ---
# The following line copies your local 'downloads' folder into the container
# so the Test.fs program can find the audio file.
# For a clean production image, you should comment out or remove this line.
COPY downloads/ ./downloads/

# Set the entry point to run the application
# The application will be started by running 'dotnet FsNatsWhisper.dll'
ENTRYPOINT ["dotnet", "FsNatsWhisper.dll"]
