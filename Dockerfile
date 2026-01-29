# Stage 1: Build the application
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Install FFmpeg
RUN apt-get update && apt-get install -y ffmpeg

# Copy the project file and restore dependencies first to leverage Docker layer caching
COPY FsNatsWhisper.fsproj .
RUN dotnet restore "FsNatsWhisper.fsproj"

# Copy the rest of the source code
COPY . .

# Publish the application to a single directory, ready for deployment
# Using -c Release for a production-optimized build
RUN dotnet publish "FsNatsWhisper.fsproj" -c Release -o /app/publish

# Stage 2: Create the final, smaller runtime image
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

# Install FFmpeg runtime dependency
RUN apt-get update && apt-get install -y ffmpeg

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
