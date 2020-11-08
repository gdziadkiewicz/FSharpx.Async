// ----------------------------------------------------------------------------
// F# async extensions (AutoCancel.fsx)
// (c) Tomas Petricek, 2011, Available under Apache 2.0 license.
// ----------------------------------------------------------------------------

// This example demonstrates how to use 'AutoCancelAgent'
// The agent automatically stops its body when disposed.

//#r @"../../bin/FSharpx.Extras.dll"
#r @"../../../bin/FSharpx.Async.dll"

open FSharpx.Control

let op = async {
  // Create a local agent that is disposed when the 
  // workflow completes (using the 'use' construct)
  use agent = AutoCancelAgent<int * AsyncReplyChannel<unit>>.Start(fun agent -> async { 
    try 
      while true do
        // Wait for a message - note that we use timeout
        // to allow cancellation (when the operation completes)
        let! msg = agent.Receive(1000)
        match msg with 
        | (n, reply) ->
            // Print number and reply to the sender
            printfn "%d" n
            reply.Reply(())
    finally 
      // Called when the agent is disposed
      printfn "agent completed" })
  
  // Do some processing using the agent...
  for i in 0 .. 10 do 
    do! Async.Sleep(100)
    do! agent.PostAndAsyncReply(fun r -> i, r) 

  do! Async.Sleep(100)
  printfn "workflow completed" }

Async.Start(op)
