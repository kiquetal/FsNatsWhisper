# FsNatsWhisper

FsNatsWhisper is an F# service that subscribes to a NATS subject, downloads an encrypted audio file from an S3-compatible storage (like Tigris), decrypts it, and saves it locally. The next phase will be to perform speech-to-text transcription using the Whisper model.

## Architecture

The following ASCII diagram illustrates the current flow of data through the system:

```ascii
                                    +-----------------+
                                    |  NATS Server    |
                                    +--------+--------+
                                             |
                                    | (1) Subscribe: 'file.uploads'
             |                               v
             +--------------------> +-------------------------+
             (Publish Request)      |   FsNatsWhisper Service |
                                    +-------------------------+
                                             |
                                             | (2) Download Metadata & File
                                             v
                                    +-----------------+
                                    | S3 / Tigris     |
                                    +--------+--------+
                                             |
                                             | (Encrypted KEK & Data)
                                             v
                                    +-------------------------+
                                    |   Decryption (AES-GCM)  |
                                    | (Master Key -> KEK -> Data) |
                                    +--------+----------------+
                                             |
                                             | (Decrypted Audio Bytes)
                                             v
                                    +-------------------------+
                                    |   Save to 'downloads/'  |
                                    |   (For Debugging)       |
                                    +-------------------------+
```

## Workflow Description

1.  **Subscription**: The application connects to a NATS server (default `nats://localhost:4222`) and subscribes to the subject `file.uploads` via JetStream.
2.  **Message Processing**: Upon receiving a message, it parses the payload to extract S3 details including the bucket name, data key, and metadata key.
3.  **Metadata Retrieval**: Downloads the metadata JSON file from S3 using the `s3_metadata_key`. This metadata contains:
    - KEK (Key Encryption Key) in base64 format, which is itself encrypted.
    - Algorithm information (AES-GCM-256)
    - Original and encrypted file sizes
    - Verification status
4.  **Key Decryption**: Uses the `MASTER_KEY` environment variable to decrypt the KEK from the metadata. The KEK is expected to have a 12-byte IV prepended to it.
5.  **File Download**: Downloads the encrypted audio file from S3 using the `s3_data_key`.
6.  **File Decryption**: Decrypts the downloaded file using the AES-GCM algorithm with the decrypted KEK. The system expects:
    - A 12-byte IV prepended to the ciphertext
    - A 16-byte authentication tag appended to the end of the ciphertext (handled by the `Crypto.decrypt` function)
7.  **Save for Debugging**: The decrypted audio data is saved to a `downloads` folder in the project's root directory for verification.
8.  **Next Steps**: The next phase of development will involve passing the decrypted audio bytes to a transcription engine and publishing the results back to NATS.

---

## Prerequisites

Before running this application, you must have the following software installed and available in your system's PATH:

-   **[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)** (or newer)
-   **[FFmpeg](https://ffmpeg.org/download.html)** - This is required for audio format conversion.

---

## Configuration

The application expects an S3-compatible environment and a NATS server.

### Environment Variables

| Variable | Description | Default | Required |
|----------|-------------|---------|----------|
| `MASTER_KEY` | Base64-encoded master key used to decrypt the KEK (Key Encryption Key) from metadata. | None | **Yes** |
| `NATS_URL` | The URL of the NATS server. | `nats://localhost:4222` | No |
| `NATS_SUBJECT` | The NATS subject to subscribe to for file upload requests. | `file.uploads` | No |
| `NATS_STREAM_NAME` | The JetStream stream name containing the messages. | `FILE_UPLOADS` | No |
| `NATS_CONSUMER_NAME` | The JetStream durable consumer name. | `file_processor` | No |
| `NATS_RESULT_SUBJECT` | The NATS subject to publish transcription results to (if no `ReplyTo` is provided). | `audio.transcription.result` | No |
| `AWS_ACCESS_KEY_ID` | S3/Tigris Access Key. | None | **Yes** |
| `AWS_SECRET_ACCESS_KEY` | S3/Tigris Secret Key. | None | **Yes** |
| `AWS_REGION` | S3 Region (e.g., `auto` for Tigris). | `us-east-1` | No |
| `S3_ENDPOINT` | Custom S3 endpoint (e.g., `https://fly.storage.tigris.dev`). | None | No |

**Note:** Ensure your AWS/S3 credentials are configured in your environment variables or `~/.aws/credentials`. The `MASTER_KEY` is required and the application will fail to start if it's not set.

## Running the Application

You can run the application using the .NET CLI. It is recommended to set the environment variables inline before the command to avoid persisting sensitive credentials in your shell session.

### Example (Bash/Linux/macOS)

```bash
MASTER_KEY="your_base64_encoded_master_key" \
NATS_URL="nats://localhost:4222" \
AWS_ACCESS_KEY_ID="your_access_key" \
AWS_SECRET_ACCESS_KEY="your_secret_key" \
AWS_REGION="auto" \
dotnet run
```

### Example (PowerShell)

```powershell
$env:MASTER_KEY="your_base64_encoded_master_key"
$env:NATS_URL="nats://localhost:4222"
$env:AWS_ACCESS_KEY_ID="your_access_key"
$env:AWS_SECRET_ACCESS_KEY="your_secret_key"
$env:AWS_REGION="auto"
dotnet run
```

## Message Format

**Request (`file.uploads`):**
```json
{
  "event_id": "unique-event-identifier",
  "email": "user@example.com",
  "file_uuid": "uuid-of-the-file",
  "s3_data_key": "path/to/encrypted/audio.enc",
  "s3_metadata_key": "path/to/metadata.json",
  "bucket_name": "my-audio-bucket",
  "timestamp": 1769307798281
}
```

**Metadata JSON (stored in S3 at `s3_metadata_key`):**
```json
{
  "version": "1.0",
  "kek": "base64_encoded_and_encrypted_key_encryption_key",
  "algorithm": "AES-GCM-256",
  "original_filename": "audio.mp3",
  "original_size": 47299640,
  "encrypted_size": 47299668,
  "verification_status": "VERIFIED",
  "timestamp": 1769307798281
}
```

**Response (`audio.transcription.result`):**

*(This is a planned feature. The service does not currently publish a response.)*

---

## Testing the Transcription Locally

A test program, `Test.fs`, is included to allow for easy, local testing of the transcription functionality without needing to run the full NATS service.

### How it Works

The test program will:
1.  Read a specified audio file from the `downloads` folder in your project root.
2.  Run the full transcription process on that file (`ffmpeg` conversion and Whisper model).
3.  Print the transcribed text to the console.
4.  Save the full transcription to a `.txt` file in the `downloads` folder.

### How to Run the Test

1.  **Place an audio file** in the `downloads` folder.
2.  **Edit `Test.fs`**: Open the `Test.fs` file and change the `audioFileName` variable to match the name of your test file.
    ```fsharp
    // in Test.fs
    let audioFileName = "your-audio-file.mp3"
    ```
3.  **Run from your terminal**:
    ```sh
    dotnet run
    ```
    The project is already configured to run the test program. You will see the transcription progress and final text in your console.

### Restoring the Main Service

After you are done testing, you must revert the following changes to run the main NATS service:
1.  **Uncomment the EntryPoint** in `Program.fs`:
    ```fsharp
    // Change this:
    // [<EntryPoint>] // Temporarily disabled for testing
    
    // To this:
    [<EntryPoint>]
    ```
2.  **Remove the test file** from the project. Open `FsNatsWhisper.fsproj` and delete this line:
    ```xml
    <Compile Include="Test.fs" />
    ```
3.  You can also delete the `Test.fs` file itself.

---

## Deployment (Docker)

To prepare the application for a production deployment, you should build it in the `Release` configuration and package it as a Docker image.

### Release Build

A `Release` build is optimized for performance and excludes debugging information, resulting in a smaller and faster application. You can create a release build by running:

```sh
dotnet publish -c Release
```

This command will compile your application and place the optimized output in the `bin/Release/net10.0/publish/` directory.

### Building the Docker Image

A `Dockerfile` is included in the project to simplify the process of creating a production-ready container. This Dockerfile uses a multi-stage build to create a small, efficient final image. It also includes the installation of `ffmpeg`, which is a required dependency for audio conversion.

To build the Docker image, run the following command from your project's root directory:

```sh
docker build -t fsnatswhisper .
```

### Running the Docker Container

Once the image is built, you can run it as a container. You must pass all the required environment variables to the container at runtime.

```sh
docker run --rm -it \
  -e MASTER_KEY="your_base64_encoded_master_key" \
  -e NATS_URL="nats://host.docker.internal:4222" \
  -e AWS_ACCESS_KEY_ID="your_access_key" \
  -e AWS_SECRET_ACCESS_KEY="your_secret_key" \
  -e AWS_REGION="auto" \
  fsnatswhisper
```

**Note**: The `NATS_URL` is set to `nats://host.docker.internal:4222` to allow the container to connect to a NATS server running on your local machine (the Docker host). This may need to be changed depending on your network setup.
