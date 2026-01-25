namespace FsNatsWhisper

open System.IO
open Amazon.S3
open Amazon.S3.Model

module S3 =

    let downloadFile (bucket: string) (key: string) : Async<byte[]> =
        async {
            // Assumes credentials are in environment variables or ~/.aws/credentials
            use client = new AmazonS3Client()
            let request = GetObjectRequest(BucketName = bucket, Key = key)
            
            try
                use! response = client.GetObjectAsync(request) |> Async.AwaitTask
                use ms = new MemoryStream()
                do! response.ResponseStream.CopyToAsync(ms) |> Async.AwaitTask
                return ms.ToArray()
            with
            | ex -> 
                printfn "Error downloading from S3: %s" ex.Message
                return raise ex
        }
