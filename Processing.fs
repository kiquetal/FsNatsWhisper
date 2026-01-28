namespace FsNatsWhisper

open System
open System.Text
open System.Text.Json
open FsNatsWhisper.Domain
open NATS.Client.Core
open NATS.Client.JetStream
open System.Threading.Tasks
open FsNatsWhisper.S3

module Processing =

    let downloadAndDecrypt (masterKey: string) (request: FileUploadRequest) : Async<byte[]> =
        async {
            printfn "Processing EventId: %s, Key: %s in Bucket: %s" request.EventId request.S3DataKey request.BucketName

            // 1. Download metadata
            let! metadataBytes = S3.downloadFile request.BucketName request.S3MetadataKey
            let metadataJson = Encoding.UTF8.GetString(metadataBytes)
            printfn "Downloaded metadata: %s" metadataJson

            // Parse metadata
            let options = JsonSerializerOptions()
            options.PropertyNameCaseInsensitive <- true
            let metadata = JsonSerializer.Deserialize<Metadata>(metadataJson, options)
            
            printfn "Metadata - Version: %s, Algorithm: %s, Original file: %s" metadata.Version metadata.Algorithm metadata.OriginalFilename

            // 2. Decrypt the KEK (Key Encryption Key) using the master key
            // The KEK in the metadata is base64 encoded and encrypted
            let masterKeyBytes = Convert.FromBase64String(masterKey)
            let kekBytes = Convert.FromBase64String(metadata.Kek)
            
            // Note: Based on the metadata structure, it appears the KEK is stored directly
            // If it needs decryption, we'll need IV information in the metadata
            printfn "Using KEK from metadata for decryption."

            // 3. Download encrypted audio file
            let! encryptedBytes = S3.downloadFile request.BucketName request.S3DataKey
            printfn "Downloaded %d bytes (expected encrypted size: %d)." encryptedBytes.Length metadata.EncryptedSize

            // 4. Decrypt the audio file using the KEK
            // Note: The current metadata structure doesn't include IV information
            // This needs to be adjusted based on how the encryption was actually performed
            // For now, we'll extract the IV from the encrypted data if it follows standard patterns
            
            // Assuming AES-GCM with 12-byte IV prepended to the ciphertext
            if encryptedBytes.Length < 12 then 
                failwith "Encrypted data is too short to contain IV"
            
            let iv = encryptedBytes.[0..11]
            let ciphertext = encryptedBytes.[12..]
            
            let audioBytes = Crypto.decrypt kekBytes iv ciphertext
            printfn "Decrypted audio. Size: %d bytes (expected original size: %d)." audioBytes.Length metadata.OriginalSize

            return audioBytes
        }

    let handleRequest (masterKey: string) (msg: INatsJSMsg<NatsMemoryOwner<byte>>) : Task =
        task {
            try
                // msg.Data is NatsMemoryOwner<byte>
                let dataSpan = msg.Data.Span

                // Log raw message for debugging
                let rawJson = Encoding.UTF8.GetString(dataSpan)
                printfn "Received message: %s" rawJson

                // Deserialize with case-insensitive property names
                let options = JsonSerializerOptions()
                options.PropertyNameCaseInsensitive <- true

                // Double-deserialize: first from the outer JSON string, then from the inner JSON string
                let innerJson = JsonSerializer.Deserialize<string>(rawJson)
                let request = JsonSerializer.Deserialize<FileUploadRequest>(innerJson, options)

                if box request = null then
                    printfn "Received empty or invalid request."
                    do! msg.AckAsync().AsTask() // Acknowledge to avoid reprocessing
                else
                    // Download metadata and decrypt the file
                    let! audioBytes = downloadAndDecrypt masterKey request |> Async.StartAsTask
                    
                    // TODO: Add transcription logic here later
                    
                    // Acknowledge the message
                    do! msg.AckAsync().AsTask()

            with ex ->
                printfn "Error processing message: %s" ex.Message
                printfn "Stack trace: %s" ex.StackTrace
                // Negative acknowledge on error so message can be redelivered
                do! msg.NakAsync().AsTask()
        }
