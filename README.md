# FsNatsWhisper

FsNatsWhisper is an F# service that subscribes to a NATS subject, downloads an encrypted audio file from an S3-compatible storage (like Tigris), decrypts it, and performs speech-to-text transcription using the Whisper model.

## Architecture

The following ASCII diagram illustrates the flow of data through the system:

```ascii
                                    +-----------------+
                                    |  NATS Server    |
                                    +--------+--------+
                                             |
    [External Publisher]                     | (1) Subscribe: 'audio.transcription.request'
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

1.  **Subscription**: The application connects to a NATS server (default `nats://localhost:4222`) and subscribes to the subject `audio.transcription.request`.
2.  **File Retrieval**: Upon receiving a message, it parses the payload for S3 details (Bucket, Key) and downloads the encrypted file from the configured S3-compatible storage (e.g., Tigris).
3.  **Decryption**: The downloaded file is decrypted using the AES-GCM algorithm. The `DecryptionKey` and `IV` (Initialization Vector) provided in the NATS message are used for this process. The system expects the authentication tag to be appended to the end of the ciphertext.
4.  **Transcription**: The decrypted raw audio (PCM) is processed by `Whisper.net` (using the `ggml-tiny.bin` model, which is downloaded automatically if missing) to generate a text transcription.
5.  **Result Publication**: The resulting text is published back to NATS, either to the `ReplyTo` subject specified in the request or to `audio.transcription.result`.

## Configuration

The application expects an S3-compatible environment and a NATS server.

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `NATS_URL` | The URL of the NATS server. | `nats://localhost:4222` |
| `NATS_SUBJECT` | The NATS subject to subscribe to for transcription requests. | `audio.transcription.request` |
| `NATS_RESULT_SUBJECT` | The NATS subject to publish transcription results to (if no `ReplyTo` is provided). | `audio.transcription.result` |
| `AWS_ACCESS_KEY_ID` | S3/Tigris Access Key. | (Required) |
| `AWS_SECRET_ACCESS_KEY` | S3/Tigris Secret Key. | (Required) |
| `AWS_REGION` | S3 Region (e.g., `auto` for Tigris). | `us-east-1` |
| `S3_ENDPOINT` | Custom S3 endpoint (e.g., `https://fly.storage.tigris.dev`). | (Optional) |

Ensure your credentials are configured in your environment variables or `~/.aws/credentials`.

## Message Format

**Request (`audio.transcription.request`):**
```json
{
  "Bucket": "my-audio-bucket",
  "Key": "path/to/encrypted/audio.enc",
  "DecryptionKeyBase64": "<Base64 encoded 32-byte key>",
  "IvBase64": "<Base64 encoded 12-byte IV>"
}
```

**Response (`audio.transcription.result`):**
```json
{
  "OriginalRequest": { ... },
  "TranscribedText": "The transcribed text from the audio file."
}
```
