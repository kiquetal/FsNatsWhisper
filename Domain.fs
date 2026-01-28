namespace FsNatsWhisper
open System.Text.Json.Serialization

module Domain =

    type FileUploadRequest = {
        [<JsonPropertyName("event_id")>]
        EventId: string
        
        [<JsonPropertyName("email")>]
        Email: string
        
        [<JsonPropertyName("file_uuid")>]
        FileUuid: string
        
        [<JsonPropertyName("s3_data_key")>]
        S3DataKey: string
        
        [<JsonPropertyName("s3_metadata_key")>]
        S3MetadataKey: string
        
        [<JsonPropertyName("bucket_name")>]
        BucketName: string
        
        [<JsonPropertyName("timestamp")>]
        Timestamp: int64
    }

    type Metadata = {
        [<JsonPropertyName("version")>]
        Version: string
        
        [<JsonPropertyName("kek")>]
        Kek: string
        
        [<JsonPropertyName("algorithm")>]
        Algorithm: string
        
        [<JsonPropertyName("original_filename")>]
        OriginalFilename: string
        
        [<JsonPropertyName("original_size")>]
        OriginalSize: int64
        
        [<JsonPropertyName("encrypted_size")>]
        EncryptedSize: int64
        
        [<JsonPropertyName("verification_status")>]
        VerificationStatus: string
        
        [<JsonPropertyName("timestamp")>]
        Timestamp: int64
    }

