# FsNatsWhisper

FsNatsWhisper is an F# service that subscribes to a NATS subject, downloads an encrypted audio file from an S3-compatible storage (like Tigris), decrypts it, and performs speech-to-text transcription using the Whisper model.

## Architecture

The following ASCII diagram illustrates the flow of data through the system:

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
                                             | (2) Download File (Bucket/Key)
                                             v
                                    +-----------------+
                                    | S3 / Tigris     |
                                    +--------+--------+
                                             |
                                             | (Encrypted Bytes)
                                             v
                                    +-------------------------+
                                    |   Decryption (AES-GCM)  |
                                    |   (Key/IV from Request) |
                                    +--------+----------------+
                                             |
                                             | (Decrypted Audio PCM)
                                             v
                                    +-------------------------+
                                    |   Whisper Engine        |
                                    |   (Local Model)         |
                                    +--------+----------------+
                                             |
                                             | (Transcribed Text)
                                             v
                                    +-------------------------+
                                    |   NATS Publisher        |
                                    +--------+----------------+
                                             |
             (Publish Result)                | (4) Publish to ReplyTo or
             <-------------------------------+     'audio.transcription.result'
```

## Workflow Description

1.  **Subscription**: The application connects to a NATS server (default `nats://localhost:4222`) and subscribes to the subject `file.uploads` via JetStream.
2.  **Message Processing**: Upon receiving a message, it parses the payload to extract S3 details including the bucket name, data key, and metadata key.
3.  **Metadata Retrieval**: Downloads the metadata JSON file from S3 using the `s3_metadata_key`. This metadata contains:
    - KEK (Key Encryption Key) in base64 format
    - Algorithm information (AES-GCM-256)
    - Original and encrypted file sizes
    - Verification status
4.  **Key Decryption**: Uses the `MASTER_KEY` environment variable to decrypt the KEK from the metadata.
5.  **File Download**: Downloads the encrypted audio file from S3 using the `s3_data_key`.
6.  **File Decryption**: Decrypts the downloaded file using the AES-GCM algorithm with the decrypted KEK. The system expects:
    - A 12-byte IV prepended to the ciphertext
    - A 16-byte authentication tag appended to the end of the ciphertext
7.  **Transcription**: The decrypted raw audio is processed by `Whisper.net` (using the `ggml-tiny.bin` model, which is downloaded automatically if missing) to generate a text transcription.
8.  **Result Publication**: The resulting text is published back to NATS, either to the `ReplyTo` subject specified in the request or to `audio.transcription.result`.

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
  "kek": "base64_encoded_key_encryption_key",
  "algorithm": "AES-GCM-256",
  "original_filename": "audio.mp3",
  "original_size": 47299640,
  "encrypted_size": 47299668,
  "verification_status": "VERIFIED",
  "timestamp": 1769307798281
}
```

**Response (`audio.transcription.result`):**
```json
{
  "event_id": "unique-event-identifier",
  "transcribed_text": "The transcribed text from the audio file.",
  "status": "success"
}
```
