namespace FsNatsWhisper

module Domain =

    type TranscriptionRequest = {
        Bucket: string
        Key: string
        DecryptionKeyBase64: string
        IvBase64: string
    }

    type TranscriptionResult = {
        OriginalRequest: TranscriptionRequest
        TranscribedText: string
    }
