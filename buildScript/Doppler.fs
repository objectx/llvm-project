module BuildScript.Doppler

open Fake.Core

open FsHttp

let private token = Environment.environVarOrFail "DOPPLER_TOKEN"

let private apiUrl =
    "https://api.doppler.com/v3/configs/config/secrets/download?format=json"

type SCCacheConfig =
    { container: string
      connectionString: string }

let request () : SCCacheConfig =
    let config =
        http {
            GET apiUrl
            AuthorizationBearer token
        }
        |> Request.send
        |> Response.toJson
        |> fun json ->
            { container = (json?SCCACHE_AZURE_BLOB_CONTAINER).GetString()
              connectionString = (json?SCCACHE_AZURE_CONNECTION_STRING).GetString() }

    Trace.logfn "SCCache config: %A (token: %s)" config token
    config
