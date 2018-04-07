﻿/// You must acquire a commercial license from henrik@haf.se to use this code.
module Logary.Targets.OpsGenie

open System
open System.Text
open Hopac
open Hopac.Infixes
open Logary
open Logary.Message
open Logary.Internals
open Logary.Internals.Chiron
open Logary.Configuration
open HttpFs.Client
open HttpFs.Composition

/// An API key
type ApiKey = string

type Responder =
  | Team of teamId:string
  | User of userId:string
  | Escalation of eid:string
  | Schedule of sid:string

/// https://docs.opsgenie.com/docs/authentication
/// The configuration record for OpsGenie communication.
type OpsGenieConf =
  { endpoint: string
    /// Authentication token used for accessing the API.
    apiKey: ApiKey
    /// Alias the message to something semantically useful.
    getAlias: Message -> string
    getResponders: Message -> Responder[] }

type Priority =
  | P1
  | P2
  | P3
  | P4
  | P5
  static member ofLogLevel = function
    | Verbose -> P1
    | Debug -> P2
    | Info -> P2
    | Warn -> P3
    | Error -> P4
    | Fatal -> P5

module internal E =
  open Logary.Message.Patterns
  open Logary.Formatting

  module E = Json.Encode

  let private limitTo (max: int) (s: string) =
    if s.Length > max then s.Substring(0, max) else s

  let message (value: string) =
    E.string (limitTo 130 value)

  let alias (messageAlias: string) =
    E.string (limitTo 512 messageAlias)

  let description (d: string) =
    E.string (limitTo 15000 d)

  let responder =
    E.propertyList << function
    | Team teamId ->
      [
        "type", String "team"
        "id", String teamId
      ]
    | User userId ->
      [
        "type", String "user"
        "id", String userId
      ]
    | Escalation eid ->
      [
        "type", String "escalation"
        "id", String eid
      ]
    | Schedule sid ->
      [
        "type", String "schedule"
        "id", String sid
      ]

  let responders =
    E.arrayWith responder

//  let visibleTo
// Custom actions that will be available for the alert.
//  let actions
  let tags (ts: Set<string>) =
    E.stringSet ts

  let details (values: seq<string * obj>) =
    values
    |> Seq.map (fun (k, v) -> k, Json.encode v)
    |> Seq.toList
    |> JsonObject.ofPropertyList
    |> Json.Object

  let entity (e: string option) =
    e |> Option.map E.string |> E.option

  let source (logger: PointName) =
    logger
    |> PointName.format
    |> limitTo 100
    |> E.string

  let priority p =
    E.string <|
    match p with
    | P1 -> "P1"
    | P2 -> "P2"
    | P3 -> "P3"
    | P4 -> "P4"
    | P5 -> "P5"

  let user (u: string option) =
    u |> Option.map (limitTo 100) |> Option.map E.string |> E.option

  let note (n: string option) =
    n |> Option.map (limitTo 25000) |> Option.map E.string |> E.option

  let logaryMessage (conf: OpsGenieConf) (x: Message) jObj =
    jObj
    |> E.required message "message" x.value
    |> E.required alias "alias" (conf.getAlias x)
    |> E.optional description "description" (Message.tryGetField "description" x)
    |> E.required responders "responders" (conf.getResponders x)
    |> E.required tags "tags" (Message.getAllTags x)
    |> E.required details "details" (Message.getAllFields x)
    |> E.required source "source" x.name
    |> E.required (Priority.ofLogLevel >> priority) "priority" x.level

  let encode (conf: OpsGenieConf) (m: Message): Json option =
    if String.IsNullOrWhiteSpace m.value then None else
    Some (logaryMessage conf m JsonObject.empty |> Json.Object)

[<AutoOpen>]
module OpsGenieConfEx =
  type OpsGenieConf with
    /// Create a new OpsGenie configuration record for Logary to use for sending
    /// events to OpsGenie with.
    /// Optional:
    /// - `getAlias`: dedup-name for the message, should be human readable
    /// - `getResponders`: special-case responding. Perhaps by the assembly that triggered
    ///   the message?
    static member create (apiKey, ?endpoint, ?getAlias, ?getResponders) =
      { endpoint = defaultArg endpoint "https://api.opsgenie.com/v2"
        apiKey = apiKey
        getAlias = defaultArg getAlias (fun x -> x.value)
        getResponders = defaultArg getResponders (fun _ -> Array.empty) }

let empty =
  OpsGenieConf.create (
    Env.varDefault "OPSGENIE_API_KEY" "OPSGENIE_API_KEY=MISSING")

module internal Impl =
  open System.Net.Http

  let endpoint (conf: OpsGenieConf) =
    let ub = UriBuilder(conf.endpoint)
    ub.Path <- "/alerts"
    ub.Uri

  /// Guards so that all sent messages are successfully written.
  let guardRespCode (runtime: RuntimeInfo) (body, statusCode) =
    /// Create alert requests are processed asynchronously, therefore valid requests are responded with HTTP status 202 - Accepted.
    if statusCode = 202 then
      Job.result ()
    else
      runtime.logger.logWithAck Error (
        eventX "OpsGenie target received response {statusCode} with {body}."
        >> setField "statusCode" statusCode
        >> setField "body" body)
      |> Job.bind id
      |> Job.bind (fun () -> Job.raises (Exception body))

  let bodyAndCode (resp: Response) =
    resp
    |> Job.useIn Response.readBodyAsString
    |> Job.map (fun body -> body, resp.statusCode)

  // Move to Composition
  let codec enc dec =
    fun next inp ->
      next (enc inp) |> Alt.afterJob dec

  // Move to Composition
  let sinkJob (sink: _ -> #Job<unit>) =
    fun next inp ->
      next inp |> Alt.afterJob sink

  type State =
    { client: HttpClient
      send: Json -> Alt<unit> }

    interface IDisposable with
      member x.Dispose() =
        x.client.Dispose()

    static member create (conf: OpsGenieConf) (runtime: RuntimeInfo) =
      let client, endpoint = new HttpClient(), endpoint conf

      let create (msg: Json) =
        Request.createWithClient client Post endpoint
        |> Request.bodyString (Json.format msg)

      let filters: JobFilter<Request, Response, Json, unit> =
        codec create bodyAndCode
        >> sinkJob (guardRespCode runtime)

      { client = client; send = filters getResponse }

  let loop (conf: OpsGenieConf) (runtime: RuntimeInfo, api: TargetAPI) =
    runtime.logger.info (
      eventX "Started OpsGenie target with endpoint {endpoint}."
      >> setField "endpoint" (endpoint conf))

    let rec loop (state: State): Job<unit> =
      Alt.choose [
        api.shutdownCh ^=> fun ack ->
          runtime.logger.verbose (eventX "Shutting down OpsGenie target.")
          ack *<= () :> Job<_>

        RingBuffer.take api.requests ^=> function
          | Log (message, ack) ->
            job {
              do runtime.logger.verbose (eventX "Writing an alert")
              do! E.encode conf message
                  |> Option.map state.send
                  |> Option.orDefault (fun () -> Alt.always ())
              do runtime.logger.verbose (eventX "Acking messages")
              do! ack *<= ()
              return! loop state
            }

          | Flush (ackCh, nack) ->
            ackCh *<= ()

      ] :> Job<_>

    job {
      use state = State.create conf runtime
      return! loop state
    }

/// Create a new Mixpanel target
[<CompiledName "Create">]
let create conf name =
  TargetConf.createSimple (Impl.loop conf) name

/// Use with LogaryFactory.New( s => s.Target<Mixpanel.Builder>() )
type Builder(conf: OpsGenieConf, callParent: Target.ParentCallback<Builder>) =
  let update (conf': OpsGenieConf): Builder =
    Builder(conf', callParent)

  /// Sets the Mixpanel authentication token.
  member x.Token(apiKey: ApiKey) =
    Builder({ conf with apiKey = apiKey }, callParent)

  /// Sets the Mixpanel API endpoint. Useful for stubbing Mixpanel locally.
  member x.WriteEndpoint(endpoint: string) =
    Builder({ conf with endpoint = endpoint }, callParent)

  /// You've finished configuring the Mixpanel target.
  member x.Done () =
    ! (callParent x)

  new(callParent: Target.ParentCallback<_>) =
    Builder(empty, callParent)

  interface Target.SpecificTargetConf with
    member x.Build name = create conf name
