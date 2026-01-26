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

    type TranscriptionResult = {
        OriginalRequest: FileUploadRequest
        TranscribedText: string
    }
