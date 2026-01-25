namespace FsNatsWhisper

open System.IO
open Amazon.S3
open Amazon.S3.Model

module S3 =

    let downloadFile (bucket: string) (key: string) : Async<byte[]> =
        async {
            let endpoint = System.Environment.GetEnvironmentVariable("S3_ENDPOINT")
            let region = System.Environment.GetEnvironmentVariable("AWS_REGION")
            
            let config = AmazonS3Config()
            if not (System.String.IsNullOrWhiteSpace(endpoint)) then
                config.ServiceURL <- endpoint
            elif not (System.String.IsNullOrWhiteSpace(region)) then
                config.RegionEndpoint <- Amazon.RegionEndpoint.GetBySystemName(region)

            // Assumes credentials are in environment variables or ~/.aws/credentials
            use client = new AmazonS3Client(config)
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
